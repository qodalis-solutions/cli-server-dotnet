using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Events;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default <see cref="ICliEventBus"/>, registered as a singleton by <c>AddCli</c>. Handlers run one after the other in
/// subscription order; a handler's exception is logged and delivery continues with the next one.
/// </summary>
public sealed class CliEventBus : ICliEventBus
{
    /// <summary>JSON options of the event fields (camelCase, like the rest of the API).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _lock = new();
    private Subscription[] _subscriptions = [];
    private readonly ILogger<CliEventBus> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of <see cref="CliEventBus"/>.</summary>
    /// <param name="logger">Logs the handlers' exceptions.</param>
    /// <param name="timeProvider">The clock of events published without a timestamp.</param>
    public CliEventBus(ILogger<CliEventBus>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<CliEventBus>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task PublishAsync(string type, object? data = null, CliEventAudience? audience = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var fields = ToFields(data);
        var timestamp = TakeTimestamp(fields) ?? _time.GetUtcNow().UtcDateTime;
        fields.Remove("type");

        return PublishAsync(new CliEvent
        {
            Type = type,
            Timestamp = timestamp,
            Data = fields,
            Audience = audience ?? CliEventAudience.Everyone,
        }, cancellationToken);
    }

    /// <summary>Publishes a built event to the subscribers whose pattern matches its type.</summary>
    public async Task PublishAsync(CliEvent cliEvent, CancellationToken cancellationToken = default)
    {
        foreach (var subscription in Volatile.Read(ref _subscriptions))
        {
            if (!CliEventPattern.Matches(cliEvent.Type, subscription.Pattern)) continue;
            try
            {
                await subscription.Handler(cliEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event handler ({Pattern}) failed on {Type}", subscription.Pattern, cliEvent.Type);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string pattern, Func<CliEvent, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, pattern, handler);
        lock (_lock) _subscriptions = [.. _subscriptions, subscription];
        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_lock) _subscriptions = _subscriptions.Where(s => s != subscription).ToArray();
    }

    /// <summary>
    /// The wire message of an event: one flat JSON object with <c>type</c>, <c>timestamp</c> (ISO 8601 UTC) and the
    /// event's fields.
    /// </summary>
    public static string Serialize(CliEvent cliEvent) => Encoding.UTF8.GetString(SerializeToUtf8Bytes(cliEvent));

    /// <summary>The wire message of an event, UTF-8 encoded (see <see cref="Serialize"/>).</summary>
    public static byte[] SerializeToUtf8Bytes(CliEvent cliEvent)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", cliEvent.Type);
            writer.WriteString("timestamp", FormatTimestamp(cliEvent.Timestamp));
            foreach (var (key, value) in cliEvent.Data)
            {
                if (key is "type" or "timestamp") continue;
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), JsonOptions);
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>ISO 8601 UTC with milliseconds, e.g. <c>2026-11-06T10:15:00.000Z</c>.</summary>
    public static string FormatTimestamp(DateTime timestamp) =>
        (timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The publisher's fields: a dictionary as given, any other object by its JSON (camelCase) properties, with
    /// primitive values as CLR values (string, long, double, bool) and objects or arrays as <see cref="JsonElement"/>s.
    /// </summary>
    private static Dictionary<string, object?> ToFields(object? data)
    {
        switch (data)
        {
            case null:
                return new Dictionary<string, object?>();
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return pairs.ToDictionary(p => p.Key, p => p.Value);
            case IDictionary dictionary:
            {
                var fields = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                    fields[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = entry.Value;
                return fields;
            }
        }

        var element = JsonSerializer.SerializeToElement(data, data.GetType(), JsonOptions);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Event data must be an object or a dictionary", nameof(data));
        return element.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value));
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.Clone(),
    };

    /// <summary>Removes a publisher-set <c>timestamp</c> (DateTime, DateTimeOffset, epoch milliseconds or ISO string).</summary>
    private static DateTime? TakeTimestamp(Dictionary<string, object?> fields)
    {
        if (!fields.Remove("timestamp", out var value)) return null;
        return value switch
        {
            DateTime d => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc),
            DateTimeOffset o => o.UtcDateTime,
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) => o.UtcDateTime,
            _ => null,
        };
    }

    private sealed class Subscription(CliEventBus bus, string pattern, Func<CliEvent, CancellationToken, Task> handler)
        : IDisposable
    {
        public string Pattern { get; } = pattern;
        public Func<CliEvent, CancellationToken, Task> Handler { get; } = handler;

        public void Dispose() => bus.Unsubscribe(this);
    }
}
