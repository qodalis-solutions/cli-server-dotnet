using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// Schema helpers that work on every dialect: quoting, table and column checks, renames, and the adoption of a
/// previous release's table (cli-server-dotnet#99). Identifiers are validated before they are quoted, so a name can
/// never inject SQL.
/// </summary>
public static class CliSqlSchema
{
    private static string Checked(string name) =>
        QcliTableNames.IsSqlIdentifier(name) ? name : throw new ArgumentException($"invalid identifier: {name}");

    /// <summary>An identifier quoted for the dialect.</summary>
    public static string QuoteIdent(string dialect, string name)
    {
        var n = Checked(name);
        return dialect switch
        {
            CliSqlDialects.MySql => $"`{n}`",
            CliSqlDialects.SqlServer => $"[{n}]",
            _ => $"\"{n}\"",
        };
    }

    /// <summary>A table with its schema, quoted (<c>"qcli"."QcliFiles"</c>); SQLite and MySQL have no schemas here.</summary>
    public static string QuoteTable(string dialect, string table, string? schema = null)
    {
        var t = QuoteIdent(dialect, table);
        return schema != null && dialect is CliSqlDialects.Postgres or CliSqlDialects.SqlServer ? $"{QuoteIdent(dialect, schema)}.{t}" : t;
    }

    /// <summary>True when the table exists (in <paramref name="schema"/>, or the connection's default schema).</summary>
    public static async Task<bool> TableExistsAsync(CliSqlSession db, string table, string? schema = null)
    {
        Checked(table);
        var rows = db.Dialect switch
        {
            CliSqlDialects.Postgres => await db.QueryAsync(
                "SELECT 1 AS x FROM information_schema.tables WHERE table_name = ? AND table_schema = COALESCE(?, current_schema())", table, schema),
            CliSqlDialects.MySql => await db.QueryAsync(
                "SELECT 1 AS x FROM information_schema.tables WHERE table_name = ? AND table_schema = DATABASE()", table),
            CliSqlDialects.SqlServer => await db.QueryAsync(
                "SELECT 1 AS x FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = ? AND TABLE_SCHEMA = COALESCE(?, SCHEMA_NAME())", table, schema),
            _ => await db.QueryAsync("SELECT 1 AS x FROM sqlite_master WHERE type = 'table' AND name = ?", table),
        };
        return rows.Count > 0;
    }

    /// <summary>True when the table has the column.</summary>
    public static async Task<bool> ColumnExistsAsync(CliSqlSession db, string table, string column, string? schema = null)
    {
        Checked(table);
        Checked(column);
        if (db.Dialect == CliSqlDialects.Sqlite)
            return (await db.QueryAsync($"PRAGMA table_info({QuoteIdent(db.Dialect, table)})"))
                .Any(r => string.Equals(r["name"] as string, column, StringComparison.Ordinal));
        var rows = db.Dialect switch
        {
            CliSqlDialects.Postgres => await db.QueryAsync(
                "SELECT 1 AS x FROM information_schema.columns WHERE table_name = ? AND column_name = ? AND table_schema = COALESCE(?, current_schema())",
                table, column, schema),
            CliSqlDialects.MySql => await db.QueryAsync(
                "SELECT 1 AS x FROM information_schema.columns WHERE table_name = ? AND column_name = ? AND table_schema = DATABASE()", table, column),
            _ => await db.QueryAsync(
                "SELECT 1 AS x FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = ? AND COLUMN_NAME = ? AND TABLE_SCHEMA = COALESCE(?, SCHEMA_NAME())",
                table, column, schema),
        };
        return rows.Count > 0;
    }

    /// <summary>Renames a table (in its schema).</summary>
    public static Task RenameTableAsync(CliSqlSession db, string from, string to, string? schema = null)
    {
        var d = db.Dialect;
        return d switch
        {
            CliSqlDialects.SqlServer => db.ExecuteAsync("EXEC sp_rename ?, ?", schema != null ? $"{Checked(schema)}.{Checked(from)}" : Checked(from), Checked(to)),
            CliSqlDialects.MySql => db.ExecuteAsync($"RENAME TABLE {QuoteIdent(d, from)} TO {QuoteIdent(d, to)}"),
            _ => db.ExecuteAsync($"ALTER TABLE {QuoteTable(d, from, schema)} RENAME TO {QuoteIdent(d, to)}"),
        };
    }

    /// <summary>Renames a column (SQLite 3.25 and later).</summary>
    public static Task RenameColumnAsync(CliSqlSession db, string table, string from, string to, string? schema = null)
    {
        var d = db.Dialect;
        if (d == CliSqlDialects.SqlServer)
            return db.ExecuteAsync("EXEC sp_rename ?, ?, 'COLUMN'", $"{(schema != null ? Checked(schema) + "." : "")}{Checked(table)}.{Checked(from)}", Checked(to));
        return db.ExecuteAsync($"ALTER TABLE {QuoteTable(d, table, schema)} RENAME COLUMN {QuoteIdent(d, from)} TO {QuoteIdent(d, to)}");
    }

