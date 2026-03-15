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

    public LogRingBuffer(int capacity = 1000)
    {
        _capacity = capacity;
        _buffer = new LogEntry[capacity];
    }

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

    public ILogger CreateLogger(string categoryName)
    {
        return new RingBufferLogger(categoryName, this);
    }

    public void Dispose()
    {
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "information";
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public class LogQueryResult
{
    public List<LogEntry> Items { get; set; } = [];
    public int Total { get; set; }
    public int Limit { get; set; }
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
