using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Qodalis.Cli.Security;

/// <summary>
/// Global MVC filter: when <see cref="CliServerOptions.RequireAuthentication"/> is on, CLI routes
/// without an authenticated <see cref="CliPrincipal"/> answer 401 (fail closed even when the
/// authentication middleware is missing), and the route's permission is enforced (403).
/// Host routes outside the CLI namespace are not affected.
/// </summary>
public sealed class CliAuthenticationGuardFilter : IAsyncActionFilter, IOrderedFilter
{
    // Runs after plugin filters that may short-circuit first (e.g. 501 in external-auth mode).
    public int Order => int.MinValue + 2000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        // MCP: the Origin check (403) and the method check (405) run before authentication.
        var mcp = Qodalis.Cli.Mcp.CliMcpGate.Check(http);
        if (mcp != null)
        {
            if (mcp.Allow != null) http.Response.Headers.Allow = mcp.Allow;
            context.Result = new JsonResult(new { error = mcp.Error }) { StatusCode = mcp.StatusCode };
            return;
        }

        var options = http.RequestServices.GetService<CliServerOptions>();
        if (options?.RequireAuthentication == true)
        {
            var rule = CliRoutePolicy.Resolve(http.Request.Method, http.Request.Path.Value);
            if (rule.IsCliRoute && !rule.IsPublic)
            {
                var principal = http.GetCliPrincipal();
                if (principal == null)
                {
                    context.Result = new JsonResult(new { error = "unauthorized" }) { StatusCode = 401 };
                    return;
                }
                if (rule.Permission != null && !principal.Can(rule.Permission))
                {
                    context.Result = new JsonResult(new { error = $"permission denied: {rule.Permission}" }) { StatusCode = 403 };
                    return;
                }
            }
        }

        await next();
    }
}
