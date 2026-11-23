using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

/// <summary>
/// Configuration options for the SQLite file storage provider: the database file and the table's name
/// (<c>QcliFiles</c> by default; <c>TablePrefix</c>, <c>Tables["Files"]</c>, and <c>LegacyTableNames</c> to keep the
/// <c>files</c> table of 1.0.0-beta.29 and older instead of renaming it).
/// </summary>
public class SqliteProviderOptions : QcliTableNameOptions
{
    /// <summary>
    /// Gets or sets the path to the SQLite database file. Use ":memory:" for an in-memory database.
    /// </summary>
    public string DbPath { get; set; } = "./data/files.db";
}
