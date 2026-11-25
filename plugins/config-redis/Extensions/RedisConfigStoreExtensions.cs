using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using StackExchange.Redis;

namespace Qodalis.Cli.Plugin.Config.Redis;

/// <summary>Extension methods for keeping the config providers' writable settings in Redis.</summary>
public static class RedisConfigStoreExtensions
{
    /// <summary>
    /// Keeps the writable settings of the config providers (<c>PATCH /api/v1/qcli/config</c>, <c>config set</c>,
    /// <c>config_set</c>) in Redis (<see cref="RedisConfigStore"/>), shared by every server using the same Redis: of every
    /// provider without its own store (the default store), or, with <paramref name="providerId"/>, of that provider only
    /// (more with <c>cli.SetConfigStore&lt;RedisConfigStore&gt;("users")</c>, sharing the same instance). Without
    /// <see cref="RedisConfigStoreOptions.Url"/> or <see cref="RedisConfigStoreOptions.Connection"/>, the app's
    /// <see cref="IConnectionMultiplexer"/> is resolved from the container.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddRedisConfigStore(o =&gt; o.Url = "redis://cache:6379/0");                      // every provider
    /// cli.AddRedisConfigStore(o =&gt; o.Url = "redis://cache:6379/0", providerId: "jobs");   // jobs only
    /// </code>
    /// </example>
    public static CliBuilder AddRedisConfigStore(this CliBuilder builder, Action<RedisConfigStoreOptions> configure,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.TryAddSingleton(sp =>
        {
            var options = new RedisConfigStoreOptions();
            configure(options);
            if (options.Client == null && string.IsNullOrWhiteSpace(options.Url) && options.Connection == null)
                options.Connection = sp.GetService<IConnectionMultiplexer>();
            return new RedisConfigStore(options, sp.GetService<TimeProvider>());
        });
        if (providerId == null) builder.SetConfigStore<RedisConfigStore>();
        else builder.SetConfigStore<RedisConfigStore>(providerId);
        return builder.AddInstalledPlugin(typeof(RedisConfigStore).Assembly);
    }
}
