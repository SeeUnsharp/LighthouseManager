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
    private static readonly TimeSpan Transition = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan TransitionStep = TimeSpan.FromMilliseconds(50);

    private readonly DimmedDisplayStore _store = DimmedDisplayStore.Default;

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
        string? shownGame = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Can be switched at runtime through the API
                if (controller.DimDisplaysInVr != enabled)
                {
                    enabled = !enabled;
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

                var now = DateTime.UtcNow;
                if (policy.Next(enabled, game != null, now) is { } dim)
                {
                    try
                    {
                        await (dim ? DimAsync(displays, stoppingToken) : RestoreAsync(displays, stoppingToken));
                        policy.Applied(dim);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Couldn't {Action} the displays, trying again in {Delay}s",
                            dim ? "dim" : "restore", RetryDelay.TotalSeconds);
                        policy.Failed(now);
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
                    await RestoreAsync(displays, timeout.Token);
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
        await TransitionAsync(control, displays, changes, ct);
        logger.LogInformation("Dimmed {Count} display(s)", changes.Count);
    }

    private async Task RestoreAsync(IDisplayBrightnessControl control, CancellationToken ct)
    {
        var saved = _store.Load();
        var displays = await control.GetDisplaysAsync(ct);
        var changes = DimmingPlan.Restore(displays, saved);
        await TransitionAsync(control, displays, changes, ct);
        _store.Delete();
        if (changes.Count < saved.Count)
            logger.LogInformation("Restored {Count} of {Total} display(s), the others were changed meanwhile or are gone",
                changes.Count, saved.Count);
        else
            logger.LogInformation("Restored {Count} display(s)", changes.Count);
    }

    /// <summary>
    ///     Changes the brightness gradually over <see cref="Transition" />, all displays together.
    /// </summary>
    private static async Task TransitionAsync(IDisplayBrightnessControl control,
        IReadOnlyList<DisplayBrightness> displays, IReadOnlyList<(string Name, int Brightness)> changes,
        CancellationToken ct)
    {
        var from = changes.Select(c => displays.First(d => d.Name == c.Name).Brightness).ToList();
        try
        {
            // By elapsed time, so slow writes (DDC/CI) make the steps coarser, not the transition longer
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < Transition)
            {
                var progress = watch.Elapsed / Transition;
                for (var i = 0; i < changes.Count; i++)
                    await control.SetBrightnessAsync(changes[i].Name,
                        DimmingPlan.Interpolate(from[i], changes[i].Brightness, progress), ct);
                await Task.Delay(TransitionStep, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Finished right away below
        }

        // Also when cancelled: a display left halfway would look changed by the user and not be restored later
        using var timeout = ct.IsCancellationRequested ? new CancellationTokenSource(ShutdownTimeout) : null;
        foreach (var (name, brightness) in changes)
            await control.SetBrightnessAsync(name, brightness, timeout?.Token ?? ct);
    }
}
