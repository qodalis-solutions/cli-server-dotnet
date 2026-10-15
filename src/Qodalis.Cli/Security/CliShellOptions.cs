using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Security;

/// <summary>
/// Options for the interactive shell WebSockets (<c>/ws/v1/qcli/shell</c>, <c>/ws/qcli/shell</c>,
/// <c>/ws/v1/qcli</c>, <c>/ws/qcli</c>). Configured with <c>cli.EnableShell(o =&gt; ...)</c>.
/// <para>
/// Access mode (exactly one applies, see <see cref="CliShellAccess.ResolveMode"/>):
/// <list type="number">
/// <item><b>users</b>: the users plugin is installed — a ticket-authenticated session with <c>shell:execute</c>.</item>
/// <item><b>token</b>: <see cref="Token"/> is set — <c>Authorization: Bearer &lt;token&gt;</c> (header or the
/// <c>_header_Authorization</c> query parameter used by browser clients).</item>
/// <item><b>custom</b>: <see cref="Authorize"/> is set — host-defined check (exceptions deny).</item>
/// <item><b>anonymous</b>: only with <see cref="AllowAnonymous"/> = true (logs a startup warning).</item>
/// </list>
/// With none of them the shell is <b>locked</b>: every connection is closed with 4401.
/// When several checks are configured, every configured check must pass
/// (users session, token and authorize).
/// </para>
/// </summary>
public sealed class CliShellOptions
{
    /// <summary>Default <see cref="MaxSessionsPerUser"/>.</summary>
    public const int DefaultMaxSessionsPerUser = 3;

    /// <summary>Default <see cref="MaxSessions"/>.</summary>
    public const int DefaultMaxSessions = 20;

    /// <summary>Upper bound for <see cref="SessionCheckInterval"/>.</summary>
    public static readonly TimeSpan MaxSessionCheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shared secret for token mode. Clients send <c>Authorization: Bearer &lt;token&gt;</c>
    /// (or <c>?_header_Authorization=Bearer%20&lt;token&gt;</c>). Compared in constant time; never logged.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Host-defined access check (custom mode). Return <c>true</c> to allow. Exceptions deny.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>>? Authorize { get; set; }

    /// <summary>
    /// Allows unauthenticated shells when no other mode applies. Dangerous: anyone who can reach
    /// the server gets a shell. Default <c>false</c>.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Concurrent shells per user (users mode) or per remote address (other modes). Default 3.
    /// Exceeding it closes the socket with 4429. 0 or less = unlimited.
    /// </summary>
    public int MaxSessionsPerUser { get; set; } = DefaultMaxSessionsPerUser;

    /// <summary>Total concurrent shells. Default 20. 0 or less = unlimited.</summary>
    public int MaxSessions { get; set; } = DefaultMaxSessions;

    /// <summary>Closes a shell (4408) after this long without stdin. Default 15 minutes; null = never.</summary>
    public TimeSpan? IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Closes a shell (4408) after this total duration. Default none.</summary>
    public TimeSpan? MaxDuration { get; set; }

    /// <summary>
    /// Users mode: how often an open shell re-checks that its session is still valid and still
    /// holds <c>shell:execute</c>. Default 30 s, capped at 60 s.
    /// </summary>
    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Working directory of the shell. Default: the server user's home directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Shell executable (e.g. <c>/bin/bash</c>). Default: detected.</summary>
    public string? Shell { get; set; }

    /// <summary>
    /// Names of server environment variables passed to the shell in addition to the minimal
    /// set (<c>PATH</c>, <c>HOME</c>, <c>USER</c>, <c>LANG</c>, <c>TERM</c>, <c>COLUMNS</c>,
    /// <c>LINES</c>, <c>SHELL</c>).
    /// </summary>
    public List<string> EnvironmentAllow { get; } = [];

    /// <summary>Environment variables set in the shell (applied last, override everything).</summary>
    public Dictionary<string, string> EnvironmentSet { get; } = new(StringComparer.Ordinal);

    internal TimeSpan EffectiveSessionCheckInterval =>
        SessionCheckInterval <= TimeSpan.Zero || SessionCheckInterval > MaxSessionCheckInterval
            ? MaxSessionCheckInterval
            : SessionCheckInterval;
}
