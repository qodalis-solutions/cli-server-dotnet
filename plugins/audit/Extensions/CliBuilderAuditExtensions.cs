using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Extensions;
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
        if (!builder.Services.Any(d => d.ServiceType == typeof(AuditPluginMarker)))
        {
            builder.Services.AddSingleton<AuditPluginMarker>();
            builder.AddInstalledPlugin(typeof(CliAuditController).Assembly);
            builder.AddApplicationPart(typeof(CliAuditController).Assembly);
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
        if (store == null || services.Any(d => d.ServiceType == typeof(AuditStoreDecoration) &&
                                               ((AuditStoreDecoration)d.ImplementationInstance!).Descriptor == store))
            return;

        foreach (var stale in services.Where(d => (d.ServiceType == typeof(IAuditStore) && Equals(d.ServiceKey, InnerStoreKey)) ||
                                                  d.ServiceType == typeof(AuditStoreDecoration)).ToList())
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
        services.AddSingleton(new AuditStoreDecoration(decorator));
    }

    /// <summary>Service key of the store <see cref="CliEventAuditStore"/> wraps.</summary>
    private const string InnerStoreKey = "Qodalis.Cli.Plugin.Audit.InnerStore";

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

    /// <summary>The <see cref="CliEventAuditStore"/> registration currently in place.</summary>
    private sealed record AuditStoreDecoration(ServiceDescriptor Descriptor);
}
