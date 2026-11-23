namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Migrations.EfCore;

/// <summary>
/// Entity Framework Core database context for the file storage provider.
/// </summary>
public class FileStorageDbContext : DbContext
{
    /// <summary>The table's base name: <c>QcliFiles</c> with the default prefix.</summary>
    public const string TableBaseName = "Files";

    /// <summary>The table of 1.0.0-beta.29 and older, renamed on upgrade.</summary>
    public const string LegacyTableName = "files";

    private readonly QcliEfTables _tables;

    /// <summary>
    /// Gets or sets the files table.
    /// </summary>
    public DbSet<FileEntity> Files { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance with the specified EF Core options.
    /// </summary>
    /// <param name="options">The database context options.</param>
    public FileStorageDbContext(DbContextOptions<FileStorageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(entity =>
        {
            entity.ToTable(_tables.Table(TableBaseName), _tables.Schema);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Path).IsUnique();
            entity.HasIndex(e => e.ParentPath);
            entity.Property(e => e.Type).HasMaxLength(16);
        });
    }
}
