using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;

namespace Qodalis.Cli.Extensions;

/// <summary>Options of the config providers (<see cref="CliBuilder.ConfigureConfig"/>).</summary>
public sealed class CliConfigOptions
{
    /// <summary>Register the <c>config</c> command (default true).</summary>
    public bool Command { get; set; } = true;

    /// <summary>Which environment variables and configuration keys the <c>host</c> provider lists (secrets masked).</summary>
    public CliHostEnvironmentOptions Environment { get; set; } = new();
}

public partial class CliBuilder
{
    /// <summary>Options of the config providers.</summary>
    public CliConfigOptions ConfigOptions { get; } = new();

    /// <summary>
    /// Registers a config provider: its sections are listed by <c>GET /api/v1/qcli/config</c> (admin plugin), the
    /// <c>config</c> command and the <c>config_*</c> MCP tools; a provider that also implements
    /// <see cref="ICliConfigWriter"/> can be changed. A <see cref="CliStoredConfigProvider"/> keeps its changes in the
    /// server's config store. Ids are unique: a duplicate throws <c>config provider already registered: &lt;id&gt;</c>
    /// when the server starts; a built-in provider with the same id is replaced.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddConfigProvider&lt;BillingConfigProvider&gt;();
    /// </code>
    /// </example>
    public CliBuilder AddConfigProvider<T>() where T : class, ICliConfigProvider
    {
        Services.AddSingleton<ICliConfigProvider, T>();
        return this;
    }

    /// <summary>Registers a config provider instance (see <see cref="AddConfigProvider{T}"/>).</summary>
    public CliBuilder AddConfigProvider(ICliConfigProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.AddSingleton(provider);
        return this;
    }

    /// <summary>Registers a config provider built from the server's services (see <see cref="AddConfigProvider{T}"/>).</summary>
    public CliBuilder AddConfigProvider(Func<IServiceProvider, ICliConfigProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Keeps writable settings in <paramref name="store"/> (default in memory, lost on restart): a
    /// <see cref="JsonFileConfigStore"/>, or the database store of <c>Qodalis.Cli.Plugin.Config.EfCore</c>.
    /// </summary>
    public CliBuilder SetConfigStore(ICliConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.RemoveAll<ICliConfigStore>();
        Services.AddSingleton(store);
        return this;
    }

    /// <summary>Keeps writable settings in a store built from the server's services (see <see cref="SetConfigStore(ICliConfigStore)"/>).</summary>
    public CliBuilder SetConfigStore(Func<IServiceProvider, ICliConfigStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.RemoveAll<ICliConfigStore>();
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>Configures the config providers (the <c>config</c> command, what the <c>host</c> provider lists).</summary>
    public CliBuilder ConfigureConfig(Action<CliConfigOptions> configure)
    {
        configure(ConfigOptions);
        return this;
    }
}
