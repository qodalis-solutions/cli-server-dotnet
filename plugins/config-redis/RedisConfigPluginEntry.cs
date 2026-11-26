using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Config.Redis.RedisConfigPluginEntry), Id = "config-redis", Order = CliPluginOrder.ConfigStore)]

namespace Qodalis.Cli.Plugin.Config.Redis;

/// <summary>
/// The config-redis plugin from the dashboard (or <c>cli.AddPlugin&lt;RedisConfigPluginEntry&gt;(new { url = "redis://cache:6379/0" })</c>):
/// Redis becomes the server's default config store (order <see cref="CliPluginOrder.ConfigStore"/>), so the settings of
/// every provider without a store of its own are kept there. Its own settings (the URL a secret) are kept in
/// <c>&lt;data dir&gt;/plugin-settings.json</c>, never in Redis itself, and read while the server starts. Without a URL
/// it is <c>needs-configuration</c> and the settings stay where they were; a change applies after a restart.
/// </summary>
public sealed class RedisConfigPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "url", Label = "Redis URL", Description = "redis://[:password@]host:6379/0 (rediss:// for TLS); kept as a secret, never returned",
            Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true,
        },
        new()
        {
            Key = "keyPrefix", Label = "Key prefix", Description = "Prepended to every key: <prefix><provider>:<section>", Type = CliConfigEntryTypes.String,
            Default = RedisConfigStore.DefaultKeyPrefix, RequiresRestart = true, Validation = new CliConfigValidation { Max = 200 },
        },
        new()
        {
            Key = "ttlSeconds", Label = "Expiry (seconds)", Description = "A section expires this long after its last change; 0: never",
            Type = CliConfigEntryTypes.Number, Default = 0, RequiresRestart = true, Validation = new CliConfigValidation { Min = 0 },
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions { Title = "Config on Redis", Description = "Redis as the server's config store", Settings = [.. Settings] });
        var url = settings.Text("url");
        if (url.Length == 0) return; // needs-configuration: the settings stay in their store
        var prefix = settings.Text("keyPrefix");
        var ttl = (int)settings.Number("ttlSeconds", 0);
        context.Builder.AddRedisConfigStore(o =>
        {
            o.Url = url;
            if (prefix.Length > 0) o.KeyPrefix = prefix;
            if (ttl > 0) o.TtlSeconds = ttl;
        });
    }
}
