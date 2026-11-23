using System.Text.RegularExpressions;

namespace Qodalis.Cli.Abstractions.Helpers;

/// <summary>
/// Names of the tables a CLI plugin creates (cli-server-dotnet#99): <see cref="TablePrefix"/> + the table's
/// PascalCase base name (<c>Files</c> → <c>QcliFiles</c>), the same in the three servers.
/// </summary>
public class QcliTableNameOptions
{
    /// <summary>Prefix of every table (default <c>Qcli</c>; empty = the plain base names).</summary>
    public string TablePrefix { get; set; } = QcliTableNames.DefaultPrefix;

    /// <summary>A name per table, by base name (<c>Tables["Files"] = "MyFiles"</c>); wins over the prefix.</summary>
    public Dictionary<string, string> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The schema of the tables, on databases that have schemas (null = the connection's default).</summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Keep the tables' names of the previous releases (<c>files</c>, <c>job_executions</c>, ...) instead of
    /// renaming them on upgrade. Off by default: an upgrade renames them, keeping the data.
    /// </summary>
    public bool LegacyTableNames { get; set; }
}

/// <summary>Resolves the plugins' table names (<see cref="QcliTableNameOptions"/>).</summary>
public static partial class QcliTableNames
{
    /// <summary>The default prefix, <c>Qcli</c>.</summary>
    public const string DefaultPrefix = "Qcli";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Whether <paramref name="name"/> is a plain SQL identifier (letters, digits, underscores).</summary>
    public static bool IsSqlIdentifier(string? name) => name != null && IdentifierPattern().IsMatch(name);

    /// <summary>
    /// The table name for <paramref name="baseName"/>: the override in <see cref="QcliTableNameOptions.Tables"/>,
    /// else <paramref name="legacyName"/> with <see cref="QcliTableNameOptions.LegacyTableNames"/>, else prefix +
    /// base name.
    /// </summary>
    /// <exception cref="ArgumentException">The resulting name is not a plain identifier.</exception>
    public static string Resolve(string baseName, QcliTableNameOptions? options = null, string? legacyName = null)
    {
        string name;
        if (options?.Tables != null && options.Tables.TryGetValue(baseName, out var custom) && !string.IsNullOrEmpty(custom))
            name = custom;
        else if (options?.LegacyTableNames == true && legacyName != null)
            name = legacyName;
        else
            name = (options?.TablePrefix ?? DefaultPrefix) + baseName;

        if (!IsSqlIdentifier(name)) throw new ArgumentException($"invalid table name: {name}", nameof(baseName));
        if (options?.Schema != null && !IsSqlIdentifier(options.Schema))
            throw new ArgumentException($"invalid schema name: {options.Schema}", nameof(options));
        return name;
    }
}
