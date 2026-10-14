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
    {
        UserId = userId;
        UserName = userName;
        Permissions = permissions;
        SessionId = sessionId;
    }

    public string UserId { get; }
    public string UserName { get; }
    public string? SessionId { get; }

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
/// Permission matching. Permissions are <c>resource:action</c>; a <c>*</c> segment matches that
/// level and everything below (<c>*</c>, <c>users:*</c>). Case-insensitive. Same semantics as
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
            if (g[i] == "*") return true;
            if (i >= r.Length || g[i] != r[i]) return false;
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
