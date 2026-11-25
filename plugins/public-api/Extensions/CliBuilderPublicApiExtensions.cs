using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Portal;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>Registers the public-api plugin.</summary>
public static class CliBuilderPublicApiExtensions
{
    /// <summary>
    /// Adds the public-api plugin: the <c>RequirePublicApi</c> guard (host tokens, permission, per-account daily and
    /// per-minute quotas, <c>X-RateLimit-*</c> headers), usage counters per token and day (in memory unless a store is
    /// set), <c>usage</c> on the users plugin's token views, <see cref="IPublicApiUsage"/> and the portal's API section.
    /// Needs the users plugin (<c>cli.AddUsers(...)</c>), which authenticates the tokens. The public OpenAPI document is
    /// the host's <c>services.AddOpenApi("public", ...)</c> (<see cref="PublicApiOptions.DocumentName"/>), served by
    /// <see cref="UseQodalisPublicApi"/>; the plugin adds the bearer scheme and each guarded operation's permission.
    /// </summary>
    public static CliBuilder AddPublicApi(this CliBuilder builder, Action<PublicApiOptions>? configure = null)
    {
        var options = new PublicApiOptions();
        configure?.Invoke(options);
        if (!PublicApiOptions.IsValidBasePath(options.BasePath))
            throw new ArgumentException($"invalid public-api base path: {options.BasePath}");
        if (string.IsNullOrWhiteSpace(options.DocumentName))
            throw new ArgumentException("public-api: DocumentName is required");

        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(UsageOnlyMarker)))
            throw new InvalidOperationException(BothMessage);
        services.AddSingleton(options);
        RegisterUsage(builder, options);

        services.AddSingleton<PublicApiGuard>();
        services.AddSingleton(new CliPortalSection(CliPortalSection.Api, options.Title, options.BasePath));

        // After the host's own transformers (PostConfigure), so the host's values win.
        services.PostConfigure<OpenApiOptions>(options.DocumentName.ToLowerInvariant(), o =>
        {
            o.AddOperationTransformer(PublicApiDocumentTransformers.AddGuardExtensions);
            o.AddDocumentTransformer(PublicApiDocumentTransformers.AddSecurity);
        });
        return builder;
    }

    /// <summary>
    /// Adds only the usage side of the plugin, for the main application of a two-service host that creates the tokens
    /// and shows "requests today" but serves no public routes: <see cref="IPublicApiUsage"/>, <c>usage</c> on the users
    /// plugin's token views, over the usage store (in memory unless one is set, e.g. <c>AddEfCorePublicApiUsageStore</c>
    /// in either order) and the quota resolver. No guard, no OpenAPI document, no portal section.
    /// <see cref="AddPublicApi"/> includes it.
    /// </summary>
    public static CliBuilder AddPublicApiUsage(this CliBuilder builder, Action<PublicApiUsageOptions>? configure = null)
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(PublicApiOptions)))
            throw new InvalidOperationException(BothMessage);
        var options = new PublicApiUsageOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton<UsageOnlyMarker>();
        return RegisterUsage(builder, options);
    }

    private const string BothMessage =
        "public-api: call AddPublicApi or AddPublicApiUsage, not both (AddPublicApi already includes the usage; " +
        "use AddPublicApiUsage only in a service that serves no public routes)";

    /// <summary>Registered by <see cref="AddPublicApiUsage"/>, so <see cref="AddPublicApi"/> can refuse the pair.</summary>
    private sealed class UsageOnlyMarker;

    private static CliBuilder RegisterUsage(CliBuilder builder, PublicApiUsageOptions options)
    {
        var services = builder.Services;
        builder.AddInstalledPlugin(typeof(PublicApiOptions).Assembly);
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<PublicApiUsageOptions>();
        services.AddSingleton(options);

        if (options.UsageStoreFactory is { } factory)
        {
            services.RemoveAll<IPublicApiUsageStore>();
            services.AddSingleton(factory);
        }
        else
        {
            services.TryAddSingleton<IPublicApiUsageStore, InMemoryPublicApiUsageStore>();
        }
        if (options.QuotaResolverType != null)
            services.TryAddScoped(options.QuotaResolverType);

        services.RemoveAll<PublicApiUsageService>();
        services.RemoveAll<IPublicApiUsage>();
        services.RemoveAll<IApiTokenUsageProvider>();
        services.AddSingleton<PublicApiUsageService>();
        services.AddSingleton<IPublicApiUsage>(sp => sp.GetRequiredService<PublicApiUsageService>());
        services.AddSingleton<IApiTokenUsageProvider>(sp => sp.GetRequiredService<PublicApiUsageService>());

        // The api command (api usage): your own usage, another account's with tokens:read
        services.AddHttpContextAccessor();
        builder.AddProcessor<CliApiCommandProcessor>();
        return builder;
    }

    /// <summary>
    /// Serves the public OpenAPI document at <c>&lt;BasePath&gt;/openapi.json</c> (anonymous, <c>Cache-Control:
    /// no-cache</c>, <c>Access-Control-Allow-Origin: *</c>) and Swagger UI for it at <c>&lt;BasePath&gt;/swagger</c>.
    /// Call after <c>UseRouting()</c> and before <c>UseEndpoints</c>/<c>MapControllers</c>. Throws
    /// <see cref="InvalidOperationException"/> when the host has no OpenAPI document named
    /// <see cref="PublicApiOptions.DocumentName"/>.
    /// </summary>
    public static IApplicationBuilder UseQodalisPublicApi(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var options = services.GetService<PublicApiOptions>()
                      ?? throw new InvalidOperationException("public-api: call cli.AddPublicApi() before UseQodalisPublicApi()");
        var provider = services.GetKeyedService<IOpenApiDocumentProvider>(options.DocumentName.ToLowerInvariant())
                       ?? throw new InvalidOperationException(
                           $"public-api: no OpenAPI document named '{options.DocumentName}'; call services.AddOpenApi(\"{options.DocumentName}\")");

        var endpoint = new PublicApiDocumentEndpoint(options, provider, services.GetService<IServer>());
        app.Use((context, next) => endpoint.InvokeAsync(context, () => next(context)));
        app.UseSwaggerUI(endpoint.CreateSwaggerUIOptions());
        return app;
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> (constructed via DI, singleton) as the public API's usage store, whether called
    /// before or after <see cref="AddPublicApi"/> (storage packages call it, e.g. <c>AddEfCorePublicApiUsageStore</c>).
    /// </summary>
    public static CliBuilder SetPublicApiUsageStore<T>(this CliBuilder builder) where T : class, IPublicApiUsageStore
    {
        builder.Services.RemoveAll<IPublicApiUsageStore>();
        builder.Services.AddSingleton<IPublicApiUsageStore, T>();
        return builder;
    }
}
