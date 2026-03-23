using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// API v2 controller for CLI operations. Only exposes processors targeting API version 2 or higher.
/// </summary>
[ApiController]
[Route("api/v2/qcli")]
public class CliControllerV2 : ControllerBase
{
    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;
    private readonly ICliServerInfoService _serverInfo;
    private readonly ILogger<CliControllerV2> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CliControllerV2"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="executor">The command executor service.</param>
    /// <param name="serverInfo">The server info service.</param>
    /// <param name="logger">The logger instance.</param>
    public CliControllerV2(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor,
        ICliServerInfoService serverInfo,
        ILogger<CliControllerV2> logger)
    {
        _registry = registry;
        _executor = executor;
        _serverInfo = serverInfo;
        _logger = logger;
    }

    /// <summary>
    /// Returns the API version and server version.
    /// </summary>
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { ApiVersion = 2, ServerVersion = _serverInfo.ServerVersion });
    }

    /// <summary>
    /// Returns command descriptors for processors targeting API version 2 or higher.
    /// </summary>
    [HttpGet("commands")]
    public IActionResult GetCommands()
    {
        var descriptors = _registry.Processors
            .Where(p => p.ApiVersion >= 2)
            .Select(_serverInfo.MapToDescriptor)
            .ToList();
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
        _logger.LogDebug("Executing command (v2): {Command}", command.Command);
        var response = await _executor.ExecuteAsync(command, cancellationToken);
        return Ok(response);
    }
}
