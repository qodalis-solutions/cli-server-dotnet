using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Portal;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>Registers the public-api plugin.</summary>
public static class CliBuilderPublicApiExtensions
{
    /// <summary>
    /// Adds the public-api plugin: the <c>RequirePublicApi</c> guard (host tokens, permission, per-account daily and
    /// per-minute quotas, <c>X-RateLimit-*</c> headers), usage counters per token and day (in memory unless a store is
    /// set), <c>usage</c> on the users plugin's token views, <see cref="IPublicApiUsage"/> and the portal's API section.
    /// Needs the users plugin (<c>cli.AddUsers(...)</c>), which authenticates the tokens.
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
        builder.AddInstalledPlugin(typeof(PublicApiOptions).Assembly);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

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

        services.AddSingleton<PublicApiGuard>();
        services.AddSingleton<PublicApiUsageService>();
        services.AddSingleton<IPublicApiUsage>(sp => sp.GetRequiredService<PublicApiUsageService>());
        services.AddSingleton<IApiTokenUsageProvider>(sp => sp.GetRequiredService<PublicApiUsageService>());
        services.AddSingleton(new CliPortalSection(CliPortalSection.Api, options.Title, options.BasePath));
        return builder;
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
