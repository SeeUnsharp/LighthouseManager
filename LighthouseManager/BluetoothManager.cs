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
using Microsoft.Extensions.Logging;
using Powerstate = LighthouseManager.Helper.Powerstate;

namespace LighthouseManager
{
    /// <summary>
    ///     Managing all Bluetooth related things
    /// </summary>
    public class BluetoothManager : IBluetoothManager
    {
        private readonly ILogger<BluetoothManager> _logger;
        private readonly string _pwrService = "00001523-1212-EFDE-1523-785FEABCD124";

        public BluetoothManager(ILogger<BluetoothManager> logger)
        {
            _logger = logger;
        }

        private BluetoothLEAdvertisementWatcher AdvertisementWatcher { get; set; }
        private List<BluetoothLEDevice> BluetoothLeDevices { get; } = new();
        private List<ulong> Basestations { get; } = new();

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
                _logger.LogError(ex.Message);
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

        /// <summary>
        ///     Changing the power state of given basestation addresses
        /// </summary>
        /// <param name="address">Bluetooth-Addresse</param>
        /// <param name="powerState">Selected power state</param>
        public async Task ChangePowerstate(ulong address, Powerstate powerState)
        {
            BluetoothLEDevice device = null;
            var macAddress = address.ToMacString();

            try
            {
                device = await Connect(address);

                if (BluetoothLeDevices.All(x => x.BluetoothAddress != device.BluetoothAddress))
                    BluetoothLeDevices.Add(device);


                var service = await device.GetGattServicesForUuidAsync(Guid.Parse(_pwrService));
                var powerstate = new Models.Characteristics.Powerstate();
                var characteristicResult =
                    await service.Services.Single().GetCharacteristicsForUuidAsync(powerstate.GetGuid());
                var characteristic = characteristicResult.Characteristics.Single();

                // Reading current state and break writing if state is already set
                var currentState = await ReadAsync(characteristic);
                GattWriteResult writeResult = null;
                switch (powerState)
                {
                    case Powerstate.Wake:
                        if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.AwakeLastSleeping ||
                            currentState.FirstOrDefault() == powerstate.PowerstateReadValues.AwakeLastStandby) break;
                        writeResult = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Wake);
                        break;
                    case Powerstate.Sleep:
                        if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.Sleeping) break;
                        writeResult = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Sleep);
                        break;
                    case Powerstate.Standby:
                        if (currentState.FirstOrDefault() == powerstate.PowerstateReadValues.Standby) break;
                        writeResult = await WriteAsync(characteristic, powerstate.PowerstateWriteValues.Standby);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(powerState), powerState, null);
                }

                if (writeResult == null)
                {
                    _logger.LogInformation(
                        $"{macAddress}: State already {powerState}.");
                }
                else if (writeResult.Status == GattCommunicationStatus.Success)
                {
                    _logger.LogInformation(
                        $"{address.ToMacString()}: Successfully executed '{powerState}' command.");
                }
                else
                {
                    var ex = new GattCommunicationException(
                        $"{macAddress}: WriteValueWithResultAsyncError",
                        writeResult.Status);
                    _logger.LogError(ex.Message);
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"{address.ToMacString()}: {ex.Message}");
                throw;
            }
            finally
            {
                device?.Dispose();
            }
        }

        public async Task Identify(ulong address)
        {
            BluetoothLEDevice device = null;
            var macAddress = address.ToMacString();

            try
            {
                device = await Connect(address);
                var service = await device.GetGattServicesForUuidAsync(Guid.Parse(_pwrService));
                var identify = new Identify();
                var characteristicResult =
                    await service.Services.Single().GetCharacteristicsForUuidAsync(identify.GetGuid());
                var characteristic = characteristicResult.Characteristics.Single();
                var writeResult = await WriteAsync(characteristic, identify.Identifing);

                if (writeResult.Status == GattCommunicationStatus.Success)
                {
                    _logger.LogInformation(
                        $"{address.ToMacString()}: Successfully executed 'Identify' command.");
                }
                else
                {
                    var ex = new GattCommunicationException(
                        $"{macAddress}: WriteValueWithResultAsyncError",
                        writeResult.Status);
                    _logger.LogError(ex.Message);
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"{macAddress}: {ex.Message}");
                throw;
            }
            finally
            {
                device?.Dispose();
            }
        }

        /// <summary>
        ///     Connects to a device and returns it if success
        /// </summary>
        /// <param name="address">Device address</param>
        /// <returns>Connected device</returns>
        private async Task<BluetoothLEDevice> Connect(ulong address)
        {
            var macAddress = address.ToMacString();

            _logger.LogInformation($"{macAddress}: Connecting to device.");

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                _logger.LogError($"{macAddress}: Failed to connect to device.");
                throw new BluetoothConnectionException("Failed to connect to device.");
            }

            return device;
        }

        private void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // Filtering for basestations, should begin with "LHB-"
            if (!args.Advertisement.LocalName.StartsWith("LHB-")) return;

            if (Basestations.All(x => x != args.BluetoothAddress))
            {
                Basestations.Add(args.BluetoothAddress);

                _logger.LogInformation(
                    $"Potential Base Station found. Name: {args.Advertisement.LocalName}, Bluetooth Address: {args.BluetoothAddress.ToMacString()}.");
            }
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