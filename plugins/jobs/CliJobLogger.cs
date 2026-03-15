using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

internal class CliJobLogger : ICliJobLogger
{
    private readonly List<JobLogEntry> _entries = [];

    public IReadOnlyList<JobLogEntry> Entries => _entries;

    public void Debug(string message) => Log("debug", message);
    public void Info(string message) => Log("info", message);
    public void Warning(string message) => Log("warning", message);
    public void Error(string message) => Log("error", message);

    private void Log(string level, string message)
    {
        _entries.Add(new JobLogEntry { Level = level, Message = message });
    }
}
