using LighthouseManager.Core;

namespace LighthouseManagerService;

/// <summary>
///     Follows base stations that report "booting" (raw 0x01/0x08): they are read again shortly, and one that doesn't
///     finish starting up within <see cref="StuckAfter" /> is reported as stuck. Pure logic, see
///     <see cref="StationController" /> for the reads.
/// </summary>
/// <remarks>
///     A normal wake goes 0x08 → 0x09 → 0x0B within ~10 s. A station was observed staying at 0x01 for hours; without
///     this it looked like a normal start in the UI until the next periodic read.
/// </remarks>
public sealed class BootWatch
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<ulong, DateTime> _bootingSince = [];

    public enum Verdict
    {
        /// <summary>Not booting (anymore).</summary>
        Settled,

        /// <summary>Booting: read again after <see cref="CheckInterval" />.</summary>
        CheckAgain,

        /// <summary>Booting for longer than <see cref="StuckAfter" />.</summary>
        Stuck
    }

    /// <summary>
    ///     A wake command succeeded: the station is expected to boot from now on.
    /// </summary>
    public void Woken(ulong address, DateTime now)
    {
        lock (_bootingSince) _bootingSince[address] = now;
    }

    /// <summary>
    ///     A state was read (or set by a command other than wake).
    /// </summary>
    public Verdict Observe(ulong address, StationState state, DateTime now)
    {
        lock (_bootingSince)
        {
            if (state != StationState.Booting)
            {
                _bootingSince.Remove(address);
                return Verdict.Settled;
            }

            if (!_bootingSince.TryGetValue(address, out var since))
                _bootingSince[address] = since = now;

            return now - since >= StuckAfter ? Verdict.Stuck : Verdict.CheckAgain;
        }
    }

    public void Forget(ulong address)
    {
        lock (_bootingSince) _bootingSince.Remove(address);
    }
}
