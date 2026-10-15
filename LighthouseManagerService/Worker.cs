using System.Diagnostics;
using LighthouseManager.Core;
using Microsoft.Extensions.Options;

namespace LighthouseManagerService;

/// <summary>
///     Wakes the base stations while one of the configured trigger processes runs (default: SteamVR's vrserver)
///     and puts them to sleep when all of them exited. Also refreshes the cached power state periodically.
///     The decisions are made by <see cref="AutomationPolicy" />.
/// </summary>
public class Worker(ILogger<Worker> logger, IOptions<AppSettings> settings, StationController controller)
    : BackgroundService
{
    private readonly AutomationPolicy _policy = new(10, TimeSpan.FromSeconds(30));

    private Task<bool> _job = Task.FromResult(true);
    private bool _jobFailureHandled = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var triggers = settings.Value.GetTriggerProcesses();
        var triggerSet = new HashSet<string>(triggers, StringComparer.OrdinalIgnoreCase);
        var addresses = controller.Addresses;
        logger.LogInformation("Interval: {Interval}, BaseStationAddresses: {Addresses}, TriggerProcesses: {Triggers}",
            settings.Value.Interval, string.Join(',', addresses.Select(MacAddress.Format)), string.Join(',', triggers));
        if (addresses.Count == 0)
            logger.LogWarning("No base stations configured. Add them in the tray app (it searches for them) or in {File}.",
                AppPaths.UserSettingsFile);
        logger.LogInformation("Start watching trigger processes.");

        var refreshInterval = TimeSpan.FromSeconds(settings.Value.StateRefreshInterval);
        var refresh = controller.RefreshAsync(null, stoppingToken);
        var lastRefresh = DateTime.UtcNow;

        // Polling instead of Process.Exited: exit events for non-child processes are unreliable on Linux
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var runningTrigger = FindRunningTrigger(triggers, triggerSet);
                controller.SetActiveTrigger(runningTrigger);

                // Faulted counts as failed; reading Result of a faulted task would throw and stop the service
                var jobFailed = _job.IsCompleted && !(_job.IsCompletedSuccessfully && _job.Result);
                if (jobFailed && !_jobFailureHandled && _job.IsFaulted)
                    logger.LogError(_job.Exception, "Automatic wake/sleep crashed");
                var newFailure = jobFailed && !_jobFailureHandled;
                if (newFailure)
                {
                    _jobFailureHandled = true;
                    _policy.ReportFailure(now);
                }

                var next = _policy.Next(runningTrigger != null, controller.AutomaticMode,
                    controller.ManualOverrideVersion, jobFailed, now);

                // A failure caused by a manual command or disabling automatic mode is not worth an error
                if (newFailure && next == null && _policy.WillRetry)
                    logger.LogError("Automatic wake/sleep failed for one or more base stations, trying again in 30s");
                else if (newFailure && next == null && _policy.GaveUp)
                    logger.LogError(
                        "Automatic wake/sleep failed for one or more base stations, giving up until trigger state changes");

                if (next is { } state)
                {
                    if (_policy.Attempts > 1)
                        logger.LogWarning("Retrying '{State}' for base stations (attempt {Attempt}/{Max})", state,
                            _policy.Attempts, _policy.MaxAttempts);
                    else if (state == Powerstate.Wake)
                        logger.LogInformation("'{Process}' detected, waking base stations", runningTrigger);
                    else
                        logger.LogInformation("Trigger processes closed, putting base stations to sleep");

                    // A new automatic job cancels the previous one inside the controller
                    _jobFailureHandled = false;
                    // Current list: base stations can be added through the API at any time
                    _job = controller.SetPowerAsync(controller.Addresses, state, true, stoppingToken);
                }

                if (refreshInterval > TimeSpan.Zero && refresh.IsCompleted && now - lastRefresh >= refreshInterval)
                {
                    refresh = controller.RefreshAsync(null, stoppingToken);
                    lastRefresh = now;
                }

                await Task.Delay(settings.Value.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Operations use stoppingToken, so they are cancelled already; don't leave a Bluetooth connection behind.
            // Base stations are deliberately not put to sleep here: stopping the service during a VR session must
            // not kill tracking.
            try
            {
                await Task.WhenAll(_job, refresh);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Operation failed during shutdown");
            }
        }
    }

    /// <returns>The first configured trigger (in configuration order) that is running, or null.</returns>
    private static string? FindRunningTrigger(IReadOnlyList<string> triggers, HashSet<string> triggerSet)
    {
        // One pass over the process table per poll, independent of the number of triggers
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (triggerSet.Contains(process.ProcessName)) running.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating
                }
            }
        }

        return triggers.FirstOrDefault(running.Contains);
    }
}
