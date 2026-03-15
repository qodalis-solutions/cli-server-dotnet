using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

public static class EfCoreJobStorageExtensions
{
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
