using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// EF Core database context for the API token table.
/// </summary>
public class ApiTokenStorageDbContext : DbContext
{
    internal DbSet<ApiTokenEntity> Tokens { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiTokenStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreApiTokenStoreExtensions.AddEfCoreApiTokenStore"/>.</param>
    public ApiTokenStorageDbContext(DbContextOptions<ApiTokenStorageDbContext> options) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyApiTokenStorageModel();
    }
}

/// <summary>
/// The API token table as part of another model, so a host that manages its database with migrations creates it
/// in its own migrations.
/// </summary>
public static class ApiTokenStorageModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>api_tokens</c> table to <paramref name="modelBuilder"/>; call it from the host's
    /// <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyApiTokenStorageModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiTokenEntity>(entity =>
        {
            entity.ToTable("api_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.Name).HasMaxLength(64);
            entity.Property(e => e.TokenHash).HasMaxLength(64);
        });

        return modelBuilder;
    }
}
