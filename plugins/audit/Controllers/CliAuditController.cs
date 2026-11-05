using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Audit.Controllers;

/// <summary>
/// The audit log API: <c>GET</c> lists entries (<c>audit:read</c> on a protected server), <c>DELETE</c> clears the
/// log (<c>audit:clear</c>). The admin dashboard's Audit page and the CLI's <c>audit</c> command read it.
/// </summary>
// No [ApiController]: refusals answer { error } like the rest of the users and audit APIs, not ProblemDetails
[Route("api/v1/qcli/audit")]
public class CliAuditController : ControllerBase
{
    /// <summary>Entries returned when the request names no limit.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The most entries one request returns, so a database store never loads the whole log.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The event recorded after the log was cleared.</summary>
    public const string ClearedEvent = "audit_cleared";

    private readonly IAuditStore _store;

    /// <summary>Initializes a new instance of the <see cref="CliAuditController"/> class.</summary>
    public CliAuditController(IAuditStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Entries newest first, filtered by <paramref name="event"/>, <paramref name="username"/> (the user an entry is
    /// about) and <paramref name="actor"/> (who did it), all exact and case-insensitive, and by time
    /// (<paramref name="from"/> &lt;= timestamp &lt; <paramref name="to"/>, each ISO 8601 or epoch milliseconds); paged
    /// with <paramref name="limit"/> (default 100, at most 1000) and <paramref name="offset"/>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetEntries(
        [FromQuery(Name = "event")] string? @event,
        [FromQuery] string? username,
        [FromQuery] string? actor,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null)
    {
        if (!ModelState.IsValid || limit < 0 || offset < 0)
            return BadRequest(new { error = "limit and offset must be integers >= 0" });
        if (!TryParseTime(from, out var fromTime))
            return BadRequest(new { error = "from must be an ISO 8601 time or epoch milliseconds" });
        if (!TryParseTime(to, out var toTime))
            return BadRequest(new { error = "to must be an ISO 8601 time or epoch milliseconds" });

        return Ok(await _store.GetEntriesAsync(new AuditQueryOptions
        {
            Event = NullIfEmpty(@event),
            Username = NullIfEmpty(username),
            Actor = NullIfEmpty(actor),
            From = fromTime,
            To = toTime,
            Limit = Math.Min(limit ?? DefaultLimit, MaxLimit),
            Offset = offset,
        }));
    }

    /// <summary>Clears the log, then records who cleared it (<c>audit_cleared</c>).</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Clear()
    {
        await _store.ClearAsync();

        var caller = HttpContext.GetCliPrincipal();
        await _store.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = ClearedEvent,
            Username = caller?.UserName ?? string.Empty,
            UserId = caller?.UserId,
            Details = "audit log cleared",
            Actor = caller?.UserName,
            ActorId = caller?.UserId,
            Metadata = new AuditMetadata { RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString() },
        });
        return NoContent();
    }

    /// <summary>Parses epoch milliseconds or an ISO 8601 time (without an offset it is UTC); empty is null.</summary>
    internal static bool TryParseTime(string? value, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        time = parsed;
        return true;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
