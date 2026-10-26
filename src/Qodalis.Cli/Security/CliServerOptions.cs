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
    /// Host names this server answers to on its CLI routes and WebSockets (DNS-rebinding protection).
    /// <c>localhost</c>, <c>*.localhost</c> and IP literals are always allowed; entries match exactly
    /// (case-insensitive, port ignored) or as a wildcard <c>*.example.com</c> (subdomains only);
    /// <c>"*"</c> disables the check. <c>null</c> (default) = enforce only while the server is unprotected
    /// (<see cref="RequireAuthentication"/> off), since rebinding cannot carry credentials; once set, the
    /// check is always enforced. Requests from other hosts answer <c>421 Misdirected Request</c>.
    /// Complementary to ASP.NET Core's <c>HostFiltering</c> middleware, which covers the whole app when configured.
    /// </summary>
    public List<string>? AllowedHosts { get; set; }

    /// <summary>
    /// True when the Host check applies: <see cref="AllowedHosts"/> was configured, or the server is
    /// unprotected (<see cref="RequireAuthentication"/> off).
    /// </summary>
    public bool EnforcesHostCheck => AllowedHosts != null || !RequireAuthentication;

    /// <summary>
    /// True when <paramref name="host"/> (a Host header value, with or without port; IPv6 in brackets)
    /// is <c>localhost</c>/<c>*.localhost</c>, an IP literal, or matches <see cref="AllowedHosts"/>.
    /// </summary>
    public bool IsHostAllowed(string? host)
    {
        var name = NormalizeHost(host);
        if (name == null) return false;
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (System.Net.IPAddress.TryParse(name, out _)) return true;

        foreach (var entry in AllowedHosts ?? [])
        {
            var allowed = entry.Trim().TrimEnd('.').ToLowerInvariant();
            if (allowed == "*") return true;
            if (allowed.StartsWith("*.", StringComparison.Ordinal))
            {
                if (name.EndsWith(allowed[1..], StringComparison.Ordinal) && name.Length > allowed.Length - 1) return true;
            }
            else if (allowed == name)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lower-cased host name without port, IPv6 brackets or trailing dot; <c>null</c> when empty or malformed.
    /// </summary>
    internal static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('['))
        {
            var end = h.IndexOf(']');
            if (end < 0) return null;
            h = h[1..end];
        }
        else
        {
            var colon = h.IndexOf(':');
            if (colon >= 0)
            {
                // A bare IPv6 literal (no brackets, several colons) is kept as is.
                if (h.IndexOf(':', colon + 1) < 0) h = h[..colon];
            }
        }
        h = h.TrimEnd('.');
        return h.Length == 0 ? null : h;
    }

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
