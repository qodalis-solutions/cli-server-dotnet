using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// Endpoint filter for minimal APIs, added only by <see cref="PublicApiEndpointExtensions.RequirePublicApi{TBuilder}"/>
/// (with its <see cref="PublicApiEndpointMetadata"/>), so every guard on an endpoint is visible in its metadata.
/// </summary>
public sealed class PublicApiEndpointFilter : IEndpointFilter
{
    private readonly string? _permission;

    internal PublicApiEndpointFilter(string? permission) => _permission = permission;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var failure = await PublicApiGuardRunner.CheckAsync(context.HttpContext, _permission);
        if (failure is { } f)
            return f.Status == StatusCodes.Status499ClientClosedRequest
                ? Results.StatusCode(f.Status)
                : Results.Json(new ErrorResponse(f.Error), statusCode: f.Status);
        return await next(context);
    }
}

/// <summary>Minimal API helpers.</summary>
public static class PublicApiEndpointExtensions
{
    /// <summary>
    /// Guards the endpoint (or every endpoint of a route group) as a public API route: only <c>host</c> tokens, with
    /// <paramref name="permission"/> when given, within the caller's quota; the caller is available through
    /// <c>HttpContext.GetQcliCaller()</c> once every check passed. Adds <see cref="PublicApiEndpointMetadata"/> (host token
    /// only) for the stacked-guard checks and the public OpenAPI document. Stacked guards (a route group and its endpoint) count the request once and every
    /// permission is checked before it is counted.
    /// <code>app.MapGet("/v1/rates", ...).RequirePublicApi("rates:read");</code>
    /// </summary>
    public static TBuilder RequirePublicApi<TBuilder>(this TBuilder builder, string? permission = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new PublicApiEndpointFilter(permission))
            .WithMetadata(new PublicApiEndpointMetadata(permission));
}

/// <summary>The metadata <see cref="PublicApiEndpointExtensions.RequirePublicApi{TBuilder}"/> adds: a host-token-only guard.</summary>
public sealed record PublicApiEndpointMetadata(string? Permission) : IQcliAuthorizeMetadata
{
    /// <summary>Always true: only <c>host</c> tokens reach a public API route.</summary>
    public bool HostTokenOnly => true;
}

internal static class PublicApiGuardRunner
{
    private const string NotConfiguredMessage = "public-api: call cli.AddPublicApi() to use RequirePublicApi";

    public static Task<(int Status, string Error)?> CheckAsync(HttpContext http, string? permission)
    {
        var guard = http.RequestServices.GetService<PublicApiGuard>()
                    ?? throw new InvalidOperationException(NotConfiguredMessage);
        return guard.CheckAsync(http, permission);
    }
}
