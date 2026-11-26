using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Audit.EfCore.AuditEfCorePluginEntry), Id = "audit-efcore")]

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// The audit log in a database from the dashboard (or <c>cli.AddPlugin&lt;AuditEfCorePluginEntry&gt;(new { connectionString = "audit.db" })</c>):
/// the audit plugin with its entries kept through EF Core. The dialect, connection (a secret) and table prefix are
/// settings kept in <c>plugin-settings.json</c> and read while the server starts; without a connection it is
/// <c>needs-configuration</c>.
/// </summary>
public sealed class AuditEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var (_, configure, tables) = context.EfStore("Audit storage", "The database the audit log is kept in", "audit.db");
        if (configure != null) context.Builder.AddEfCoreAuditStore(configure, o => tables(o));
    }
}
