namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Entity Framework Core database context for the file storage provider.
/// </summary>
public class FileStorageDbContext : DbContext
{
    /// <summary>
    /// Gets or sets the files table.
    /// </summary>
    public DbSet<FileEntity> Files { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance with the specified EF Core options.
    /// </summary>
    /// <param name="options">The database context options.</param>
    public FileStorageDbContext(DbContextOptions<FileStorageDbContext> options) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(entity =>
        {
            entity.ToTable("files");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Path).IsUnique();
            entity.HasIndex(e => e.ParentPath);
            entity.Property(e => e.Type).HasMaxLength(16);
        });
    }
}
