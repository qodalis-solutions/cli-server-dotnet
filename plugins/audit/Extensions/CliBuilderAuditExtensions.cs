using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Audit.Controllers;

namespace Qodalis.Cli.Plugin.Audit.Extensions;

/// <summary>
/// Extension methods for the audit plugin on <see cref="CliBuilder"/>.
/// </summary>
public static class CliBuilderAuditExtensions
{
    /// <summary>
    /// Adds the audit log: <see cref="IAuditStore"/> (in memory, the latest 1000 entries, unless a store was set) and
    /// its API at <c>/api/v1/qcli/audit</c>, shown as the dashboard's Audit page. The users plugin adds it and records
    /// sign-ins, token and group changes; a host records its own changes through <see cref="IAuditStore"/>.
    /// Every recorded entry is published as <c>audit:recorded</c> (audience <c>audit:read</c>): the store is wrapped in
    /// <see cref="CliEventAuditStore"/>. Calling it again only wraps a store set since.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddAudit(this CliBuilder builder)
    {
        // Matched by name: plugins installed from the marketplace each load their own copy of this assembly (the users
        // plugin's, audit-efcore's), whose marker is another type; the first copy adds the routes, the others nothing
        if (!builder.Services.Any(d => d.ServiceType.FullName == typeof(AuditPluginMarker).FullName))
        {
            builder.Services.AddSingleton<AuditPluginMarker>();
            builder.AddInstalledPlugin(typeof(CliAuditController).Assembly);
            builder.AddApplicationPart(typeof(CliAuditController).Assembly);
            // The dashboard's Audit page, when the package carries its build
            builder.AddDashboardModule(CliDashboardModules.Audit, typeof(CliAuditController).Assembly);
            builder.Services.TryAddSingleton<IAuditStore, InMemoryAuditStore>();
        }

        PublishRecordedEntries(builder.Services);
        return builder;
    }

    /// <summary>
    /// Wraps the registered store in <see cref="CliEventAuditStore"/> (publishes <c>audit:recorded</c>), once: a store
    /// set later (<c>SetAuditStore</c>, the users plugin's option) is wrapped when it replaces the previous one.
    /// </summary>
    private static void PublishRecordedEntries(IServiceCollection services)
    {
        var store = services.LastOrDefault(d => d.ServiceType == typeof(IAuditStore) && !d.IsKeyedService);
        if (store == null || DecoratorOf(services) == store)
            return;

        foreach (var stale in services.Where(d => (d.ServiceType == typeof(IAuditStore) && Equals(d.ServiceKey, InnerStoreKey)) ||
                                                  IsDecoration(d)).ToList())
            services.Remove(stale);
        services.Remove(store);

        services.Add(store.ImplementationInstance != null
            ? new ServiceDescriptor(typeof(IAuditStore), InnerStoreKey, store.ImplementationInstance)
            : store.ImplementationFactory != null
                ? new ServiceDescriptor(typeof(IAuditStore), InnerStoreKey, (sp, _) => store.ImplementationFactory(sp), store.Lifetime)
                : new ServiceDescriptor(typeof(IAuditStore), InnerStoreKey, store.ImplementationType!, store.Lifetime));

        var decorator = new ServiceDescriptor(typeof(IAuditStore), sp => new CliEventAuditStore(
            sp.GetRequiredKeyedService<IAuditStore>(InnerStoreKey), sp.GetService<ICliEventBus>()), store.Lifetime);
        services.Add(decorator);
        // The decorator's descriptor under a key: framework types only, so another copy of this assembly finds it too
        services.AddKeyedSingleton<ServiceDescriptor>(DecorationKey, decorator);
    }

    /// <summary>The <see cref="CliEventAuditStore"/> registration in place (whichever copy of this assembly added it); null when none.</summary>
    private static ServiceDescriptor? DecoratorOf(IServiceCollection services) =>
        services.LastOrDefault(IsDecoration)?.KeyedImplementationInstance as ServiceDescriptor;

    private static bool IsDecoration(ServiceDescriptor d) => d.ServiceType == typeof(ServiceDescriptor) && Equals(d.ServiceKey, DecorationKey);

    /// <summary>Service key of the store <see cref="CliEventAuditStore"/> wraps.</summary>
    private const string InnerStoreKey = "Qodalis.Cli.Plugin.Audit.InnerStore";

    /// <summary>Service key of the <see cref="CliEventAuditStore"/> registration in place.</summary>
    private const string DecorationKey = "Qodalis.Cli.Plugin.Audit.Decoration";

    /// <summary>
    /// Keeps the audit log in <typeparamref name="T"/> (resolved from DI, a singleton) without adding the plugin: no
    /// routes, no dashboard page. For a store package (audit-efcore): the audit plugin, added in code or from the
    /// marketplace (before or after), keeps this store and publishes its entries. Use <see cref="SetAuditStore{T}"/>
    /// to add the plugin too.
    /// </summary>
    /// <typeparam name="T">The store implementation.</typeparam>
    /// <param name="services">The server's services.</param>
    /// <returns>The services for chaining.</returns>
    public static IServiceCollection UseAuditStore<T>(this IServiceCollection services) where T : class, IAuditStore
    {
        // Already wrapped (the plugin was added before): the wrapped store is replaced, the wrapper stays. Matched by
        // key, so a store package with its own copy of this assembly replaces the store of the plugin's copy.
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IAuditStore) && Equals(d.ServiceKey, InnerStoreKey));
        if (inner != null && DecoratorOf(services) is { } decorator && services.Contains(decorator))
        {
            foreach (var stale in services.Where(d => d.ServiceType == typeof(IAuditStore) && Equals(d.ServiceKey, InnerStoreKey)).ToList())
                services.Remove(stale);
            services.AddKeyedSingleton<IAuditStore, T>(InnerStoreKey);
            return services;
        }
        // Not added yet: the plugin keeps it (TryAdd) and wraps it when it is added
        services.RemoveAll<IAuditStore>();
        services.AddSingleton<IAuditStore, T>();
        return services;
    }

    /// <summary>Keeps the audit log in <typeparamref name="T"/> (resolved from DI, a singleton) and adds the plugin.</summary>
    /// <typeparam name="T">The store implementation.</typeparam>
    /// <param name="builder">The CLI builder instance.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder SetAuditStore<T>(this CliBuilder builder) where T : class, IAuditStore
    {
        builder.Services.RemoveAll<IAuditStore>();
        builder.Services.AddSingleton<IAuditStore, T>();
        return builder.AddAudit();
    }

    /// <summary>Keeps the audit log in <paramref name="store"/> and adds the plugin.</summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="store">The store instance.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder SetAuditStore(this CliBuilder builder, IAuditStore store)
    {
        builder.Services.RemoveAll<IAuditStore>();
        builder.Services.AddSingleton(store);
        return builder.AddAudit();
    }

    /// <summary>Marks the plugin as added.</summary>
    private sealed class AuditPluginMarker;

}
