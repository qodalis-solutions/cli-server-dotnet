using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Users.EfCore.UsersEfCorePluginEntry), Id = "users-efcore", Order = 5)]

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// The users plugin's API tokens in a database from the dashboard (or <c>cli.AddPlugin&lt;UsersEfCorePluginEntry&gt;(...)</c>):
/// loads at order 5, before the users plugin (<see cref="CliPluginOrder.Auth"/>). The dialect, connection (a secret) and
/// table prefix are settings kept in <c>plugin-settings.json</c> and read while the server starts; without a
/// connection it is <c>needs-configuration</c> and the tokens stay in memory.
/// </summary>
public sealed class UsersEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var (_, configure, tables) = context.EfStore("Users storage", "The database the API tokens are kept in", "users.db");
        if (configure != null) context.Builder.AddEfCoreApiTokenStore(configure, o => tables(o));
    }
}
