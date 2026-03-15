using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Jobs;

internal static partial class IntervalParser
{
    [GeneratedRegex(@"^(\d+)(s|m|h|d)$")]
    private static partial Regex IntervalRegex();

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
