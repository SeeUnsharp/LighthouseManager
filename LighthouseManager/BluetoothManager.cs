using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using LighthouseManager.Helper;

namespace LighthouseManager
{
    /// <summary>
    ///     Managing all Bluetooth related things
    /// </summary>
    public class BluetoothManager
    {
        private readonly string _pwrService = "00001523-1212-efde-1523-785feabcd124";
        private readonly string _pwrCharacteristic = "00001525-1212-efde-1523-785feabcd124";
        private const int PwrOn = 0x01;
        private const int PwrOff = 0x00;
        private readonly int _eDeviceNotAvailable = unchecked((int) 0x800710df);
        private BluetoothLEAdvertisementWatcher AdvertisementWatcher { get; set; }

        public static List<ulong> Basestations { get; set; } = new();

        /// <summary>
        ///     Initializing new BluetoothLEAdvertisementWatcher and listening for devices
        /// </summary>
        public void StartWatcher()
        {
            AdvertisementWatcher = new BluetoothLEAdvertisementWatcher {ScanningMode = BluetoothLEScanningMode.Active};
            AdvertisementWatcher.Received += WatcherOnReceived;

            try
            {
                AdvertisementWatcher.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        ///     Stop listening for devices and clearing collected basestations
        /// </summary>
        public void StopWatcher()
        {
            AdvertisementWatcher.Stop();
            Basestations.Clear();
        }

        private static void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // Filtering for basestations, should begin with "LHB-"
            if (!args.Advertisement.LocalName.StartsWith("LHB-")) return;

            if (Basestations.All(x => x != args.BluetoothAddress))
            {
                Basestations.Add(args.BluetoothAddress);

                Console.WriteLine(
                    $"Potential Base Station found. Name: {args.Advertisement.LocalName}, Bluetooth Address: {args.BluetoothAddress.ToMacString()}.");
            }
        }

        /// <summary>
        ///     Changing the power state of given basestation addresses
        /// </summary>
        /// <param name="address">Bluetooth-Addresse</param>
        /// <param name="powerState">Selected power state</param>
        public async void ChangePowerstate(ulong address, Powerstate powerState)
        {
            BluetoothLEDevice device = null;

            try
            {
                Console.WriteLine($"{address.ToMacString()}: Connecting to device.");
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

                if (device == null) Console.WriteLine($"{address.ToMacString()}: Failed to connect to device.");
            }
            catch (Exception ex) when (ex.HResult == _eDeviceNotAvailable)
            {
                Console.WriteLine("Bluetooth radio is not on.");
            }

            if (device != null)
            {
                Console.WriteLine($"{address.ToMacString()}: Trying to get Gatt services and characteristics.");
                var gattServiceResult = await device.GetGattServicesAsync();

                if (gattServiceResult.Status == GattCommunicationStatus.Success)
                {
                    var characteristicsResult = await gattServiceResult.Services
                        .Single(s => s.Uuid == Guid.Parse(_pwrService))
                        .GetCharacteristicsAsync();

                    if (characteristicsResult.Status == GattCommunicationStatus.Success)
                    {
                        var characteristic =
                            characteristicsResult.Characteristics.Single(c =>
                                c.Uuid == Guid.Parse(_pwrCharacteristic));
                        try
                        {
                            var writer = new DataWriter();
                            writer.WriteByte(powerState == Powerstate.On ? PwrOn : PwrOff);
                            var result = await characteristic.WriteValueWithResultAsync(writer.DetachBuffer());

                            Console.WriteLine(
                                result.Status == GattCommunicationStatus.Success
                                    ? $"{address.ToMacString()}: Successfully executed '{powerState}' command."
                                    : $"{address.ToMacString()}: Execution failed: {result.Status}.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{address.ToMacString()}: {ex.Message}");
                        }
                    }
                }
            }

            device?.Dispose();
        }
    }
}