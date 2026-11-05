using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Plugin.Logs.Seq;

/// <summary>
/// Writes every log entry to Seq as a CLEF event: the rendered message and its template, the level, the exception,
/// the logger category (<c>SourceContext</c>), the service (<c>Service</c>) and the entry's own values. Configure it
/// under <c>Logging:Seq</c> like any provider.
/// </summary>
[ProviderAlias("Seq")]
internal sealed class SeqLoggerProvider : ILoggerProvider
{
    private const string OriginalFormat = "{OriginalFormat}";

    private readonly SeqLogShipper _shipper;
    private readonly LogLevel _minimumLevel;

    public SeqLoggerProvider(SeqLogShipper shipper, SeqLogsOptions options, string serviceName)
    {
        _shipper = shipper;
        _minimumLevel = options.MinimumLevel;
        ServiceName = serviceName;
    }

    /// <summary>The <c>Service</c> every event carries.</summary>
    public string ServiceName { get; }

    public ILogger CreateLogger(string categoryName) => new SeqLogger(this, categoryName);

    public void Dispose()
    {
        // The shipper is a DI singleton: the container disposes it after the providers, which sends what is queued.
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    /// <summary>The CLEF event for one entry.</summary>
    internal string ToClef<TState>(
        DateTimeOffset timestamp, string category, LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("@t", timestamp.UtcDateTime.ToString("O"));
            json.WriteString("@m", formatter(state, exception));
            json.WriteString("@l", SeqLevel(level));
            if (exception != null) json.WriteString("@x", exception.ToString());

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var written = new HashSet<string>(StringComparer.Ordinal) { "SourceContext", "Service", "EventId" };
                foreach (var (key, value) in values)
                {
                    if (key == OriginalFormat)
                    {
                        if (value is string template) json.WriteString("@mt", template);
                        continue;
                    }
                    // CLEF reserves names starting with @; Seq reads @@name as the property @name.
                    var name = key.StartsWith('@') ? "@" + key : key;
                    if (string.IsNullOrEmpty(key) || !written.Add(name)) continue;
                    json.WritePropertyName(name);
                    WriteValue(json, value);
                }
            }

            if (eventId.Id != 0 || eventId.Name != null)
            {
                json.WriteStartObject("EventId");
                if (eventId.Id != 0) json.WriteNumber("Id", eventId.Id);
                if (eventId.Name != null) json.WriteString("Name", eventId.Name);
                json.WriteEndObject();
            }
            json.WriteString("SourceContext", category);
            json.WriteString("Service", ServiceName);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case string s: json.WriteStringValue(s); break;
            case bool b: json.WriteBooleanValue(b); break;
            case int or long or short or byte or sbyte or ushort or uint or ulong or decimal:
                json.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!); break;
            case double d when double.IsFinite(d): json.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): json.WriteNumberValue(f); break;
            case DateTime dt: json.WriteStringValue(dt.ToString("O")); break;
            case DateTimeOffset dto: json.WriteStringValue(dto.ToString("O")); break;
            case Enum e: json.WriteStringValue(e.ToString()); break;
            default:
                json.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    /// <summary>Seq's names for the levels.</summary>
    internal static string SeqLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => "Information",
    };

    private sealed class SeqLogger(SeqLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!provider.IsEnabled(logLevel)) return;
            try
            {
                provider._shipper.Enqueue(provider.ToClef(DateTimeOffset.UtcNow, category, logLevel, eventId, state, exception, formatter));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A value that cannot be written must not break the caller's code; the entry is lost.
            }
        }
    }
}
