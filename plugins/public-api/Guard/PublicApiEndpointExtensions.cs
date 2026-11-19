using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>Endpoint filter for minimal APIs; see <see cref="PublicApiEndpointExtensions"/>.</summary>
public sealed class PublicApiEndpointFilter(string? permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var failure = await PublicApiGuardRunner.CheckAsync(context.HttpContext, permission);
        if (failure is { } f)
            return Results.Json(new ErrorResponse(f.Error), statusCode: f.Status);
        return await next(context);
    }
}

/// <summary>Minimal API helpers.</summary>
public static class PublicApiEndpointExtensions
{
    /// <summary>
    /// Guards the endpoint (or every endpoint of a route group) as a public API route: only <c>host</c> tokens, with
    /// <paramref name="permission"/> when given, within the caller's quota; the caller is available through
    /// <c>HttpContext.GetQcliCaller()</c>. Adds <see cref="QcliAuthorizeEndpointMetadata"/> (host token only) for the
    /// public OpenAPI document.
    /// <code>app.MapGet("/v1/rates", ...).RequirePublicApi("rates:read");</code>
    /// </summary>
    public static TBuilder RequirePublicApi<TBuilder>(this TBuilder builder, string? permission = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new PublicApiEndpointFilter(permission))
            .WithMetadata(new QcliAuthorizeEndpointMetadata(permission, true));
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
