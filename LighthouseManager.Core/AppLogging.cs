using System.Reflection;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace LighthouseManager.Core;

public static class AppLogging
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u4}]<{ThreadId}> [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    ///     Configures the global Serilog logger: console plus a daily rolling file in App_Data/logs next to the
    ///     executable, and logs the startup banner.
    /// </summary>
    public static void Configure(string logFileName)
    {
        // AppContext.BaseDirectory is the executable directory, also for single-file publishes
        // (as long as IncludeAllContentForSelfExtract is not used) and Windows services
        var logFile = Path.Combine(AppContext.BaseDirectory, "App_Data", "logs", logFileName);

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
        Log.Information("Application Starts. Version: {Version}", Assembly.GetEntryAssembly()?.GetName().Version);
        Log.Information("Application Directory: {Directory}", AppContext.BaseDirectory);
    }

    public static async Task CloseAsync()
    {
        Log.Information("====================================================================");
        await Log.CloseAndFlushAsync();
    }
}
