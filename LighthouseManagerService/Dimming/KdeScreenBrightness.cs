#if !WINDOWS
using Tmds.DBus;

namespace LighthouseManagerService.Dimming;

/// <summary>
///     Brightness of the desktop displays through KDE Plasma (6.1+, <c>org.kde.ScreenBrightness</c> on the session bus):
///     laptop panels and external monitors with DDC/CI.
/// </summary>
public sealed class KdeScreenBrightness : IDisplayBrightnessControl
{
    private const string Service = "org.kde.ScreenBrightness";
    private const string RootPath = "/org/kde/ScreenBrightness";

    /// <summary>
    ///     <c>SuppressIndicator</c>: no brightness popup on the screen.
    /// </summary>
    private const uint SuppressIndicator = 0x1;

    /// <summary>
    ///     Only KDE Plasma (powerdevil) provides the service; other desktops, or Plasma before 6.1, don't.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            return await Connection.Session.IsServiceActiveAsync(Service).WaitAsync(ct);
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or InvalidOperationException)
        {
            // No session bus (e.g. not started from a desktop session)
            return false;
        }
    }

    public async Task<IReadOnlyList<DisplayBrightness>> GetDisplaysAsync(CancellationToken ct)
    {
        var root = Connection.Session.CreateProxy<IScreenBrightness>(Service, RootPath);
        var names = await root.GetAsync<string[]>("DisplaysDBusNames").WaitAsync(ct);
        var displays = new List<DisplayBrightness>();
        foreach (var name in names)
        {
            var display = Display(name);
            displays.Add(new DisplayBrightness(name,
                await display.GetAsync<string>("Label").WaitAsync(ct),
                await display.GetAsync<int>("Brightness").WaitAsync(ct),
                await display.GetAsync<int>("MaxBrightness").WaitAsync(ct)));
        }

        return displays;
    }

    public Task SetBrightnessAsync(string name, int brightness, CancellationToken ct)
    {
        return Display(name).SetBrightnessAsync(brightness, SuppressIndicator).WaitAsync(ct);
    }

    private static IScreenBrightnessDisplay Display(string name)
    {
        return Connection.Session.CreateProxy<IScreenBrightnessDisplay>(Service, $"{RootPath}/{name}");
    }
}

[DBusInterface("org.kde.ScreenBrightness")]
public interface IScreenBrightness : IDBusObject
{
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.kde.ScreenBrightness.Display")]
public interface IScreenBrightnessDisplay : IDBusObject
{
    Task SetBrightnessAsync(int brightness, uint flags);
    Task<T> GetAsync<T>(string prop);
}
#endif
