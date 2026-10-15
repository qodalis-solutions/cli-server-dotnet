using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Security;

/// <summary>
/// The authenticated caller of a CLI server request or WebSocket, as established by an
/// authentication plugin (e.g. the users plugin). Absent when the server is unprotected.
/// </summary>
public sealed class CliPrincipal
{
    /// <summary><see cref="HttpContext.Items"/> key holding the principal.</summary>
    public const string ItemKey = "Qodalis.Cli.Principal";

    public CliPrincipal(string userId, string userName, IReadOnlyList<string> permissions, string? sessionId = null)
        : this(userId, userName, permissions, sessionId, null)
    {
    }

    /// <summary>Creates a principal authenticated by a session (<paramref name="sessionId"/>) or an API token (<paramref name="tokenId"/>).</summary>
    public CliPrincipal(string userId, string userName, IReadOnlyList<string> permissions, string? sessionId, string? tokenId)
    {
        UserId = userId;
        UserName = userName;
        Permissions = permissions;
        SessionId = sessionId;
        TokenId = tokenId;
    }

    public string UserId { get; }
    public string UserName { get; }
    public string? SessionId { get; }

    /// <summary>Id of the API token that authenticated the caller (no session), when applicable.</summary>
    public string? TokenId { get; }

    /// <summary>Effective permissions (<c>resource:action</c> with <c>*</c> wildcards).</summary>
    public IReadOnlyList<string> Permissions { get; }

    public bool Can(string permission) => CliPermissionMatcher.HasPermission(Permissions, permission);
}

/// <summary>HttpContext helpers for <see cref="CliPrincipal"/>.</summary>
public static class CliPrincipalHttpContextExtensions
{
    public static CliPrincipal? GetCliPrincipal(this HttpContext context) =>
        context.Items.TryGetValue(CliPrincipal.ItemKey, out var value) ? value as CliPrincipal : null;

    public static void SetCliPrincipal(this HttpContext context, CliPrincipal principal) =>
        context.Items[CliPrincipal.ItemKey] = principal;
}

/// <summary>
/// Permission matching. Permissions are <c>resource:action</c>; a trailing <c>*</c> segment matches
/// that level and everything below (<c>*</c>, <c>users:*</c>); a <c>*</c> in any other segment matches
/// exactly one segment (<c>*:read</c> matches <c>users:read</c> only). Case-insensitive. Same semantics as
/// <c>cliPermissionMatches</c> in <c>@qodalis/cli-core</c>.
/// </summary>
public static class CliPermissionMatcher
{
    public static bool Matches(string granted, string required)
    {
        var g = granted.Trim().ToLowerInvariant().Split(':');
        var r = required.Trim().ToLowerInvariant().Split(':');
        for (var i = 0; i < g.Length; i++)
        {
            // Only a trailing '*' grants everything below; elsewhere it matches one segment.
            if (g[i] == "*" && i == g.Length - 1) return true;
            if (i >= r.Length) return false;
            if (g[i] != "*" && g[i] != r[i]) return false;
        }
        return g.Length == r.Length;
    }

    public static bool HasPermission(IEnumerable<string>? granted, string required) =>
        granted?.Any(p => Matches(p, required)) == true;

    /// <summary>Permissions of <paramref name="required"/> not matched by any of <paramref name="granted"/>.</summary>
    public static List<string> Missing(IEnumerable<string>? granted, IEnumerable<string> required)
    {
        var list = granted?.ToList() ?? [];
        return required.Where(r => !HasPermission(list, r)).ToList();
    }

    /// <summary>True when every permission in <paramref name="target"/> is matched by <paramref name="granted"/>.</summary>
    public static bool Covers(IEnumerable<string>? granted, IEnumerable<string> target) =>
        Missing(granted, target).Count == 0;
}
