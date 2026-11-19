using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Authorization;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>Options of the public-api plugin (<c>cli.AddPublicApi(o => ...)</c>).</summary>
public sealed class PublicApiOptions
{
    /// <summary>
    /// Where the OpenAPI document (<c>&lt;base&gt;/openapi.json</c>) and Swagger UI (<c>&lt;base&gt;/swagger</c>) are
    /// served. Starts with <c>/</c>, no trailing <c>/</c>, not <c>/</c> itself. Default <c>/qcli/api</c>.
    /// </summary>
    public string BasePath { get; set; } = "/qcli/api";

    /// <summary>The named document of <c>services.AddOpenApi(name)</c> that is published. Default <c>public</c>.</summary>
    public string DocumentName { get; set; } = "public";

    /// <summary>The API section's label on the portal (the document's <c>info.title</c> stays the document's). Default <c>API reference</c>.</summary>
    public string Title { get; set; } = "API reference";

    /// <summary>The document's server URL when the host's document has no <c>servers</c>; null = the request's <c>scheme://host[pathBase]</c>.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Days of per-day counters kept (usage views need 30). Default 31.</summary>
    public int UsageRetentionDays { get; set; } = 31;

    internal Func<QcliCaller, IServiceProvider, CancellationToken, ValueTask<PublicApiQuota>>? QuotaResolver { get; private set; }
    internal Type? QuotaResolverType { get; private set; }
    internal Func<IServiceProvider, IPublicApiUsageStore>? UsageStoreFactory { get; private set; }

    /// <summary>
    /// Resolves quotas with <typeparamref name="T"/>, registered as a scoped service (unless the host registered it)
    /// and resolved from the request's services for every guarded request.
    /// </summary>
    public PublicApiOptions UseQuotaResolver<T>() where T : class, IPublicApiQuotaResolver
    {
        QuotaResolverType = typeof(T);
        QuotaResolver = null;
        return this;
    }

    /// <summary>Resolves quotas with <paramref name="resolver"/>; the service provider is the request's.</summary>
    public PublicApiOptions UseQuotaResolver(Func<QcliCaller, IServiceProvider, CancellationToken, ValueTask<PublicApiQuota>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        QuotaResolver = resolver;
        QuotaResolverType = null;
        return this;
    }

    /// <summary>Uses <typeparamref name="T"/> (constructed via DI, singleton) as the usage store.</summary>
    public PublicApiOptions SetUsageStore<T>() where T : class, IPublicApiUsageStore
    {
        UsageStoreFactory = sp => ActivatorUtilities.GetServiceOrCreateInstance<T>(sp);
        return this;
    }

    /// <summary>Uses the store <paramref name="factory"/> builds (singleton) as the usage store.</summary>
    public PublicApiOptions SetUsageStore(Func<IServiceProvider, IPublicApiUsageStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        UsageStoreFactory = factory;
        return this;
    }

    /// <summary>The caller's quota from the configured resolver; <see cref="PublicApiQuota.Unlimited"/> without one.</summary>
    internal async ValueTask<PublicApiQuota> ResolveQuotaAsync(QcliCaller caller, IServiceProvider services, CancellationToken ct)
    {
        if (QuotaResolver != null) return await QuotaResolver(caller, services, ct) ?? PublicApiQuota.Unlimited;
        if (QuotaResolverType != null)
            return await ((IPublicApiQuotaResolver)services.GetRequiredService(QuotaResolverType)).GetQuotaAsync(caller, ct)
                   ?? PublicApiQuota.Unlimited;
        return PublicApiQuota.Unlimited;
    }

    /// <summary>True for a valid base path: starts with <c>/</c>, no trailing <c>/</c>, not <c>/</c>, no whitespace, <c>?</c> or <c>#</c>.</summary>
    internal static bool IsValidBasePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && path.Length > 1 && path[^1] != '/' &&
        !path.Contains("//", StringComparison.Ordinal) && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '#' or '\\');
}
