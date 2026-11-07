using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace LighthouseManagerService.Dimming;

/// <summary>
///     Dims the desktop displays while a VR game is shown (<see cref="StationController.DimDisplaysInVr" />) and restores
///     their brightness afterwards. Linux with Monado and KDE Plasma for now. The decisions are made by
///     <see cref="DimmingPolicy" /> and <see cref="DimmingPlan" />.
/// </summary>
public class DisplayDimmer(ILogger<DisplayDimmer> logger, IOptions<AppSettings> settings,
    StationController controller) : BackgroundService
{
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransitionStep = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     How often to check whether brightness control is available (e.g. Plasma started after the service).
    /// </summary>
    private static readonly TimeSpan AvailabilityInterval = TimeSpan.FromSeconds(30);

    private readonly DimmedDisplayStore _store = DimmedDisplayStore.Default;

    // Displays whose restore was interrupted, see DimmingPlan.Restore
    private readonly HashSet<string> _interrupted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = settings.Value.Interval;
#if WINDOWS
        if (controller.DimDisplaysInVr) logger.LogWarning("Dimming displays during VR is not supported on Windows yet");
        await Task.CompletedTask;
#else
        IDisplayBrightnessControl displays = new KdeScreenBrightness();
        using IVrSessionSource sessions = new MonadoSessions(logger);

        // Left dimmed by a previous run (crash, reboot): restored once no game is shown
        var policy = new DimmingPolicy(RestoreDelay, RetryDelay, _store.Exists);
        var enabled = false;
        var available = false;
        var availabilityCheckedAt = DateTime.MinValue;
        var failureLogged = false;
        string? shownGame = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (now - availabilityCheckedAt >= AvailabilityInterval)
                {
                    availabilityCheckedAt = now;
                    available = await displays.IsAvailableAsync(stoppingToken);
                    controller.DimmingSupported = available;
                }

                // Can be switched at runtime through the API; nothing to do where brightness can't be changed
                var active = controller.DimDisplaysInVr && available;
                if (active != enabled)
                {
                    enabled = active;
                    if (enabled) logger.LogInformation("Dimming displays while a VR game is shown (Monado, KDE Plasma)");
                    // Not connected to the VR runtime while turned off
                    else sessions.Disconnect();
                }

                var game = enabled ? VrSessions.FindGame(sessions.GetClients()) : null;
                if (game != shownGame)
                {
                    if (game != null) logger.LogInformation("VR game '{Game}' is shown", game);
                    else logger.LogInformation("VR game '{Game}' ended", shownGame);
                    shownGame = game;
                }

                if (policy.Next(enabled, game != null, now) is { } dim)
                {
                    try
                    {
                        // The game in the status is current already while the fade runs
                        controller.SetDimmingState(policy.Dimmed, game);
                        await (dim ? DimAsync(displays, stoppingToken) : RestoreAsync(displays, stoppingToken));
                        policy.Applied(dim);
                        failureLogged = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Full details once; a lasting problem would otherwise fill the log every retry
                        if (!failureLogged)
                            logger.LogError(ex, "Couldn't {Action} the displays, trying again every {Delay}s",
                                dim ? "dim" : "restore", RetryDelay.TotalSeconds);
                        else
                            logger.LogDebug("Couldn't {Action} the displays again: {Message}", dim ? "dim" : "restore",
                                ex.Message);
                        failureLogged = true;
                        // Saved before changing anything: some displays may be dimmed already and must be restored
                        policy.Failed(now, partlyDimmed: dim && _store.Exists);
                    }
                }

                controller.SetDimmingState(policy.Dimmed, game);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Unlike the base stations, don't leave the displays dark when the service stops; a restarted service
            // dims them again while the game still runs
            if (policy.Dimmed)
            {
                using var timeout = new CancellationTokenSource(ShutdownTimeout);
                try
                {
                    // At once: logout or an update is waiting
                    await RestoreAsync(displays, timeout.Token, fade: false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Couldn't restore the displays on shutdown, restoring on next start");
                }
            }
        }
#endif
    }

    private async Task DimAsync(IDisplayBrightnessControl control, CancellationToken ct)
    {
        var displays = await control.GetDisplaysAsync(ct);
        var (saved, changes) = DimmingPlan.Dim(displays, _store.Load());
        // Saved first: the original brightness must survive a crash right after dimming
        _store.Save(saved);
        // A fade can take up to 10 s: turning dimming off meanwhile stops it where it is
        if (!await TransitionAsync(control, displays, changes, Fade, ct, stop: () => !controller.DimDisplaysInVr))
        {
            // Restored from there right after: halfway would otherwise look changed by the user
            _interrupted.UnionWith(changes.Select(c => c.Name));
            logger.LogInformation("Dimming stopped, it was turned off");
            return;
        }

        logger.LogInformation("Dimmed {Count} display(s)", changes.Count);
    }

    private async Task RestoreAsync(IDisplayBrightnessControl control, CancellationToken ct, bool fade = true)
    {
        var saved = _store.Load();
        var displays = await control.GetDisplaysAsync(ct);
        var changes = DimmingPlan.Restore(displays, saved, _interrupted);
        // Until done: a failed write leaves a display between dimmed and original brightness
        _interrupted.UnionWith(changes.Select(c => c.Name));
        await TransitionAsync(control, displays, changes, fade ? Fade : TimeSpan.Zero, ct);
        _interrupted.Clear();
        _store.Delete();
        if (changes.Count < saved.Count)
            logger.LogInformation("Restored {Count} of {Total} display(s), the others were changed meanwhile or are gone",
                changes.Count, saved.Count);
        else
            logger.LogInformation("Restored {Count} display(s)", changes.Count);
    }

    // Changeable at runtime (tray slider, API)
    private TimeSpan Fade => TimeSpan.FromSeconds(controller.DimFadeSeconds);

    /// <summary>
    ///     Changes the brightness gradually over <paramref name="duration" /> (zero: at once), all displays together.
    /// </summary>
    /// <param name="stop">Checked before every step: true leaves the displays where they are.</param>
    /// <returns>false if <paramref name="stop" /> ended it early.</returns>
    private static async Task<bool> TransitionAsync(IDisplayBrightnessControl control,
        IReadOnlyList<DisplayBrightness> displays, IReadOnlyList<(string Name, int Brightness)> changes,
        TimeSpan duration, CancellationToken ct, Func<bool>? stop = null)
    {
        var from = changes.Select(c => displays.First(d => d.Name == c.Name).Brightness).ToList();
        try
        {
            // By elapsed time, so slow writes (DDC/CI) make the steps coarser, not the transition longer
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < duration)
            {
                if (stop?.Invoke() == true) return false;
                var progress = watch.Elapsed / duration;
                for (var i = 0; i < changes.Count; i++)
                    await control.SetBrightnessAsync(changes[i].Name,
                        DimmingPlan.Interpolate(from[i], changes[i].Brightness, progress), ct);
                await Task.Delay(TransitionStep, ct);
            }
        }
        catch (Exception)
        {
            // Cancelled or a failed step: the end values are set right away below
        }

        // Also then: a display left halfway would look changed by the user and not be restored later
        using var timeout = ct.IsCancellationRequested ? new CancellationTokenSource(ShutdownTimeout) : null;
        foreach (var (name, brightness) in changes)
            await control.SetBrightnessAsync(name, brightness, timeout?.Token ?? ct);
        return true;
    }
}
