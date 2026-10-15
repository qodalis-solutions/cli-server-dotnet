using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Security;

/// <summary>
/// Server-wide security options for the CLI server (configured through <c>CliBuilder</c>).
/// </summary>
public sealed class CliServerOptions
{
    /// <summary>
    /// Enables the interactive shell WebSockets (<c>/ws/v1/qcli/shell</c>, <c>/ws/qcli/shell</c>,
    /// <c>/ws/v1/qcli</c>, <c>/ws/qcli</c>). Default <c>false</c>.
    /// </summary>
    public bool EnableShell { get; set; }

    /// <summary>Shell access, limits and environment (see <see cref="CliShellOptions"/>).</summary>
    public CliShellOptions Shell { get; } = new();

    /// <summary>
    /// Origins allowed for CORS and WebSocket upgrades (e.g. <c>http://localhost:4300</c>;
    /// <c>http://127.0.0.1:*</c> matches any port). Empty = CORS not configured by the library
    /// and no Origin restriction on WebSockets.
    /// </summary>
    public List<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// When true (set by an authentication plugin such as the users plugin with
    /// <c>ProtectServer</c>), every CLI route and socket requires an authenticated
    /// <see cref="CliPrincipal"/> except the public routes (fail closed).
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>
    /// MCP endpoint options (<c>POST /api/qcli/mcp</c>); <c>null</c> = disabled (default).
    /// Set with <c>CliBuilder.AddMcp()</c>.
    /// </summary>
    public Qodalis.Cli.Mcp.McpOptions? Mcp { get; set; }

    /// <summary>
    /// True when <paramref name="origin"/> is the request's own origin (same-origin requests, such as
    /// the bundled admin dashboard, are never cross-site and are always allowed) or is listed in
    /// <see cref="AllowedOrigins"/>.
    /// </summary>
    public bool IsOriginAllowed(string? origin, HttpRequest request)
        => IsSameOrigin(origin, request) || IsOriginAllowed(origin);

    /// <summary>
    /// True when <paramref name="origin"/> equals <c>Request.Scheme://Request.Host</c> (honours
    /// forwarded headers when the host configured <c>ForwardedHeaders</c>). Scheme and host are compared
    /// case-insensitively and default ports (http:80, https:443) are normalized.
    /// </summary>
    public static bool IsSameOrigin(string? origin, HttpRequest request)
    {
        if (string.IsNullOrEmpty(origin) || !request.Host.HasValue || string.IsNullOrEmpty(request.Scheme))
            return false;
        if (!Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var o)) return false;
        if (o.AbsolutePath != "/" || !string.IsNullOrEmpty(o.Query) || !string.IsNullOrEmpty(o.Fragment)) return false;

        var scheme = request.Scheme.ToLowerInvariant() switch
        {
            "ws" => "http",
            "wss" => "https",
            var s => s,
        };
        if (!Uri.TryCreate($"{scheme}://{request.Host.ToUriComponent()}", UriKind.Absolute, out var self))
            return false;

        return string.Equals(o.Scheme, self.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.IdnHost, self.IdnHost, StringComparison.OrdinalIgnoreCase)
            && o.Port == self.Port;
    }

    /// <summary>True when <paramref name="origin"/> is listed in <see cref="AllowedOrigins"/>.</summary>
    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        var o = origin.TrimEnd('/');
        foreach (var allowed in AllowedOrigins)
        {
            var a = allowed.TrimEnd('/');
            if (string.Equals(a, o, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.EndsWith(":*", StringComparison.Ordinal))
            {
                var prefix = a[..^1]; // keep the colon
                if (o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    o.Length > prefix.Length && o[prefix.Length..].All(char.IsDigit))
                    return true;
                if (string.Equals(o, a[..^2], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
