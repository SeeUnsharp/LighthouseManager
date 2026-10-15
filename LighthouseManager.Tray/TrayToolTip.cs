namespace LighthouseManager.Tray;

/// <summary>
///     Tray icon tooltip text.
/// </summary>
public static class TrayToolTip
{
    /// <summary>Windows cuts tray tooltips off after 127 characters.</summary>
    public const int MaxLength = 127;

    /// <summary>
    ///     One line per station while it fits, a summary ("2 On, 1 Sleeping") otherwise.
    /// </summary>
    public static string Build(string statusText, IReadOnlyList<(string Name, string State)> stations)
    {
        var header = $"LighthouseManager\n{statusText}";
        if (stations.Count == 0) return Truncate(header);

        var detailed = header + "\n" + string.Join("\n", stations.Select(s => $"{s.Name}: {s.State}"));
        if (detailed.Length <= MaxLength) return detailed;

        var summary = string.Join(", ", stations.GroupBy(s => s.State).Select(g => $"{g.Count()} {g.Key}"));
        return Truncate($"{header}\n{stations.Count} base stations: {summary}");
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
