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
    /// <param name="configure">The table's name (<c>TablePrefix</c>, <c>Tables["Files"]</c>, <c>LegacyTableNames</c>).</param>
    public static void UseSqlite(this FileSystemOptions options, string dbPath, Action<SqliteProviderOptions>? configure = null)
    {
        var providerOptions = new SqliteProviderOptions { DbPath = dbPath };
        configure?.Invoke(providerOptions);
        options.Provider = new SqliteFileStorageProvider(providerOptions);
    }
}
