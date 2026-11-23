using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// One direction of a migration: SQL, SQL per dialect (<c>postgres</c>, <c>mysql</c>, <c>mssql</c>, <c>sqlite</c>),
/// or code given the session.
/// </summary>
public sealed class CliMigrationStep
{
    private CliMigrationStep() { }

    /// <summary>The same SQL on every dialect.</summary>
    public string? Sql { get; private init; }

    /// <summary>SQL by dialect.</summary>
    public IReadOnlyDictionary<string, string>? SqlByDialect { get; private init; }

    /// <summary>Code run with the session (and its transaction, unless the migration has none).</summary>
    public Func<CliSqlSession, Task>? Code { get; private init; }

    /// <summary>For a code step, the SQL a dry run shows (e.g. EF Core's script); null shows a comment.</summary>
    public Func<CliSqlSession, Task<string?>>? Script { get; private init; }

    public static CliMigrationStep FromSql(string sql) => new() { Sql = sql };

    public static CliMigrationStep FromSql(IReadOnlyDictionary<string, string> sqlByDialect) => new() { SqlByDialect = sqlByDialect };

    public static CliMigrationStep FromCode(Func<CliSqlSession, Task> code, Func<CliSqlSession, Task<string?>>? script = null) =>
        new() { Code = code, Script = script };

    public static implicit operator CliMigrationStep(string sql) => FromSql(sql);

    /// <summary>The step's SQL for <paramref name="dialect"/>; null for code.</summary>
    /// <exception cref="InvalidOperationException">SQL by dialect without this dialect.</exception>
    public string? SqlFor(string dialect, string migrationId)
    {
        if (Sql != null) return Sql;
        if (SqlByDialect == null) return null;
        return SqlByDialect.TryGetValue(dialect, out var sql)
            ? sql
            : throw new InvalidOperationException($"migration {migrationId} has no SQL for {dialect}");
    }
}

/// <summary>A versioned schema change.</summary>
public sealed class CliMigration
{
    /// <summary>Sortable id (<c>0001</c>, <c>20261120111649_PublicApi</c>); numeric parts compare as numbers.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CliMigrationStep Up { get; init; }

    /// <summary>Rolls the migration back; without it the migration cannot be rolled back.</summary>
    public CliMigrationStep? Down { get; init; }

    /// <summary>Run the step and its history row in one transaction (default true).</summary>
    public bool Transaction { get; init; } = true;

    /// <summary>SHA-256 of the up SQL for the dialect; null for code.</summary>
    internal string? ChecksumFor(string dialect)
    {
        var sql = Up.SqlFor(dialect, Id);
        return sql == null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
    }
}

/// <summary>Where a database's migrations come from (sources apply in the order they were added).</summary>
public interface ICliMigrationSource
{
    /// <summary>Unique within a database (<c>app</c>, <c>qcli-users</c>).</summary>
    string Name { get; }

    Task<IReadOnlyList<CliMigration>> LoadAsync(CliSqlSession session);
}

/// <summary>
/// A source that keeps its own history (EF Core's <c>__EFMigrationsHistory</c>): its migrations are applied when it
/// says so, and the plugin records only the runs. Its migrations run outside the plugin's transactions.
/// </summary>
public interface ICliTrackedMigrationSource : ICliMigrationSource
{
    /// <summary>The ids of the applied migrations.</summary>
    Task<IReadOnlyList<string>> GetAppliedAsync(CliSqlSession session);
}

/// <summary>Migrations given in code: how plugins ship their schema.</summary>
public sealed class CliCodeMigrationSource(string name, IReadOnlyList<CliMigration> migrations) : ICliMigrationSource
{
    public string Name { get; } = name;

    public Task<IReadOnlyList<CliMigration>> LoadAsync(CliSqlSession session) => Task.FromResult(migrations);
}

/// <summary>
/// Migrations from a folder: <c>0001_create_users.up.sql</c> with an optional <c>0001_create_users.down.sql</c>. A file
/// starting with <c>-- qcli:no-transaction</c> runs outside a transaction.
/// </summary>
public sealed partial class CliSqlFolderMigrationSource(string directory, string name = "app") : ICliMigrationSource
{
    public string Name { get; } = name;

    [GeneratedRegex(@"^([0-9A-Za-z]+)_(.+?)\.(up|down)\.sql$")]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"[_-]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^\s*--\s*qcli:no-transaction\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoTransaction();

    public async Task<IReadOnlyList<CliMigration>> LoadAsync(CliSqlSession session)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"migrations folder not found: {directory}");
        var files = new Dictionary<string, (string Name, string? Up, string? Down)>();
        foreach (var path in Directory.GetFiles(directory, "*.sql"))
        {
            var match = FilePattern().Match(Path.GetFileName(path));
            if (!match.Success) continue;
            var id = match.Groups[1].Value;
            var sql = await File.ReadAllTextAsync(path, session.CancellationToken);
            var title = Separators().Replace(match.Groups[2].Value, " ");
            var entry = files.GetValueOrDefault(id, (title, null, null));
            entry = match.Groups[3].Value == "up" ? entry with { Up = sql } : entry with { Down = sql };
            files[id] = entry;
        }

        return files
            .Select(f => f.Value.Up == null
                ? throw new InvalidOperationException($"migration {f.Key} has a down file but no up file in {directory}")
                : new CliMigration
                {
                    Id = f.Key,
                    Name = f.Value.Name,
                    Up = f.Value.Up,
                    Down = f.Value.Down == null ? null : CliMigrationStep.FromSql(f.Value.Down),
                    Transaction = !NoTransaction().IsMatch(f.Value.Up),
                })
            .ToList();
    }
}

/// <summary>Orders migration ids: numeric runs compare as numbers (<c>2</c> before <c>10</c>).</summary>
public sealed partial class CliMigrationIdComparer : IComparer<string>
{
    public static readonly CliMigrationIdComparer Instance = new();

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Parts();

    public int Compare(string? x, string? y)
    {
        var a = Parts().Matches(x ?? string.Empty);
        var b = Parts().Matches(y ?? string.Empty);
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var (pa, pb) = (a[i].Value, b[i].Value);
            int c;
            if (char.IsDigit(pa[0]) && char.IsDigit(pb[0]))
            {
                var (ta, tb) = (pa.TrimStart('0'), pb.TrimStart('0'));
                c = ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
            }
            else
            {
                c = string.CompareOrdinal(pa, pb);
            }
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }
}
