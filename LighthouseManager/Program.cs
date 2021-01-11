using System;
using System.Linq;
using FluentArgs;
using LighthouseManager.Helper;

namespace LighthouseManager
{
    internal class Program
    {
        public static BluetoothManager BluetoothManager { get; set; } = new();

        private static void Main(string[] args)
        {
            Console.CancelKeyPress += delegate
            {
                BluetoothManager.Dispose();
                BluetoothManager.StopWatcher();
            };

            FluentArgsBuilder.New()
                .DefaultConfigsWithAppDescription("An app to manage SteamVR Lighthouse.")
                .RegisterHelpFlag("-h", "--help")
                .Given.Flag("-d", "--discover").Then(() =>
                {
                    BluetoothManager.StartWatcher();
                })
                .Given.Flag("-w", "--wake").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace.")
                    .WithDescription("Wakes up base station(s).")
                    .WithExamples("'-w -a 00:11:22:33:FF:EE' or multiple addresses '-w -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                    .IsRequired()
                    .Call(addresses =>
                    {
                        var baseStations = addresses.Select(g => g.ToMacUlong());

                        foreach (var baseStation in baseStations)
                            BluetoothManager.ChangePowerstate(baseStation, Powerstate.Wake);
                    }))
                .Given.Flag("-s", "--sleep").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace.")
                    .WithDescription("Sleeps base station(s).")
                    .WithExamples("'-s -a 00:11:22:33:FF:EE' or multiple addresses '-s -a 00:11:22:33:FF:EE,00:11:22:33:FF:EF'")
                    .IsRequired()
                    .Call(addresses =>
                    {
                        var baseStations = addresses.Select(g => g.ToMacUlong());

                        foreach (var baseStation in baseStations)
                            BluetoothManager.ChangePowerstate(baseStation, Powerstate.Sleep);
                    })).Invalid().Parse(args);

            Console.ReadLine();
        }
    }
}