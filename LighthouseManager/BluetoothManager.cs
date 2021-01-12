using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using LighthouseManager.Helper;
using LighthouseManager.Models.Characteristics;
using Powerstate = LighthouseManager.Helper.Powerstate;

namespace LighthouseManager
{
    /// <summary>
    ///     Managing all Bluetooth related things
    /// </summary>
    public class BluetoothManager : IDisposable
    {
        private readonly string _pwrService = "00001523-1212-EFDE-1523-785FEABCD124";
        private BluetoothLEAdvertisementWatcher AdvertisementWatcher { get; set; }
        private List<BluetoothLEDevice> BluetoothLeDevices { get; } = new();
        private List<ulong> Basestations { get; } = new();

        public void Dispose()
        {
            AdvertisementWatcher?.Stop();
            BluetoothLeDevices.ForEach(x => x.Dispose());
        }

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
            if (AdvertisementWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                AdvertisementWatcher.Stop();

            Basestations.Clear();
        }

        private void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender,
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
        public async Task ChangePowerstate(ulong address, Powerstate powerState)
        {
            Console.WriteLine($"{address.ToMacString()}: Connecting to device.");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

            if (device == null) Console.WriteLine($"{address.ToMacString()}: Failed to connect to device.");

            if (device != null)
            {
                if (BluetoothLeDevices.All(x => x.BluetoothAddress != device.BluetoothAddress))
                    BluetoothLeDevices.Add(device);

                Console.WriteLine($"{address.ToMacString()}: Trying to get Gatt services and characteristics.");
                var gattServiceResult = await device.GetGattServicesAsync();

                if (gattServiceResult.Status == GattCommunicationStatus.Success)
                {
                    var characteristicsResult = await gattServiceResult.Services
                        .Single(s => s.Uuid == Guid.Parse(_pwrService))
                        .GetCharacteristicsAsync();

                    if (characteristicsResult.Status == GattCommunicationStatus.Success)
                    {
                        var powerstate = new Models.Characteristics.Powerstate();

                        var characteristic =
                            characteristicsResult.Characteristics.Single(c =>
                                c.Uuid == powerstate.GetGuid());

                        // Reading current state and break writing if state is already set
                        var currentState = await ReadAsync(characteristic);
                        GattWriteResult result = null;
                        switch (powerState)
                        {
                            case Powerstate.Wake:
                                if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.AwakeLastSleeping ||
                                    currentState.FirstOrDefault() == powerstate.PowerstateReadValues.AwakeLastStandby) break;
                                result = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Wake);
                                break;
                            case Powerstate.Sleep:
                                if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.Sleeping) break;
                                result = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Sleep);
                                break;
                            case Powerstate.Standby:
                                if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.Standby) break;
                                result = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Standby);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(powerState), powerState, null);
                        }

                        if (result == null)
                        {
                            Console.WriteLine(
                                $"{address.ToMacString()}: State already {powerState}.");
                        }
                        else if (result.Status == GattCommunicationStatus.Success)
                        {
                            Console.WriteLine(
                                $"{address.ToMacString()}: Successfully executed '{powerState}' command.");
                        }
                        else
                        {
                            device.Dispose();
                            throw new GattCommunicationException(
                                $"{address.ToMacString()}: WriteValueWithResultAsyncError",
                                gattServiceResult.Status);
                        }
                    }
                    else
                    {
                        device.Dispose();
                        throw new GattCommunicationException($"{address.ToMacString()}: GetCharacteristicsAsyncError",
                            gattServiceResult.Status);
                    }
                }
                else
                {
                    device.Dispose();
                    throw new GattCommunicationException($"{address.ToMacString()}: GetGattServicesAsyncError",
                        gattServiceResult.Status);
                }
            }

            device?.Dispose();
        }

        private async Task<GattWriteResult> WriteAsync(GattCharacteristic characteristic, byte value)
        {
            var writer = new DataWriter();
            writer.WriteByte(value);
            return await characteristic.WriteValueWithResultAsync(writer.DetachBuffer());
        }

        private async Task<byte[]> ReadAsync(GattCharacteristic characteristic)
        {
            var result = await characteristic.ReadValueAsync();
            if (result.Status == GattCommunicationStatus.Success)
            {
                var reader = DataReader.FromBuffer(result.Value);
                var input = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(input);
                return input;
            }

            return null;
        }
    }
}