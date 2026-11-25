using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// The config API over the server's config providers (<see cref="ICliConfigService"/>):
/// <list type="bullet">
/// <item><c>GET /</c>: <c>{ providers: [...] }</c>, the providers the caller may see (<c>config:read</c>)</item>
/// <item><c>GET /{provider}</c>: one provider</item>
/// <item><c>PATCH /{provider}/{section}</c>: <c>{ changes, version? }</c> (<c>config:write</c>): 200 with the section, 400
/// <c>{ error, errors }</c> per key, 403, 404, 409 <c>{ error, version }</c> when the version is stale</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/qcli/config")]
public class ConfigController : ControllerBase
{
    private readonly ICliConfigService _service;
    private readonly CliServerOptions _serverOptions;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(ICliConfigService service, CliServerOptions serverOptions, ILogger<ConfigController> logger)
    {
        _service = service;
        _serverOptions = serverOptions;
        _logger = logger;
    }

    /// <summary>
    /// Who is asking: the server's caller on a protected server, else the dashboard session (the static admin of
    /// password mode may do everything).
    /// </summary>
    public static CliConfigContext ContextOf(HttpContext http, CliServerOptions serverOptions)
    {
        if (serverOptions.RequireAuthentication && http.GetCliPrincipal() != null)
            return CliConfigContexts.Of(http, serverOptions, "api");
        var principal = http.GetCliPrincipal();
        if (principal != null)
            return new CliConfigContext(new CliConfigCaller(principal.UserId, principal.UserName, principal.Permissions), principal.Can, "api");
        var user = http.Items["AdminUser"] as ClaimsPrincipal;
        return CliConfigContext.For(new CliConfigCaller(null, user?.Identity?.Name ?? "admin", ["*"]), "api");
    }

    private IActionResult Error(Exception ex)
    {
        if (ex is CliConfigException config)
            return new ContentResult { StatusCode = config.Status, Content = config.ToJson().ToJsonString(), ContentType = "application/json" };
        _logger.LogError(ex, "[qcli-admin] config request failed");
        return StatusCode(500, new { error = "internal server error" });
    }

    /// <summary>The providers the caller may see.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        try
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(new { providers = await _service.ListAsync(ContextOf(HttpContext, _serverOptions), cancellationToken) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex);
        }
    }

    /// <summary>One provider.</summary>
    [HttpGet("{provider}")]
    public async Task<IActionResult> Get(string provider, CancellationToken cancellationToken)
    {
        try
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(await _service.GetAsync(provider, ContextOf(HttpContext, _serverOptions), cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex);
        }
    }

    /// <summary>Changes settings of a section: <c>{ changes, version? }</c>.</summary>
    [HttpPatch("{provider}/{section}")]
    public async Task<IActionResult> Update(string provider, string section, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonNode.Parse(body.GetRawText()) as JsonObject;
            var changes = json?["changes"] is JsonObject c ? c.ToDictionary(p => p.Key, p => p.Value?.DeepClone()) : null;
            string? version = null;
            if (json?["version"] is { } v)
            {
                version = CliConfigValues.AsString(v) ?? throw new CliConfigException(400, "version must be a string");
            }
            var context = ContextOf(HttpContext, _serverOptions);
            return Ok(await _service.UpdateAsync(provider, section, new CliConfigUpdateRequest { Changes = changes, Version = version },
                context, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex);
        }
    }
}
