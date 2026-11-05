using System.Globalization;
using System.Text.Json;
using Qodalis.Cli.Abstractions.Logs;

namespace Qodalis.Cli.Plugin.Logs.Seq;

/// <summary>
/// The dashboard's log history from Seq (<c>/api/events</c>): every service that writes there, across restarts.
/// Seq does not count matches, so a page reports <see cref="LogPage.HasMore"/> instead of a total, and paging stops
/// <see cref="MaxDepth"/> entries back (narrow the filters or the time range to look further).
/// </summary>
internal sealed class SeqLogStore : ILogStore, IDisposable
{
    /// <summary>How far back offset paging reaches.</summary>
    internal const int MaxDepth = 10_000;

    private static readonly TimeSpan ServicesCacheDuration = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly string _serviceName;
    private readonly object _servicesLock = new();
    private (DateTimeOffset At, IReadOnlyList<string> Services)? _services;

    public SeqLogStore(SeqLogsOptions options, string serviceName, HttpMessageHandler? handler = null)
    {
        _serviceName = serviceName;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = options.BaseAddress;
        _http.Timeout = options.RequestTimeout;
        if (!string.IsNullOrEmpty(options.ApiKey)) _http.DefaultRequestHeaders.Add("X-Seq-ApiKey", options.ApiKey);
    }

    public string Name => "seq";

    public bool Persistent => true;

    public async Task<LogPage> QueryAsync(LogQueryOptions options, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(options.Limit, 1, LogQueryOptions.MaxLimit);
        var offset = Math.Max(options.Offset, 0);
        if (offset >= MaxDepth) return new LogPage();
        var count = Math.Min(offset + limit + 1, MaxDepth + 1);

        var query = new List<string> { $"count={count}", "render=true" };
        if (Filter(options) is { } filter) query.Add("filter=" + Uri.EscapeDataString(filter));
        if (options.From is { } from) query.Add("fromDateUtc=" + Uri.EscapeDataString(from.UtcDateTime.ToString("O")));
        if (options.To is { } to) query.Add("toDateUtc=" + Uri.EscapeDataString(to.UtcDateTime.ToString("O")));

        using var response = await _http.GetAsync("api/events?" + string.Join('&', query), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var events = document.RootElement.EnumerateArray().ToList();
        return new LogPage
        {
            Entries = events.Skip(offset).Take(limit).Select(ToRecord).ToList(),
            HasMore = events.Count > offset + limit && offset + limit < MaxDepth,
        };
    }

    public async Task<IReadOnlyList<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        lock (_servicesLock)
        {
            if (_services is { } cached && DateTimeOffset.UtcNow - cached.At < ServicesCacheDuration) return cached.Services;
        }

        IReadOnlyList<string> services;
        try
        {
            var q = Uri.EscapeDataString("select distinct(Service) as service from stream");
            using var response = await _http.GetAsync($"api/data?q={q}", cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            services = document.RootElement.GetProperty("Rows").EnumerateArray()
                .Select(row => row.EnumerateArray().FirstOrDefault())
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Append(_serviceName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The list only fills the dashboard's service filter: without Seq's answer, offer this service.
            return [_serviceName];
        }

        lock (_servicesLock) _services = (DateTimeOffset.UtcNow, services);
        return services;
    }

    /// <summary>The Seq filter expression for <paramref name="options"/>; null when nothing is filtered.</summary>
    internal static string? Filter(LogQueryOptions options)
    {
        var clauses = new List<string>();

        var levels = options.Levels.Select(LogLevels.Normalize).OfType<string>().Distinct().ToList();
        if (levels.Count > 0 && levels.Count < LogLevels.All.Count)
        {
            var seqLevels = levels.SelectMany(SeqLevels).Select(Literal);
            clauses.Add($"@Level in [{string.Join(", ", seqLevels)}]");
        }
        if (!string.IsNullOrEmpty(options.Search))
        {
            var text = Literal(options.Search);
            clauses.Add($"(Contains(@Message, {text}) ci or Contains(SourceContext, {text}) ci)");
        }
        if (!string.IsNullOrEmpty(options.Service)) clauses.Add($"Service = {Literal(options.Service)}");

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    /// <summary>The Seq levels of a dashboard level (Seq also accepts short and lower-case names writers send).</summary>
    private static IEnumerable<string> SeqLevels(string level) => level switch
    {
        LogLevels.Debug => ["Verbose", "Debug", "Trace", "verbose", "debug", "trace"],
        LogLevels.Info => ["Information", "Info", "information", "info"],
        LogLevels.Warn => ["Warning", "Warn", "warning", "warn"],
        _ => ["Error", "Fatal", "Critical", "error", "fatal", "critical"],
    };

    /// <summary>A Seq string literal: single quotes, doubled inside.</summary>
    internal static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private LogRecord ToRecord(JsonElement e)
    {
        string? Property(string name)
        {
            if (!e.TryGetProperty("Properties", out var properties)) return null;
            foreach (var p in properties.EnumerateArray())
            {
                if (p.GetProperty("Name").GetString() == name && p.TryGetProperty("Value", out var value))
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
            return null;
        }

        return new LogRecord
        {
            Timestamp = DateTimeOffset.Parse(e.GetProperty("Timestamp").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            Level = LogLevels.Normalize(e.TryGetProperty("Level", out var level) ? level.GetString() : null) ?? LogLevels.Info,
            Message = e.TryGetProperty("RenderedMessage", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()!
                : string.Empty,
            Source = Property("SourceContext") ?? string.Empty,
            Service = Property("Service"),
            Exception = e.TryGetProperty("Exception", out var exception) && exception.ValueKind == JsonValueKind.String
                ? exception.GetString()
                : null,
        };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("Error", out var error)) detail = ": " + error.GetString();
        }
        catch (JsonException)
        {
            // Not Seq's JSON error: the status says enough.
        }
        throw new HttpRequestException($"Seq answered {(int)response.StatusCode} {response.ReasonPhrase}{detail}", null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
