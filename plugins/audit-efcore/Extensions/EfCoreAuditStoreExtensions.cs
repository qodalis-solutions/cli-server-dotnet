using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Audit.Extensions;

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// Extension methods for keeping the audit log in a database.
/// </summary>
public static class EfCoreAuditStoreExtensions
{
    /// <summary>
    /// Keeps the audit log in a database through EF Core instead of in memory, and adds the audit plugin. Call it
    /// before or after <c>AddUsers</c> (which keeps it). The table (<c>audit_entries</c>) is created on first use, or
    /// by the host's migrations with <c>modelBuilder.ApplyAuditStorageModel()</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreAuditStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        builder.Services.AddDbContextFactory<AuditStorageDbContext>(configure);
        builder.SetAuditStore<EfCoreAuditStore>();
        return builder.AddInstalledPlugin(typeof(EfCoreAuditStore).Assembly);
    }
}
