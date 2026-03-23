using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Parses human-readable interval strings (e.g., "30s", "5m", "1h", "1d") into <see cref="TimeSpan"/> values.
/// </summary>
internal static partial class IntervalParser
{
    [GeneratedRegex(@"^(\d+)(s|m|h|d)$")]
    private static partial Regex IntervalRegex();

    /// <summary>
    /// Parses an interval string into a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="interval">The interval string (e.g., "30s", "5m", "1h", "1d").</param>
    /// <returns>The parsed <see cref="TimeSpan"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the format is invalid.</exception>
    public static TimeSpan Parse(string interval)
    {
        var match = IntervalRegex().Match(interval);
        if (!match.Success)
            throw new ArgumentException($"Invalid interval format: '{interval}'. Expected format: '30s', '5m', '1h', '1d'.");

        var value = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            _ => throw new ArgumentException($"Unknown interval unit: '{match.Groups[2].Value}'"),
        };
    }
}
