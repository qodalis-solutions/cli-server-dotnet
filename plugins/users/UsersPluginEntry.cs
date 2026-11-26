using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Users.UsersPluginEntry), Id = "users", Order = CliPluginOrder.Auth)]

namespace Qodalis.Cli.Plugin.Users;

/// <summary>
/// The users plugin from the dashboard (or <c>cli.AddPlugin&lt;UsersPluginEntry&gt;(new { protectServer = true })</c>):
/// sign-in, users, groups, permissions, sessions, API tokens and the audit log. It loads first
/// (<see cref="CliPluginOrder.Auth"/>) and its middleware runs ahead of the host's pipeline, so it protects every route
/// and socket from the first request (do not also call <c>UseQodalisUsers</c>). Its setup is its settings (provider
/// <c>users-plugin</c>, kept in <c>plugin-settings.json</c> and read while the server starts; sessions, lockout and
/// passwords stay the <c>users</c> provider's). Its stores are those users-efcore (order 5) set, else in memory.
/// </summary>
public sealed class UsersPluginEntry : ICliPluginEntry
{
    /// <summary>The id of its settings provider (<c>users</c> is the provider of its sessions and passwords).</summary>
    public const string SettingsId = "users-plugin";

    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "defaultAdminPassword", Label = "root password", Description = "The seeded root user's password; empty: a random one, logged once",
            Type = CliConfigEntryTypes.Secret, RequiresRestart = true,
        },
        new() { Key = "protectServer", Label = "Protect the server", Description = "Every route and socket needs a sign-in", Type = CliConfigEntryTypes.Boolean, Default = true, RequiresRestart = true },
        new()
        {
            Key = "allowNonExpiringTokens", Label = "Tokens without expiry", Description = "API tokens may be created without an expiry date",
            Type = CliConfigEntryTypes.Boolean, Default = false, RequiresRestart = true,
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Id = SettingsId, Title = "Users (setup)", Description = "How the users plugin signs in and protects the server", Settings = [.. Settings],
        });
        context.Builder.AddUsers(o =>
        {
            o.ProtectServer = settings.Flag("protectServer", true);
            o.AllowNonExpiringTokens = settings.Flag("allowNonExpiringTokens", false);
            if (settings.Text("defaultAdminPassword") is { Length: > 0 } password) o.DefaultAdminPassword = password;
        });
        context.Services.AddTransient<IStartupFilter, UsersStartupFilter>();
    }

    /// <summary>The users middleware first in the pipeline (after CORS, so preflights are answered).</summary>
    private sealed class UsersStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseCors();
            app.UseQodalisUsers();
            next(app);
        };
    }
}
