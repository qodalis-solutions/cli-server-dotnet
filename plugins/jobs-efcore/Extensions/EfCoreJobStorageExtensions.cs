using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// Extension methods for configuring EF Core-based job storage on <see cref="CliBuilder"/>.
/// </summary>
public static class EfCoreJobStorageExtensions
{
    /// <summary>
    /// Registers the EF Core job storage provider, replacing the default in-memory provider.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreJobStorage(this CliBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        builder.Services.AddDbContextFactory<JobStorageDbContext>(configure);

        RemoveService(builder.Services, typeof(ICliJobStorageProvider));
        builder.Services.AddSingleton<ICliJobStorageProvider, EfCoreJobStorageProvider>();

        return builder;
    }

    private static void RemoveService(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }
}
