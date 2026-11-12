using Microsoft.AspNetCore.Http;
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
    /// Returns the data explorer sources the caller may use, with their metadata (<c>readOnly</c>, <c>canExport</c>).
    /// </summary>
    [HttpGet("sources")]
    public IActionResult GetSources()
    {
        var sources = _registry.GetSources(DataExplorerCallers.From(HttpContext));
        return Ok(sources);
    }

    /// <summary>
    /// Executes a query against a registered data source: 403 without the source's permissions, 429 over its rate
    /// limit; other refusals (read-only, allowed objects) are failed results with <c>errorCode</c>.
    /// </summary>
    /// <param name="request">The query execution request.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpPost("execute")]
    public async Task<IActionResult> Execute(
        [FromBody] DataExplorerExecuteRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("Executing data explorer query on source: {Source}", request.Source);
        var result = await _executor.ExecuteAsync(request, DataExplorerCallers.From(HttpContext), ct);
        return result.ErrorCode switch
        {
            DataExplorerErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            DataExplorerErrorCodes.RateLimited => StatusCode(StatusCodes.Status429TooManyRequests, new { error = result.Error }),
            _ => Ok(result),
        };
    }

    /// <summary>
    /// Returns the schema (tables, columns) for the specified data source: the allowed tables only, without dropped
    /// columns; 403 without the source's permissions.
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
        var missing = DataExplorerPolicy.MissingPermissions(options.RequiredPermissions, DataExplorerCallers.From(HttpContext));
        if (missing.Count > 0)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"forbidden: missing permission {string.Join(", ", missing)}" });
        }

        var schema = await provider.GetSchemaAsync(options, ct);
        if (schema == null)
        {
            return NotFound(new { error = "Schema introspection is not supported by this data source." });
        }

        return Ok(DataExplorerPolicy.FilterSchema(schema, options));
    }
}
