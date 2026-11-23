using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Migrations;
using Qodalis.Cli.Plugin.Migrations.EfCore;
using Qodalis.Cli.Plugin.Audit.Extensions;

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// Extension methods for keeping the audit log in a database.
/// </summary>
public static class EfCoreAuditStoreExtensions
{
    /// <summary>
    /// Keeps the audit log in a database through EF Core instead of in memory, and adds the audit plugin. Call it
    /// before or after <c>AddUsers</c> (which keeps it). The table (<c>QcliAuditEntries</c>) is created on first use
    /// (renaming the <c>audit_entries</c> of 1.0.0-beta.29 and older), by the migrations plugin
    /// (<see cref="AddAuditStoreSchema"/>), or by the host's migrations with <c>modelBuilder.ApplyAuditStorageModel()</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <param name="options">The table's name (<c>TablePrefix</c>, <c>Tables</c>, <c>Schema</c>, <c>LegacyTableNames</c>).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreAuditStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<EfCoreAuditStoreOptions>? options = null)
    {
        var storeOptions = new EfCoreAuditStoreOptions();
        options?.Invoke(storeOptions);
        var legacy = new Dictionary<string, string> { [AuditStorageModelBuilderExtensions.TableBaseName] = AuditStorageModelBuilderExtensions.LegacyTableName };
        var tables = QcliEfTables.Resolve(storeOptions, legacy);
        builder.Services.AddDbContextFactory<AuditStorageDbContext>(o =>
        {
            configure(o);
            o.UseQcliTables(tables);
        });
        builder.Services.AddSingleton(QcliEfStoreRenames<AuditStorageDbContext>.For(tables, storeOptions, legacy));
        builder.SetAuditStore<EfCoreAuditStore>();
        return builder.AddInstalledPlugin(typeof(EfCoreAuditStore).Assembly);
    }

    /// <summary>
    /// The audit log's table as source <c>qcli-audit</c> of <paramref name="database"/> in the migrations plugin
    /// (registered on the store's connection when the database is not added yet), so the dashboard shows it and
    /// startup applies it: <c>cli.AddMigrations(m =&gt; m.AddAuditStoreSchema("cli", db =&gt; db.ApplyMigrationsOnStartup()))</c>.
    /// Call <see cref="AddEfCoreAuditStore"/> too.
    /// </summary>
    public static CliMigrationsBuilder AddAuditStoreSchema(this CliMigrationsBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null) =>
        builder.AddEfCoreStore<AuditStorageDbContext>(database, "qcli-audit", "audit log table", configure);
}
