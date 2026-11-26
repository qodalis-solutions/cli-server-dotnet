using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.PublicApi.EfCore.PublicApiEfCorePluginEntry), Id = "public-api-efcore")]

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>
/// The public API's usage in a database from the dashboard (or <c>cli.AddPlugin&lt;PublicApiEfCorePluginEntry&gt;(...)</c>).
/// The dialect, connection (a secret) and table prefix are settings kept in <c>plugin-settings.json</c> and read while
/// the server starts; without a connection it is <c>needs-configuration</c>.
/// </summary>
public sealed class PublicApiEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var (_, configure, tables) = context.EfStore("Public API usage storage", "The database the public API's usage is kept in", "public-api.db");
        if (configure != null) context.Builder.AddEfCorePublicApiUsageStore(configure, o => tables(o));
    }
}
