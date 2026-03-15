using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/v1/qcli")]
public class CliController : ControllerBase
{
    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;

    public CliController(ICliCommandRegistry registry, ICliCommandExecutorService executor)
    {
        _registry = registry;
        _executor = executor;
    }

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { Version = "1.0.0" });
    }

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

    [HttpGet("commands")]
    public IActionResult GetCommands()
    {
        var descriptors = _registry.Processors.Select(MapToDescriptor).ToList();
        return Ok(descriptors);
    }

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
