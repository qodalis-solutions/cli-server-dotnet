using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// EF Core database context for the API token table.
/// </summary>
public class ApiTokenStorageDbContext : DbContext
{
    private readonly QcliEfTables _tables;

    internal DbSet<ApiTokenEntity> Tokens { get; set; } = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiTokenStorageDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options configured via <see cref="EfCoreApiTokenStoreExtensions.AddEfCoreApiTokenStore"/>.</param>
    public ApiTokenStorageDbContext(DbContextOptions<ApiTokenStorageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyApiTokenStorageModel(_tables.Table(ApiTokenStorageModelBuilderExtensions.TableBaseName), _tables.Schema);
    }
}

/// <summary>
/// The API token table as part of another model, so a host that manages its database with migrations creates it
/// in its own migrations.
/// </summary>
public static class ApiTokenStorageModelBuilderExtensions
{
    /// <summary>The table's base name: <c>QcliApiTokens</c> with the default prefix.</summary>
    public const string TableBaseName = "ApiTokens";

    /// <summary>The table of 1.0.0-beta.29 and older, renamed on upgrade.</summary>
    public const string LegacyTableName = "api_tokens";

    /// <summary>
    /// Adds the API token table (<c>QcliApiTokens</c> unless named) to <paramref name="modelBuilder"/>; call it from
    /// the host's <c>OnModelCreating</c>.
    /// </summary>
    /// <param name="modelBuilder">The host's model builder.</param>
    /// <param name="table">The table name (the store's <c>TablePrefix</c>/<c>Tables</c> must give the same name).</param>
    /// <param name="schema">The table's schema.</param>
    public static ModelBuilder ApplyApiTokenStorageModel(this ModelBuilder modelBuilder, string table = "Qcli" + TableBaseName, string? schema = null)
    {
        modelBuilder.Entity<ApiTokenEntity>(entity =>
        {
            entity.ToTable(table, schema);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.Name).HasMaxLength(64);
            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.Property(e => e.Audience).HasMaxLength(16);
        });

        return modelBuilder;
    }
}
