using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

/// <summary>
/// Extension methods for configuring the EF Core file storage provider on <see cref="FileSystemOptions"/>.
/// </summary>
public static class EfCoreFileSystemExtensions
{
    /// <summary>
    /// Configures the file system to use Entity Framework Core as the storage backend.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="dbOptions">The EF Core database context options for <see cref="FileStorageDbContext"/>.</param>
    public static void UseEfCore(this FileSystemOptions options, DbContextOptions<FileStorageDbContext> dbOptions)
    {
        var db = new FileStorageDbContext(dbOptions);
        options.Provider = new EfCoreFileStorageProvider(db);
    }
}
