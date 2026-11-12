using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Link requests in memory: a terminal (<c>server add</c> in the web CLI) asks to be linked and a signed-in
/// user approves it in a browser, which creates the user's API token for the terminal (the OAuth device flow;
/// spec: web-cli <c>docs/superpowers/specs/2026-11-12-server-linking-design.md</c>).
/// </summary>
public sealed partial class LinkService
{
    /// <summary>Seconds a link request waits for its approval.</summary>
    public const int ExpiresInSeconds = 600;

    /// <summary>Seconds between two polls of the terminal.</summary>
    public const int PollIntervalSeconds = 2;

    /// <summary>Pending requests kept at most; the oldest is dropped when full.</summary>
    public const int MaxPendingLinks = 100;

    /// <summary>Link requests created per client address and minute at most.</summary>
    public const int CreateRateLimit = 10;

    /// <summary>The approval page of the admin dashboard.</summary>
    public const string DefaultApprovalUrl = "/qcli/admin/link";

    public const string NotFoundMessage = "link request not found or expired";

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;
    private const int MaxClientLength = 100;
    private const int TokenNameMax = 64;
    private const int MaxOriginLength = 200;
    private const int MaxNameAttempts = 20;
    private const long RateWindowMs = 60_000;
    private const int MaxTrackedAddresses = 50_000;

    private enum LinkStatus { Pending, Approving, Approved, Denied }

    private sealed class LinkRequest
    {
        public required string LinkId { get; init; }
        /// <summary>sha256 of the normalised user code.</summary>
        public required byte[] CodeHash { get; init; }
        public required string UserCode { get; init; }
        public required string Client { get; init; }
        public List<string>? Permissions { get; init; }
        public string? Origin { get; init; }
        public string? RemoteAddress { get; init; }
        public long CreatedAt { get; init; }
        public long ExpiresAt { get; init; }
        public long Sequence { get; init; }
        public LinkStatus Status { get; set; }
        public string? Token { get; set; }
        public string? UserName { get; set; }
        public long? TokenExpiresAt { get; set; }
    }

    private readonly Dictionary<string, LinkRequest> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> _attempts = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly AuthService _auth;
    private readonly TimeProvider _time;
    private readonly string _approvalUrl;
    private long _sequence;

