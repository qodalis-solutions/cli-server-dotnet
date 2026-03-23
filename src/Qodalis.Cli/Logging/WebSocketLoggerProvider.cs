using Microsoft.Extensions.Logging;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Logging;

/// <summary>
/// Logger provider that forwards log messages to connected WebSocket clients via <see cref="ICliLogSocketManager"/>.
/// </summary>
public class WebSocketLoggerProvider : ILoggerProvider
{
    private readonly ICliLogSocketManager _logSocketManager;

    /// <summary>
    /// Initializes a new instance of <see cref="WebSocketLoggerProvider"/>.
    /// </summary>
    /// <param name="logSocketManager">The log socket manager for broadcasting log messages.</param>
    public WebSocketLoggerProvider(ICliLogSocketManager logSocketManager)
    {
        _logSocketManager = logSocketManager;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new WebSocketLogger(categoryName, _logSocketManager);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Logger that broadcasts log messages to WebSocket clients for real-time monitoring.
/// </summary>
public class WebSocketLogger : ILogger
{
    private readonly string _category;
    private readonly ICliLogSocketManager _logSocketManager;

    /// <summary>
    /// Initializes a new instance of <see cref="WebSocketLogger"/>.
    /// </summary>
    /// <param name="category">The logger category name.</param>
    /// <param name="logSocketManager">The log socket manager for broadcasting.</param>
    public WebSocketLogger(string category, ICliLogSocketManager logSocketManager)
    {
        _category = category;
        _logSocketManager = logSocketManager;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        var level = MapLogLevel(logLevel);

        // Fire and forget — logging should not block the caller
        _ = _logSocketManager.BroadcastLogAsync(level, message, _category);
    }

    /// <summary>
    /// Maps a .NET <see cref="LogLevel"/> to the CLI log level string used in WebSocket messages.
    /// </summary>
    /// <param name="logLevel">The .NET log level.</param>
    /// <returns>The CLI log level string (e.g., "verbose", "debug", "information", "warning", "error", "fatal").</returns>
    public static string MapLogLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "verbose",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "information"
    };
}
