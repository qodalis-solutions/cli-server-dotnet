using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Config.EfCore;

/// <summary>Extension methods for keeping the config providers' writable settings in a database.</summary>
public static class EfCoreConfigStoreExtensions
{
    /// <summary>
    /// Keeps the writable settings of the config providers (<c>PATCH /api/v1/qcli/config</c>, <c>config set</c>,
    /// <c>config_set</c>) in a database through EF Core instead of in memory, so they survive restarts and are shared by
    /// every instance of the server. The table (<c>QcliConfig</c>; <paramref name="tables"/>: <c>TablePrefix</c>,
    /// <c>Tables["Config"]</c>, <c>Schema</c>) is created on first use, by the databases plugin
    /// (<see cref="AddConfigStoreSchema"/>), or by the host's migrations with <c>modelBuilder.ApplyConfigStorageModel()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddEfCoreConfigStore(o =&gt; o.UseSqlite("Data Source=qcli.db"));
    /// </code>
    /// </example>
    public static CliBuilder AddEfCoreConfigStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<QcliTableNameOptions>? tables = null)
    {
        var names = new QcliTableNameOptions();
        tables?.Invoke(names);
        var none = new Dictionary<string, string>();
        var resolved = QcliEfTables.Resolve(names, new Dictionary<string, string> { [ConfigStorageModelBuilderExtensions.TableBaseName] = "Qcli" + ConfigStorageModelBuilderExtensions.TableBaseName });
        var renames = new QcliEfStoreRenames<ConfigStorageDbContext>(none);
        builder.Services.AddDbContextFactory<ConfigStorageDbContext>(o =>
        {
            configure(o);
            o.UseQcliTables(resolved);
        });
        builder.SetConfigStore(sp => new EfCoreConfigStore(sp.GetRequiredService<IDbContextFactory<ConfigStorageDbContext>>(),
            renames, sp.GetService<TimeProvider>()));
        return builder.AddInstalledPlugin(typeof(EfCoreConfigStore).Assembly);
    }

    /// <summary>
    /// The config table as source <c>qcli-config</c> of <paramref name="database"/> in the databases plugin (registered
    /// on the store's connection when the database is not added yet). Call <see cref="AddEfCoreConfigStore"/> too.
    /// </summary>
    public static CliDatabasesBuilder AddConfigStoreSchema(this CliDatabasesBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null) =>
        builder.AddEfCoreStore<ConfigStorageDbContext>(database, "qcli-config", "Config table", configure);
}
