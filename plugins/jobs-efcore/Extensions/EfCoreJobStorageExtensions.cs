using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// Extension methods for configuring EF Core-based job storage on <see cref="CliBuilder"/>.
/// </summary>
public static class EfCoreJobStorageExtensions
{
    /// <summary>
    /// Registers the EF Core job storage provider, replacing the default in-memory provider. The tables
    /// (<c>QcliJobExecutions</c>, <c>QcliJobLogEntries</c>, <c>QcliJobStates</c>) are created on first use (renaming the
    /// <c>job_*</c> tables of 1.0.0-beta.29 and older), by the databases plugin (<see cref="AddJobStorageSchema"/>),
    /// or by the host's migrations with <c>modelBuilder.ApplyJobStorageModel()</c>.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Callback to configure the <see cref="DbContextOptionsBuilder"/> (e.g., connection string).</param>
    /// <param name="options">The tables' names (<c>TablePrefix</c>, <c>Tables</c>, <c>Schema</c>, <c>LegacyTableNames</c>).</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddEfCoreJobStorage(this CliBuilder builder, Action<DbContextOptionsBuilder> configure,
        Action<EfCoreJobStorageOptions>? options = null)
    {
        var storeOptions = new EfCoreJobStorageOptions();
        options?.Invoke(storeOptions);
        var tables = QcliEfTables.Resolve(storeOptions, JobStorageModelBuilderExtensions.LegacyTables);
        builder.AddInstalledPlugin(typeof(JobStorageDbContext).Assembly);
        builder.Services.AddDbContextFactory<JobStorageDbContext>(o =>
        {
            configure(o);
            o.UseQcliTables(tables);
        });
        builder.Services.AddSingleton(QcliEfStoreRenames<JobStorageDbContext>.For(tables, storeOptions, JobStorageModelBuilderExtensions.LegacyTables));

        RemoveService(builder.Services, typeof(ICliJobStorageProvider));
        builder.Services.AddSingleton<ICliJobStorageProvider, EfCoreJobStorageProvider>();

        return builder;
    }

    /// <summary>
    /// The job storage tables as source <c>qcli-jobs</c> of <paramref name="database"/> in the databases plugin
    /// (registered on the store's connection when the database is not added yet). Call
    /// <see cref="AddEfCoreJobStorage"/> too.
    /// </summary>
    public static CliDatabasesBuilder AddJobStorageSchema(this CliDatabasesBuilder builder, string database,
        Action<CliMigrationDatabaseOptions>? configure = null) =>
        builder.AddEfCoreStore<JobStorageDbContext>(database, "qcli-jobs", "job runs, logs and states tables", configure);

    private static void RemoveService(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }
}
