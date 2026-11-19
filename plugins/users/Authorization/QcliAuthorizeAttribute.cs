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
    /// Authenticates the request (reusing the middleware's result when present), refuses anything but a <c>host</c>
    /// token when <paramref name="hostTokenOnly"/> is set, and checks <paramref name="permission"/>. Returns
    /// <c>(status, error)</c> on failure, or null on success; on success the caller is available through
    /// <see cref="QcliCallerHttpContextExtensions.GetQcliCaller"/> and, with <see cref="UsersModuleOptions.SetHostUser"/>,
    /// as <c>HttpContext.User</c> unless the host's own authentication already set a user.
    /// </summary>
    public static async Task<(int Status, string Error)?> AuthorizeAsync(HttpContext context, string? permission,
        bool hostTokenOnly = false)
    {
        var failure = await CheckAsync(context, permission, hostTokenOnly);
        if (failure == null) Admit(context);
        return failure;
    }

    /// <summary>
    /// The checks of <see cref="AuthorizeAsync"/> without admitting the caller: a guard with checks of its own (the
    /// public-api plugin's quota) calls <see cref="Admit"/> once they all passed, so a refused request never carries a
    /// caller. On success <c>GetUsersAuth()</c> holds the authenticated request.
    /// </summary>
    public static async Task<(int Status, string Error)?> CheckAsync(HttpContext context, string? permission,
        bool hostTokenOnly = false)
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
        // The audience is checked before the permission: a cli token never learns which permissions a route needs.
        if (hostTokenOnly && !auth.IsHostToken) return (403, AuthService.HostTokenRequiredMessage);
        if (permission != null && !auth.Can(permission)) return (403, $"permission denied: {permission}");
        return null;
    }

    /// <summary>
    /// Admits the request <see cref="CheckAsync"/> passed: sets <see cref="QcliCallerHttpContextExtensions.GetQcliCaller"/>
    /// and, with <see cref="UsersModuleOptions.SetHostUser"/>, <c>HttpContext.User</c> (unless the host's own
    /// authentication already set an authenticated user). Does nothing for a request that was not authenticated.
    /// </summary>
    public static void Admit(HttpContext context)
    {
        var auth = context.GetUsersAuth();
        if (auth == null) return;
        context.SetQcliCaller(auth.ToQcliCaller());
        var options = context.RequestServices.GetService<UsersModuleOptions>();
        if (options?.SetHostUser != false && context.User.Identity?.IsAuthenticated != true)
        {
            context.Items[ReplacedUserKey] = context.User;
            context.User = CliHostUser.Create(auth.ToCliPrincipal(), auth.User.Email, auth.ApiToken);
        }
    }

    /// <summary>
    /// Undoes <see cref="Admit"/>: removes the caller and gives <c>HttpContext.User</c> back the principal it had
    /// before, when <see cref="Admit"/> replaced it. A guard stacked after a users guard calls it when it refuses the
    /// request, so a refused request never carries a caller. It restores the principal that was current before the
    /// guard admitted the request. Does nothing for a request that was not admitted.
    /// </summary>
    public static void Revoke(HttpContext context)
    {
        context.Items.Remove(QcliCallerHttpContextExtensions.ItemKey);
        if (context.Items.TryGetValue(ReplacedUserKey, out var previous) && previous is System.Security.Claims.ClaimsPrincipal user)
        {
            context.Items.Remove(ReplacedUserKey);
            context.User = user;
        }
    }

    private static readonly object ReplacedUserKey = new();
}

/// <summary>
/// What a users-plugin guard requires, left on the endpoint so the public OpenAPI document can show it
/// (<c>x-required-permission</c>, <c>x-host-token-only</c>).
/// </summary>
public interface IQcliAuthorizeMetadata
{
    /// <summary>The required permission; null means "authenticated".</summary>
    string? Permission { get; }

    /// <summary>Only <c>host</c> tokens are accepted.</summary>
    bool HostTokenOnly { get; }
}

/// <summary>The metadata <see cref="QcliAuthorizeEndpointExtensions.RequireQcliAuthorization{TBuilder}"/> adds.</summary>
public sealed record QcliAuthorizeEndpointMetadata(string? Permission, bool HostTokenOnly) : IQcliAuthorizeMetadata;

/// <summary>
/// Protects a host MVC controller or action with the users plugin: requires an authenticated
/// session (bearer token or external adapter) and, optionally, a permission.
/// <code>[QcliAuthorize("reports:read")]</code>
/// With <see cref="HostTokenOnly"/> only <c>host</c> tokens are accepted:
/// <code>[QcliAuthorize("rates:read", HostTokenOnly = true)]</code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class QcliAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter, IQcliAuthorizeMetadata
{
    public QcliAuthorizeAttribute() { }

    public QcliAuthorizeAttribute(string permission) => Permission = permission;

    /// <summary>Required permission; null means "authenticated".</summary>
    public string? Permission { get; }

    /// <summary>Refuse sessions, external-adapter callers and <c>cli</c> tokens (403 <c>forbidden: a host token is required</c>).</summary>
    public bool HostTokenOnly { get; init; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var failure = await QcliAuthorization.AuthorizeAsync(context.HttpContext, Permission, HostTokenOnly);
        if (failure is { } f)
            context.Result = new JsonResult(new ErrorResponse(f.Error)) { StatusCode = f.Status };
    }
}

/// <summary>Endpoint filter for minimal APIs; see <see cref="QcliAuthorizeEndpointExtensions"/>.</summary>
public sealed class QcliAuthorizeEndpointFilter(string? permission, bool hostTokenOnly = false) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var failure = await QcliAuthorization.AuthorizeAsync(context.HttpContext, permission, hostTokenOnly);
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
    /// With <paramref name="hostTokenOnly"/> only <c>host</c> tokens are accepted. Also adds
    /// <see cref="QcliAuthorizeEndpointMetadata"/> for the public OpenAPI document.
    /// </summary>
    public static TBuilder RequireQcliAuthorization<TBuilder>(this TBuilder builder, string? permission = null,
        bool hostTokenOnly = false)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new QcliAuthorizeEndpointFilter(permission, hostTokenOnly))
            .WithMetadata(new QcliAuthorizeEndpointMetadata(permission, hostTokenOnly));
}
