using System.Globalization;
using System.Text.RegularExpressions;

namespace LighthouseManager.Core;

public static class MacAddress
{
    // Six hex pairs separated consistently by ':' or '-', or twelve hex digits without separators
    private static readonly Regex Pattern = new(
        "^(?:[0-9A-Fa-f]{2}(?<sep>[:-])(?:[0-9A-Fa-f]{2}\\k<sep>){4}[0-9A-Fa-f]{2}|[0-9A-Fa-f]{12})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Parses "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF" or "AABBCCDDEEFF".
    /// </summary>
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (text == null) return false;

        var trimmed = text.Trim();
        if (!Pattern.IsMatch(trimmed)) return false;

        var hex = trimmed.Replace(":", "").Replace("-", "");
        return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>
    ///     Parses all addresses; on failure <paramref name="invalid" /> is the first address that could not be parsed.
    /// </summary>
    public static bool TryParseList(IEnumerable<string> texts, out IReadOnlyList<ulong> addresses,
        out string? invalid)
    {
        var result = new List<ulong>();
        addresses = result;
        invalid = null;

        foreach (var text in texts)
        {
            if (!TryParse(text, out var address))
            {
                invalid = text;
                return false;
            }

            if (!result.Contains(address)) result.Add(address);
        }

        return true;
    }

    public static string Format(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>
    ///     InTheHand device ids depend on the Bluetooth backend: BlueZ uses "AA:BB:...", WinRT plain hex. The backend
    ///     is selected by the target framework, so this has to be decided at compile time as well.
    /// </summary>
    public static string ToDeviceId(ulong address)
    {
#if WINDOWS
        return address.ToString("X12", CultureInfo.InvariantCulture);
#else
        return Format(address);
#endif
    }
}
