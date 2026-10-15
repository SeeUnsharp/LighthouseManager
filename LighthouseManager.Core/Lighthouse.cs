using InTheHand.Bluetooth;

namespace LighthouseManager.Core;

public enum Powerstate
{
    Wake,
    Sleep,
    Standby
}

/// <summary>
///     Power state as read from a base station.
/// </summary>
public enum StationState
{
    Unknown,
    Sleeping,
    Standby,

    /// <summary>Spinning up after a wake command.</summary>
    Booting,
    Awake
}

/// <param name="Name">Bluetooth name (e.g. "LHB-5E6F7A8B"), if known.</param>
/// <param name="RawValue">Raw powerstate byte, for clients that know more states than <see cref="StationState" />.</param>
public sealed record StationReading(StationState State, string? Name, byte RawValue);

/// <summary>
///     Lighthouse 2.0 GATT layout, see https://gist.github.com/BenWoodford/3a1e500a4ea2673525f5adb4120fd47c
/// </summary>
internal static class Lighthouse
{
    public const string NamePrefix = "LHB-";

    public static readonly BluetoothUuid ControlService =
        BluetoothUuid.FromGuid(Guid.Parse("00001523-1212-EFDE-1523-785FEABCD124"));

    public static readonly BluetoothUuid PowerstateCharacteristic =
        BluetoothUuid.FromGuid(Guid.Parse("00001525-1212-EFDE-1523-785FEABCD124"));

    public static readonly BluetoothUuid IdentifyCharacteristic =
        BluetoothUuid.FromGuid(Guid.Parse("00008421-1212-EFDE-1523-785FEABCD124"));

    public const byte IdentifyValue = 1;

    public static byte WriteValue(Powerstate state)
    {
        return state switch
        {
            Powerstate.Sleep => 0,
            Powerstate.Wake => 1,
            Powerstate.Standby => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    public static StationState ToStationState(byte readValue)
    {
        return readValue switch
        {
            0 => StationState.Sleeping,
            2 => StationState.Standby,
            // 9 = awake (last sleeping), 11 = awake (last standby)
            9 or 11 => StationState.Awake,
            // Observed while spinning up after a wake command
            1 or 8 => StationState.Booting,
            _ => StationState.Unknown
        };
    }

    /// <summary>
    ///     Whether the value read from the powerstate characteristic already matches the requested state.
    /// </summary>
    public static bool IsInState(byte readValue, Powerstate state)
    {
        return state switch
        {
            // 9 = awake (last sleeping), 11 = awake (last standby)
            Powerstate.Wake => readValue is 9 or 11,
            Powerstate.Sleep => readValue == 0,
            Powerstate.Standby => readValue == 2,
            _ => false
        };
    }
}

/// <param name="Name">Bluetooth name, e.g. "LHB-1A2B3C4D".</param>
public sealed record DiscoveredStation(ulong Address, string Name);

public sealed class BluetoothConnectionException(string message) : Exception(message);
