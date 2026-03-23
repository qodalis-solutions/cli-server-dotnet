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
    }
}
