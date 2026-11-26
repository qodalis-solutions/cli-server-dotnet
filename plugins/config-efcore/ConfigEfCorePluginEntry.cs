using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Config.EfCore.ConfigEfCorePluginEntry), Id = "config-efcore", Order = CliPluginOrder.ConfigStore)]

namespace Qodalis.Cli.Plugin.Config.EfCore;

/// <summary>
/// The server's config store in a database from the dashboard (or <c>cli.AddPlugin&lt;ConfigEfCorePluginEntry&gt;(...)</c>):
/// order <see cref="CliPluginOrder.ConfigStore"/>; the settings of every provider without a store of its own are kept
/// there. Its own settings (dialect, connection a secret, table prefix) are kept in <c>plugin-settings.json</c>, never in
/// the database they point at, and read while the server starts; without a connection it is <c>needs-configuration</c>.
/// </summary>
public sealed class ConfigEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var (_, configure, tables) = context.EfStore("Config in a database", "A database as the server's config store", "config.db");
        if (configure != null) context.Builder.AddEfCoreConfigStore(configure, tables);
    }
}
