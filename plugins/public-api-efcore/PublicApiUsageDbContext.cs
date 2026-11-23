using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>
/// EF Core database context for the public API's usage counters.
/// </summary>
public class PublicApiUsageDbContext : DbContext
{
    private readonly QcliEfTables _tables;

    internal DbSet<ApiUsageEntity> Usage { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicApiUsageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCorePublicApiUsageStoreExtensions.AddEfCorePublicApiUsageStore"/>.</param>
    public PublicApiUsageDbContext(DbContextOptions<PublicApiUsageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyPublicApiUsageModel(_tables.Table(PublicApiUsageModelBuilderExtensions.TableBaseName), _tables.Schema);
    }
}

/// <summary>
/// The usage table as part of another model, so a host that manages its database with migrations creates it in its
/// own migrations.
/// </summary>
public static class PublicApiUsageModelBuilderExtensions
{
    /// <summary>The table's base name: <c>QcliApiUsage</c> with the default prefix.</summary>
    public const string TableBaseName = "ApiUsage";

    /// <summary>The table of 1.0.0-beta.29, renamed on upgrade.</summary>
    public const string LegacyTableName = "qcli_api_usage";

    /// <summary>
    /// Adds the usage table (<c>QcliApiUsage</c> unless named; (<c>TokenId</c>, <c>UserId</c>, <c>Window</c>, <c>Count</c>; primary key
    /// (<c>TokenId</c>, <c>Window</c>), index (<c>UserId</c>, <c>Window</c>)) to <paramref name="modelBuilder"/>; call it
    /// from the host's <c>OnModelCreating</c>.
    /// </summary>
    /// <param name="modelBuilder">The host's model builder.</param>
    /// <param name="table">The table name (the store's <c>TablePrefix</c>/<c>Tables</c> must give the same name).</param>
    /// <param name="schema">The table's schema.</param>
    public static ModelBuilder ApplyPublicApiUsageModel(this ModelBuilder modelBuilder, string table = "Qcli" + TableBaseName, string? schema = null)
    {
        modelBuilder.Entity<ApiUsageEntity>(entity =>
        {
            entity.ToTable(table, schema);
            entity.HasKey(e => new { e.TokenId, e.Window });
            entity.HasIndex(e => new { e.UserId, e.Window });
            entity.Property(e => e.TokenId).HasMaxLength(64);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.Window).HasMaxLength(16);
        });

        return modelBuilder;
    }
}
