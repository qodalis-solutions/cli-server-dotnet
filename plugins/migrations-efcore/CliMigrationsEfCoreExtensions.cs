using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.Migrations.EfCore;

/// <summary>EF Core sources on <see cref="CliMigrationsBuilder"/>.</summary>
public static class CliMigrationsEfCoreExtensions
{
    /// <summary>
    /// Adds <typeparamref name="TContext"/>'s EF Core migrations to <paramref name="database"/> (registered on the
    /// context's connection when not added yet). Applying on startup replaces the host's <c>MigrateAsync()</c>:
    /// <c>m.AddEfCore&lt;AppDbContext&gt;("main", db =&gt; db.ApplyMigrationsOnStartup())</c>.
    /// </summary>
    /// <param name="builder">The migrations builder.</param>
    /// <param name="database">The database name.</param>
    /// <param name="configure">Configures the database (e.g. apply on startup).</param>
    /// <param name="sourceName">The source's name (default <c>ef</c>).</param>
    public static CliMigrationsBuilder AddEfCore<TContext>(this CliMigrationsBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null, string sourceName = "ef") where TContext : DbContext
    {
        builder.AddDatabase(database, db =>
        {
            db.Connection ??= CliEfCoreContexts.NewConnection<TContext>;
            configure?.Invoke(db);
        });
        return builder.AddSource(database, new CliEfCoreMigrationSource<TContext>(sourceName));
    }

    /// <summary>
    /// Adds a CLI EF Core store's schema (<see cref="QcliEfStoreSchema.Source{TContext}"/>, source <paramref name="sourceName"/>) to
    /// <paramref name="database"/>, registered on the store's connection when not added yet. Used by the stores'
    /// <c>Add…Schema</c> methods.
    /// </summary>
    public static CliMigrationsBuilder AddEfCoreStore<TContext>(this CliMigrationsBuilder builder, string database, string sourceName, string description,
        Action<CliMigrationDatabaseOptions>? configure = null) where TContext : DbContext
    {
        builder.AddDatabase(database, db =>
        {
            db.Connection ??= CliEfCoreContexts.NewConnection<TContext>;
            configure?.Invoke(db);
        });
        return builder.AddSource(database, QcliEfStoreSchema.Source<TContext>(sourceName, description));
    }
}
