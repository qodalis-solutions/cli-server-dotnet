using System.Globalization;

namespace Qodalis.Cli.Abstractions.Insights;

/// <summary>
/// A time range <c>[Since, Until)</c> cut into <see cref="Count"/> equal columns, as the dashboard's charts ask for them
/// (<c>since</c>/<c>until</c> or <c>from</c>/<c>to</c>, and <c>buckets</c>): column <c>i</c> covers
/// <c>[Since + i * BucketMs, Since + (i + 1) * BucketMs)</c>.
/// </summary>
public sealed class TimeBuckets
{
    /// <summary>Columns when the request names none.</summary>
    public const int DefaultCount = 24;

    /// <summary>The most columns a request gets (fewer than 1 become 1).</summary>
    public const int MaxCount = 200;

    /// <summary>The range when the request names no start: the last 24 hours.</summary>
    public static readonly TimeSpan DefaultSpan = TimeSpan.FromHours(24);

    /// <summary>Creates the columns of <c>[since, until)</c>; <paramref name="until"/> must be after <paramref name="since"/>.</summary>
    public TimeBuckets(DateTime since, DateTime until, int count)
    {
        Since = AsUtc(since);
        Until = AsUtc(until);
        if (Until <= Since) throw new ArgumentException("until must be after since");
        Count = Math.Clamp(count, 1, MaxCount);
        BucketMs = (Until - Since).TotalMilliseconds / Count;
    }

    /// <summary>The start of the range (UTC, inclusive).</summary>
    public DateTime Since { get; }

    /// <summary>The end of the range (UTC, exclusive).</summary>
    public DateTime Until { get; }

    /// <summary>The number of columns.</summary>
    public int Count { get; }

    /// <summary>The width of a column in milliseconds.</summary>
    public double BucketMs { get; }

    /// <summary>The start of column <paramref name="index"/>.</summary>
    public DateTime StartOf(int index) => Since.AddTicks((long)Math.Round(index * BucketMs * TimeSpan.TicksPerMillisecond));

    /// <summary>The column of <paramref name="time"/>, or -1 when it is outside <c>[Since, Until)</c>.</summary>
    public int IndexOf(DateTime time)
    {
        var utc = AsUtc(time);
        if (utc < Since || utc >= Until) return -1;
        var index = (int)Math.Floor((utc - Since).TotalMilliseconds / BucketMs);
        return Math.Min(index, Count - 1);
    }

    /// <summary>Whether <paramref name="time"/> is in <c>[Since, Until)</c>.</summary>
    public bool Contains(DateTime time) => IndexOf(time) >= 0;

    /// <summary>
    /// Reads the range from a request: <paramref name="until"/> defaults to <paramref name="now"/> (or the current
    /// time), <paramref name="since"/> to 24 hours before <paramref name="until"/>, <paramref name="buckets"/> to 24
    /// (clamped to 1..200). False with an <paramref name="error"/> for a time or count that does not parse, or an
    /// empty range.
    /// </summary>
    public static bool TryParse(
        string? since, string? until, string? buckets, out TimeBuckets? result, out string? error,
        string sinceName = "since", string untilName = "until", DateTime? now = null)
    {
        result = null;
        if (!TryParseTime(until, out var end)) { error = $"{untilName} must be an ISO 8601 time"; return false; }
        if (!TryParseTime(since, out var start)) { error = $"{sinceName} must be an ISO 8601 time"; return false; }

        var count = DefaultCount;
        if (!string.IsNullOrWhiteSpace(buckets))
        {
            if (!int.TryParse(buckets.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            {
                error = "buckets must be an integer";
                return false;
            }
        }

        var to = end ?? AsUtc(now ?? DateTime.UtcNow);
        var from = start ?? to - DefaultSpan;
        if (to <= from) { error = $"{untilName} must be after {sinceName}"; return false; }

        error = null;
        result = new TimeBuckets(from, to, count);
        return true;
    }

    /// <summary>
    /// Parses an ISO 8601 time (without an offset it is UTC) into UTC; an empty value is null. False when it does not
    /// parse.
    /// </summary>
    public static bool TryParseTime(string? value, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        utc = parsed.UtcDateTime;
        return true;
    }

    /// <summary>A UTC time as the insight endpoints write it: ISO 8601 with milliseconds, e.g. <c>2026-11-05T21:00:00.000Z</c>.</summary>
    public static string Format(DateTime time) =>
        AsUtc(time).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Format(DateTime)"/>
    public static string Format(DateTimeOffset time) => Format(time.UtcDateTime);

    /// <summary>Treats a time without a kind as UTC and converts a local one.</summary>
    public static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
    };

    /// <summary>The nearest-rank percentile <paramref name="percentile"/> (0..1) of ascending <paramref name="sorted"/> values.</summary>
    public static long? Percentile(IReadOnlyList<long> sorted, double percentile)
    {
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
