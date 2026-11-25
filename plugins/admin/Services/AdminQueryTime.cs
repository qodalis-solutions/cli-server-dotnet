using System.Globalization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>A refused query value; the message names it (<c>--since must be ...</c>).</summary>
public sealed class AdminQueryException(string message) : Exception(message);

/// <summary>Query times of the admin command.</summary>
public static partial class AdminQueryTime
{
    [GeneratedRegex(@"^(\d+)\s*([smhd])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoRegex();

    /// <summary>
    /// A start time given as a duration back from <paramref name="now"/> (<c>30s</c>, <c>5m</c>, <c>1h</c>, <c>7d</c>)
    /// or an ISO 8601 date (UTC when it names no offset). Throws <see cref="AdminQueryException"/> for anything else, a
    /// zero duration or a time in the future.
    /// </summary>
    public static DateTimeOffset ParseSince(string value, string name, DateTimeOffset now)
    {
        var text = value.Trim();
        if (RelativeRegex().Match(text) is { Success: true } relative)
        {
            if (!long.TryParse(relative.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                throw new AdminQueryException($"{name} must be more than 0");
            var unit = char.ToLowerInvariant(relative.Groups[2].Value[0]) switch
            {
                's' => TimeSpan.FromSeconds(1),
                'm' => TimeSpan.FromMinutes(1),
                'h' => TimeSpan.FromHours(1),
                _ => TimeSpan.FromDays(1),
            };
            return now - unit * amount;
        }
        if (!IsoRegex().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            throw new AdminQueryException($"{name} must be a duration (30s, 5m, 1h, 7d) or an ISO 8601 date");
        if (date > now) throw new AdminQueryException($"{name} is in the future");
        return date;
    }
}
