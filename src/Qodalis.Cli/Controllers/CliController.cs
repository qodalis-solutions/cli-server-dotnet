using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
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

    /// <summary>
    /// Initializes a new instance of <see cref="CliController"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="executor">The command executor service.</param>
    public CliController(ICliCommandRegistry registry, ICliCommandExecutorService executor)
    {
        _registry = registry;
        _executor = executor;
    }

    /// <summary>
    /// Returns the current API version.
    /// </summary>
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { Version = "1.0.0" });
    }

    /// <summary>
    /// Returns the server capabilities including OS, shell, and version information.
    /// </summary>
    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        var os = Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => "win32",
            PlatformID.Unix => OperatingSystem.IsMacOS() ? "darwin" : "linux",
            _ => "unknown",
        };

        var shell = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "powershell"
            : "bash";

        var shellPath = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
            : "/bin/bash";

        return Ok(new
        {
            Shell = true,
            Os = os,
            ShellPath = shellPath,
            Version = "1.0.0",
        });
    }

    /// <summary>
    /// Returns all registered command processor descriptors.
    /// </summary>
    [HttpGet("commands")]
    public IActionResult GetCommands()
    {
        var descriptors = _registry.Processors.Select(MapToDescriptor).ToList();
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

    private static CliServerCommandDescriptor MapToDescriptor(ICliCommandProcessor processor)
    {
        return new CliServerCommandDescriptor
        {
            Command = processor.Command,
            Description = processor.Description,
            Version = processor.Version,
            Parameters = processor.Parameters?.Select(p => new CliCommandParameterDescriptorDto
            {
                Name = p.Name,
                Aliases = p.Aliases,
                Description = p.Description,
                Required = p.Required,
                Type = p.Type,
                DefaultValue = p.DefaultValue,
            }).ToList(),
            Processors = processor.Processors?.Select(MapToDescriptor).ToList(),
        };
    }
}
