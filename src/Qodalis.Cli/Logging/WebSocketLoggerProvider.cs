using Microsoft.Extensions.Logging;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Logging;

public class WebSocketLoggerProvider : ILoggerProvider
{
    private readonly CliLogSocketManager _logSocketManager;

    public WebSocketLoggerProvider(CliLogSocketManager logSocketManager)
    {
        _logSocketManager = logSocketManager;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new WebSocketLogger(categoryName, _logSocketManager);
    }

    public void Dispose()
    {
    }
}

public class WebSocketLogger : ILogger
{
    private readonly string _category;
    private readonly CliLogSocketManager _logSocketManager;

    public WebSocketLogger(string category, CliLogSocketManager logSocketManager)
    {
        _category = category;
        _logSocketManager = logSocketManager;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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
