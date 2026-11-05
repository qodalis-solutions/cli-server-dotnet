using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// EF Core database context for job storage tables (executions, log entries, and job states).
/// </summary>
public class JobStorageDbContext : DbContext
{
    internal DbSet<JobExecutionEntity> Executions { get; set; } = null!;
    internal DbSet<JobLogEntryEntity> LogEntries { get; set; } = null!;
    internal DbSet<JobStateEntity> States { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreJobStorageExtensions.AddEfCoreJobStorage"/>.</param>
    public JobStorageDbContext(DbContextOptions<JobStorageDbContext> options) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyJobStorageModel();
    }
}

/// <summary>
/// The job storage tables as part of another model, so a host that manages its database with migrations creates
/// them in its own migrations (and can fill them from its own tables there).
/// </summary>
public static class JobStorageModelBuilderExtensions
{
    /// <summary>
    /// Adds the job storage tables (<c>job_executions</c>, <c>job_log_entries</c>, <c>job_states</c>) to
    /// <paramref name="modelBuilder"/>; call it from the host's <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyJobStorageModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobExecutionEntity>(entity =>
        {
            entity.ToTable("job_executions");
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
            entity.ToTable("job_log_entries");
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
            entity.ToTable("job_states");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.JobId).IsUnique();
            entity.Property(e => e.JobId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(32);
        });

        return modelBuilder;
    }
}
