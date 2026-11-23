using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// Extension methods for keeping the users plugin's API tokens in a database.
/// </summary>
public static class EfCoreApiTokenStoreExtensions
{
    /// <summary>
    /// Keeps the users plugin's API tokens (<c>token create</c>, terminals linked with <c>server add</c>) in a database
    /// through EF Core instead of in memory, so they survive restarts. Call it before or after <c>AddUsers</c>; a store
    /// set with <c>SetApiTokenStore</c> in <c>AddUsers</c> wins. The table (<c>QcliApiTokens</c>) is created on first use
    /// (renaming the <c>api_tokens</c> of 1.0.0-beta.29 and older), by the databases plugin
    /// (<see cref="AddApiTokenStoreSchema"/>), or by the host's migrations with <c>modelBuilder.ApplyApiTokenStorageModel()</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <param name="options">Optional store options: the table's name (<c>TablePrefix</c>, <c>Tables</c>, <c>Schema</c>,
    /// <c>LegacyTableNames</c>), or <c>o =&gt; o.UpgradeSchema = false</c> when the host's migrations own the table.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreApiTokenStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<EfCoreApiTokenStoreOptions>? options = null)
    {
        var storeOptions = new EfCoreApiTokenStoreOptions();
        options?.Invoke(storeOptions);
        var legacy = new Dictionary<string, string> { [ApiTokenStorageModelBuilderExtensions.TableBaseName] = ApiTokenStorageModelBuilderExtensions.LegacyTableName };
        var tables = QcliEfTables.Resolve(storeOptions, legacy);
        var renames = QcliEfStoreRenames<ApiTokenStorageDbContext>.For(tables, storeOptions, legacy);
        builder.Services.AddDbContextFactory<ApiTokenStorageDbContext>(o =>
        {
            configure(o);
            o.UseQcliTables(tables);
        });
        builder.Services.AddSingleton(renames);
        builder.Services.RemoveAll<IApiTokenStore>();
        builder.Services.AddSingleton<IApiTokenStore>(sp => new EfCoreApiTokenStore(
            sp.GetRequiredService<IDbContextFactory<ApiTokenStorageDbContext>>(), sp.GetService<TimeProvider>(), storeOptions, renames));
        return builder.AddInstalledPlugin(typeof(EfCoreApiTokenStore).Assembly);
    }

    /// <summary>
    /// The API token table as source <c>qcli-api-tokens</c> of <paramref name="database"/> in the databases plugin
    /// (registered on the store's connection when the database is not added yet). Call
    /// <see cref="AddEfCoreApiTokenStore"/> too.
    /// </summary>
    public static CliDatabasesBuilder AddApiTokenStoreSchema(this CliDatabasesBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null) =>
        builder.AddEfCoreStore<ApiTokenStorageDbContext>(database, "qcli-api-tokens", "API token table", configure);
}
