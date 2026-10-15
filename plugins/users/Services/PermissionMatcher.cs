namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Permission string helpers. Permissions are <c>resource:action</c>; a <c>*</c> segment
/// matches that level and everything below (<c>*</c>, <c>users:*</c>). Case-insensitive.
/// Same semantics as <c>cliPermissionMatches</c> in <c>@qodalis/cli-core</c>.
/// </summary>
public static class PermissionMatcher
{
    public static bool Matches(string granted, string required) =>
        Qodalis.Cli.Security.CliPermissionMatcher.Matches(granted, required);

    public static bool HasPermission(IEnumerable<string>? granted, string required) =>
        Qodalis.Cli.Security.CliPermissionMatcher.HasPermission(granted, required);

    /// <summary>Entries of <paramref name="target"/> not covered by <paramref name="granted"/>.</summary>
    public static List<string> Missing(IEnumerable<string>? granted, IEnumerable<string> target) =>
        Qodalis.Cli.Security.CliPermissionMatcher.Missing(granted, target);

    /// <summary>True when every permission of <paramref name="target"/> is matched by <paramref name="granted"/>.</summary>
    public static bool Covers(IEnumerable<string>? granted, IEnumerable<string> target) =>
        Qodalis.Cli.Security.CliPermissionMatcher.Covers(granted, target);

    /// <summary>Union of permission lists, de-duplicated case-insensitively, first-seen order.</summary>
    public static List<string> Merge(IEnumerable<IEnumerable<string>?> lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var list in lists)
        {
            if (list == null) continue;
            foreach (var p in list)
            {
                var key = p?.Trim();
                if (!string.IsNullOrEmpty(key) && seen.Add(key))
                    result.Add(key);
            }
        }
        return result;
    }
}

/// <summary>
/// Built-in permission strings enforced by the users plugin endpoints.
/// </summary>
public static class UsersPermissions
{
    public const string UsersRead = "users:read";
    public const string UsersCreate = "users:create";
    public const string UsersUpdate = "users:update";
    public const string UsersDelete = "users:delete";
    public const string GroupsRead = "groups:read";
    public const string GroupsCreate = "groups:create";
    public const string GroupsUpdate = "groups:update";
    public const string GroupsDelete = "groups:delete";
    public const string SessionsRead = "sessions:read";
    public const string SessionsRevoke = "sessions:revoke";
    public const string AuditRead = "audit:read";
    public const string AuditClear = "audit:clear";
    public const string TokensRead = "tokens:read";
    public const string TokensRevoke = "tokens:revoke";
}
