using Avalonia;
using Avalonia.Controls;

namespace LighthouseManager.Tray;

internal static class Program
{
    private const string DefaultApiUrl = "http://127.0.0.1:47800";

    /// <summary>
    ///     Service API address: "--api-url <url>" argument, LIGHTHOUSEMANAGER_API_URL environment variable, or the
    ///     service's default.
    /// </summary>
    public static Uri ApiUrl { get; private set; } = new(DefaultApiUrl);

    [STAThread]
    public static int Main(string[] args)
    {
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
