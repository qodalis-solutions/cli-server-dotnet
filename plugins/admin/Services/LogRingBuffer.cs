using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// A fixed-size ring buffer that stores recent log entries for the admin logs endpoint.
/// Implements ILoggerProvider to plug into the ASP.NET Core logging pipeline.
/// </summary>
public class LogRingBuffer : ILoggerProvider
{
    private readonly LogEntry[] _buffer;
    private readonly int _capacity;
    private long _writeIndex;
    private long _count;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance with the specified capacity.
    /// </summary>
    /// <param name="capacity">Maximum number of log entries to retain.</param>
    public LogRingBuffer(int capacity = 1000)
    {
        _capacity = capacity;
        _buffer = new LogEntry[capacity];
    }

    /// <summary>
    /// Adds a log entry to the ring buffer, overwriting the oldest entry if at capacity.
    /// </summary>
    /// <param name="entry">The log entry to add.</param>
    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            var index = _writeIndex % _capacity;
            _buffer[index] = entry;
            _writeIndex++;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>
    /// Queries log entries with optional filtering by level and search text, returning most recent first.
    /// </summary>
    /// <param name="level">Optional log level filter.</param>
    /// <param name="search">Optional text to search in message and source.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="offset">Number of entries to skip for pagination.</param>
    /// <returns>A <see cref="LogQueryResult"/> containing the matched entries and total count.</returns>
    public LogQueryResult Query(string? level = null, string? search = null, int limit = 100, int offset = 0)
    {
        List<LogEntry> snapshot;
        lock (_lock)
        {
            var startIndex = _count < _capacity ? 0 : _writeIndex % _capacity;
            snapshot = new List<LogEntry>((int)_count);
            for (long i = 0; i < _count; i++)
            {
                var idx = (startIndex + i) % _capacity;
                snapshot.Add(_buffer[idx]);
            }
        }

        // Apply filters
        IEnumerable<LogEntry> filtered = snapshot;

        if (!string.IsNullOrEmpty(level))
        {
            filtered = filtered.Where(e => e.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Source.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        // Most recent first
        var ordered = filtered.Reverse().ToList();
        var total = ordered.Count;
        var items = ordered.Skip(offset).Take(Math.Min(limit, 500)).ToList();

        return new LogQueryResult { Items = items, Total = total, Limit = limit, Offset = offset };
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new RingBufferLogger(categoryName, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Represents a single log entry captured by the ring buffer.
/// </summary>
public class LogEntry
{
    /// <summary>UTC timestamp when the entry was recorded.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>Log severity level (e.g., "INFO", "ERROR", "WARN", "DEBUG").</summary>
    public string Level { get; set; } = "information";
    /// <summary>The log message text.</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>The logger category name (source of the log entry).</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Paginated result from a log query.
/// </summary>
public class LogQueryResult
{
    /// <summary>The matched log entries for the current page.</summary>
    public List<LogEntry> Items { get; set; } = [];
    /// <summary>Total number of entries matching the filter.</summary>
    public int Total { get; set; }
    /// <summary>Maximum entries per page.</summary>
    public int Limit { get; set; }
    /// <summary>Number of entries skipped.</summary>
    public int Offset { get; set; }
}

internal class RingBufferLogger : ILogger
{
    private readonly string _category;
    private readonly LogRingBuffer _buffer;

    public RingBufferLogger(string category, LogRingBuffer buffer)
    {
        _category = category;
        _buffer = buffer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        _buffer.Add(new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = MapLogLevel(logLevel),
            Message = message,
            Source = _category,
        });
    }

    private static string MapLogLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "ERROR",
        _ => "INFO",
    };
}
