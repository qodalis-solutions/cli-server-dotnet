using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>
/// Extension methods for keeping the public API's usage counters in a database.
/// </summary>
public static class EfCorePublicApiUsageStoreExtensions
{
    /// <summary>
    /// Keeps the public API's usage counters in a database through EF Core instead of in memory, so the quotas hold
    /// across every process that serves the API and usage survives restarts. Call it before or after
    /// <c>AddPublicApi</c>; a store set with <c>PublicApiOptions.SetUsageStore</c> wins. The table
    /// (<c>qcli_api_usage</c>) is created on first use, or by the host's migrations with
    /// <c>modelBuilder.ApplyPublicApiUsageModel()</c> and <c>o =&gt; o.EnsureCreated = false</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <param name="options">Optional store options.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCorePublicApiUsageStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<EfCorePublicApiUsageStoreOptions>? options = null)
    {
        var storeOptions = new EfCorePublicApiUsageStoreOptions();
        options?.Invoke(storeOptions);
        builder.Services.AddDbContextFactory<PublicApiUsageDbContext>(configure);
        builder.Services.TryAddSingleton(storeOptions);
        builder.SetPublicApiUsageStore<EfCorePublicApiUsageStore>();
        return builder.AddInstalledPlugin(typeof(EfCorePublicApiUsageStore).Assembly);
    }
}
