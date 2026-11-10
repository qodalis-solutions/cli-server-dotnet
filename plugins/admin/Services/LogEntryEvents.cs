using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Publishes every entry the <see cref="LogRingBuffer"/> captures as a <c>log:entry</c> event (<c>timestamp</c>,
/// <c>level</c>, <c>message</c>, <c>source</c>; audience <c>logs:read</c>), which the dashboard's Logs page follows
/// live. Registered by <c>AddAdmin</c>; publishes while the host runs.
/// </summary>
public sealed class LogEntryEvents(LogRingBuffer buffer, ICliEventBus bus) : IHostedService, IDisposable
{
    /// <summary>The event type.</summary>
    public const string EventType = "log:entry";

    /// <summary>Who receives the entries: the sockets of users who may read the logs.</summary>
    public static readonly CliEventAudience Audience = CliEventAudience.WithPermission(CliRoutePolicy.LogsRead);

    /// <summary>
    /// Set while an entry is delivered: a line logged meanwhile (the bus about a failing handler, a socket about a
    /// failed send) is kept in the buffer but not published, so delivery can never start a loop.
    /// </summary>
    private static readonly AsyncLocal<bool> Publishing = new();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        buffer.EntryAdded += OnEntryAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        buffer.EntryAdded -= OnEntryAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => buffer.EntryAdded -= OnEntryAdded;

    private void OnEntryAdded(LogEntry entry)
    {
        if (Publishing.Value) return;
        _ = PublishAsync(entry);
    }

    private async Task PublishAsync(LogEntry entry)
    {
        // Set inside the async method: it flows into the handlers (and the sends they start), not back to the logger
        Publishing.Value = true;
        try
        {
            await bus.PublishAsync(EventType, new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Utc),
                ["level"] = entry.Level,
                ["message"] = entry.Message,
                ["source"] = entry.Source,
            }, Audience);
        }
        catch
        {
            // Never log here: a log line is an event too
        }
    }
}
