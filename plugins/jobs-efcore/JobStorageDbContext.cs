using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Migrations.EfCore;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// EF Core database context for job storage tables (executions, log entries, and job states).
/// </summary>
public class JobStorageDbContext : DbContext
{
    private readonly QcliEfTables _tables;

    internal DbSet<JobExecutionEntity> Executions { get; set; } = null!;
    internal DbSet<JobLogEntryEntity> LogEntries { get; set; } = null!;
    internal DbSet<JobStateEntity> States { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreJobStorageExtensions.AddEfCoreJobStorage"/>.</param>
    public JobStorageDbContext(DbContextOptions<JobStorageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyJobStorageModel(
            _tables.Table(JobStorageModelBuilderExtensions.ExecutionsTable),
            _tables.Table(JobStorageModelBuilderExtensions.LogEntriesTable),
            _tables.Table(JobStorageModelBuilderExtensions.StatesTable),
            _tables.Schema);
    }
}

/// <summary>
/// The job storage tables as part of another model, so a host that manages its database with migrations creates
/// them in its own migrations (and can fill them from its own tables there).
/// </summary>
public static class JobStorageModelBuilderExtensions
{
    /// <summary>Base names of the tables (<c>QcliJobExecutions</c>, ... with the default prefix).</summary>
    public const string ExecutionsTable = "JobExecutions", LogEntriesTable = "JobLogEntries", StatesTable = "JobStates";

    /// <summary>The tables of 1.0.0-beta.29 and older, by base name, renamed on upgrade.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyTables = new Dictionary<string, string>
    {
        [ExecutionsTable] = "job_executions", [LogEntriesTable] = "job_log_entries", [StatesTable] = "job_states",
    };

    /// <summary>
    /// Adds the job storage tables (<c>QcliJobExecutions</c>, <c>QcliJobLogEntries</c>, <c>QcliJobStates</c> unless
    /// named) to <paramref name="modelBuilder"/>; call it from the host's <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyJobStorageModel(this ModelBuilder modelBuilder,
        string executionsTable = "Qcli" + ExecutionsTable, string logEntriesTable = "Qcli" + LogEntriesTable,
        string statesTable = "Qcli" + StatesTable, string? schema = null)
    {
        modelBuilder.Entity<JobExecutionEntity>(entity =>
        {
            entity.ToTable(executionsTable, schema);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId).IsUnique();
            entity.HasIndex(e => e.JobId);
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.ExecutionId).HasMaxLength(64);
            entity.Property(e => e.JobId).HasMaxLength(256);
            entity.Property(e => e.JobName).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<JobLogEntryEntity>(entity =>
        {
            entity.ToTable(logEntriesTable, schema);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId);
            entity.Property(e => e.ExecutionId).HasMaxLength(64);
            entity.Property(e => e.Level).HasMaxLength(16);
            entity.HasOne(e => e.Execution)
                .WithMany(e => e.Logs)
                .HasForeignKey(e => e.ExecutionId)
                .HasPrincipalKey(e => e.ExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobStateEntity>(entity =>
        {
            entity.ToTable(statesTable, schema);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.JobId).IsUnique();
            entity.Property(e => e.JobId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(32);
        });

        return modelBuilder;
    }
}
