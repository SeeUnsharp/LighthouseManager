using System.Text.Json;
using System.Text.Json.Serialization;
using LighthouseManager.Core;

namespace LighthouseManagerService.Dimming;

/// <param name="Name">Platform id of the display (KDE: D-Bus name like "display0").</param>
/// <param name="Label">Human readable name (monitor model), also used to detect a different display under a reused id.</param>
public sealed record DisplayBrightness(string Name, string Label, int Brightness, int MaxBrightness);

/// <summary>
///     Brightness control of the desktop displays.
/// </summary>
public interface IDisplayBrightnessControl
{
    Task<IReadOnlyList<DisplayBrightness>> GetDisplaysAsync(CancellationToken ct);
    Task SetBrightnessAsync(string name, int brightness, CancellationToken ct);
}

/// <summary>
///     A display dimmed by us, with the brightness to restore.
/// </summary>
public sealed record DimmedDisplay(string Name, string Label, int Brightness, int DimmedTo);

/// <summary>
///     Which displays to change for dimming and restoring. Pure logic.
/// </summary>
public static class DimmingPlan
{
    /// <summary>
    ///     Lowest brightness. With DDC/CI that's the monitor's lowest backlight setting, not off.
    /// </summary>
    public const int DimmedBrightness = 0;

    /// <param name="saved">Displays dimmed before (left dimmed by a previous run): their original brightness is kept.</param>
    /// <returns>The displays to remember (save before changing anything) and the brightness changes.</returns>
    public static (IReadOnlyList<DimmedDisplay> Saved, IReadOnlyList<(string Name, int Brightness)> Changes) Dim(
        IReadOnlyList<DisplayBrightness> displays, IReadOnlyList<DimmedDisplay> saved)
    {
        var newSaved = new List<DimmedDisplay>();
        var changes = new List<(string, int)>();
        foreach (var display in displays)
        {
            var previous = saved.FirstOrDefault(s => Matches(s, display));
            if (previous != null)
                newSaved.Add(previous);
            else if (display.Brightness > DimmedBrightness)
                newSaved.Add(new DimmedDisplay(display.Name, display.Label, display.Brightness, DimmedBrightness));
            else
                continue;

            if (display.Brightness > DimmedBrightness) changes.Add((display.Name, DimmedBrightness));
        }

        return (newSaved, changes);
    }

    /// <summary>
    ///     Restores displays that are still dimmed. A display whose brightness was changed meanwhile (by the user) is
    ///     left alone, as is one that's gone or was replaced.
    /// </summary>
    public static IReadOnlyList<(string Name, int Brightness)> Restore(IReadOnlyList<DisplayBrightness> displays,
        IReadOnlyList<DimmedDisplay> saved)
    {
        var changes = new List<(string, int)>();
        foreach (var dimmed in saved)
        {
            var display = displays.FirstOrDefault(d => Matches(dimmed, d));
            // 1 % tolerance: the value read back can be rounded to the monitor's own (DDC/CI) scale
            if (display != null && display.Brightness <= dimmed.DimmedTo + display.MaxBrightness / 100)
                changes.Add((display.Name, dimmed.Brightness));
        }

        return changes;
    }

    /// <summary>
    ///     Brightness during a transition, eased in and out (smoothstep) so it starts and ends softly.
    /// </summary>
    /// <param name="progress">0 = <paramref name="from" />, 1 = <paramref name="to" />.</param>
    public static int Interpolate(int from, int to, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return (int)Math.Round(from + (to - from) * (t * t * (3 - 2 * t)));
    }

    private static bool Matches(DimmedDisplay dimmed, DisplayBrightness display)
    {
        return dimmed.Name == display.Name && dimmed.Label == display.Label;
    }
}

[JsonSerializable(typeof(List<DimmedDisplay>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class DimmingJsonContext : JsonSerializerContext;

/// <summary>
///     Remembers the original brightness in the state directory, so the displays are restored even after a crash or
///     reboot while dimmed.
/// </summary>
public sealed class DimmedDisplayStore(string file)
{
    public static DimmedDisplayStore Default => new(Path.Combine(AppPaths.StateDirectory, "dimmed-displays.json"));

    public bool Exists => File.Exists(file);

    public IReadOnlyList<DimmedDisplay> Load()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), DimmingJsonContext.Default.ListDimmedDisplay) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<DimmedDisplay> displays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(displays.ToList(), DimmingJsonContext.Default.ListDimmedDisplay));
        File.Move(temp, file, true);
    }

    public void Delete()
    {
        File.Delete(file);
    }
}
