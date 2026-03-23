using Microsoft.AspNetCore.Mvc;
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
    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;
    private readonly ICliServerInfoService _serverInfo;

    /// <summary>
    /// Initializes a new instance of <see cref="CliController"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="executor">The command executor service.</param>
    /// <param name="serverInfo">The server info service.</param>
    public CliController(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor,
        ICliServerInfoService serverInfo)
    {
        _registry = registry;
        _executor = executor;
        _serverInfo = serverInfo;
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
        var response = await _executor.ExecuteAsync(command, cancellationToken);
        return Ok(response);
    }
}
