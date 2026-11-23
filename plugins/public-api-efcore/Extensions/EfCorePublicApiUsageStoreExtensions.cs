using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Migrations;
using Qodalis.Cli.Plugin.Migrations.EfCore;

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>
/// Extension methods for keeping the public API's usage counters in a database.
/// </summary>
public static class EfCorePublicApiUsageStoreExtensions
{
    /// <summary>
    /// Keeps the public API's usage counters in a database through EF Core instead of in memory, so the quotas hold
    /// across every process that serves the API and usage survives restarts. Call it before or after
    /// <c>AddPublicApi</c>; a store set with <c>PublicApiOptions.SetUsageStore</c> wins. The table
    /// (<c>QcliApiUsage</c>) is created on first use (renaming the <c>qcli_api_usage</c> of 1.0.0-beta.29), by the
    /// migrations plugin (<see cref="AddPublicApiUsageSchema"/>), or by the host's migrations with
    /// <c>modelBuilder.ApplyPublicApiUsageModel()</c> and <c>o =&gt; o.EnsureCreated = false</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <param name="options">Optional store options: the table's name (<c>TablePrefix</c>, <c>Tables</c>, <c>Schema</c>,
    /// <c>LegacyTableNames</c>), <c>EnsureCreated</c>.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCorePublicApiUsageStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<EfCorePublicApiUsageStoreOptions>? options = null)
    {
        var storeOptions = new EfCorePublicApiUsageStoreOptions();
        options?.Invoke(storeOptions);
        var legacy = new Dictionary<string, string> { [PublicApiUsageModelBuilderExtensions.TableBaseName] = PublicApiUsageModelBuilderExtensions.LegacyTableName };
        var tables = QcliEfTables.Resolve(storeOptions, legacy);
        builder.Services.AddDbContextFactory<PublicApiUsageDbContext>(o =>
        {
            configure(o);
            o.UseQcliTables(tables);
        });
        builder.Services.TryAddSingleton(storeOptions);
        builder.Services.TryAddSingleton(QcliEfStoreRenames<PublicApiUsageDbContext>.For(tables, storeOptions, legacy));
        builder.SetPublicApiUsageStore<EfCorePublicApiUsageStore>();
        return builder.AddInstalledPlugin(typeof(EfCorePublicApiUsageStore).Assembly);
    }

    /// <summary>
    /// The usage table as source <c>qcli-api-usage</c> of <paramref name="database"/> in the migrations plugin
    /// (registered on the store's connection when the database is not added yet). Call
    /// <see cref="AddEfCorePublicApiUsageStore"/> too.
    /// </summary>
    public static CliMigrationsBuilder AddPublicApiUsageSchema(this CliMigrationsBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null) =>
        builder.AddEfCoreStore<PublicApiUsageDbContext>(database, "qcli-api-usage", "public API usage table", configure);
}
