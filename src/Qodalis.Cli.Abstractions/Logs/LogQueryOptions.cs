namespace Qodalis.Cli.Abstractions.Logs;

/// <summary>Filters and paging for <see cref="ILogStore.QueryAsync"/>. Unset filters match everything.</summary>
public class LogQueryOptions
{
    /// <summary>The most entries a page returns.</summary>
    public const int MaxLimit = 500;

    /// <summary>The levels to return (<see cref="LogLevels"/>); empty for all.</summary>
    public IReadOnlyCollection<string> Levels { get; set; } = [];

    /// <summary>Text the message or the source contains, ignoring case.</summary>
    public string? Search { get; set; }

    /// <summary>The service that wrote the entries, exactly.</summary>
    public string? Service { get; set; }

    /// <summary>Entries written at or after this time.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Entries written before this time.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>How many entries to return, at most <see cref="MaxLimit"/>.</summary>
    public int Limit { get; set; } = 100;

    /// <summary>How many of the newest matching entries to skip.</summary>
    public int Offset { get; set; }
}
