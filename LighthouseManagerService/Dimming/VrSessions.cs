namespace LighthouseManagerService.Dimming;

/// <summary>
///     Client state flags as reported by libmonado (<c>mnd_client_flags</c> in monado.h).
/// </summary>
[Flags]
public enum MonadoClientFlags : uint
{
    PrimaryApp = 1u << 0,
    SessionActive = 1u << 1,
    SessionVisible = 1u << 2,
    SessionFocused = 1u << 3,
    SessionOverlay = 1u << 4,
    IoActive = 1u << 5
}

/// <summary>
///     An OpenXR application connected to the VR runtime.
/// </summary>
public sealed record VrClient(string Name, MonadoClientFlags Flags);

/// <summary>
///     Source of the OpenXR applications connected to the VR runtime.
/// </summary>
public interface IVrSessionSource : IDisposable
{
    /// <returns>The connected applications, or null when no supported runtime is running.</returns>
    IReadOnlyList<VrClient>? GetClients();

    /// <summary>
    ///     Closes the connection to the runtime; the next <see cref="GetClients" /> connects again.
    /// </summary>
    void Disconnect();
}

public static class VrSessions
{
    /// <summary>
    ///     The VR game being shown: a client with a visible session that isn't an overlay (e.g. WlxOverlay-S, which
    ///     also runs without a game). Connected clients without a session, like monitoring tools, don't count.
    /// </summary>
    /// <returns>The game's name, or null.</returns>
    public static string? FindGame(IEnumerable<VrClient>? clients)
    {
        var games = (clients ?? [])
            .Where(c => c.Flags.HasFlag(MonadoClientFlags.SessionVisible) &&
                        !c.Flags.HasFlag(MonadoClientFlags.SessionOverlay))
            .ToList();
        return (games.FirstOrDefault(c => c.Flags.HasFlag(MonadoClientFlags.PrimaryApp)) ?? games.FirstOrDefault())
            ?.Name;
    }
}
