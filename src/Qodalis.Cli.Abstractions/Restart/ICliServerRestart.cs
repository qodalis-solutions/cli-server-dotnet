using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions.Restart;

// Restarting the server from the dashboard, the `admin restart` command or the API (/api/v1/qcli/server/restart), and
// the list of changes waiting for a restart. The same contract and JSON in the Node, .NET and Python servers. The
// server implements ICliServerRestart (a singleton in the container); plugins mark their own pending changes with
// MarkRequired.

/// <summary>Names of the restart feature.</summary>
public static class CliServerRestartConstants
{
    /// <summary>The permission <c>POST /api/v1/qcli/server/restart</c> and <c>admin restart</c> need.</summary>
    public const string Permission = "server:restart";

    /// <summary>Published when the pending list changes (<c>{ pending }</c>, audience <c>admin:access</c>).</summary>
    public const string RequiredEvent = "server:restart-required";

    /// <summary>Published when a restart starts (<c>{ reason?, by, pending, mode }</c>, audience <c>admin:access</c>).</summary>
    public const string RestartingEvent = "server:restarting";

    /// <summary>The audit event of a restart.</summary>
    public const string AuditEvent = "server.restart";

    /// <summary>The WebSocket close code sent to every socket when the server restarts (RFC 6455: service restart).</summary>
    public const int CloseCode = 1012;

    /// <summary>The close reason sent with <see cref="CloseCode"/>.</summary>
    public const string CloseReason = "Server restarting";

    /// <summary>The route of the restart API.</summary>
    public const string Path = "/api/v1/qcli/server/restart";
}

/// <summary>
/// How the server restarts:
/// <list type="bullet">
/// <item><c>exit</c>: graceful shutdown, then the process exits with the exit code (default 75) for a supervisor to start
/// it again (Docker restart policy, systemd <c>Restart=always</c>, PM2, Kubernetes, IIS);</item>
/// <item><c>self</c>: graceful shutdown, then the same command is started again (same arguments, environment and working
/// directory) and this process exits: bare runs and development;</item>
/// <item><c>handler</c>: graceful shutdown, then the host's function;</item>
/// <item><c>auto</c> (default): <c>exit</c> when a supervisor is detected, otherwise restarting is not supported.</item>
/// </list>
/// </summary>
public static class CliServerRestartModes
{
    public const string Auto = "auto";
    public const string Exit = "exit";
    public const string Self = "self";
    public const string Handler = "handler";

    /// <summary>Every mode.</summary>
    public static readonly IReadOnlyList<string> All = [Auto, Exit, Self, Handler];
}

/// <summary>A supervisor that starts the process again when it exits.</summary>
public static class CliServerSupervisors
{
    public const string Docker = "docker";
    public const string Systemd = "systemd";
    public const string Pm2 = "pm2";
    public const string Kubernetes = "kubernetes";

    /// <summary>IIS (the ASP.NET Core Module): it starts the app again when the process exits.</summary>
    public const string Iis = "iis";
}

/// <summary>A change that takes effect only after a restart, as <see cref="ICliServerRestart.MarkRequired"/> takes it.</summary>
/// <param name="Id">Unique key: marking the same id again keeps the first entry (e.g. <c>config:cors.cors.mode</c>).</param>
/// <param name="Source">Who marked it: <c>config</c>, a plugin's id, ...</param>
/// <param name="Description">One line for people: <c>CORS: Mode changed</c>.</param>
public sealed record CliRestartRequirementInput(string Id, string Source, string Description);

/// <summary>A pending change as listed by the API.</summary>
public sealed class CliRestartRequirement
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }

    /// <summary>When it was marked (ISO 8601 UTC).</summary>
    [JsonPropertyName("since")] public required string Since { get; init; }
}

/// <summary><c>GET /api/v1/qcli/server/restart</c>.</summary>
public sealed class CliServerRestartStatus
{
    /// <summary>The server can restart itself (from the dashboard, the command or the API).</summary>
    [JsonPropertyName("supported")] public bool Supported { get; init; }

    /// <summary>The mode in effect (<c>auto</c> resolves to <c>exit</c>); null when not supported.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Mode { get; init; }

    /// <summary>The supervisor detected (<see cref="CliServerSupervisors"/>), also with an explicit mode; null when none.</summary>
    [JsonPropertyName("supervisor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Supervisor { get; init; }

    /// <summary>Why it is not supported (only when <see cref="Supported"/> is false).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>When this process started serving (ISO 8601 UTC): it changes once the server restarted.</summary>
    [JsonPropertyName("startedAt")] public required string StartedAt { get; init; }

    /// <summary>Changes waiting for a restart, oldest first; empty after every start.</summary>
    [JsonPropertyName("pending")] public IReadOnlyList<CliRestartRequirement> Pending { get; init; } = [];
}

/// <summary>The signed-in caller asking for a restart.</summary>
public sealed record CliServerRestartActor(string? UserId, string UserName);

/// <summary>Who asks for a restart, and why.</summary>
public sealed class CliServerRestartRequest
{
    /// <summary>Free text kept in the audit log (at most 500 characters).</summary>
    public string? Reason { get; init; }

    /// <summary>The signed-in caller; null on a server without sign-in.</summary>
    public CliServerRestartActor? Actor { get; init; }

    /// <summary>Where it came from: <c>api</c>, <c>command</c>, ... (audited).</summary>
    public string? Via { get; init; }
}

/// <summary>An accepted restart: <c>{ restarting: true, pending }</c> (the ids that were pending).</summary>
public sealed class CliServerRestartResult
{
    [JsonPropertyName("restarting")] public bool Restarting { get; init; } = true;
    [JsonPropertyName("pending")] public IReadOnlyList<string> Pending { get; init; } = [];
}

/// <summary>A refused restart: 409 not supported or already restarting, 403 without sign-in, 400 bad input.</summary>
public sealed class CliServerRestartException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The server's restart service: the changes waiting for a restart (<see cref="MarkRequired"/>,
/// <see cref="ClearRequired"/>; empty on every start) and the restart itself.
/// </summary>
public interface ICliServerRestart
{
    /// <summary>When this process started serving (ISO 8601 UTC).</summary>
    string StartedAt { get; }

    /// <summary>True once a restart was accepted.</summary>
    bool Restarting { get; }

    CliServerRestartStatus Status();

    /// <summary>The pending changes (copies), oldest first.</summary>
    IReadOnlyList<CliRestartRequirement> Pending();

    /// <summary>
    /// Adds a pending change (nothing happens when the id is already pending); publishes <c>server:restart-required</c>.
    /// Throws <see cref="ArgumentException"/> without an id, a source or a description.
    /// </summary>
    void MarkRequired(CliRestartRequirementInput requirement);

    /// <summary>Removes a pending change (e.g. a setting set back to its value at start); true when it was pending.</summary>
    bool ClearRequired(string id);

    /// <summary>
    /// Accepts a restart: audits it, publishes <c>server:restarting</c> and returns; the graceful shutdown and the mode's
    /// action follow in the background. Throws <see cref="CliServerRestartException"/>.
    /// </summary>
    Task<CliServerRestartResult> RestartAsync(CliServerRestartRequest? request = null, CancellationToken cancellationToken = default);
}
