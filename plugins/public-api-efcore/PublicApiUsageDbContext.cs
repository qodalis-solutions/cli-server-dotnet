using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>
/// EF Core database context for the public API's usage counters.
/// </summary>
public class PublicApiUsageDbContext : DbContext
{
    internal DbSet<ApiUsageEntity> Usage { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicApiUsageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCorePublicApiUsageStoreExtensions.AddEfCorePublicApiUsageStore"/>.</param>
    public PublicApiUsageDbContext(DbContextOptions<PublicApiUsageDbContext> options) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyPublicApiUsageModel();
    }
}

/// <summary>
/// The usage table as part of another model, so a host that manages its database with migrations creates it in its
/// own migrations.
/// </summary>
public static class PublicApiUsageModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>qcli_api_usage</c> table (<c>TokenId</c>, <c>UserId</c>, <c>Window</c>, <c>Count</c>; primary key
    /// (<c>TokenId</c>, <c>Window</c>), index (<c>UserId</c>, <c>Window</c>)) to <paramref name="modelBuilder"/>; call it
    /// from the host's <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyPublicApiUsageModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiUsageEntity>(entity =>
        {
            entity.ToTable("qcli_api_usage");
            entity.HasKey(e => new { e.TokenId, e.Window });
            entity.HasIndex(e => new { e.UserId, e.Window });
            entity.Property(e => e.TokenId).HasMaxLength(64);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.Window).HasMaxLength(16);
        });

        return modelBuilder;
    }
}
