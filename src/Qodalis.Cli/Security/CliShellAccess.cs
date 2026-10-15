using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Security;

/// <summary>Close codes of the shell WebSocket (readable by browsers, unlike HTTP upgrade statuses).</summary>
public static class CliShellCloseCodes
{
    /// <summary>Login required, or the bound session ended.</summary>
    public const int Unauthorized = 4401;

    /// <summary>Forbidden: missing <c>shell:execute</c>, bad token or authorize returned false.</summary>
    public const int Forbidden = 4403;

    /// <summary>The shell is disabled on this server.</summary>
    public const int Disabled = 4404;

    /// <summary>Idle timeout or maximum duration reached.</summary>
    public const int Timeout = 4408;

    /// <summary>Too many shell sessions (per user/address or total).</summary>
    public const int TooManySessions = 4429;
}

/// <summary>Shell access modes as reported by <c>/capabilities</c> (<c>shellAuth</c>).</summary>
public static class CliShellAuthModes
{
    public const string Users = "users";
    public const string Token = "token";
    public const string Custom = "custom";
    public const string Anonymous = "anonymous";
    public const string Locked = "locked";
}

/// <summary>Outcome of a shell access check.</summary>
public sealed record CliShellAccessResult(bool Allowed, string Mode, int CloseCode, string Reason, CliPrincipal? Principal)
{
    public static CliShellAccessResult Allow(string mode, CliPrincipal? principal) => new(true, mode, 0, string.Empty, principal);

    public static CliShellAccessResult Deny(string mode, int code, string reason) => new(false, mode, code, reason, null);
}

/// <summary>
/// An authentication failure recorded by an authentication plugin for a shell upgrade, so the
/// shell endpoint can accept the socket and close it with a readable code (C5) instead of the
/// plugin answering with an HTTP status.
/// </summary>
public sealed record CliAuthFailure(int StatusCode, string Message)
{
    /// <summary><see cref="HttpContext.Items"/> key.</summary>
    public const string ItemKey = "Qodalis.Cli.AuthFailure";
}

/// <summary>
/// Shell access decisions (web-cli spec Appendix C1): mode resolution, token check
/// (constant time) and the host <see cref="CliShellOptions.Authorize"/> callback.
/// </summary>
public static class CliShellAccess
{
    /// <summary>Query parameter used by browser clients to forward an Authorization header.</summary>
    public const string HeaderQueryParameter = "_header_Authorization";

    public const string LoginRequired = "login required";
    public const string Forbidden = "forbidden";
    public const string NotConfigured = "shell access is not configured";
    public const string ShellDisabled = "shell disabled";

    /// <summary>
    /// The access mode: <c>users</c> when an authentication plugin is installed, otherwise
    /// <c>token</c>, then <c>custom</c>, then <c>anonymous</c> (explicit opt-in), else <c>locked</c>.
    /// </summary>
    public static string ResolveMode(CliShellOptions options, bool usersMode)
    {
        if (usersMode) return CliShellAuthModes.Users;
        // AllowAnonymous is ignored once a token or authorize callback is configured.
        if (!string.IsNullOrEmpty(options.Token)) return CliShellAuthModes.Token;
        if (options.Authorize != null) return CliShellAuthModes.Custom;
        if (options.AllowAnonymous) return CliShellAuthModes.Anonymous;
        return CliShellAuthModes.Locked;
    }

    /// <summary>
    /// Decides whether the shell upgrade in <paramref name="context"/> is allowed. In users mode
    /// the principal must have been set by the authentication plugin and hold <c>shell:execute</c>.
    /// A configured token and/or authorize callback must pass in every mode.
    /// </summary>
    public static async Task<CliShellAccessResult> AuthorizeAsync(HttpContext context, CliShellOptions options,
        bool usersMode, ILogger? logger = null)
    {
        var mode = ResolveMode(options, usersMode);
        CliPrincipal? principal = null;

        if (mode == CliShellAuthModes.Locked)
            return CliShellAccessResult.Deny(mode, CliShellCloseCodes.Unauthorized, NotConfigured);

        if (usersMode)
        {
            principal = context.GetCliPrincipal();
            if (principal == null)
            {
                var failure = context.Items.TryGetValue(CliAuthFailure.ItemKey, out var f) ? f as CliAuthFailure : null;
                return failure?.StatusCode == StatusCodes.Status403Forbidden
                    ? CliShellAccessResult.Deny(mode, CliShellCloseCodes.Forbidden, Forbidden)
                    : CliShellAccessResult.Deny(mode, CliShellCloseCodes.Unauthorized, LoginRequired);
            }
            if (!principal.Can(CliRoutePolicy.ShellExecute))
                return CliShellAccessResult.Deny(mode, CliShellCloseCodes.Forbidden, Forbidden);
        }

        if (!string.IsNullOrEmpty(options.Token))
        {
            var presented = PresentedBearerToken(context.Request);
            if (presented == null)
                return CliShellAccessResult.Deny(mode, CliShellCloseCodes.Unauthorized, LoginRequired);
            if (!TokenMatches(presented, options.Token))
                return CliShellAccessResult.Deny(mode, CliShellCloseCodes.Forbidden, Forbidden);
        }

        if (options.Authorize != null)
        {
            bool allowed;
            try
            {
                allowed = await options.Authorize(context);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Shell authorize callback failed; access denied");
                allowed = false;
            }
            if (!allowed)
                return CliShellAccessResult.Deny(mode, CliShellCloseCodes.Forbidden, Forbidden);
        }

        return CliShellAccessResult.Allow(mode, principal);
    }

    /// <summary>
    /// The bearer token from the <c>Authorization</c> header or, for browser clients, the
    /// <c>_header_Authorization</c> query parameter. Null when absent or not a bearer token.
    /// </summary>
    public static string? PresentedBearerToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(value))
            value = request.Query[HeaderQueryParameter].FirstOrDefault();
        if (string.IsNullOrEmpty(value) || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = value["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Constant-time token comparison: both values are hashed (SHA-256) so neither the content
    /// nor the length of the secret leaks through timing, then compared with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// </summary>
    public static bool TokenMatches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
