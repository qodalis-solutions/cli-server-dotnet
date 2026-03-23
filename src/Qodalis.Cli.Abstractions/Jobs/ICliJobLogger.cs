namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Provides logging methods for job execution, with messages captured in the job execution history.
/// </summary>
public interface ICliJobLogger
{
    /// <summary>Logs a debug-level message.</summary>
    /// <param name="message">The message to log.</param>
    void Debug(string message);

    /// <summary>Logs an informational message.</summary>
    /// <param name="message">The message to log.</param>
    void Info(string message);

    /// <summary>Logs a warning message.</summary>
    /// <param name="message">The message to log.</param>
    void Warning(string message);

    /// <summary>Logs an error message.</summary>
    /// <param name="message">The message to log.</param>
    void Error(string message);
}
