using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// EF Core database context for the audit log table.
/// </summary>
public class AuditStorageDbContext : DbContext
{
    private readonly QcliEfTables _tables;

    internal DbSet<AuditEntryEntity> Entries { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreAuditStoreExtensions.AddEfCoreAuditStore"/>.</param>
    public AuditStorageDbContext(DbContextOptions<AuditStorageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyAuditStorageModel(_tables.Table(AuditStorageModelBuilderExtensions.TableBaseName), _tables.Schema);
    }
}

/// <summary>
/// The audit log table as part of another model, so a host that manages its database with migrations creates it
/// in its own migrations (and can fill it from its own tables there).
/// </summary>
public static class AuditStorageModelBuilderExtensions
{
    /// <summary>The table's base name: <c>QcliAuditEntries</c> with the default prefix.</summary>
    public const string TableBaseName = "AuditEntries";

    /// <summary>The table of 1.0.0-beta.29 and older, renamed on upgrade.</summary>
    public const string LegacyTableName = "audit_entries";

    /// <summary>
    /// Adds the audit log table (<c>QcliAuditEntries</c> unless named) to <paramref name="modelBuilder"/>; call it
    /// from the host's <c>OnModelCreating</c>.
    /// </summary>
    /// <param name="modelBuilder">The host's model builder.</param>
    /// <param name="table">The table name (the store's <c>TablePrefix</c>/<c>Tables</c> must give the same name).</param>
    /// <param name="schema">The table's schema.</param>
    public static ModelBuilder ApplyAuditStorageModel(this ModelBuilder modelBuilder, string table = "Qcli" + TableBaseName, string? schema = null)
    {
        modelBuilder.Entity<AuditEntryEntity>(entity =>
        {
            entity.ToTable(table, schema);
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
