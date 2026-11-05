using Qodalis.Cli.Abstractions.Insights;

namespace Qodalis.Cli.Abstractions.Logs;

/// <summary>What <see cref="ILogStore.HistogramAsync"/> counts: the filters of a log query over a range of columns.</summary>
public class LogHistogramOptions
{
    /// <summary>The range and its columns; <see cref="LogQueryOptions.From"/>/<see cref="LogQueryOptions.To"/> of <see cref="Filter"/> are ignored.</summary>
    public required TimeBuckets Range { get; init; }

    /// <summary>The levels, text and service to count (as in <see cref="ILogStore.QueryAsync"/>).</summary>
    public LogQueryOptions Filter { get; init; } = new();

    /// <summary>The filter limited to <see cref="Range"/>.</summary>
    public LogQueryOptions RangeFilter(int limit = LogQueryOptions.MaxLimit, int offset = 0) => new()
    {
        Levels = Filter.Levels,
        Search = Filter.Search,
        Service = Filter.Service,
        From = new DateTimeOffset(Range.Since),
        To = new DateTimeOffset(Range.Until),
        Limit = limit,
        Offset = offset,
    };
}

/// <summary>Entries per level in each column of a <see cref="TimeBuckets"/> range.</summary>
public class LogHistogram
{
    /// <summary>Creates an empty histogram of <paramref name="range"/>.</summary>
    public LogHistogram(TimeBuckets range)
    {
        Range = range;
        Buckets = Enumerable.Range(0, range.Count).Select(_ => new LogLevelCounts()).ToArray();
    }

    /// <summary>The range and its columns.</summary>
    public TimeBuckets Range { get; }

    /// <summary>The counts of each column, oldest first.</summary>
    public IReadOnlyList<LogLevelCounts> Buckets { get; }

    /// <summary>The counts of the whole range.</summary>
    public LogLevelCounts Totals
    {
        get
        {
            var totals = new LogLevelCounts();
            foreach (var bucket in Buckets)
            {
                foreach (var level in LogLevels.All) totals.Add(level, bucket[level]);
            }
            return totals;
        }
    }

    /// <summary>Counts <paramref name="count"/> entries of <paramref name="level"/> written at <paramref name="time"/>; outside the range they are ignored.</summary>
    public void Add(DateTime time, string? level, long count = 1)
    {
        var index = Range.IndexOf(time);
        if (index >= 0) Buckets[index].Add(level, count);
    }

    /// <summary>Counts <paramref name="count"/> entries of <paramref name="level"/> in column <paramref name="index"/>; outside the columns they are ignored.</summary>
    public void AddToBucket(int index, string? level, long count)
    {
        if (index >= 0 && index < Buckets.Count) Buckets[index].Add(level, count);
    }
}

/// <summary>Entries per level (<see cref="LogLevels"/>).</summary>
public class LogLevelCounts
{
    private readonly long[] _counts = new long[LogLevels.All.Count];

    /// <summary>The count of <paramref name="level"/> (<c>DEBUG</c>, <c>INFO</c>, <c>WARN</c> or <c>ERROR</c>).</summary>
    public long this[string level] => Index(level) is var i and >= 0 ? _counts[i] : 0;

    /// <summary>Adds <paramref name="count"/> to <paramref name="level"/>; a level not in <see cref="LogLevels"/> counts as <c>INFO</c>.</summary>
    public void Add(string? level, long count = 1)
    {
        var i = Index(LogLevels.Normalize(level) ?? LogLevels.Info);
        _counts[i] += count;
    }

    /// <summary>The counts keyed by level, lowest first.</summary>
    public Dictionary<string, long> ToDictionary() => LogLevels.All.ToDictionary(l => l, l => this[l]);

    private static int Index(string level)
    {
        for (var i = 0; i < LogLevels.All.Count; i++)
        {
            if (LogLevels.All[i] == level) return i;
        }
        return -1;
    }
}
