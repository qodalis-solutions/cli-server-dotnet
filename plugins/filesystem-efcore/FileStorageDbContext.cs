namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

using Microsoft.EntityFrameworkCore;

public class FileStorageDbContext : DbContext
{
    public DbSet<FileEntity> Files { get; set; } = null!;

    public FileStorageDbContext(DbContextOptions<FileStorageDbContext> options) : base(options) { }

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
