using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// <c>/api/v1/qcli/updates</c> (behind the admin sign-in; <c>updates:read</c> or <c>plugins:install</c> on a protected
/// server, else 403 <c>{ error: "forbidden: missing permission updates:read" }</c>; 404 when the marketplace is off):
/// <list type="bullet">
/// <item><c>GET /</c>: the last check's result (<c>checkedAt</c>, <c>checking</c>, <c>server</c>, <c>dashboardPlugins</c>, <c>serverPlugins</c>, <c>codePlugins</c>, <c>count</c>, <c>error?</c>)</item>
/// <item><c>POST /check</c>: checks now and answers the new result (502 <c>{ error }</c> only when the check itself throws)</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/qcli/updates")]
public class UpdatesController(IServiceProvider services, ILogger<UpdatesController> logger) : ControllerBase
{
    /// <summary>True when the caller may see and check the updates: <c>updates:read</c> or <c>plugins:install</c> (anyone without a principal: the admin sign-in let them in).</summary>
    public static bool CanRead(CliPrincipal? principal) =>
        principal == null || principal.Can(CliRoutePolicy.UpdatesRead) || principal.Can(CliRoutePolicy.PluginsInstall);

    private IActionResult? Refused(out UpdateChecker? checker)
    {
        checker = services.GetService<UpdateChecker>();
        if (checker == null) return StatusCode(404, new { error = "not found" });
        if (!CanRead(HttpContext.GetCliPrincipal()))
            return StatusCode(403, new { error = $"forbidden: missing permission {CliRoutePolicy.UpdatesRead}" });
        return null;
    }

    [HttpGet]
    public IActionResult Get()
    {
        if (Refused(out var checker) is { } refused) return refused;
        Response.Headers.CacheControl = "no-store";
        return Ok(checker!.Result());
    }

    [HttpPost("check")]
    public async Task<IActionResult> Check()
    {
        if (Refused(out var checker) is { } refused) return refused;
        try
        {
            return Ok(await checker!.CheckAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[qcli-admin] update check failed");
            return StatusCode(502, new { error = ex.Message.Length > 0 ? ex.Message : "update check failed" });
        }
    }
}
