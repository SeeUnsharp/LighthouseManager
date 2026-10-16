using Avalonia;
using Avalonia.Controls;
using LighthouseManager.Tray.Integration;
using Velopack;

namespace LighthouseManager.Tray;

internal static class Program
{
    private const string DefaultApiUrl = "http://127.0.0.1:47800";

    /// <summary>
    ///     Service API address: "--api-url <url>" argument, LIGHTHOUSEMANAGER_API_URL environment variable, or the
    ///     service's default.
    /// </summary>
    public static Uri ApiUrl { get; private set; } = new(DefaultApiUrl);

    /// <summary>
    ///     The single instance lock of this tray, used by the app to listen for "show the flyout" requests.
    /// </summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles the installer hooks and exits for them
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
            velopack = velopack
                .OnAfterInstallFastCallback(_ => SystemIntegration.Install())
                .OnAfterUpdateFastCallback(_ => SystemIntegration.Install(update: true))
                // The service runs from the app folder, its files must not be in use while they are replaced
                .OnBeforeUpdateFastCallback(_ => SystemIntegration.StopService())
                .OnBeforeUninstallFastCallback(_ => SystemIntegration.Uninstall());
        // Linux has no install/update hooks: the AppImage was replaced, restart the service from the new one
        velopack.OnRestarted(_ =>
        {
            if (OperatingSystem.IsLinux()) SystemIntegration.RestartService();
        }).Run();

        if (SystemIntegration.TryForward(args) is { } exitCode) return exitCode;

        // Same as "Remove from system…" in the tray menu (Linux; Windows uninstalls through "Installed apps")
        if (args.Contains("--uninstall"))
        {
            SystemIntegration.Uninstall();
            return 0;
        }

        // One tray per user: a second start shows the running tray's flyout instead
        using var instance = SingleInstance.TryAcquire();
        if (instance == null)
        {
            SingleInstance.ActivateRunningInstance();
            return 0;
        }

        Instance = instance;

        // AppImage: (re)create autostart, menu entry and systemd unit, e.g. after the file was moved
        if (OperatingSystem.IsLinux() && SystemIntegration.IsManaged) SystemIntegration.Install();

        var index = Array.IndexOf(args, "--api-url");
        var url = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Environment.GetEnvironmentVariable("LIGHTHOUSEMANAGER_API_URL") ?? DefaultApiUrl;
        ApiUrl = new Uri(url.EndsWith('/') ? url : url + "/");

        // The app lives in the tray, closing the flyout must not end it
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the XAML designer
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
