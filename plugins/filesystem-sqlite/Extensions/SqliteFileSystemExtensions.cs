namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

public static class SqliteFileSystemExtensions
{
    public static void UseSqlite(this FileSystemOptions options, string dbPath)
    {
        options.Provider = new SqliteFileStorageProvider(new SqliteProviderOptions { DbPath = dbPath });
    }
}
