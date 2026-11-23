using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

/// <summary>
/// Extension methods for configuring the EF Core file storage provider on <see cref="FileSystemOptions"/>.
/// </summary>
public static class EfCoreFileSystemExtensions
{
    /// <summary>
    /// Configures the file system to use Entity Framework Core as the storage backend. The table is
    /// <c>QcliFiles</c> (<paramref name="tables"/>: <c>TablePrefix</c>, <c>Tables["Files"]</c>, <c>Schema</c>); the
    /// <c>files</c> table of 1.0.0-beta.29 and older is renamed, keeping the files, unless <c>LegacyTableNames</c>.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="dbOptions">The EF Core database context options for <see cref="FileStorageDbContext"/>.</param>
    /// <param name="tables">The table's name.</param>
    public static void UseEfCore(this FileSystemOptions options, DbContextOptions<FileStorageDbContext> dbOptions,
        Action<QcliTableNameOptions>? tables = null)
    {
        var names = new QcliTableNameOptions();
        tables?.Invoke(names);
        var legacy = new Dictionary<string, string> { [FileStorageDbContext.TableBaseName] = FileStorageDbContext.LegacyTableName };
        var resolved = QcliEfTables.Resolve(names, legacy);
        var builder = new DbContextOptionsBuilder<FileStorageDbContext>(dbOptions);
        builder.UseQcliTables(resolved);
        var db = new FileStorageDbContext(builder.Options);
        options.Provider = new EfCoreFileStorageProvider(db, QcliEfStoreRenames<FileStorageDbContext>.For(resolved, names, legacy).Renames);
    }
}
