namespace Qodalis.Cli.Abstractions.Logs;

/// <summary>One log entry as the dashboard shows it.</summary>
public class LogRecord
{
    /// <summary>When it was written.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The level: <c>DEBUG</c>, <c>INFO</c>, <c>WARN</c> or <c>ERROR</c> (see <see cref="LogLevels"/>).</summary>
    public string Level { get; set; } = LogLevels.Info;

    /// <summary>The rendered message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The logger category, e.g. <c>RateBell.Jobs.FetchRates</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The service that wrote it, e.g. <c>ratebell-api</c>; null when the store holds only this process's.</summary>
    public string? Service { get; set; }

    /// <summary>The exception, when one was logged.</summary>
    public string? Exception { get; set; }
}

/// <summary>A page of <see cref="LogRecord"/>s.</summary>
public class LogPage
{
    /// <summary>The entries, newest first.</summary>
    public List<LogRecord> Entries { get; set; } = [];

    /// <summary>How many entries match, or null when the store cannot count them (then see <see cref="HasMore"/>).</summary>
    public int? Total { get; set; }

    /// <summary>Whether older matching entries follow this page.</summary>
    public bool HasMore { get; set; }
}

/// <summary>The levels of a <see cref="LogRecord"/>.</summary>
public static class LogLevels
{
    /// <summary>Trace and debug.</summary>
    public const string Debug = "DEBUG";

    /// <summary>Information.</summary>
    public const string Info = "INFO";

    /// <summary>Warnings.</summary>
    public const string Warn = "WARN";

    /// <summary>Errors and critical errors.</summary>
    public const string Error = "ERROR";

    /// <summary>Every level, lowest first.</summary>
    public static readonly IReadOnlyList<string> All = [Debug, Info, Warn, Error];

    /// <summary>The level for a name such as <c>Information</c>, <c>warn</c> or <c>Fatal</c>; null when unknown.</summary>
    public static string? Normalize(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "DEBUG" or "TRACE" or "VERBOSE" => Debug,
        "INFO" or "INFORMATION" => Info,
        "WARN" or "WARNING" => Warn,
        "ERROR" or "CRITICAL" or "FATAL" => Error,
        _ => null,
    };
}
