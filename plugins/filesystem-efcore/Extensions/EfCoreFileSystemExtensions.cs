using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.FileSystem.EfCore;

public static class EfCoreFileSystemExtensions
{
    public static void UseEfCore(this FileSystemOptions options, DbContextOptions<FileStorageDbContext> dbOptions)
    {
        var db = new FileStorageDbContext(dbOptions);
        options.Provider = new EfCoreFileStorageProvider(db);
    }
}
