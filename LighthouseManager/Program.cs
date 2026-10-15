using FluentArgs;
using LighthouseManager.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LighthouseManager;

internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;

    private static async Task<int> Main(string[] args)
    {
        AppLogging.Configure("LighthouseManager.log");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
            var manager = new BaseStationManager(loggerFactory.CreateLogger<BaseStationManager>());
            return await RunAsync(args, manager, cts.Token);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Application terminated unexpectedly");
            return Failure;
        }
        finally
        {
            await AppLogging.CloseAsync();
        }
    }

    private static async Task<int> RunAsync(string[] args, BaseStationManager manager, CancellationToken ct)
    {
        Func<Task<int>>? command = null;

        var parsed = await FluentArgsBuilder.New()
            .DefaultConfigsWithAppDescription("An app to manage Valve Lighthouse 2.0 base stations.")
            .RegisterHelpFlag("-h", "--help")
            .Given.Flag("-d", "--discover").Then(() =>
                command = async () =>
                {
                    await manager.DiscoverAsync(ct);
                    return Success;
                })
            .Given.Flag("-i", "--identify").Then(b => b
                .Parameter("-a", "--address")
                .WithDescription("Identifies a base station.")
                .WithExamples("'-i -a 00:11:22:33:FF:EE'")
                .IsRequired()
                .Call(address => command = () => WithAddresses([address],
                    a => manager.IdentifyAsync(a.Single(), ct))))
            .Given.Flag("-r", "--read").Then(b => b
                .ListParameter("-a", "--addresses")
                .WithDescription("Reads the power state of base station(s).")
                .WithExamples("'-r -a 00:11:22:33:FF:EE' or multiple addresses '-r -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                .IsRequired()
                .Call(addresses => command = () => WithAddresses(addresses, a => ReadAsync(manager, a, ct))))
            .Given.Flag("-w", "--wake").Then(b => b
                .ListParameter("-a", "--addresses")
                .WithDescription("Wakes up base station(s).")
                .WithExamples(
                    "'-w -a 00:11:22:33:FF:EE' or multiple addresses '-w -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                .IsRequired()
                .Call(addresses => command = () => WithAddresses(addresses,
                    a => manager.ChangePowerstateAsync(a, Powerstate.Wake, ct))))
            .Given.Flag("-s", "--sleep").Then(b => b
                .ListParameter("-a", "--addresses")
                .WithDescription("Sleeps base station(s).")
                .WithExamples(
                    "'-s -a 00:11:22:33:FF:EE' or multiple addresses '-s -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                .IsRequired()
                .Call(addresses => command = () => WithAddresses(addresses,
                    a => manager.ChangePowerstateAsync(a, Powerstate.Sleep, ct))))
            .Invalid()
            .ParseAsync(args);

        // Help output or invalid arguments
        if (!parsed || command == null) return Failure;

        // Two processes connecting to the same base stations fail
        if (SingleInstance.IsHeld("service"))
        {
            Log.Error("LighthouseManagerService is running and controls the base stations. Use the tray app or its " +
                      "API instead, e.g. curl -X POST http://127.0.0.1:47800/api/stations/wake");
            return Failure;
        }

        return await command();
    }

    private static async Task<bool> ReadAsync(BaseStationManager manager, IReadOnlyList<ulong> addresses,
        CancellationToken ct)
    {
        var readings = await Task.WhenAll(addresses.Select(a => manager.ReadStateAsync(a, ct)));
        for (var i = 0; i < addresses.Count; i++)
        {
            var reading = readings[i];
            if (reading == null) continue;
            Log.Information("{Address}: {State} (raw 0x{Raw:X2}), Name: {Name}", MacAddress.Format(addresses[i]),
                reading.State, reading.RawValue, reading.Name);
        }

        return readings.All(r => r != null);
    }

    private static async Task<int> WithAddresses(IEnumerable<string> input, Func<IReadOnlyList<ulong>, Task<bool>> action)
    {
        if (!MacAddress.TryParseList(input, out var addresses, out var invalid))
        {
            Log.Error("Invalid address: '{Address}'.", invalid);
            return Failure;
        }

        return await action(addresses) ? Success : Failure;
    }
}
