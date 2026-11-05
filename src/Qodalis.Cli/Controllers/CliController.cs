using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// API v1 controller for CLI operations including command listing, execution, and server capabilities.
/// </summary>
[ApiController]
[Route("api/v1/qcli")]
public class CliController : ControllerBase
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;
    private readonly ICliServerInfoService _serverInfo;
    private readonly ILogger<CliController> _logger;
    private readonly CliServerOptions _serverOptions;
    private readonly CliCommandUsage? _usage;

    /// <summary>
    /// Initializes a new instance of <see cref="CliController"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="executor">The command executor service.</param>
    /// <param name="serverInfo">The server info service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serverOptions">Server options.</param>
    /// <param name="usage">The command execution counters (<c>GET commands/stats</c>).</param>
    public CliController(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor,
        ICliServerInfoService serverInfo,
        ILogger<CliController> logger,
        CliServerOptions? serverOptions = null,
        CliCommandUsage? usage = null)
    {
        _usage = usage;
        _serverOptions = serverOptions ?? new CliServerOptions();
        _registry = registry;
        _executor = executor;
        _serverInfo = serverInfo;
        _logger = logger;
    }

    /// <summary>
    /// When the server is protected, enforces the processor's declared
    /// <see cref="ICliCommandProcessor.RequiredPermissions"/> (every processor from the root to the resolved sub-command).
    /// </summary>
    private ObjectResult? CheckCommandPermissions(CliProcessCommand command)
    {
        if (!_serverOptions.RequireAuthentication) return null;

        var principal = HttpContext.GetCliPrincipal();
        if (principal == null)
            return StatusCode(401, new { error = "unauthorized" });

        // Every processor on the path counts, not only the root and the resolved one
        var required = CliCommandPermissions.RequiredFor(_registry, command.Command, command.ChainCommands);
        var missing = CliCommandPermissions.Missing(principal, required);
        return missing.Count == 0
            ? null
            : StatusCode(403, new { error = $"permission denied: {string.Join(", ", missing)}" });
    }

    /// <summary>
    /// Returns the current API version.
    /// </summary>
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { Version = _serverInfo.ServerVersion });
    }

    /// <summary>
    /// Returns the server capabilities including OS, shell, and version information.
    /// </summary>
    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        return Ok(_serverInfo.GetCapabilities());
    }

    /// <summary>
    /// Returns all registered command processor descriptors.
    /// </summary>
    [HttpGet("commands")]
    public IActionResult GetCommands()
    {
        var descriptors = _registry.Processors.Select(_serverInfo.MapToDescriptor).ToList();
        return Ok(descriptors);
    }

    /// <summary>
    /// How often each root command ran since the server started (through <c>execute</c>, <c>execute/stream</c> and
    /// MCP): executions, failures, mean duration in milliseconds and the last times; most executed first.
    /// </summary>
    [HttpGet("commands/stats")]
    public IActionResult GetCommandStats()
    {
        var usage = _usage;
        return Ok(new
        {
            since = FormatTime(usage?.Since ?? System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()),
            commands = (usage?.GetEntries() ?? []).Select(e => new
            {
                command = e.Command,
                known = e.Known,
                executions = e.Executions,
                failures = e.Failures,
                avgDuration = e.AvgDuration,
                lastExecutedAt = e.LastExecutedAt is { } executed ? FormatTime(executed) : null,
                lastFailedAt = e.LastFailedAt is { } failed ? FormatTime(failed) : null,
            }),
        });
    }

    private static string FormatTime(DateTime time) => Qodalis.Cli.Abstractions.Insights.TimeBuckets.Format(time);

    /// <summary>
    /// Executes a CLI command and returns the response.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteAsync(
        [FromBody] CliProcessCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing command: {Command}", command.Command);
        var denied = CheckCommandPermissions(command);
        if (denied != null) return denied;
        var response = await _executor.ExecuteAsync(command, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Executes a CLI command and streams the output as Server-Sent Events.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [HttpPost("execute/stream")]
    public async Task ExecuteStream(
        [FromBody] CliProcessCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stream executing command: {Command}", command.Command);

        var denied = CheckCommandPermissions(command);
        if (denied != null)
        {
            Response.StatusCode = denied.StatusCode ?? 403;
            await Response.WriteAsJsonAsync(denied.Value);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task WriteEvent(string eventType, object data)
        {
            // For CliServerOutput, serialize as the base type so the [JsonPolymorphic]
            // type discriminator ("type": "text", etc.) is included in the JSON.
            var type = data is CliServerOutput ? typeof(CliServerOutput) : data.GetType();
            var json = System.Text.Json.JsonSerializer.Serialize(data, type, _jsonOptions);
            await Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
            await Response.Body.FlushAsync();
        }

        // Executions the executor does not run (unknown, disabled, streaming processors) are counted here.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool? countedHere = null;
        var known = false;
        var failed = true;
        try
        {
            var processor = _registry.FindProcessor(command.Command, command.ChainCommands);
            known = processor != null;

            if (processor == null)
            {
                countedHere = true;
                await WriteEvent("error", new { message = $"Unknown command: {command.Command}" });
                return;
            }

            if (_executor.IsBlocked(processor))
            {
                countedHere = true;
                await WriteEvent("error", new { message = $"Command '{command.Command}' is currently disabled." });
                return;
            }

            int exitCode;

            if (processor is ICliStreamCommandProcessor streamProcessor)
            {
                countedHere = true;
                exitCode = await streamProcessor.HandleStreamAsync(command, async output =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteEvent("output", output is CliServerOutput block ? CliOutputLimits.Apply(block, _serverOptions.MaxTableRows) : output);
                }, cancellationToken);
                failed = exitCode != 0;
            }
            else
            {
                countedHere = false;
                var response = await _executor.ExecuteAsync(command, cancellationToken);
                foreach (var output in response.Outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteEvent("output", output);
                }
                exitCode = response.ExitCode;
            }

            await WriteEvent("done", new { exitCode });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected — silently end the stream
            _logger.LogDebug("Stream cancelled for command: {Command}", command.Command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream execution failed: {Command}", command.Command);
            await WriteEvent("error", new { message = $"Error executing command: {ex.Message}" });
        }
        finally
        {
            if (countedHere == true)
                _usage?.Record(command.Command, known, System.Diagnostics.Stopwatch.GetElapsedTime(started), failed);
        }
    }
}
