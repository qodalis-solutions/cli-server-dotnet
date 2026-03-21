using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/qcli/data-explorer")]
public class DataExplorerController : ControllerBase
{
    private readonly DataExplorerRegistry _registry;
    private readonly DataExplorerExecutorService _executor;

    public DataExplorerController(DataExplorerRegistry registry, DataExplorerExecutorService executor)
    {
        _registry = registry;
        _executor = executor;
    }

    [HttpGet("sources")]
    public IActionResult GetSources()
    {
        var sources = _registry.GetSources();
        return Ok(sources);
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute(
        [FromBody] DataExplorerExecuteRequest request,
        CancellationToken ct)
    {
        var result = await _executor.ExecuteAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("schema")]
    public async Task<IActionResult> GetSchema(
        [FromQuery] string source,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source))
        {
            return BadRequest(new { error = "\"source\" query parameter is required." });
        }

        var entry = _registry.Get(source);
        if (entry == null)
        {
            return NotFound(new { error = $"Unknown data source: '{source}'" });
        }

        var (provider, options) = entry.Value;
        var schema = await provider.GetSchemaAsync(options, ct);
        if (schema == null)
        {
            return NotFound(new { error = "Schema introspection is not supported by this data source." });
        }

        return Ok(schema);
    }
}
