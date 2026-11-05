using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// Extension methods for keeping the users plugin's audit log in a database.
/// </summary>
public static class EfCoreAuditStoreExtensions
{
    /// <summary>
    /// Keeps the users plugin's audit log in a database through EF Core, replacing the in-memory store. Call it
    /// before or after <c>AddUsers</c>; called after it, it also replaces a store set there with <c>SetAuditStore</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreAuditStore(this CliBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        builder.AddInstalledPlugin(typeof(EfCoreAuditStore).Assembly);
        builder.Services.AddDbContextFactory<AuditStorageDbContext>(configure);

        builder.Services.TryAddSingleton<EfCoreAuditStore>();
        builder.Services.RemoveAll<IAuditStore>();
        builder.Services.AddSingleton<IAuditStore>(sp => sp.GetRequiredService<EfCoreAuditStore>());

        return builder;
    }
}
