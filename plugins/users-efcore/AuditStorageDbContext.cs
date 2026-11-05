using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// EF Core database context for the audit log table.
/// </summary>
public class AuditStorageDbContext : DbContext
{
    internal DbSet<AuditEntryEntity> Entries { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreAuditStoreExtensions.AddEfCoreAuditStore"/>.</param>
    public AuditStorageDbContext(DbContextOptions<AuditStorageDbContext> options) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyAuditStorageModel();
    }
}

/// <summary>
/// The audit log table as part of another model, so a host that manages its database with migrations creates it
/// in its own migrations (and can fill it from its own tables there).
/// </summary>
public static class AuditStorageModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>audit_entries</c> table to <paramref name="modelBuilder"/>; call it from the host's
    /// <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyAuditStorageModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntryEntity>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EntryId).IsUnique();
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Event);
            entity.Property(e => e.EntryId).HasMaxLength(64);
            entity.Property(e => e.Event).HasMaxLength(64);
            entity.Property(e => e.Username).HasMaxLength(256);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.Actor).HasMaxLength(256);
            entity.Property(e => e.ActorId).HasMaxLength(128);
            entity.Property(e => e.Target).HasMaxLength(256);
        });

        return modelBuilder;
    }
}
