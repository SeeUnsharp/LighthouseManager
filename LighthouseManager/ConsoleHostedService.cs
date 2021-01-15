using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentArgs;
using LighthouseManager.Helper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace LighthouseManager
{
    internal sealed class ConsoleHostedService : IHostedService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IBluetoothManager _bluetoothManager;
        private readonly ILogger _logger;
        private int? _exitCode;

        public ConsoleHostedService(ILogger<ConsoleHostedService> logger, IHostApplicationLifetime appLifetime,
            IBluetoothManager bluetoothManager)
        {
            _logger = logger;
            _appLifetime = appLifetime;
            _bluetoothManager = bluetoothManager;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var args = Environment.GetCommandLineArgs().ToList().Skip(1).ToArray();

            _logger.LogDebug($"Starting with arguments: {string.Join(" ", Environment.GetCommandLineArgs())}");

            _appLifetime.ApplicationStarted.Register(async () =>
            {
                await Task.Run(async () =>
                {
                    await FluentArgsBuilder.New()
                        .DefaultConfigsWithAppDescription("An app to manage SteamVR Lighthouse.")
                        .RegisterHelpFlag("-h", "--help")
                        .Given.Flag("-d", "--discover").Then(() => { _bluetoothManager.StartWatcher(); })
                        .Given.Flag("-w", "--wake").Then(b => b
                            .ListParameter("-a", "--addresses")
                            .WithValidation(n => !string.IsNullOrWhiteSpace(n),
                                "An address must not only contain whitespace.")
                            .WithDescription("Wakes up base station(s).")
                            .WithExamples(
                                "'-w -a 00:11:22:33:FF:EE' or multiple addresses '-w -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                            .IsRequired()
                            .Call(addresses => { ChangePowerstate(addresses, Powerstate.Wake); }))
                        .Given.Flag("-s", "--sleep").Then(b => b
                            .ListParameter("-a", "--addresses")
                            .WithValidation(n => !string.IsNullOrWhiteSpace(n),
                                "An address must not only contain whitespace.")
                            .WithDescription("Sleeps base station(s).")
                            .WithExamples(
                                "'-s -a 00:11:22:33:FF:EE' or multiple addresses '-s -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                            .IsRequired()
                            .Call(addresses => { ChangePowerstate(addresses, Powerstate.Sleep); }))
                        .Invalid().ParseAsync(args);
                }, cancellationToken);
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Exiting with return code: {_exitCode}");

            Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
            return Task.CompletedTask;
        }

        private async void ChangePowerstate(IReadOnlyList<string> addresses, Powerstate powerstate)
        {
            var regex =
                new Regex(
                    "^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}|(?:[0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2}|(?:[0-9a-fA-F]{2}){5}[0-9a-fA-F]{2}$");

            if (addresses.Any(x => !regex.IsMatch(x)))
            {
                Console.WriteLine("One or more addresses invalid.");
            }
            else
            {
                var retryCount = 10;

                var retryPolicy = Policy
                    .Handle<COMException>()
                    .Or<GattCommunicationException>()
                    .Or<InvalidOperationException>()
                    .Or<BluetoothConnectionException>()
                    .WaitAndRetryAsync(retryCount, t => TimeSpan.FromMilliseconds(500),
                        (ex, t, i, c) => { _logger.LogError($"Retrying {i}/{retryCount}."); });

                var baseStations = addresses.Select(g => g.ToMacUlong());

                var tasks = baseStations.Select(baseStation =>
                    retryPolicy.ExecuteAndCaptureAsync(() =>
                        _bluetoothManager.ChangePowerstate(baseStation, powerstate))).ToList();

                var policyResults = await Task.WhenAll(tasks);

                if (policyResults.All(x => x.Outcome == OutcomeType.Successful))
                {
                    _exitCode = 0;
                }
                else
                {
                    _logger.LogError($"One or more tasks failed after {retryCount} retries.");
                    _exitCode = 1;
                }

                _appLifetime.StopApplication();
            }
        }
    }
}