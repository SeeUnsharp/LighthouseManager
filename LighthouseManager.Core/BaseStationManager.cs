using InTheHand.Bluetooth;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace LighthouseManager.Core;

/// <summary>
///     Discovers and controls Lighthouse 2.0 base stations over Bluetooth LE (WinRT on Windows, BlueZ on Linux).
/// </summary>
public sealed class BaseStationManager(ILogger<BaseStationManager> logger)
{
    private const int Retries = 10;

    // Per attempt: BLE calls can hang (e.g. BlueZ never completing a connect), and callers may pass no token
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    // BlueZ has one discovery session per adapter, parallel scans would cancel each other
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private readonly ResiliencePipeline _retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            MaxRetryAttempts = Retries,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Constant,
            OnRetry = args =>
            {
                logger.LogWarning("Retrying {Attempt}/{Retries}: {Message}", args.AttemptNumber + 1, Retries,
                    args.Outcome.Exception?.Message);
                return default;
            }
        })
        .AddTimeout(AttemptTimeout)
        .Build();

    /// <summary>
    ///     Scans until cancelled and logs every base station found.
    /// </summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await Bluetooth.GetAvailabilityAsync().WaitAsync(cancellationToken))
            logger.LogWarning("No Bluetooth adapter available or Bluetooth is turned off.");

        logger.LogInformation("Scanning for base stations. Press Ctrl+C to stop.");
        var found = new HashSet<ulong>();

        try
        {
            await PlatformDiscoverAsync((name, address) =>
            {
                if (name?.StartsWith(Lighthouse.NamePrefix, StringComparison.Ordinal) != true) return;

                lock (found)
                {
                    if (!found.Add(address)) return;
                }

                logger.LogInformation("Potential Base Station found. Name: {Name}, Bluetooth Address: {Address}.",
                    name, MacAddress.Format(address));
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     Scans for <paramref name="duration" /> and returns the base stations found (name prefix "LHB-").
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredStation>> FindBaseStationsAsync(TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, string>();
        void OnDevice(string? name, ulong address)
        {
            if (name?.StartsWith(Lighthouse.NamePrefix, StringComparison.Ordinal) != true) return;
            lock (found) found[address] = name;
        }

#if WINDOWS
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        try
        {
            await PlatformDiscoverAsync(OnDevice, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Scan time is over
        }
#else
        // One BlueZ discovery for the whole duration; cancelling it midway would drop the partial result
        foreach (var device in await ScanAsync(duration, cancellationToken))
            if (MacAddress.TryParse(device.Id, out var address))
                OnDevice(device.Name, address);
#endif

        lock (found)
        {
            logger.LogInformation("Discovery found {Count} base station(s).", found.Count);
            return found.Select(f => new DiscoveredStation(f.Key, f.Value)).OrderBy(s => s.Name).ToList();
        }
    }

#if WINDOWS
    // InTheHand's ScanForDevicesAsync only lists devices Windows already knows (DeviceInformation.FindAllAsync),
    // so use an active advertisement watcher like the original implementation (the name is in the scan response).
    private static async Task PlatformDiscoverAsync(Action<string?, ulong> onDevice,
        CancellationToken cancellationToken)
    {
        var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active
        };
        watcher.Received += (_, args) => onDevice(args.Advertisement.LocalName, args.BluetoothAddress);
        watcher.Start();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            watcher.Stop();
        }
    }
#else
    private async Task PlatformDiscoverAsync(Action<string?, ulong> onDevice, CancellationToken cancellationToken)
    {
        while (true)
        {
            var started = DateTime.UtcNow;
            var devices = await ScanAsync(TimeSpan.FromSeconds(10), cancellationToken);

            foreach (var device in devices)
                if (MacAddress.TryParse(device.Id, out var address))
                    onDevice(device.Name, address);

            // A scan that returns immediately means the adapter is not scanning, don't spin
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(1))
            {
                logger.LogWarning("Scan returned immediately, is Bluetooth turned on? Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }
#endif

    /// <summary>
    ///     Changes the power state of all given base stations in parallel.
    /// </summary>
    /// <returns>true if every base station succeeded (within the retry budget).</returns>
    public Task<bool> ChangePowerstateAsync(IEnumerable<ulong> addresses, Powerstate state,
        CancellationToken cancellationToken = default)
    {
        return RunAllAsync(addresses, (a, token) => ChangePowerstateOnceAsync(a, state, token), cancellationToken);
    }

    /// <summary>
    ///     Flashes the LED of a base station.
    /// </summary>
    /// <returns>true on success (within the retry budget).</returns>
    public Task<bool> IdentifyAsync(ulong address, CancellationToken cancellationToken = default)
    {
        return RunAllAsync([address], IdentifyOnceAsync, cancellationToken);
    }

    /// <summary>
    ///     Reads power state and name of a base station without changing it.
    /// </summary>
    /// <returns>null if the base station could not be read (within the retry budget).</returns>
    public async Task<StationReading?> ReadStateAsync(ulong address, CancellationToken cancellationToken = default)
    {
        var result = await RunWithRetryAsync(address, token => ReadStateOnceAsync(address, token),
            cancellationToken);
        return result.Success ? result.Value : null;
    }

    private async Task<bool> RunAllAsync(IEnumerable<ulong> addresses, Func<ulong, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(addresses.Select(address => RunWithRetryAsync(address, async token =>
        {
            await action(address, token);
            return true;
        }, cancellationToken)));

        return results.All(x => x.Success);
    }

    /// <param name="action">One attempt; gets a token that also fires on the per-attempt timeout.</param>
    private async Task<(bool Success, T? Value)> RunWithRetryAsync<T>(ulong address,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        Outcome<T> outcome;
        try
        {
            outcome = await _retry.ExecuteOutcomeAsync(
                async (ctx, _) => Outcome.FromResult(await action(ctx.CancellationToken)), context, 0);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }

        switch (outcome.Exception)
        {
            case null:
                return (true, outcome.Result);
            case OperationCanceledException:
                logger.LogInformation("{Address}: Cancelled.", MacAddress.Format(address));
                return (false, default);
        }

        logger.LogError("{Address}: Failed after {Retries} retries: {Message}", MacAddress.Format(address),
            Retries, outcome.Exception.Message);
        return (false, default);
    }

    private async Task<StationReading> ReadStateOnceAsync(ulong address, CancellationToken cancellationToken)
    {
        return await WithCharacteristicAsync(address, Lighthouse.PowerstateCharacteristic, cancellationToken,
            async (device, characteristic) =>
            {
                var value = await characteristic.ReadValueAsync().WaitAsync(cancellationToken);
                var raw = value is { Length: > 0 } ? value[0] : (byte)0xFF;
                return new StationReading(Lighthouse.ToStationState(raw), device.Name, raw);
            });
    }

    private async Task ChangePowerstateOnceAsync(ulong address, Powerstate state, CancellationToken cancellationToken)
    {
        var mac = MacAddress.Format(address);
        await WithCharacteristicAsync(address, Lighthouse.PowerstateCharacteristic, cancellationToken,
            async characteristic =>
            {
                // Skip writing if the base station is already in the requested state
                var current = await characteristic.ReadValueAsync().WaitAsync(cancellationToken);
                if (current is { Length: > 0 } && Lighthouse.IsInState(current[0], state))
                {
                    // ToString(): the log template's ":lj" would render an enum as quoted JSON in trimmed builds
                    logger.LogInformation("{Address}: State already {State}.", mac, state.ToString());
                    return;
                }

                await characteristic.WriteValueWithResponseAsync([Lighthouse.WriteValue(state)])
                    .WaitAsync(cancellationToken);
                logger.LogInformation("{Address}: Successfully executed '{State}' command.", mac, state.ToString());
            });
    }

    private async Task IdentifyOnceAsync(ulong address, CancellationToken cancellationToken)
    {
        await WithCharacteristicAsync(address, Lighthouse.IdentifyCharacteristic, cancellationToken,
            async characteristic =>
            {
                await characteristic.WriteValueWithResponseAsync([Lighthouse.IdentifyValue])
                    .WaitAsync(cancellationToken);
                logger.LogInformation("{Address}: Successfully executed 'Identify' command.",
                    MacAddress.Format(address));
            });
    }

    private Task WithCharacteristicAsync(ulong address, BluetoothUuid characteristicUuid,
        CancellationToken cancellationToken, Func<GattCharacteristic, Task> action)
    {
        return WithCharacteristicAsync(address, characteristicUuid, cancellationToken, async (_, characteristic) =>
        {
            await action(characteristic);
            return true;
        });
    }

    private async Task<T> WithCharacteristicAsync<T>(ulong address, BluetoothUuid characteristicUuid,
        CancellationToken cancellationToken, Func<BluetoothDevice, GattCharacteristic, Task<T>> action)
    {
        var mac = MacAddress.Format(address);
        var device = await GetDeviceAsync(address, cancellationToken);

        try
        {
            logger.LogInformation("{Address}: Connecting to device.", mac);
            await device.Gatt.ConnectAsync().WaitAsync(cancellationToken);

            var service = await device.Gatt.GetPrimaryServiceAsync(Lighthouse.ControlService)
                              .WaitAsync(cancellationToken)
                          ?? throw new BluetoothConnectionException($"{mac}: Lighthouse control service not found.");
            var characteristic = await service.GetCharacteristicAsync(characteristicUuid)
                                     .WaitAsync(cancellationToken)
                                 ?? throw new BluetoothConnectionException(
                                     $"{mac}: Characteristic {characteristicUuid} not found.");

            return await action(device, characteristic);
        }
        finally
        {
            // Must not hide the original exception, e.g. when the connect itself failed
            try
            {
                device.Gatt.Disconnect();
            }
            catch (Exception ex)
            {
                logger.LogDebug("{Address}: Disconnect failed: {Message}", mac, ex.Message);
            }
        }
    }

    private async Task<BluetoothDevice> GetDeviceAsync(ulong address, CancellationToken cancellationToken)
    {
        var id = MacAddress.ToDeviceId(address);
        var device = await BluetoothDevice.FromIdAsync(id).WaitAsync(cancellationToken);
        if (device != null) return device;

        // BlueZ only resolves devices it has seen in a recent scan
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            // A scan for another address may have found this one while waiting for the lock
            device = await BluetoothDevice.FromIdAsync(id).WaitAsync(cancellationToken);
            if (device != null) return device;

            logger.LogInformation("{Address}: Device not known yet, scanning.", MacAddress.Format(address));
            var devices = await ScanUnlockedAsync(TimeSpan.FromSeconds(5), cancellationToken);

            return devices.FirstOrDefault(d => MacAddress.TryParse(d.Id, out var a) && a == address)
                   ?? await BluetoothDevice.FromIdAsync(id).WaitAsync(cancellationToken)
                   ?? throw new BluetoothConnectionException($"{MacAddress.Format(address)}: Failed to find device.");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<BluetoothDevice>> ScanAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            return await ScanUnlockedAsync(timeout, cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private static async Task<IReadOnlyCollection<BluetoothDevice>> ScanUnlockedAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var devices = await Bluetooth.ScanForDevicesAsync(
            new RequestDeviceOptions { AcceptAllDevices = true, Timeout = timeout }, cancellationToken);

        // On BlueZ a cancelled scan returns the partial result instead of throwing
        cancellationToken.ThrowIfCancellationRequested();
        return devices;
    }
}
