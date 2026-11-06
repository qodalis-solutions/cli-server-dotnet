namespace Qodalis.Cli.Abstractions.Events;

/// <summary>
/// The server's in-process event bus (one per server, registered by <c>AddCli</c>). Plugins and host applications
/// publish events and subscribe to them; the events WebSocket is one subscriber of <c>*</c> that delivers each event
/// to the sockets of its audience.
/// </summary>
public interface ICliEventBus
{
    /// <summary>
    /// Publishes an event. Handlers run in subscription order and the task completes when they all completed;
    /// a handler's exception is logged and never fails the publisher.
    /// </summary>
    /// <param name="type">The event type, <c>namespace:name</c>.</param>
    /// <param name="data">The fields: an anonymous object, a dictionary or a POCO; serialized camelCase and merged into
    /// the message. A <c>timestamp</c> field sets the event's time; <c>type</c> is reserved.</param>
    /// <param name="audience">Who receives it over the events WebSocket; everyone when null.</param>
    /// <param name="cancellationToken">Passed to the handlers.</param>
    Task PublishAsync(string type, object? data = null, CliEventAudience? audience = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to the events whose type matches <paramref name="pattern"/> (see <see cref="CliEventPattern"/>).
    /// Handlers receive every matching event regardless of its audience. Dispose the result to unsubscribe.
    /// </summary>
    IDisposable Subscribe(string pattern, Func<CliEvent, CancellationToken, Task> handler);
}