    public LinkService(AuthService auth, UsersModuleOptions options, TimeProvider? timeProvider = null)
    {
        _auth = auth;
        _time = timeProvider ?? TimeProvider.System;
        _approvalUrl = options.LinkApprovalUrl ?? DefaultApprovalUrl;
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    [GeneratedRegex(@"[^A-Za-z0-9._ -]")]
    private static partial Regex TokenNameInvalidChars();

    [GeneratedRegex(@"[\s-]")]
    private static partial Regex CodeSeparators();

    [GeneratedRegex(@"[\u0000-\u001f\u007f]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"^(https?://[^\s]+|/(?![/\\])[^\s]*)$")]
    private static partial Regex ApprovalUrlRegex();

    /// <summary>True for an http(s) URL or a root-relative path (not <c>//host</c>).</summary>
    public static bool IsValidApprovalUrl(string? url) => url != null && ApprovalUrlRegex().IsMatch(url);

    /// <summary>Upper case without dashes and spaces (<c>abcd-2345</c> → <c>ABCD2345</c>).</summary>
    public static string NormalizeUserCode(string code) => CodeSeparators().Replace(code, string.Empty).ToUpperInvariant();

    /// <summary>A token name from a client name: <c>link &lt;client&gt;</c>, <c>[A-Za-z0-9._ -]</c> only, at most 64 characters.</summary>
    public static string TokenName(string client, int suffix = 0)
    {
        var tail = suffix > 0 ? $" {suffix + 1}" : string.Empty;
        var name = TokenNameInvalidChars().Replace($"link {client}", "-").Trim();
        var max = TokenNameMax - tail.Length;
        if (name.Length > max) name = name[..max];
        return name.TrimEnd() + tail;
    }

    /// <summary>Where the approval page is: <c>{code}</c> replaced, otherwise <c>?code=</c> appended.</summary>
    public static string VerificationUrl(string approvalUrl, string userCode)
    {
        var code = Uri.EscapeDataString(userCode);
        if (approvalUrl.Contains("{code}", StringComparison.Ordinal)) return approvalUrl.Replace("{code}", code);
        return $"{approvalUrl}{(approvalUrl.Contains('?') ? '&' : '?')}code={code}";
    }

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeUserCode(code)));

    // -------------------------------------------------------------------------
    // Requests
    // -------------------------------------------------------------------------

    /// <summary>Records a creation attempt of <paramref name="clientAddress"/>; false when it made 10 in the last minute.</summary>
    public bool TryAcquire(string clientAddress)
    {
        var now = Now();
        lock (_lock)
        {
            if (_attempts.Count > MaxTrackedAddresses) PruneAttemptsLocked(now);
            if (!_attempts.TryGetValue(clientAddress, out var queue))
                _attempts[clientAddress] = queue = new Queue<long>();
            while (queue.Count > 0 && queue.Peek() <= now - RateWindowMs) queue.Dequeue();
            if (queue.Count >= CreateRateLimit) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary><c>POST /link</c>: validates <c>{ client, permissions? }</c> (400) and creates a pending request.</summary>
    public LinkCreated Create(JsonElement body, string? origin, string? remoteAddress)
    {
        var client = body.ValueKind == JsonValueKind.Object &&
                     body.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!.Trim()
            : string.Empty;
        if (client.Length is < 1 or > MaxClientLength || ControlChars().IsMatch(client))
            throw UsersApiException.BadRequest($"client must be 1-{MaxClientLength} characters");

        List<string>? permissions = null;
        if (body.TryGetProperty("permissions", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() == 0 ||
                p.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || e.GetString()!.Length == 0))
                throw UsersApiException.BadRequest("permissions must be a non-empty array of strings");
            permissions = UsersValidation.Permissions(p.EnumerateArray().Select(e => e.GetString()!).ToList());
        }

        var code = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++) code.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        var raw = code.ToString();
        var userCode = $"{raw[..4]}-{raw[4..]}";
        var linkId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = Now();

        lock (_lock)
        {
            SweepLocked(now);
            while (_links.Count >= MaxPendingLinks)
                _links.Remove(_links.Values.MinBy(l => l.Sequence)!.LinkId);
            _links[linkId] = new LinkRequest
            {
                LinkId = linkId,
                CodeHash = HashCode(raw),
                UserCode = userCode,
                Client = client,
                Permissions = permissions,
                Origin = string.IsNullOrEmpty(origin) ? null : origin.Length > MaxOriginLength ? origin[..MaxOriginLength] : origin,
                RemoteAddress = string.IsNullOrEmpty(remoteAddress) ? null : remoteAddress,
                CreatedAt = now,
                ExpiresAt = now + ExpiresInSeconds * 1000L,
                Sequence = ++_sequence,
                Status = LinkStatus.Pending,
            };
        }

        return new LinkCreated
        {
            LinkId = linkId,
            UserCode = userCode,
            VerificationUrl = VerificationUrl(_approvalUrl, userCode),
            ExpiresIn = ExpiresInSeconds,
            Interval = PollIntervalSeconds,
        };
    }

    /// <summary>
    /// <c>GET /link/{linkId}</c>: <c>pending</c>; <c>approved</c> with the token or <c>denied</c> once, after which
    /// the request is gone; null for unknown, expired or collected requests.
    /// </summary>
    public LinkPoll? Poll(string linkId)
    {
        lock (_lock)
        {
            SweepLocked(Now());
            if (!_links.TryGetValue(linkId, out var link)) return null;
            switch (link.Status)
            {
                case LinkStatus.Approved:
                    _links.Remove(linkId);
                    return new LinkPollApproved
                    {
                        Status = "approved",
                        Token = link.Token!,
                        UserName = link.UserName!,
                        ExpiresAt = link.TokenExpiresAt,
                    };
                case LinkStatus.Denied:
                    _links.Remove(linkId);
                    return new LinkPoll { Status = "denied" };
                default:
                    return new LinkPoll { Status = "pending" };
            }
        }
    }

    /// <summary>The pending request with this user code (constant-time compare of the code hashes).</summary>
    private LinkRequest? FindPendingLocked(string userCode)
    {
        SweepLocked(Now());
        var hash = HashCode(userCode);
        LinkRequest? found = null;
        foreach (var link in _links.Values)
        {
            if (CryptographicOperations.FixedTimeEquals(link.CodeHash, hash) && link.Status == LinkStatus.Pending)
                found = link;
        }
        return found;
    }

    /// <summary><c>GET /link/code/{userCode}</c>: what the approval page shows (404 when unknown or expired).</summary>
    public LinkView View(string userCode)
    {
        lock (_lock)
        {
            var link = FindPendingLocked(userCode) ?? throw UsersApiException.NotFound(NotFoundMessage);
            return new LinkView
            {
                UserCode = link.UserCode,
                Client = link.Client,
                Origin = link.Origin,
                RemoteAddress = link.RemoteAddress,
                Permissions = link.Permissions == null ? null : [.. link.Permissions],
                CreatedAt = link.CreatedAt,
                ExpiresAt = link.ExpiresAt,
            };
        }
    }

    /// <summary>
    /// <c>POST /link/code/{userCode}/approve</c>: creates the approver's API token for the terminal through
    /// <see cref="AuthService.CreateApiTokenAsync"/> (session only, scope within the approver's permissions,
    /// the token limit and the default expiry), named <c>link &lt;client&gt;</c> with a number when the name is taken.
    /// Single-use: 404 afterwards. Audits <c>link_approved</c>.
    /// </summary>
    public async Task<LinkApproved> ApproveAsync(UsersAuthContext caller, string userCode)
    {
        LinkRequest link;
        lock (_lock)
        {
            link = FindPendingLocked(userCode) ?? throw UsersApiException.NotFound(NotFoundMessage);
            // Claimed while the token is created: a parallel approval or denial answers 404
            link.Status = LinkStatus.Approving;
        }

        (string Token, ApiToken ApiToken) created;
        try
        {
            created = await CreateTokenAsync(caller, link);
        }
        catch
        {
            lock (_lock)
            {
                if (link.Status == LinkStatus.Approving) link.Status = LinkStatus.Pending;
            }
            throw;
        }

        bool collectable;
        lock (_lock)
        {
            SweepLocked(Now());
            collectable = _links.ContainsKey(link.LinkId);
            if (collectable)
            {
                link.Status = LinkStatus.Approved;
                link.Token = created.Token;
                link.UserName = caller.User.Name;
                link.TokenExpiresAt = created.ApiToken.ExpiresAt;
            }
        }
        if (!collectable)
        {
            // Expired (or dropped) while the token was created: the terminal can no longer collect it,
            // so the token is revoked instead of staying behind unused
            await _auth.RevokeApiTokenAsync(caller, created.ApiToken.Id, canRevokeAny: false);
            throw UsersApiException.NotFound(NotFoundMessage);
        }

        await _auth.AuditAsync(AuditEvents.LinkApproved, caller.User.Name,
            $"{link.Client} ({created.ApiToken.Name})", caller.User.Name,
            new AuditMetadata { Client = link.Client, Origin = link.Origin, TokenId = created.ApiToken.Id },
            caller.User.Id, caller.User.Id);
        return new LinkApproved { TokenId = created.ApiToken.Id, TokenName = created.ApiToken.Name };
    }

    private async Task<(string Token, ApiToken ApiToken)> CreateTokenAsync(UsersAuthContext caller, LinkRequest link)
    {
        for (var suffix = 0; ; suffix++)
        {
            try
            {
                return await _auth.CreateApiTokenAsync(caller, new CreateApiTokenRequest
                {
                    Name = TokenName(link.Client, suffix),
                    Permissions = link.Permissions == null ? null : [.. link.Permissions],
                });
            }
            catch (UsersApiException ex) when (ex.StatusCode == 409 && suffix < MaxNameAttempts)
            {
                // The name is taken: try "link <client> 2", "… 3", …
            }
        }
    }

    /// <summary><c>POST /link/code/{userCode}/deny</c>: single-use (404 afterwards). Audits <c>link_denied</c>.</summary>
    public async Task DenyAsync(UsersAuthContext caller, string userCode)
    {
        LinkRequest link;
        lock (_lock)
        {
            link = FindPendingLocked(userCode) ?? throw UsersApiException.NotFound(NotFoundMessage);
            link.Status = LinkStatus.Denied;
        }
        await _auth.AuditAsync(AuditEvents.LinkDenied, caller.User.Name, link.Client, caller.User.Name,
            new AuditMetadata { Client = link.Client, Origin = link.Origin }, caller.User.Id, caller.User.Id);
    }

    /// <summary>
    /// Drops expired requests and stale rate-limit entries (run by the plugin's cleanup timer). An approved token
    /// that was not collected in time stays listed with the user's tokens.
    /// </summary>
    public void Sweep()
    {
        var now = Now();
        lock (_lock)
        {
            SweepLocked(now);
            PruneAttemptsLocked(now);
        }
    }

    private void PruneAttemptsLocked(long now)
    {
        foreach (var (address, queue) in _attempts.ToList())
        {
            while (queue.Count > 0 && queue.Peek() <= now - RateWindowMs) queue.Dequeue();
            if (queue.Count == 0) _attempts.Remove(address);
        }
    }

    private void SweepLocked(long now)
    {
        foreach (var link in _links.Values.Where(l => l.ExpiresAt <= now).ToList())
            _links.Remove(link.LinkId);
    }

    /// <summary>Requests kept in memory (tests).</summary>
    internal int Count
    {
        get { lock (_lock) return _links.Count; }
    }
}
