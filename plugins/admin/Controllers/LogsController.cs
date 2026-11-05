using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Insights;
using Qodalis.Cli.Abstractions.Logs;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller for querying server log entries from the <see cref="ILogStore"/>: the latest entries of this
/// process by default, or a persistent store such as Seq.
/// </summary>
[Route("api/v1/qcli/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsController"/> class.
    /// </summary>
    public LogsController(ILogStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Queries log entries, newest first.
    /// </summary>
    /// <param name="level">Optional level, or comma-separated levels (<c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>).</param>
    /// <param name="search">Optional text to search in messages and sources.</param>
    /// <param name="service">Optional service that wrote the entries.</param>
    /// <param name="from">Optional ISO 8601 time: entries written at or after it.</param>
    /// <param name="to">Optional ISO 8601 time: entries written before it.</param>
    /// <param name="limit">Maximum number of entries to return (100 by default, at most 500).</param>
    /// <param name="offset">Number of entries to skip for pagination.</param>
    /// <param name="cancellationToken">Cancelled when the request is aborted.</param>
    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] string? level = null,
        [FromQuery] string? search = null,
        [FromQuery] string? service = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) limit = 100;
        if (offset < 0) offset = 0;

        if (!TryParseLevels(level, out var levels, out var levelError)) return BadRequest(new { error = levelError });

        if (!TryParseTime(from, out var fromTime)) return BadRequest(new { error = "from must be an ISO 8601 time." });
        if (!TryParseTime(to, out var toTime)) return BadRequest(new { error = "to must be an ISO 8601 time." });

        var options = new LogQueryOptions
        {
            Levels = levels,
            Search = string.IsNullOrWhiteSpace(search) ? null : search,
            Service = string.IsNullOrWhiteSpace(service) ? null : service,
            From = fromTime,
            To = toTime,
            Limit = Math.Min(limit, LogQueryOptions.MaxLimit),
            Offset = offset,
        };
        LogPage page;
        IReadOnlyList<string> services;
        try
        {
            page = await _store.QueryAsync(options, cancellationToken);
            services = await _store.GetServicesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"The log store ({_store.Name}) is unavailable: {ex.Message}" });
        }

        return Ok(new
        {
            entries = page.Entries.Select(e => new
            {
                timestamp = e.Timestamp.UtcDateTime.ToString("o"),
                level = e.Level,
                message = e.Message,
                source = e.Source,
                service = e.Service,
                exception = e.Exception,
            }),
            total = page.Total,
            limit = options.Limit,
            offset,
            hasMore = page.HasMore,
            store = _store.Name,
            persistent = _store.Persistent,
            services,
        });
    }

    /// <summary>
    /// Counts the entries per level (<c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>) in <paramref name="buckets"/>
    /// equal columns (default 24, 1..200) of <c>[from, to)</c> (ISO 8601; default the last 24 hours), with the filters of
    /// <see cref="GetLogs"/>. The in-memory history counts its buffer; Seq counts in Seq.
    /// </summary>
    /// <param name="from">Optional ISO 8601 start (inclusive); default 24 hours before <paramref name="to"/>.</param>
    /// <param name="to">Optional ISO 8601 end (exclusive); default now.</param>
    /// <param name="buckets">The number of columns.</param>
    /// <param name="level">Optional level, or comma-separated levels.</param>
    /// <param name="search">Optional text to search in messages and sources.</param>
    /// <param name="service">Optional service that wrote the entries.</param>
    /// <param name="cancellationToken">Cancelled when the request is aborted.</param>
    [HttpGet("histogram")]
    public async Task<IActionResult> GetHistogram(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? buckets = null,
        [FromQuery] string? level = null,
        [FromQuery] string? search = null,
        [FromQuery] string? service = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseLevels(level, out var levels, out var levelError)) return BadRequest(new { error = levelError });
        if (!TimeBuckets.TryParse(from, to, buckets, out var range, out var rangeError, "from", "to"))
            return BadRequest(new { error = rangeError });

        LogHistogram histogram;
        try
        {
            histogram = await _store.HistogramAsync(new LogHistogramOptions
            {
                Range = range!,
                Filter = new LogQueryOptions
                {
                    Levels = levels,
                    Search = string.IsNullOrWhiteSpace(search) ? null : search,
                    Service = string.IsNullOrWhiteSpace(service) ? null : service,
                },
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"The log store ({_store.Name}) is unavailable: {ex.Message}" });
        }

        return Ok(new
        {
            from = TimeBuckets.Format(range!.Since),
            to = TimeBuckets.Format(range.Until),
            bucketMs = range.BucketMs,
            buckets = histogram.Buckets.Select((counts, i) =>
            {
                var bucket = new Dictionary<string, object> { ["start"] = TimeBuckets.Format(range.StartOf(i)) };
                foreach (var (name, count) in counts.ToDictionary()) bucket[name] = count;
                return bucket;
            }),
            totals = histogram.Totals.ToDictionary(),
            store = _store.Name,
        });
    }

    private static bool TryParseLevels(string? level, out List<string> levels, out string? error)
    {
        levels = [];
        error = null;
        foreach (var name in (level ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LogLevels.Normalize(name) is not { } normalized)
            {
                error = $"Unknown level '{name}': use {string.Join(", ", LogLevels.All)}.";
                return false;
            }
            if (!levels.Contains(normalized)) levels.Add(normalized);
        }
        return true;
    }

    private static bool TryParseTime(string? value, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        time = parsed;
        return true;
    }
}
