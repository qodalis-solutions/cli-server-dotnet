namespace Qodalis.Cli.Abstractions.Events;

/// <summary>
/// A server event published on <see cref="ICliEventBus"/>. The events WebSocket (<c>/ws/v1/qcli/events</c>) sends it
/// to the sockets of its <see cref="Audience"/> as one flat JSON object: <c>type</c>, <c>timestamp</c> and the
/// fields of <see cref="Data"/>.
/// </summary>
public sealed class CliEvent
{
    /// <summary>The event type, <c>namespace:name</c> (e.g. <c>job:completed</c>).</summary>
    public required string Type { get; init; }

    /// <summary>When the event happened (UTC).</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>The publisher's fields (camelCase), merged into the wire message; <c>type</c> and <c>timestamp</c> are reserved.</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();

    /// <summary>Who receives the event over the events WebSocket (not sent on the wire).</summary>
    public CliEventAudience Audience { get; init; } = CliEventAudience.Everyone;
}
