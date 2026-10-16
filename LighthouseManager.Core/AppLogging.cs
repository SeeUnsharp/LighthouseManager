using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace LighthouseManager.Core;

public static class AppLogging
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u4}]<{ThreadId}> [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    ///     Configures the global Serilog logger: console plus a daily rolling file in <see cref="AppPaths.LogDirectory" />,
    ///     and logs the startup banner.
    /// </summary>
    public static void Configure(string logFileName)
    {
        var logFile = Path.Combine(AppPaths.LogDirectory, logFileName);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithThreadId()
            .Enrich.FromLogContext()
            .WriteTo.Console(LogEventLevel.Information, Template, theme: AnsiConsoleTheme.Literate)
            .WriteTo.File(logFile, LogEventLevel.Information, Template,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 90)
            .CreateLogger();

        Log.Information("====================================================================");
        Log.Information("Application Starts. Version: {Version}", AppPaths.Version);
        // AppContext.BaseDirectory is the executable directory, also for single-file publishes
        // (as long as IncludeAllContentForSelfExtract is not used) and Windows services
        Log.Information("Application Directory: {Directory}", AppContext.BaseDirectory);
        Log.Information("Settings: {Settings}, Logs: {Logs}", AppPaths.UserSettingsFile, AppPaths.LogDirectory);
    }

    public static async Task CloseAsync()
    {
        Log.Information("====================================================================");
        await Log.CloseAndFlushAsync();
    }
}
