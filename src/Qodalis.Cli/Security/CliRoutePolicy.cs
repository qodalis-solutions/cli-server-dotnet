namespace Qodalis.Cli.Security;

/// <summary>Access rule for a CLI server route (see <see cref="CliRoutePolicy"/>).</summary>
/// <param name="IsCliRoute">The path belongs to the CLI server namespace.</param>
/// <param name="IsPublic">Reachable without authentication.</param>
/// <param name="Permission">Required permission, or null for "authenticated".</param>
/// <param name="IsWebSocket">A WebSocket endpoint.</param>
public sealed record CliRouteRule(bool IsCliRoute, bool IsPublic, string? Permission, bool IsWebSocket)
{
    public static readonly CliRouteRule NotCli = new(false, true, null, false);
}

/// <summary>
/// Permission table for CLI server routes when the server is protected
/// (users plugin <c>ProtectServer</c>). Users-plugin routes are authenticated here and
/// authorized by their own controllers.
/// </summary>
public static class CliRoutePolicy
{
    public const string CommandsExecute = "commands:execute";
    public const string FilesRead = "files:read";
    public const string FilesWrite = "files:write";
    public const string DataQuery = "data:query";
    public const string JobsRead = "jobs:read";
    public const string JobsManage = "jobs:manage";
    public const string LogsRead = "logs:read";
    public const string ShellExecute = "shell:execute";
    public const string AdminAccess = "admin:access";

    private static readonly string[] CliPrefixes = ["/api/v1/qcli", "/api/v2/qcli", "/api/qcli", "/api/cli", "/ws/v1/qcli", "/ws/qcli"];

    private static readonly string[] PublicGet =
    [
        "/api/qcli/versions", "/api/cli/versions", "/api/v1/qcli/version", "/api/v1/qcli/capabilities",
        "/api/qcli/health", "/api/v1/qcli/health", "/api/cli/health",
    ];

    private static readonly string[] PublicPost = ["/api/v1/qcli/sessions", "/api/v1/qcli/auth/login"];

    private static readonly string[] AdminPrefixes =
    [
        "/api/v1/qcli/status", "/api/v1/qcli/plugins", "/api/v1/qcli/config", "/api/v1/qcli/logs",
        "/api/v1/qcli/ws/clients", "/api/v1/qcli/auth/me",
    ];

    private static readonly string[] FsReadActions = ["ls", "cat", "stat", "download"];

    public static bool IsCliPath(string path) => CliPrefixes.Any(p => IsUnder(path, p));

    public static CliRouteRule Resolve(string method, string? rawPath)
    {
        var path = (rawPath ?? string.Empty).TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0 || !IsCliPath(path)) return CliRouteRule.NotCli;

        var isGet = HttpMethodsEquals(method, "GET") || HttpMethodsEquals(method, "HEAD");

        if (path.StartsWith("/ws/", StringComparison.Ordinal))
        {
            if (path is "/ws/v1/qcli/events" or "/ws/qcli/events") return new(true, false, null, true);
            if (path is "/ws/v1/qcli/logs" or "/ws/qcli/logs") return new(true, false, LogsRead, true);
            if (path is "/ws/v1/qcli/shell" or "/ws/qcli/shell" or "/ws/v1/qcli" or "/ws/qcli")
                return new(true, false, ShellExecute, true);
            return new(true, false, null, true);
        }

        if (isGet && PublicGet.Contains(path)) return new(true, true, null, false);
        if (HttpMethodsEquals(method, "POST") && PublicPost.Contains(path)) return new(true, true, null, false);
        if (HttpMethodsEquals(method, "OPTIONS")) return new(true, true, null, false);

        if (path is "/api/v1/qcli/execute" or "/api/v1/qcli/execute/stream") return new(true, false, CommandsExecute, false);
        if (IsUnder(path, "/api/qcli/fs"))
        {
            var action = path.Length > "/api/qcli/fs/".Length ? path["/api/qcli/fs/".Length..].Split('/')[0] : string.Empty;
            return new(true, false, isGet && FsReadActions.Contains(action) ? FilesRead : FilesWrite, false);
        }
        if (IsUnder(path, "/api/qcli/data-explorer")) return new(true, false, DataQuery, false);
        if (IsUnder(path, "/api/v1/qcli/jobs")) return new(true, false, isGet ? JobsRead : JobsManage, false);
        if (AdminPrefixes.Any(p => IsUnder(path, p))) return new(true, false, AdminAccess, false);

        return new(true, false, null, false);
    }

    private static bool HttpMethodsEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
