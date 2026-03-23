using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// In-memory logger that collects log entries during a job execution.
/// </summary>
internal class CliJobLogger : ICliJobLogger
{
    private readonly List<JobLogEntry> _entries = [];

    /// <summary>
    /// Gets the log entries recorded during the job execution.
    /// </summary>
    public IReadOnlyList<JobLogEntry> Entries => _entries;

    /// <inheritdoc />
    public void Debug(string message) => Log("debug", message);
    /// <inheritdoc />
    public void Info(string message) => Log("info", message);
    /// <inheritdoc />
    public void Warning(string message) => Log("warning", message);
    /// <inheritdoc />
    public void Error(string message) => Log("error", message);

    private void Log(string level, string message)
    {
        _entries.Add(new JobLogEntry { Level = level, Message = message });
    }
}
