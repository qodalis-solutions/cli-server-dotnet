namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

/// <summary>
/// Configuration options for the SQLite file storage provider.
/// </summary>
public class SqliteProviderOptions
{
    /// <summary>
    /// Gets or sets the path to the SQLite database file. Use ":memory:" for an in-memory database.
    /// </summary>
    public string DbPath { get; set; } = "./data/files.db";
}
