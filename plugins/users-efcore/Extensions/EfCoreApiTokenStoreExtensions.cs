using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// Extension methods for keeping the users plugin's API tokens in a database.
/// </summary>
public static class EfCoreApiTokenStoreExtensions
{
    /// <summary>
    /// Keeps the users plugin's API tokens (<c>token create</c>, terminals linked with <c>server add</c>) in a database
    /// through EF Core instead of in memory, so they survive restarts. Call it before or after <c>AddUsers</c>; a store
    /// set with <c>SetApiTokenStore</c> in <c>AddUsers</c> wins. The table (<c>api_tokens</c>) is created on first use,
    /// or by the host's migrations with <c>modelBuilder.ApplyApiTokenStorageModel()</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreApiTokenStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        builder.Services.AddDbContextFactory<ApiTokenStorageDbContext>(configure);
        builder.Services.RemoveAll<IApiTokenStore>();
        builder.Services.AddSingleton<IApiTokenStore>(sp => new EfCoreApiTokenStore(
            sp.GetRequiredService<IDbContextFactory<ApiTokenStorageDbContext>>(), sp.GetService<TimeProvider>()));
        return builder.AddInstalledPlugin(typeof(EfCoreApiTokenStore).Assembly);
    }
}
