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
