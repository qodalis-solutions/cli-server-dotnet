using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Database.Postgres.PostgresDatabasePluginEntry), Id = "database-postgres", Order = CliPluginOrder.Driver)]

namespace Qodalis.Cli.Plugin.Database.Postgres;

/// <summary>
/// The PostgreSQL driver from the dashboard (or <c>cli.AddPlugin&lt;PostgresDatabasePluginEntry&gt;(new { connectionString = "..." })</c>):
/// shares its assemblies (Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL) with the plugins installed from the marketplace, so the databases plugin
/// and the EF Core stores can use the <c>postgres</c> dialect, and keeps the connection they use unless they set
/// their own (a secret in <c>plugin-settings.json</c>, read while the server starts; without it the plugin is
/// <c>needs-configuration</c>). Loads first (order Driver).
/// </summary>
public sealed class PostgresDatabasePluginEntry() : CliDatabaseDriverPluginEntry("postgres", "PostgreSQL", "Npgsql", "Npgsql.EntityFrameworkCore.PostgreSQL");
