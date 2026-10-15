using Microsoft.AspNetCore.Hosting.Server;
using LighthouseManager.Core;
using LighthouseManagerService;
using Serilog;

AppLogging.Configure("LighthouseManagerService.log");

try
{
    // Content root next to the executable so appsettings.json is found when started as Windows service / systemd unit
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    // Both are no-ops unless running as a Windows service or under systemd
    builder.Services.AddWindowsService(o => o.ServiceName = "LighthouseManager");
    builder.Services.AddSystemd();

    builder.Services.AddSerilog();
    // Invalid settings fail the start (non-zero exit) instead of a running service that does nothing
    builder.Services.AddOptions<AppSettings>()
        .Bind(builder.Configuration.GetSection("AppSettings"))
        .Validate(s => s.Interval >= 1000, "AppSettings:Interval lower than 1000 milliseconds is not allowed.")
        .Validate(s => s.TryGetAddresses(out var addresses) && addresses.Count > 0,
            "AppSettings:BaseStationAddresses must contain at least one valid address (comma separated).")
        .Validate(s => s.GetTriggerProcesses().Count > 0,
            "AppSettings:TriggerProcesses must contain at least one process name (comma separated).")
        .Validate(s => s.StateRefreshInterval >= 0, "AppSettings:StateRefreshInterval must not be negative.")
        .ValidateOnStart();
    builder.Services.AddSingleton<BaseStationManager>();
    builder.Services.AddSingleton<StationController>();
    builder.Services.AddHostedService<Worker>();

    // The API settings are needed before the host is built, so they are read directly
    var settings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
    var apiEnabled = !string.IsNullOrWhiteSpace(settings.ApiUrl);
    if (apiEnabled)
    {
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException($"AppSettings:ApiUrl '{settings.ApiUrl}' is not a valid http URL.");
        builder.WebHost.UseUrls(settings.ApiUrl);
    }
    else
    {
        // Kestrel would fall back to localhost:5000 without URLs, so replace the server to not listen at all
        builder.Services.AddSingleton<IServer, NoListenerServer>();
    }

    // Same contract everywhere: the source generated context carries naming and enum settings
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
        o.SerializerOptions.Converters.Add(new CamelCaseEnumConverter<StationState>());
    });

    var allowedOrigins = settings.GetAllowedOrigins();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(allowedOrigins.ToArray()).AllowAnyHeader().AllowAnyMethod()));

    var app = builder.Build();
    app.UseLocalRequestGuard(allowedOrigins);
    app.UseCors();
    app.MapApi();

    if (apiEnabled) Log.Information("API listening on {Url}", settings.ApiUrl);
    await app.RunAsync();
}
catch (Exception e)
{
    Log.Fatal(e, "Application terminated unexpectedly");
    // Non-zero so systemd Restart=on-failure / Windows service recovery can kick in
    Environment.ExitCode = 1;
}
finally
{
    await AppLogging.CloseAsync();
}
