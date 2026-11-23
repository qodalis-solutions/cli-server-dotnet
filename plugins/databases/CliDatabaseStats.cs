using System.Data.Common;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>The database server: version and since when it runs.</summary>
public sealed class CliDatabaseServerInfo
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UptimeSeconds { get; set; }
}

/// <summary>Size on disk, in bytes.</summary>
public sealed class CliDatabaseSize
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Bytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DataBytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IndexBytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LogBytes { get; set; }
}

/// <summary>Connections: running a statement now, open, and the server's maximum.</summary>
public sealed class CliDatabaseConnections
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Active { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Open { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Max { get; set; }
}

/// <summary>Counters since the server started; the dashboard turns two samples into rates.</summary>
public sealed class CliDatabaseCounters
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Queries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Transactions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Commits { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Rollbacks { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RowsRead { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RowsWritten { get; set; }
}

/// <summary>A table among the largest.</summary>
public sealed class CliDatabaseTable
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; init; }

    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Rows { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Bytes { get; init; }
}

/// <summary>A metric that could not be read (e.g. a missing permission).</summary>
public sealed record CliDatabaseUnavailable(string Metric, string Error);

/// <summary>
/// A database's stats (<c>GET /api/v1/qcli/databases/{db}/stats</c>): each group is read on its own, and a group that
/// fails is left out and listed in <see cref="Unavailable"/>.
/// </summary>
public sealed class CliDatabaseStats
{
    public required string Name { get; init; }
    public required string Dialect { get; init; }
    public required string CollectedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliDatabaseServerInfo? Server { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliDatabaseSize? Size { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliDatabaseConnections? Connections { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliDatabaseCounters? Counters { get; set; }

    /// <summary><c>database</c> when the counters are the database's own, <c>server</c> when they cover the whole server.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountersScope { get; set; }

    /// <summary>Reads served from memory, 0 to 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheHitRatio { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CliDatabaseTable>? Tables { get; set; }

    public List<CliDatabaseUnavailable> Unavailable { get; } = [];
}

/// <summary>Reads <see cref="CliDatabaseStats"/> on PostgreSQL, MySQL, SQL Server and SQLite.</summary>
internal static class CliDatabaseStatsCollector
{
    private const int TopTables = 10;

    public static async Task<CliDatabaseStats> CollectAsync(string name, CliSqlSession db)
    {
        var stats = new CliDatabaseStats
        {
            Name = name,
            Dialect = db.Dialect,
            CollectedAt = CliSqlSession.Iso(DateTimeOffset.UtcNow)!,
        };
        async Task Group(string metric, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stats.Unavailable.Add(new(metric, ex.Message));
            }
        }

        switch (db.Dialect)
        {
            case CliSqlDialects.Postgres:
                await Group("server", async () => stats.Server = await PostgresServerAsync(db));
                await Group("size", async () => stats.Size = await PostgresSizeAsync(db));
                await Group("connections", async () => stats.Connections = await PostgresConnectionsAsync(db));
                await Group("counters", async () =>
                {
                    (stats.Counters, stats.CacheHitRatio) = await PostgresCountersAsync(db);
                    stats.CountersScope = "database";
                });
                await Group("tables", async () => stats.Tables = await PostgresTablesAsync(db));
                break;
            case CliSqlDialects.MySql:
                Dictionary<string, long>? status = null;
                await Group("server", async () =>
                {
                    status = await MySqlStatusAsync(db);
                    stats.Server = await MySqlServerAsync(db, status);
                });
                await MySqlFreshTableStatsAsync(db);
                await Group("size", async () => stats.Size = await MySqlSizeAsync(db));
                await Group("connections", async () => stats.Connections = await MySqlConnectionsAsync(db));
                await Group("counters", async () =>
                {
                    status ??= await MySqlStatusAsync(db);
                    (stats.Counters, stats.CacheHitRatio) = MySqlCounters(status);
                    stats.CountersScope = "server";
                });
                await Group("tables", async () => stats.Tables = await MySqlTablesAsync(db));
                break;
            case CliSqlDialects.SqlServer:
                await Group("server", async () => stats.Server = await SqlServerServerAsync(db));
                await Group("size", async () => stats.Size = await SqlServerSizeAsync(db));
                await Group("connections", async () => stats.Connections = await SqlServerConnectionsAsync(db));
                await Group("counters", async () =>
                {
                    (stats.Counters, stats.CacheHitRatio) = await SqlServerCountersAsync(db);
                    stats.CountersScope = "server";
                });
                await Group("tables", async () => stats.Tables = await SqlServerTablesAsync(db));
                break;
            case CliSqlDialects.Sqlite:
                await Group("server", async () => stats.Server = new() { Version = $"SQLite {await ScalarAsync(db, "SELECT sqlite_version()")}" });
                await Group("size", async () => stats.Size = new() { Bytes = await SqliteSizeAsync(db) });
                await Group("tables", async () => stats.Tables = await SqliteTablesAsync(db));
                break;
        }
        return stats;
    }

    /// <summary>The database's size in bytes (the list's <c>sizeBytes</c>).</summary>
    public static async Task<long?> SizeAsync(CliSqlSession db) => db.Dialect switch
    {
        CliSqlDialects.Postgres => Long(await ScalarAsync(db, "SELECT pg_database_size(current_database())")),
        CliSqlDialects.MySql => Long(await ScalarAsync(db,
            "SELECT COALESCE(SUM(data_length + index_length), 0) FROM information_schema.tables WHERE table_schema = DATABASE()")),
        CliSqlDialects.SqlServer => Long(await ScalarAsync(db, "SELECT CAST(SUM(CAST(size AS bigint)) * 8192 AS bigint) FROM sys.database_files")),
        CliSqlDialects.Sqlite => await SqliteSizeAsync(db),
        _ => null,
    };

    // PostgreSQL

    private static async Task<CliDatabaseServerInfo> PostgresServerAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT version() AS v, pg_postmaster_start_time() AS started, EXTRACT(EPOCH FROM (now() - pg_postmaster_start_time())) AS uptime"))[0];
        return new()
        {
            Version = FirstWords(Str(row["v"])),
            StartedAt = CliSqlSession.Iso(row["started"]),
            UptimeSeconds = Long(row["uptime"]),
        };
    }

    private static async Task<CliDatabaseSize> PostgresSizeAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT pg_database_size(current_database()) AS total, " +
            "(SELECT COALESCE(SUM(pg_indexes_size(relid)), 0) FROM pg_stat_user_tables) AS idx"))[0];
        var total = Long(row["total"]);
        var index = Long(row["idx"]);
        return new() { Bytes = total, IndexBytes = index, DataBytes = total - index };
    }

    private static async Task<CliDatabaseConnections> PostgresConnectionsAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT (SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database()) AS open, " +
            "(SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND state = 'active') AS active, " +
            "current_setting('max_connections') AS max"))[0];
        return new() { Open = Long(row["open"]), Active = Long(row["active"]), Max = Long(row["max"]) };
    }

    private static async Task<(CliDatabaseCounters, double?)> PostgresCountersAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT xact_commit, xact_rollback, tup_returned + tup_fetched AS rows_read, " +
            "tup_inserted + tup_updated + tup_deleted AS rows_written, blks_hit, blks_read " +
            "FROM pg_stat_database WHERE datname = current_database()"))[0];
        var commits = Long(row["xact_commit"]);
        var rollbacks = Long(row["xact_rollback"]);
        var hit = Long(row["blks_hit"]) ?? 0;
        var read = Long(row["blks_read"]) ?? 0;
        return (new()
        {
            Commits = commits,
            Rollbacks = rollbacks,
            Transactions = commits + rollbacks,
            RowsRead = Long(row["rows_read"]),
            RowsWritten = Long(row["rows_written"]),
        }, hit + read > 0 ? (double)hit / (hit + read) : null);
    }

    private static async Task<List<CliDatabaseTable>> PostgresTablesAsync(CliSqlSession db)
    {
        var rows = await db.QueryAsync(
            "SELECT schemaname, relname, n_live_tup, pg_total_relation_size(relid) AS bytes FROM pg_stat_user_tables " +
            $"ORDER BY bytes DESC, relname LIMIT {TopTables}");
        return rows.Select(r => new CliDatabaseTable
        {
            Schema = Str(r["schemaname"]),
            Name = Str(r["relname"])!,
            Rows = Long(r["n_live_tup"]),
            Bytes = Long(r["bytes"]),
        }).ToList();
    }

    // MySQL

    private static async Task<Dictionary<string, long>> MySqlStatusAsync(CliSqlSession db)
    {
        var rows = await db.QueryAsync(
            "SHOW GLOBAL STATUS WHERE Variable_name IN ('Uptime', 'Questions', " +
            "'Com_commit', 'Com_rollback', 'Innodb_rows_read', 'Innodb_rows_inserted', 'Innodb_rows_updated', 'Innodb_rows_deleted', " +
            "'Innodb_buffer_pool_read_requests', 'Innodb_buffer_pool_reads')");
        var status = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var values = r.Values.ToList();
            if (values.Count >= 2 && Long(values[1]) is { } value) status[Str(values[0])!] = value;
        }
        return status;
    }

    private static async Task<CliDatabaseServerInfo> MySqlServerAsync(CliSqlSession db, Dictionary<string, long> status)
    {
        var version = Str(await ScalarAsync(db, "SELECT VERSION()"));
        var uptime = status.TryGetValue("Uptime", out var u) ? u : (long?)null;
        return new()
        {
            Version = version == null ? null : version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? $"MariaDB {version}" : $"MySQL {version}",
            UptimeSeconds = uptime,
            StartedAt = uptime == null ? null : CliSqlSession.Iso(DateTimeOffset.UtcNow.AddSeconds(-uptime.Value)),
        };
    }

    private static async Task<CliDatabaseSize> MySqlSizeAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT COALESCE(SUM(data_length), 0) AS data, COALESCE(SUM(index_length), 0) AS idx " +
            "FROM information_schema.tables WHERE table_schema = DATABASE()"))[0];
        var data = Long(row["data"]);
        var index = Long(row["idx"]);
        return new() { Bytes = data + index, DataBytes = data, IndexBytes = index };
    }

    /// <summary>This database's connections (every one with the PROCESS privilege, else the user's own).</summary>
    private static async Task<CliDatabaseConnections> MySqlConnectionsAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT COUNT(*) AS open_count, COALESCE(SUM(command NOT IN ('Sleep', 'Daemon')), 0) AS active_count " +
            "FROM information_schema.processlist WHERE db = DATABASE()"))[0];
        return new()
        {
            Open = Long(row["open_count"]),
            Active = Long(row["active_count"]),
            Max = Long(await ScalarAsync(db, "SELECT @@max_connections")),
        };
    }

    /// <summary>information_schema.tables caches InnoDB sizes (a day by default on MySQL 8): read them fresh. MariaDB has no such cache.</summary>
    private static async Task MySqlFreshTableStatsAsync(CliSqlSession db)
    {
        try
        {
            await db.ExecuteAsync("SET SESSION information_schema_stats_expiry = 0");
        }
        catch (DbException)
        {
            // MariaDB, older MySQL
        }
    }

    private static (CliDatabaseCounters, double?) MySqlCounters(Dictionary<string, long> s)
    {
        long? Get(string key) => s.TryGetValue(key, out var v) ? v : null;
        var commits = Get("Com_commit");
        var rollbacks = Get("Com_rollback");
        var requests = Get("Innodb_buffer_pool_read_requests");
        var reads = Get("Innodb_buffer_pool_reads");
        return (new()
        {
            Queries = Get("Questions"),
            Commits = commits,
            Rollbacks = rollbacks,
            Transactions = commits + rollbacks,
            RowsRead = Get("Innodb_rows_read"),
            RowsWritten = Get("Innodb_rows_inserted") + Get("Innodb_rows_updated") + Get("Innodb_rows_deleted"),
        }, requests > 0 && reads != null ? 1 - (double)reads.Value / requests.Value : null);
    }

    private static async Task<List<CliDatabaseTable>> MySqlTablesAsync(CliSqlSession db)
    {
        var rows = await db.QueryAsync(
            "SELECT table_name AS name, table_rows AS row_count, data_length + index_length AS bytes FROM information_schema.tables " +
            $"WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY bytes DESC, name LIMIT {TopTables}");
        return rows.Select(r => new CliDatabaseTable { Name = Str(r["name"])!, Rows = Long(r["row_count"]), Bytes = Long(r["bytes"]) }).ToList();
    }

    // SQL Server

    private static async Task<CliDatabaseServerInfo> SqlServerServerAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS v, CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition"))[0];
        var info = new CliDatabaseServerInfo { Version = $"SQL Server {Str(row["v"])} ({Str(row["edition"])})" };
        // Needs VIEW SERVER STATE: without it the version is still shown
        try
        {
            var started = await ScalarAsync(db, "SELECT sqlserver_start_time FROM sys.dm_os_sys_info");
            if (started is DateTime time)
            {
                var utc = DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime();
                info.StartedAt = CliSqlSession.Iso(new DateTimeOffset(utc, TimeSpan.Zero));
                info.UptimeSeconds = (long)(DateTime.UtcNow - utc).TotalSeconds;
            }
        }
        catch (System.Data.Common.DbException)
        {
            // No VIEW SERVER STATE
        }
        return info;
    }

    private static async Task<CliDatabaseSize> SqlServerSizeAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT CAST(SUM(CASE WHEN type = 0 THEN CAST(size AS bigint) ELSE 0 END) * 8192 AS bigint) AS data, " +
            "CAST(SUM(CASE WHEN type = 1 THEN CAST(size AS bigint) ELSE 0 END) * 8192 AS bigint) AS log, " +
            "CAST(SUM(CAST(size AS bigint)) * 8192 AS bigint) AS total FROM sys.database_files"))[0];
        var size = new CliDatabaseSize { Bytes = Long(row["total"]), DataBytes = Long(row["data"]), LogBytes = Long(row["log"]) };
        try
        {
            size.IndexBytes = Long(await ScalarAsync(db,
                "SELECT CAST(COALESCE(SUM(used_page_count), 0) * 8192 AS bigint) FROM sys.dm_db_partition_stats WHERE index_id > 1"));
        }
        catch (System.Data.Common.DbException)
        {
            // No VIEW DATABASE STATE
        }
        return size;
    }

    private static async Task<CliDatabaseConnections> SqlServerConnectionsAsync(CliSqlSession db)
    {
        var row = (await db.QueryAsync(
            "SELECT (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND is_user_process = 1) AS open_count, " +
            "(SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND session_id <> @@SPID) AS active, " +
            "@@MAX_CONNECTIONS AS max_count"))[0];
        return new() { Open = Long(row["open_count"]), Active = Long(row["active"]), Max = Long(row["max_count"]) };
    }

    private static async Task<(CliDatabaseCounters, double?)> SqlServerCountersAsync(CliSqlSession db)
    {
        var rows = await db.QueryAsync(
            "SELECT RTRIM(object_name) AS object_name, RTRIM(counter_name) AS counter_name, RTRIM(instance_name) AS instance_name, cntr_value " +
            "FROM sys.dm_os_performance_counters WHERE counter_name IN ('Batch Requests/sec', 'Transactions/sec', " +
            "'Buffer cache hit ratio', 'Buffer cache hit ratio base')");
        if (rows.Count == 0) throw new InvalidOperationException("no performance counters (VIEW SERVER STATE is needed)");
        long? Find(string counter, string? instance = null) => rows
            .Where(r => Str(r["counter_name"]) == counter && (instance == null || Str(r["instance_name"]) == instance))
            .Select(r => Long(r["cntr_value"]))
            .FirstOrDefault();
        var database = Str(await ScalarAsync(db, "SELECT DB_NAME()"));
        var ratio = Find("Buffer cache hit ratio");
        var ratioBase = Find("Buffer cache hit ratio base");
        return (new()
        {
            Queries = Find("Batch Requests/sec"),
            Transactions = Find("Transactions/sec", database),
        }, ratioBase > 0 && ratio != null ? Math.Min(1, (double)ratio.Value / ratioBase.Value) : null);
    }

    private static async Task<List<CliDatabaseTable>> SqlServerTablesAsync(CliSqlSession db)
    {
        var rows = await db.QueryAsync(
            $"SELECT TOP {TopTables} s.name AS schema_name, t.name AS table_name, " +
            "SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS row_count, " +
            "CAST(SUM(p.reserved_page_count) * 8192 AS bigint) AS bytes " +
            "FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "JOIN sys.dm_db_partition_stats p ON p.object_id = t.object_id " +
            "GROUP BY s.name, t.name ORDER BY bytes DESC, t.name");
        return rows.Select(r => new CliDatabaseTable
        {
            Schema = Str(r["schema_name"]),
            Name = Str(r["table_name"])!,
            Rows = Long(r["row_count"]),
            Bytes = Long(r["bytes"]),
        }).ToList();
    }

    // SQLite

    private static async Task<long?> SqliteSizeAsync(CliSqlSession db) =>
        Long(await ScalarAsync(db, "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()"));

    private static async Task<List<CliDatabaseTable>> SqliteTablesAsync(CliSqlSession db)
    {
        var names = (await db.QueryAsync(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
            .Select(r => Str(r["name"])!)
            .ToList();
        Dictionary<string, long>? bytes = null;
        try
        {
            // dbstat is only in builds compiled with SQLITE_ENABLE_DBSTAT_VTAB; indexes count to their table
            bytes = (await db.QueryAsync(
                    "SELECT COALESCE(m.tbl_name, d.name) AS name, SUM(d.pgsize) AS bytes FROM dbstat d " +
                    "LEFT JOIN sqlite_master m ON m.name = d.name GROUP BY COALESCE(m.tbl_name, d.name)"))
                .ToDictionary(r => Str(r["name"])!, r => Long(r["bytes"]) ?? 0);
        }
        catch (System.Data.Common.DbException)
        {
            // No dbstat: rows only
        }

        var tables = new List<CliDatabaseTable>();
        var counted = bytes != null ? names.OrderByDescending(n => bytes.GetValueOrDefault(n)).Take(20) : names.Take(20);
        foreach (var name in counted)
        {
            var rows = Long(await ScalarAsync(db, $"SELECT COUNT(*) FROM {CliSqlSchema.QuoteIdent(CliSqlDialects.Sqlite, name)}"));
            tables.Add(new() { Name = name, Rows = rows, Bytes = bytes?.GetValueOrDefault(name) });
        }
        return tables
            .OrderByDescending(t => t.Bytes ?? -1)
            .ThenByDescending(t => t.Rows ?? -1)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(TopTables)
            .ToList();
    }

    // Helpers

    private static async Task<object?> ScalarAsync(CliSqlSession db, string sql)
    {
        var rows = await db.QueryAsync(sql);
        return rows.Count == 0 ? null : rows[0].Values.FirstOrDefault();
    }

    private static string? Str(object? value) => value is null or DBNull ? null : value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

    private static long? Long(object? value) => value switch
    {
        null or DBNull => null,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        ulong ul => (long)ul,
        uint ui => ui,
        decimal d => (long)Math.Round(d),
        double d => (long)Math.Round(d),
        float f => (long)Math.Round(f),
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => (long)Math.Round(parsed),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    /// <summary><c>PostgreSQL 17.2 on x86_64-pc-linux-gnu, compiled by ...</c> -> <c>PostgreSQL 17.2</c>.</summary>
    private static string? FirstWords(string? version)
    {
        if (version == null) return null;
        var parts = version.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : version;
    }
}
