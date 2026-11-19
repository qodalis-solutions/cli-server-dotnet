using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// Guards a host MVC controller or action as a public API route: a <c>host</c> token, the permission and the caller's
/// quota (see <see cref="PublicApiEndpointExtensions.RequirePublicApi{TBuilder}"/>).
/// <code>[RequirePublicApi("rates:read")]</code>
/// On a controller and on its action both apply: every permission is checked and the request is counted once.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequirePublicApiAttribute : Attribute, IAsyncAuthorizationFilter, IQcliAuthorizeMetadata
{
    public RequirePublicApiAttribute() { }

    public RequirePublicApiAttribute(string permission) => Permission = permission;

    /// <summary>Required permission; null means any host token.</summary>
    public string? Permission { get; }

    /// <summary>Always true: only <c>host</c> tokens reach a public API route.</summary>
    public bool HostTokenOnly => true;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var failure = await PublicApiGuardRunner.CheckAsync(context.HttpContext, Permission);
        if (failure is { } f)
            context.Result = f.Status == StatusCodes.Status499ClientClosedRequest
                ? new StatusCodeResult(f.Status)
                : new JsonResult(new ErrorResponse(f.Error)) { StatusCode = f.Status };
    }
}
