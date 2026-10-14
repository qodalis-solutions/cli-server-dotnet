using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Authorization;

/// <summary>
/// Shared authorization logic for host routes protected with the users plugin.
/// </summary>
public static class QcliAuthorization
{
    /// <summary>
    /// Authenticates the request (reusing the middleware's result when present) and checks
    /// <paramref name="permission"/>. Returns <c>(status, error)</c> on failure, or null on success.
    /// </summary>
    public static async Task<(int Status, string Error)?> AuthorizeAsync(HttpContext context, string? permission)
    {
        var auth = context.GetUsersAuth();
        if (auth == null)
        {
            var authService = context.RequestServices.GetService<AuthService>();
            if (authService == null) return (401, "unauthorized");
            try
            {
                auth = await authService.AuthenticateRequestAsync(context, context.RequestServices.GetService<IExternalAuthAdapter>());
            }
            catch (UsersApiException ex)
            {
                return (ex.StatusCode, ex.Message);
            }
            if (auth == null) return (401, "unauthorized");
            context.SetUsersAuth(auth);
            context.SetCliPrincipal(auth.ToCliPrincipal());
        }

        if (auth.User.MustChangePassword) return (403, AuthService.PasswordChangeRequiredMessage);
        if (permission != null && !auth.Can(permission)) return (403, $"permission denied: {permission}");
        return null;
    }
}

/// <summary>
/// Protects a host MVC controller or action with the users plugin: requires an authenticated
/// session (bearer token or external adapter) and, optionally, a permission.
/// <code>[QcliAuthorize("reports:read")]</code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class QcliAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public QcliAuthorizeAttribute() { }

    public QcliAuthorizeAttribute(string permission) => Permission = permission;

    /// <summary>Required permission; null means "authenticated".</summary>
    public string? Permission { get; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var failure = await QcliAuthorization.AuthorizeAsync(context.HttpContext, Permission);
        if (failure is { } f)
            context.Result = new JsonResult(new ErrorResponse(f.Error)) { StatusCode = f.Status };
    }
}

/// <summary>Endpoint filter for minimal APIs; see <see cref="QcliAuthorizeEndpointExtensions"/>.</summary>
public sealed class QcliAuthorizeEndpointFilter(string? permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var failure = await QcliAuthorization.AuthorizeAsync(context.HttpContext, permission);
        if (failure is { } f)
            return Results.Json(new ErrorResponse(f.Error), statusCode: f.Status);
        return await next(context);
    }
}

/// <summary>Minimal API helpers.</summary>
public static class QcliAuthorizeEndpointExtensions
{
    /// <summary>
    /// Requires a users-plugin session (and optionally <paramref name="permission"/>) for the endpoint:
    /// <code>app.MapGet("/reports", ...).RequireQcliAuthorization("reports:read");</code>
    /// </summary>
    public static TBuilder RequireQcliAuthorization<TBuilder>(this TBuilder builder, string? permission = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new QcliAuthorizeEndpointFilter(permission));
}
