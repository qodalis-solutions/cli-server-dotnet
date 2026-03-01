using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/v2/cli")]
public class CliControllerV2 : ControllerBase
{
    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;

    public CliControllerV2(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor)
    {
        _registry = registry;
        _executor = executor;
    }

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { ApiVersion = 2, ServerVersion = "1.0.0" });
    }

    [HttpGet("commands")]
    public IActionResult GetCommands()
    {
        var descriptors = _registry.Processors
            .Where(p => p.ApiVersion >= 2)
            .Select(MapToDescriptor)
            .ToList();
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

    private static CliServerCommandDescriptor MapToDescriptor(ICliCommandProcessor p) =>
        new()
        {
            Command = p.Command,
            Description = p.Description,
            Version = p.Version,
            ApiVersion = p.ApiVersion,
            Parameters = p.Parameters?.Select(param => new CliCommandParameterDescriptorDto
            {
                Name = param.Name,
                Description = param.Description,
                Type = param.Type,
                Required = param.Required,
                DefaultValue = param.DefaultValue,
                Aliases = param.Aliases,
            }).ToList(),
            Processors = p.Processors?.Select(MapToDescriptor).ToList(),
        };
}
