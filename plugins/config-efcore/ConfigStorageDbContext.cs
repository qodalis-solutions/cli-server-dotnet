using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Config.EfCore;

/// <summary>A row of the config table: one provider section, its values as JSON and its version.</summary>
public class ConfigSectionEntity
{
    public string Provider { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;

    /// <summary>Grows by one on every write; a concurrency token, so two servers sharing the table never lose a change.</summary>
    public long Version { get; set; }

    /// <summary>The section's stored values, a JSON object.</summary>
    public string Data { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Entity Framework Core context of <see cref="EfCoreConfigStore"/> (table <c>QcliConfig</c>).</summary>
public class ConfigStorageDbContext : DbContext
{
    private readonly QcliEfTables _tables;

    public ConfigStorageDbContext(DbContextOptions<ConfigStorageDbContext> options) : base(options)
    {
        _tables = QcliEfTables.Of(options);
    }

    internal DbSet<ConfigSectionEntity> Sections { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigStorageModel(_tables.Table(ConfigStorageModelBuilderExtensions.TableBaseName), _tables.Schema);
    }
}

/// <summary>The config table's model, for a host whose own migrations create it.</summary>
public static class ConfigStorageModelBuilderExtensions
{
    /// <summary>The table's base name: <c>QcliConfig</c> with the default prefix.</summary>
    public const string TableBaseName = "Config";

    /// <summary>Adds the config table (<c>QcliConfig</c>: <c>Provider</c>, <c>Section</c>, <c>Version</c>, <c>Data</c>, <c>UpdatedAt</c>).</summary>
    public static ModelBuilder ApplyConfigStorageModel(this ModelBuilder modelBuilder, string table = "Qcli" + TableBaseName, string? schema = null)
    {
        modelBuilder.Entity<ConfigSectionEntity>(entity =>
        {
            entity.ToTable(table, schema);
            entity.HasKey(e => new { e.Provider, e.Section });
            entity.Property(e => e.Provider).HasMaxLength(150);
            entity.Property(e => e.Section).HasMaxLength(150);
            entity.Property(e => e.Version).IsConcurrencyToken();
        });
        return modelBuilder;
    }
}
