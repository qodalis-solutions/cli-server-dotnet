using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Audit;
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
    /// Calling it again does nothing.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddAudit(this CliBuilder builder)
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(AuditPluginMarker)))
            return builder;

        builder.Services.AddSingleton<AuditPluginMarker>();
        builder.AddInstalledPlugin(typeof(CliAuditController).Assembly);
        builder.AddApplicationPart(typeof(CliAuditController).Assembly);
        builder.Services.TryAddSingleton<IAuditStore, InMemoryAuditStore>();
        return builder;
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
