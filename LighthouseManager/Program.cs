using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentArgs;
using LighthouseManager.Helper;
using Polly;

namespace LighthouseManager
{
    internal class Program
    {
        public static BluetoothManager BluetoothManager { get; set; } = new();

        private static void Main(string[] args)
        {
            Console.CancelKeyPress += delegate { BluetoothManager.Dispose(); };

            FluentArgsBuilder.New()
                .DefaultConfigsWithAppDescription("An app to manage SteamVR Lighthouse.")
                .RegisterHelpFlag("-h", "--help")
                .Given.Flag("-d", "--discover").Then(() => { BluetoothManager.StartWatcher(); })
                .Given.Flag("-w", "--wake").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace.")
                    .WithDescription("Wakes up base station(s).")
                    .WithExamples(
                        "'-w -a 00:11:22:33:FF:EE' or multiple addresses '-w -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                    .IsRequired()
                    .Call(addresses => { ChangePowerstate(addresses, Powerstate.Wake); }))
                .Given.Flag("-s", "--sleep").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace.")
                    .WithDescription("Sleeps base station(s).")
                    .WithExamples(
                        "'-s -a 00:11:22:33:FF:EE' or multiple addresses '-s -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                    .IsRequired()
                    .Call(addresses => { ChangePowerstate(addresses, Powerstate.Sleep); }))
                .Invalid().Parse(args);

            Console.ReadLine();
        }

        private static async void ChangePowerstate(IReadOnlyList<string> addresses, Powerstate powerstate)
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
                var retryPolicy = Policy
                    .Handle<COMException>()
                    .Or<GattCommunicationException>()
                    .WaitAndRetryAsync(10, t => TimeSpan.FromMilliseconds(500),
                        (ex, t, i, c) => { Console.WriteLine($"{ex.Message}. Failed, retrying {i}/10."); });

                var baseStations = addresses.Select(g => g.ToMacUlong());

                var tasks = baseStations.Select(baseStation =>
                        retryPolicy.ExecuteAndCaptureAsync(() =>
                            BluetoothManager.ChangePowerstate(baseStation, powerstate)))
                    .Cast<Task>().ToList();

                await Task.WhenAll(tasks);
            }

            Environment.Exit(0);
        }
    }
}