using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The job events published by the jobs API and MCP tools (<c>job:triggered</c>, <c>job:updated</c>,
/// <c>job:runs-deleted</c>), so they reach clients whatever the <see cref="Abstractions.Jobs.ICliJobManager"/>. The
/// scheduler publishes the run and state events (<c>job:started</c>, <c>job:paused</c>, ...). Audience <c>jobs:read</c>.
/// </summary>
internal sealed class JobEvents(ICliEventBus? bus, string? actor)
{
    public static readonly CliEventAudience Audience = CliEventAudience.WithPermission(CliRoutePolicy.JobsRead);

    public Task TriggeredAsync(string jobId) => PublishAsync("job:triggered", new() { ["jobId"] = jobId });

    public Task UpdatedAsync(string jobId) => PublishAsync("job:updated", new() { ["jobId"] = jobId });

    /// <summary>One run deleted (<paramref name="executionId"/>).</summary>
    public Task RunDeletedAsync(string jobId, string executionId) =>
        PublishAsync("job:runs-deleted", new() { ["jobId"] = jobId, ["executionId"] = executionId });

    /// <summary>Runs deleted in bulk; nothing is published when none was.</summary>
    public Task RunsDeletedAsync(string jobId, int count) => count == 0
        ? Task.CompletedTask
        : PublishAsync("job:runs-deleted", new() { ["jobId"] = jobId, ["count"] = count });

    private Task PublishAsync(string type, Dictionary<string, object?> data)
    {
        if (bus == null) return Task.CompletedTask;
        if (actor != null) data["actor"] = actor;
        return bus.PublishAsync(type, data, Audience);
    }
}
