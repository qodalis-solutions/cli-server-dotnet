using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Database.SqlServer.SqlServerDatabasePluginEntry), Id = "database-mssql", Order = CliPluginOrder.Driver)]

namespace Qodalis.Cli.Plugin.Database.SqlServer;

/// <summary>
/// The SQL Server driver from the dashboard (or <c>cli.AddPlugin&lt;SqlServerDatabasePluginEntry&gt;(new { connectionString = "..." })</c>):
/// shares its assemblies (Microsoft.Data.SqlClient, Microsoft.EntityFrameworkCore.SqlServer) with the plugins installed from the marketplace, so the databases plugin
/// and the EF Core stores can use the <c>mssql</c> dialect, and keeps the connection they use unless they set
/// their own (a secret in <c>plugin-settings.json</c>, read while the server starts; without it the plugin is
/// <c>needs-configuration</c>). Loads first (order Driver).
/// </summary>
public sealed class SqlServerDatabasePluginEntry() : CliDatabaseDriverPluginEntry("mssql", "SQL Server", "Microsoft.Data.SqlClient", "Microsoft.EntityFrameworkCore.SqlServer");
