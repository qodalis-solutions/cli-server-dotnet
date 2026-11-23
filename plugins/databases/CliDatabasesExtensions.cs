using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Mcp;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>Registers databases and their migration sources (<see cref="CliDatabasesExtensions.AddDatabases"/>).</summary>
public sealed partial class CliDatabasesBuilder
{
    internal List<(string Name, CliMigrationDatabaseOptions Options)> Databases { get; } = [];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex DatabaseName();

    /// <summary>
    /// Registers a database (its name is used in the API, the terminal and the dashboard), or configures one already
    /// registered.
    /// </summary>
    /// <example><c>d.AddDatabase("main", db =&gt; db.UseConnection(_ =&gt; new NpgsqlConnection(cs)).ApplyMigrationsOnStartup())</c></example>
    public CliDatabasesBuilder AddDatabase(string name, Action<CliMigrationDatabaseOptions> configure)
    {
        if (!DatabaseName().IsMatch(name)) throw new ArgumentException($"invalid database name: {name}", nameof(name));
        var existing = Databases.FirstOrDefault(d => d.Name == name).Options;
        if (existing == null)
        {
            existing = new CliMigrationDatabaseOptions();
            Databases.Add((name, existing));
        }
        configure(existing);
        return this;
    }

    private CliMigrationDatabaseOptions Database(string name) =>
        Databases.FirstOrDefault(d => d.Name == name).Options ?? throw new ArgumentException($"unknown database: {name}");

    /// <summary>Adds a source of migrations to a database; sources apply in the order they were added.</summary>
    public CliDatabasesBuilder AddSource(string database, ICliMigrationSource source) => AddSource(database, _ => source);

    /// <summary>Adds a source created from the application's services (e.g. a store's schema).</summary>
    public CliDatabasesBuilder AddSource(string database, Func<IServiceProvider, ICliMigrationSource> source)
    {
        Database(database).Sources.Add(source);
        return this;
    }

    /// <summary>Migrations from <c>NNNN_name.up.sql</c> / <c>.down.sql</c> files in <paramref name="directory"/> (source <c>app</c> unless named).</summary>
    public CliDatabasesBuilder AddSqlFolder(string database, string directory, string sourceName = "app") =>
        AddSource(database, new CliSqlFolderMigrationSource(directory, sourceName));

    /// <summary>Migrations given in code, as source <paramref name="sourceName"/> (how plugins ship their schema).</summary>
    public CliDatabasesBuilder AddMigrations(string database, string sourceName, IReadOnlyList<CliMigration> migrations) =>
        AddSource(database, new CliCodeMigrationSource(sourceName, migrations));
}

/// <summary>Adds the databases plugin to the CLI server.</summary>
public static class CliDatabasesExtensions
{
    private sealed class State
    {
        public CliDatabasesBuilder Builder { get; } = new();
    }

    /// <summary>
    /// The databases plugin: the server's relational databases (PostgreSQL, MySQL, SQL Server, SQLite) with their size,
    /// connections, load and largest tables, and their migrations, applied under a lock, recorded in
    /// <c>QcliMigrations</c> / <c>QcliMigrationRuns</c>, and shown, run and rolled back from the dashboard, the terminal
    /// (<c>databases</c>), MCP and the API at <c>/api/v1/qcli/databases</c>. Call it several times to add databases and
    /// sources (plugins add their schema this way).
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddDatabases(d =&gt; d
    ///     .AddDatabase("main", db =&gt; db.UseConnection(_ =&gt; new NpgsqlConnection(cs)).ApplyMigrationsOnStartup())
    ///     .AddSqlFolder("main", "./migrations"));
    /// </code>
    /// </example>
    public static CliBuilder AddDatabases(this CliBuilder builder, Action<CliDatabasesBuilder> configure)
    {
        var state = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(State))?.ImplementationInstance as State;
        if (state == null)
        {
            state = new State();
            builder.Services.AddSingleton(state);
            Register(builder, state);
        }
        configure(state.Builder);
        return builder;
    }

    private static void Register(CliBuilder builder, State state)
    {
        builder.AddInstalledPlugin(typeof(CliDatabases).Assembly);
        builder.AddApplicationPart(typeof(CliDatabasesController).Assembly);
        builder.AddPermissionsOnce("databases", p => p.Group("Databases")
            .Add(DatabasesPermissions.Read, "See databases, their stats and migrations")
            .Add(DatabasesPermissions.Migrate, "Apply and roll back database migrations"));
        builder.AddRoutePermission(CliDatabasesController.Prefix, DatabasesPermissions.Read, DatabasesPermissions.Migrate);

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => new CliDatabases(
            state.Builder.Databases.Select(d => new CliMigrationRunner(d.Name, d.Options, sp)).ToList(), sp));
        builder.AddProcessor<CliDatabasesCommandProcessor>();
        builder.Services.AddMcpToolProvider<DatabasesMcpToolProvider>();
        builder.Services.AddHostedService<MigrationsStartupService>();

        // The dashboard's Databases page, when the package carries its build
        var page = DatabasesDashboardPage.Files();
        if (page != null && builder.ServerOptions.CliRoutes)
        {
            builder.AddDashboardExtension(DatabasesDashboardPage.Extension(page));
            builder.Services.AddTransient<IStartupFilter>(_ => new DatabasesDashboardPage.StartupFilter(page));
        }
    }
}

/// <summary>Applies the pending migrations of the databases marked <c>ApplyOnStartup</c> before the server takes requests.</summary>
internal sealed class MigrationsStartupService(CliDatabases databases) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => databases.ApplyOnStartupAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
