namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

/// <summary>
/// Extension methods for configuring the SQLite file storage provider on <see cref="FileSystemOptions"/>.
/// </summary>
public static class SqliteFileSystemExtensions
{
    /// <summary>
    /// Configures the file system to use a SQLite database as the storage backend.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="dbPath">The path to the SQLite database file, or ":memory:" for in-memory storage.</param>
    public static void UseSqlite(this FileSystemOptions options, string dbPath)
    {
        options.Provider = new SqliteFileStorageProvider(new SqliteProviderOptions { DbPath = dbPath });
    }
}
