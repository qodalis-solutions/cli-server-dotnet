using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.FileSystem.EfCore.FileSystemEfCorePluginEntry), Id = "filesystem-efcore")]

namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

/// <summary>
/// The file storage in a database from the dashboard (or <c>cli.AddPlugin&lt;FileSystemEfCorePluginEntry&gt;(new { connectionString = "files.db" })</c>):
/// the filesystem API over files kept through EF Core, read-only unless <c>readOnly</c> is turned off. The dialect,
/// connection (a secret) and table prefix are kept in <c>plugin-settings.json</c> and read while the server starts;
/// without a connection it is <c>needs-configuration</c>. Refused when the storage is already set.
/// </summary>
public sealed class FileSystemEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        if (context.Builder.HasFileStorage) throw new InvalidOperationException("the filesystem storage is already set by the host or another plugin");
        var (settings, configure, tables) = context.EfStore("Filesystem (database)", "Files kept in a database", "files.db",
            [FileSystemPluginEntry.ReadOnlySetting("Uploads, new folders and deletes are refused")]);
        if (configure == null) return;
        var db = new DbContextOptionsBuilder<FileStorageDbContext>();
        configure(db);
        var files = new FileSystemOptions();
        files.UseEfCore(db.Options, tables);
        var storage = files.Provider!;
        context.Builder.AddFileSystem(o => o.Provider = settings.Flag("readOnly", true) ? new ReadOnlyFileStorageProvider(storage) : storage);
    }
}
