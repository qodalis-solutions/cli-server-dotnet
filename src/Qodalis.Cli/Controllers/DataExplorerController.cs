using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// Controller for the data explorer API, enabling query execution and schema introspection across registered data sources.
/// </summary>
[ApiController]
[Route("api/qcli/data-explorer")]
public class DataExplorerController : ControllerBase
{
    private readonly IDataExplorerRegistry _registry;
    private readonly IDataExplorerExecutorService _executor;
    private readonly ILogger<DataExplorerController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DataExplorerController"/>.
    /// </summary>
    /// <param name="registry">The data explorer provider registry.</param>
    /// <param name="executor">The data explorer query executor service.</param>
    /// <param name="logger">The logger instance.</param>
    public DataExplorerController(IDataExplorerRegistry registry, IDataExplorerExecutorService executor, ILogger<DataExplorerController> logger)
    {
        _registry = registry;
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Returns all registered data explorer sources with their metadata.
    /// </summary>
    [HttpGet("sources")]
    public IActionResult GetSources()
    {
        var sources = _registry.GetSources();
        return Ok(sources);
    }

    /// <summary>
    /// Executes a query against a registered data source.
    /// </summary>
    /// <param name="request">The query execution request.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpPost("execute")]
    public async Task<IActionResult> Execute(
        [FromBody] DataExplorerExecuteRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("Executing data explorer query on source: {Source}", request.Source);
        var result = await _executor.ExecuteAsync(request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns the schema (tables, columns) for the specified data source.
    /// </summary>
    /// <param name="source">The data source name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
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
            _logger.LogWarning("Data explorer source not found: {Source}", source);
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
