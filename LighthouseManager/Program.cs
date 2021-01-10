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
            FluentArgsBuilder.New()
                .DefaultConfigsWithAppDescription("An app to manage SteamVR Lighthouse")
                .RegisterHelpFlag("-h", "--help")
                .Given.Flag("-s", "--scan").Then(() =>
                {
                    BluetoothManager.StartWatcher();
                })
                .Given.Flag("-on").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace")
                    .IsRequired()
                    .Call(addresses =>
                    {
                        var baseStations = addresses.Select(g => g.ToMacUlong());

                        foreach (var baseStation in baseStations)
                            BluetoothManager.ChangePowerstate(baseStation, Powerstate.On);
                    }))
                .Given.Flag("-off").Then(b => b
                    .ListParameter("-a", "--addresses")
                    .WithValidation(n => !string.IsNullOrWhiteSpace(n), "An address must not only contain whitespace")
                    .IsRequired()
                    .Call(addresses =>
                    {
                        var baseStations = addresses.Select(g => g.ToMacUlong());

                        foreach (var baseStation in baseStations)
                            BluetoothManager.ChangePowerstate(baseStation, Powerstate.Off);
                    })).Invalid().Parse(args);

            Console.ReadLine();
        }
    }
}