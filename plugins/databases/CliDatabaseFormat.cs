using System.Globalization;
using System.Text;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>Database stats as text, for the terminal and MCP.</summary>
internal static class CliDatabaseFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary><c>1536</c> -> <c>1.5 KB</c>.</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString(value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Stats(CliDatabaseStats stats)
    {
        var text = new StringBuilder($"{stats.Name} ({stats.Dialect})");
        if (stats.Server?.Version != null) text.Append($"\n  Server:      {stats.Server.Version}");
        if (stats.Server?.UptimeSeconds is { } uptime) text.Append($"\n  Uptime:      {TimeSpan.FromSeconds(uptime):d\\.hh\\:mm\\:ss}");
        if (stats.Size?.Bytes is { } size)
        {
            text.Append($"\n  Size:        {Bytes(size)}");
            var parts = new List<string>();
            if (stats.Size.DataBytes is { } data) parts.Add($"data {Bytes(data)}");
            if (stats.Size.IndexBytes is { } index) parts.Add($"indexes {Bytes(index)}");
            if (stats.Size.LogBytes is { } log) parts.Add($"log {Bytes(log)}");
            if (parts.Count > 0) text.Append($" ({string.Join(", ", parts)})");
        }
        if (stats.Connections is { } c)
        {
            text.Append($"\n  Connections: {c.Active?.ToString(CultureInfo.InvariantCulture) ?? "?"} active, {c.Open?.ToString(CultureInfo.InvariantCulture) ?? "?"} open");
            if (c.Max is { } max) text.Append($", max {max}");
        }
        if (stats.Counters is { } k)
        {
            var parts = new List<string>();
            if (k.Queries is { } q) parts.Add($"{Count(q)} queries");
            if (k.Transactions is { } t) parts.Add($"{Count(t)} transactions");
            if (k.RowsRead is { } r) parts.Add($"{Count(r)} rows read");
            if (k.RowsWritten is { } w) parts.Add($"{Count(w)} rows written");
            if (parts.Count > 0) text.Append($"\n  Since start: {string.Join(", ", parts)}{(stats.CountersScope == "server" ? " (whole server)" : "")}");
        }
        if (stats.CacheHitRatio is { } ratio) text.Append($"\n  Cache hits:  {(ratio * 100).ToString("0.0", CultureInfo.InvariantCulture)}%");
        if (stats.Tables is { Count: > 0 } tables)
        {
            text.Append("\n  Largest tables:");
            foreach (var t in tables)
            {
                var name = t.Schema != null ? $"{t.Schema}.{t.Name}" : t.Name;
                var details = new List<string>();
                if (t.Bytes is { } b) details.Add(Bytes(b));
                if (t.Rows is { } rows) details.Add($"{Count(rows)} rows");
                text.Append($"\n    {name}{(details.Count > 0 ? $"  {string.Join(", ", details)}" : "")}");
            }
        }
        foreach (var u in stats.Unavailable) text.Append($"\n  ({u.Metric} unavailable: {u.Error})");
        return text.ToString();
    }
}
