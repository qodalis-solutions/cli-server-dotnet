using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Databases.EfCore;

/// <summary>
/// The table names a CLI store's DbContext maps its entities to (<c>QcliAuditEntries</c>, ...), carried in its
/// <see cref="DbContextOptions"/> so two stores with different names in one process each get their own model.
/// </summary>
public sealed class QcliEfTables : IDbContextOptionsExtension
{
    private readonly Dictionary<string, string> _tables;
    private ExtensionInfo? _info;

    internal QcliEfTables(Dictionary<string, string> tables, string? schema)
    {
        _tables = tables;
        Schema = schema;
    }

    /// <summary>The schema of the tables (null = the connection's default).</summary>
    public string? Schema { get; }

    /// <summary>The name of the table <paramref name="baseName"/> (<c>AuditEntries</c> → <c>QcliAuditEntries</c>).</summary>
    public string Table(string baseName) => _tables.TryGetValue(baseName, out var name) ? name : QcliTableNames.DefaultPrefix + baseName;

    internal string Signature => string.Join(";", _tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}")) + "|" + Schema;

    /// <summary>The names a context was configured with (<see cref="QcliEfTablesExtensions.UseQcliTables"/>), or the defaults.</summary>
    public static QcliEfTables Of(DbContextOptions options) => options.FindExtension<QcliEfTables>() ?? Default;

    /// <summary><c>Qcli</c> + base name, no schema.</summary>
    public static readonly QcliEfTables Default = new(new(StringComparer.Ordinal), null);

    /// <summary>Resolves <paramref name="baseNames"/> with <paramref name="options"/> (prefix, overrides, legacy names).</summary>
    public static QcliEfTables Resolve(QcliTableNameOptions? options, IReadOnlyDictionary<string, string> baseNames)
    {
        var tables = baseNames.ToDictionary(b => b.Key, b => QcliTableNames.Resolve(b.Key, options, b.Value), StringComparer.Ordinal);
        return new QcliEfTables(tables, options?.Schema);
    }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) { }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(QcliEfTables extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"QcliTables={((QcliEfTables)Extension).Signature} ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Qcli:Tables"] = ((QcliEfTables)Extension).Signature;
    }
}

/// <summary>Model cache key that tells contexts with different table names apart.</summary>
internal sealed class QcliModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.GetService<IDbContextOptions>().FindExtension<QcliEfTables>()?.Signature, designTime);
}

public static class QcliEfTablesExtensions
{
    /// <summary>Maps a CLI store's tables to <paramref name="tables"/> (see <see cref="QcliEfTables.Resolve"/>).</summary>
    public static DbContextOptionsBuilder UseQcliTables(this DbContextOptionsBuilder builder, QcliEfTables tables)
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(tables);
        builder.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, QcliModelCacheKeyFactory>();
        return builder;
    }

    /// <inheritdoc cref="UseQcliTables(DbContextOptionsBuilder, QcliEfTables)"/>
    public static DbContextOptionsBuilder<TContext> UseQcliTables<TContext>(this DbContextOptionsBuilder<TContext> builder, QcliEfTables tables)
        where TContext : DbContext
    {
        UseQcliTables((DbContextOptionsBuilder)builder, tables);
        return builder;
    }
}