    /// <summary>
    /// Moves a table of a previous release to its new name and columns: when <paramref name="legacy"/> exists and
    /// <paramref name="table"/> does not, renames the table, then each column of <paramref name="columns"/>
    /// (old → new) present under its old name. Returns true when it renamed anything.
    /// </summary>
    public static async Task<bool> AdoptLegacyTableAsync(CliSqlSession db, string legacy, string table,
        IReadOnlyDictionary<string, string>? columns = null, string? schema = null)
    {
        if (legacy == table || !await TableExistsAsync(db, legacy, schema) || await TableExistsAsync(db, table, schema))
            return false;
        await RenameTableAsync(db, legacy, table, schema);
        foreach (var (from, to) in columns ?? new Dictionary<string, string>())
        {
            if (from != to && await ColumnExistsAsync(db, table, from, schema))
                await RenameColumnAsync(db, table, from, to, schema);
        }
        return true;
    }

    internal sealed record HistoryTables(string Migrations, string Runs);

    private sealed record Types(string Key, string Text, string Long, string Time, string Int);

    private static Types TypesOf(string dialect) => dialect switch
    {
        CliSqlDialects.Postgres => new("varchar(150)", "varchar(255)", "text", "timestamptz", "integer"),
        CliSqlDialects.MySql => new("varchar(150)", "varchar(255)", "longtext", "datetime(3)", "int"),
        CliSqlDialects.SqlServer => new("nvarchar(150)", "nvarchar(255)", "nvarchar(max)", "datetime2", "int"),
        _ => new("TEXT", "TEXT", "TEXT", "TEXT", "INTEGER"),
    };

    /// <summary>Creates the history tables when missing (<c>QcliMigrations</c>, <c>QcliMigrationRuns</c>), as the Node and Python servers do.</summary>
    internal static async Task EnsureHistoryTablesAsync(CliSqlSession db, HistoryTables tables, string? schema)
    {
        var d = db.Dialect;
        var t = TypesOf(d);
        string Q(string name) => QuoteIdent(d, name);
        async Task Create(string table, string[] columns)
        {
            if (await TableExistsAsync(db, table, schema)) return;
            await db.ExecuteAsync($"CREATE TABLE {QuoteTable(d, table, schema)} ({string.Join(", ", columns)})");
        }

        await Create(tables.Migrations,
        [
            $"{Q("Source")} {t.Key} NOT NULL", $"{Q("Id")} {t.Key} NOT NULL", $"{Q("Name")} {t.Text} NOT NULL",
            $"{Q("Checksum")} {t.Key} NULL", $"{Q("AppliedAt")} {t.Time} NOT NULL", $"{Q("DurationMs")} {t.Int} NOT NULL",
            $"{Q("AppliedBy")} {t.Text} NULL", $"{Q("RunId")} {t.Key} NULL",
            $"CONSTRAINT {Q("PK_" + tables.Migrations)} PRIMARY KEY ({Q("Source")}, {Q("Id")})",
        ]);
        await Create(tables.Runs,
        [
            $"{Q("Id")} {t.Key} NOT NULL", $"{Q("Direction")} {t.Key} NOT NULL", $"{Q("Target")} {t.Key} NULL",
            $"{Q("Trigger")} {t.Key} NOT NULL", $"{Q("Actor")} {t.Text} NULL", $"{Q("StartedAt")} {t.Time} NOT NULL",
            $"{Q("FinishedAt")} {t.Time} NULL", $"{Q("Status")} {t.Key} NOT NULL", $"{Q("Error")} {t.Long} NULL",
            $"{Q("Applied")} {t.Long} NULL",
            $"CONSTRAINT {Q("PK_" + tables.Runs)} PRIMARY KEY ({Q("Id")})",
        ]);
    }

    /// <summary>
    /// Takes the database's migration lock on the session: waits for it (<paramref name="wait"/>), or returns false at
    /// once when another process holds it. SQLite has one writer per file and no session locks: always true.
    /// </summary>
    internal static async Task<bool> AcquireLockAsync(CliSqlSession db, string key, bool wait)
    {
        switch (db.Dialect)
        {
            case CliSqlDialects.Postgres:
                {
                    var rows = await db.QueryAsync($"SELECT {(wait ? "pg_advisory_lock" : "pg_try_advisory_lock")}(hashtext(?))::text AS ok", key);
                    return wait || rows[0]["ok"] as string == "true";
                }
            case CliSqlDialects.MySql:
                {
                    var rows = await db.QueryAsync("SELECT GET_LOCK(?, ?) AS ok", key, wait ? -1 : 0);
                    return Convert.ToInt64(rows[0]["ok"] ?? 0L) == 1;
                }
            case CliSqlDialects.SqlServer:
                {
                    var rows = await db.QueryAsync(
                        "DECLARE @r int; EXEC @r = sp_getapplock @Resource = ?, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = ?; SELECT @r AS ok",
                        key, wait ? -1 : 0);
                    return Convert.ToInt64(rows[0]["ok"] ?? -1L) >= 0;
                }
            default:
                return true;
        }
    }

    /// <summary>Releases the lock <see cref="AcquireLockAsync"/> took.</summary>
    internal static Task ReleaseLockAsync(CliSqlSession db, string key) => db.Dialect switch
    {
        CliSqlDialects.Postgres => db.QueryAsync("SELECT pg_advisory_unlock(hashtext(?))::text AS ok", key),
        CliSqlDialects.MySql => db.QueryAsync("SELECT RELEASE_LOCK(?) AS ok", key),
        CliSqlDialects.SqlServer => db.ExecuteAsync("EXEC sp_releaseapplock @Resource = ?, @LockOwner = 'Session'", key),
        _ => Task.CompletedTask,
    };
}
