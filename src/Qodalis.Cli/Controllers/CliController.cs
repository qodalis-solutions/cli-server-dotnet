using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
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

    /// <summary>
    /// Initializes a new instance of <see cref="CliController"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="executor">The command executor service.</param>
    /// <param name="serverInfo">The server info service.</param>
    /// <param name="logger">The logger instance.</param>
    public CliController(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor,
        ICliServerInfoService serverInfo,
        ILogger<CliController> logger)
    {
        _registry = registry;
        _executor = executor;
        _serverInfo = serverInfo;
        _logger = logger;
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

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task WriteEvent(string eventType, object data)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(data, data.GetType(), _jsonOptions);
            await Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
            await Response.Body.FlushAsync();
        }

        try
        {
            var processor = _registry.FindProcessor(command.Command, command.ChainCommands);

            if (processor == null)
            {
                await WriteEvent("error", new { message = $"Unknown command: {command.Command}" });
                return;
            }

            if (_executor.IsBlocked(processor))
            {
                await WriteEvent("error", new { message = $"Command '{command.Command}' is currently disabled." });
                return;
            }

            int exitCode;

            if (processor is ICliStreamCommandProcessor streamProcessor)
            {
                exitCode = await streamProcessor.HandleStreamAsync(command, async output =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteEvent("output", output);
                }, cancellationToken);
            }
            else
            {
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
    }
}
