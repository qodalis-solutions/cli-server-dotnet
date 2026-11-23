using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>Where a migration stands in a database: <c>applied</c>, <c>pending</c>, <c>changed</c>, <c>missing</c> or <c>failed</c>.</summary>
public static class CliMigrationStatus
{
    public const string Applied = "applied";
    public const string Pending = "pending";
    public const string Changed = "changed";
    public const string Missing = "missing";
    public const string Failed = "failed";
}

/// <summary>A migration with its status (<c>GET /api/v1/qcli/databases/{db}/migrations</c>).</summary>
public sealed class CliMigrationState
{
    public required string Source { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedBy { get; init; }

    public bool HasDown { get; init; }
}

/// <summary>A migration a run applied or rolled back.</summary>
public sealed record CliMigrationApplied(string Source, string Id, string Name, long DurationMs);

/// <summary>Where a failed run stopped.</summary>
public sealed record CliMigrationRef(string Source, string Id);

/// <summary>One invocation of up or down, kept in <c>QcliMigrationRuns</c>.</summary>
public sealed class CliMigrationRun
{
    public required string Id { get; init; }

    /// <summary><c>up</c> or <c>down</c>.</summary>
    public required string Direction { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    /// <summary><c>startup</c>, <c>api</c>, <c>terminal</c> or <c>mcp</c>.</summary>
    public required string Trigger { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    public required string StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishedAt { get; set; }

    /// <summary><c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
    public required string Status { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    public List<CliMigrationApplied> Applied { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliMigrationRef? Failed { get; set; }
}

/// <summary>A run's history page.</summary>
public sealed record CliMigrationRunsPage(IReadOnlyList<CliMigrationRun> Items, int Total);

/// <summary>Options of a run.</summary>
public sealed class CliMigrationRunOptions
{
    /// <summary>Up: stop after this migration (<c>id</c> or <c>source:id</c>). Down: roll back to it, keeping it (<c>base</c> = everything).</summary>
    public string? To { get; init; }

    /// <summary>Run although a migration is <c>changed</c> or <c>missing</c>.</summary>
    public bool Force { get; init; }

    public string? Actor { get; init; }

    /// <summary><c>startup</c>, <c>api</c>, <c>terminal</c> or <c>mcp</c>.</summary>
    public string Trigger { get; init; } = "api";

    /// <summary>Wait for another process's run instead of answering busy (startup does).</summary>
    public bool Wait { get; init; }
}

/// <summary>A refused request: <see cref="StatusCode"/> is the HTTP status the API answers (400, 404 or 409).</summary>
public sealed class CliMigrationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>A database to migrate.</summary>
public sealed class CliMigrationDatabaseOptions : QcliTableNameOptions
{
    /// <summary>Creates a new, closed connection to the database (one per status read or run).</summary>
    public Func<IServiceProvider, DbConnection>? Connection { get; set; }

    /// <summary>Apply pending migrations when the server starts (startup waits for them; a failure stops it).</summary>
    public bool ApplyOnStartup { get; set; }

    internal List<Func<IServiceProvider, ICliMigrationSource>> Sources { get; } = [];

    /// <summary>Sets the connection factory.</summary>
    public CliMigrationDatabaseOptions UseConnection(Func<IServiceProvider, DbConnection> connection)
    {
        Connection = connection;
        return this;
    }

    /// <summary>Adds a source (for a runner used without <see cref="CliDatabasesBuilder"/>, e.g. a store migrating its own schema).</summary>
    public CliMigrationDatabaseOptions WithSource(ICliMigrationSource source)
    {
        Sources.Add(_ => source);
        return this;
    }

    /// <summary>Applies pending migrations at startup.</summary>
    public CliMigrationDatabaseOptions ApplyMigrationsOnStartup(bool apply = true)
    {
        ApplyOnStartup = apply;
        return this;
    }
}

/// <summary>Runs one database's migrations: status, up, down, and the run history.</summary>
public sealed class CliMigrationRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CliMigrationDatabaseOptions _options;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly List<ICliMigrationSource> _sources;
    private readonly CliSqlSchema.HistoryTables _tables;
    private string? _dialect;

    public CliMigrationRunner(string name, CliMigrationDatabaseOptions options, IServiceProvider services)
    {
        Name = name;
        _options = options;
        _services = services;
        if (options.Connection == null) throw new ArgumentException($"migration database {name} has no connection");
        _tables = new(QcliTableNames.Resolve("Migrations", options), QcliTableNames.Resolve("MigrationRuns", options));
        _sources = options.Sources.Select(f => f(services)).ToList();
        var duplicate = _sources.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new ArgumentException($"migration source already added to {name}: {duplicate.Key}");
    }

    public string Name { get; }

    /// <summary>The database's dialect (known once a connection was made).</summary>
    public string Dialect => _dialect ??= CliSqlDialects.Of(NewConnection());

    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public bool ApplyOnStartup => _options.ApplyOnStartup;

    private DbConnection NewConnection() => _options.Connection!(_services);

    private async Task<T> SessionAsync<T>(Func<CliSqlSession, Task<T>> work, CancellationToken ct)
    {
        await using var connection = NewConnection();
        _dialect ??= CliSqlDialects.Of(connection);
        var session = new CliSqlSession(connection, _dialect, _services, ct);
        await session.EnsureOpenAsync();
        return await work(session);
    }

    private sealed record Loaded(ICliMigrationSource Source, CliMigration Migration, string? Checksum)
    {
        public bool Tracked => Source is ICliTrackedMigrationSource;
        public string Key => $"{Source.Name}\0{Migration.Id}";
    }

    private sealed record HistoryRow(string Source, string Id, string Name, string? Checksum, string? AppliedAt, long? DurationMs, string? AppliedBy)
    {
        public string Key => $"{Source}\0{Id}";
    }

    private async Task<List<Loaded>> LoadAsync(CliSqlSession db)
    {
        var all = new List<Loaded>();
        foreach (var source in _sources)
        {
            var migrations = (await source.LoadAsync(db)).OrderBy(m => m.Id, CliMigrationIdComparer.Instance).ToList();
            for (var i = 1; i < migrations.Count; i++)
                if (migrations[i].Id == migrations[i - 1].Id) throw new InvalidOperationException($"duplicate migration {source.Name}:{migrations[i].Id}");
            all.AddRange(migrations.Select(m => new Loaded(source, m, source is ICliTrackedMigrationSource ? null : m.ChecksumFor(db.Dialect))));
        }
        return all;
    }

    private string Table(string table) => CliSqlSchema.QuoteTable(Dialect, table, _options.Schema);

    private string Col(string column) => CliSqlSchema.QuoteIdent(Dialect, column);

    private string Cols(params string[] columns) => string.Join(", ", columns.Select(Col));

    /// <summary>The history of the plugin's own sources, plus the applied migrations of the tracked ones.</summary>
    private async Task<List<HistoryRow>> HistoryAsync(CliSqlSession db)
    {
        var rows = new List<HistoryRow>();
        if (await CliSqlSchema.TableExistsAsync(db, _tables.Migrations, _options.Schema))
        {
            foreach (var r in await db.QueryAsync(
                         $"SELECT {Cols("Source", "Id", "Name", "Checksum", "AppliedAt", "DurationMs", "AppliedBy")} FROM {Table(_tables.Migrations)}"))
            {
                rows.Add(new(Str(r["Source"])!, Str(r["Id"])!, Str(r["Name"])!, Str(r["Checksum"]), CliSqlSession.Iso(r["AppliedAt"]),
                    r["DurationMs"] == null ? null : Convert.ToInt64(r["DurationMs"], CultureInfo.InvariantCulture), Str(r["AppliedBy"])));
            }
        }
        foreach (var source in _sources.OfType<ICliTrackedMigrationSource>())
        {
            rows.RemoveAll(r => r.Source == source.Name);
            rows.AddRange((await source.GetAppliedAsync(db)).Select(id => new HistoryRow(source.Name, id, id, null, null, null, null)));
        }
        return rows;
    }

    private static string? Str(object? value) => value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private async Task<CliMigrationRef?> LastFailureAsync(CliSqlSession db)
    {
        var run = (await RunsOnAsync(db, 1, 0)).Items.FirstOrDefault();
        return run?.Status == "failed" ? run.Failed : null;
    }

    private List<CliMigrationState> StatesOf(List<Loaded> loaded, List<HistoryRow> rows, CliMigrationRef? failed = null)
    {
        var applied = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var known = loaded.Select(l => l.Key).ToHashSet();
        var states = loaded.Select(l =>
        {
            if (!applied.TryGetValue(l.Key, out var row))
            {
                var isFailed = failed != null && failed.Source == l.Source.Name && failed.Id == l.Migration.Id;
                return new CliMigrationState
                {
                    Source = l.Source.Name,
                    Id = l.Migration.Id,
                    Name = l.Migration.Name,
                    HasDown = l.Migration.Down != null,
                    Status = isFailed ? CliMigrationStatus.Failed : CliMigrationStatus.Pending,
                };
            }
            return new CliMigrationState
            {
                Source = l.Source.Name,
                Id = l.Migration.Id,
                Name = l.Migration.Name,
                HasDown = l.Migration.Down != null,
                Status = row.Checksum != null && l.Checksum != null && row.Checksum != l.Checksum ? CliMigrationStatus.Changed : CliMigrationStatus.Applied,
                AppliedAt = row.AppliedAt,
                DurationMs = row.DurationMs,
                AppliedBy = row.AppliedBy,
            };
        }).ToList();
        // Applied, but gone from its source (a source this runner still has)
        var sources = SourceNames.ToHashSet();
        states.AddRange(rows.Where(r => sources.Contains(r.Source) && !known.Contains(r.Key)).Select(r => new CliMigrationState
        {
            Source = r.Source,
            Id = r.Id,
            Name = r.Name,
            Status = CliMigrationStatus.Missing,
            AppliedAt = r.AppliedAt,
            DurationMs = r.DurationMs,
            AppliedBy = r.AppliedBy,
            HasDown = false,
        }));
        return states;
    }

    /// <summary>Every migration with its status.</summary>
    public Task<List<CliMigrationState>> StatusAsync(CancellationToken ct = default) =>
        SessionAsync(async db => StatesOf(await LoadAsync(db), await HistoryAsync(db), await LastFailureAsync(db)), ct);

    /// <summary>Size, connections, load counters and the largest tables; a metric that cannot be read is listed as unavailable.</summary>
    public Task<CliDatabaseStats> StatsAsync(CancellationToken ct = default) =>
        SessionAsync(db => CliDatabaseStatsCollector.CollectAsync(Name, db), ct);

    /// <summary>The database's size in bytes.</summary>
    public Task<long?> SizeAsync(CancellationToken ct = default) => SessionAsync(CliDatabaseStatsCollector.SizeAsync, ct);

    /// <summary>The latest runs, newest first.</summary>
    public Task<CliMigrationRunsPage> RunsAsync(int limit = 20, int offset = 0, CancellationToken ct = default) =>
        SessionAsync(db => RunsOnAsync(db, limit, offset), ct);

    private async Task<CliMigrationRunsPage> RunsOnAsync(CliSqlSession db, int limit, int offset)
    {
        if (!await CliSqlSchema.TableExistsAsync(db, _tables.Runs, _options.Schema)) return new([], 0);
        var table = Table(_tables.Runs);
        var total = Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) AS n FROM {table}"))[0]["n"], CultureInfo.InvariantCulture);
        limit = Math.Max(1, limit);
        offset = Math.Max(0, offset);
        var page = Dialect == CliSqlDialects.SqlServer
            ? $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"LIMIT {limit} OFFSET {offset}";
        var rows = await db.QueryAsync(
            $"SELECT {Cols("Id", "Direction", "Target", "Trigger", "Actor", "StartedAt", "FinishedAt", "Status", "Error", "Applied")} FROM {table} ORDER BY {Col("StartedAt")} DESC, {Col("Id")} DESC {page}");
        return new(rows.Select(RunOf).ToList(), total);
    }

    private sealed record RunDetail(List<CliMigrationApplied>? Applied, CliMigrationRef? Failed);

    private static CliMigrationRun RunOf(Dictionary<string, object?> r)
    {
        var detail = Str(r["Applied"]) is { Length: > 0 } json ? JsonSerializer.Deserialize<RunDetail>(json, Json) : null;
        return new CliMigrationRun
        {
            Id = Str(r["Id"])!,
            Direction = Str(r["Direction"])!,
            Target = Str(r["Target"]),
            Trigger = Str(r["Trigger"])!,
            Actor = Str(r["Actor"]),
            StartedAt = CliSqlSession.Iso(r["StartedAt"])!,
            FinishedAt = CliSqlSession.Iso(r["FinishedAt"]),
            Status = Str(r["Status"])!,
            Error = Str(r["Error"]),
            Applied = detail?.Applied ?? [],
            Failed = detail?.Failed,
        };
    }

    /// <summary>Finds a migration by <c>id</c> or <c>source:id</c>.</summary>
    private static int Find(List<Loaded> loaded, string target)
    {
        var qualified = target.IndexOf(':') > 0;
        var matches = loaded.Select((l, i) => (l, i))
            .Where(x => qualified ? $"{x.l.Source.Name}:{x.l.Migration.Id}" == target : x.l.Migration.Id == target)
            .ToList();
        if (matches.Count == 0) throw new CliMigrationException($"unknown migration: {target}", 404);
        if (matches.Count > 1) throw new CliMigrationException($"migration {target} is in several sources: name it as source:id", 400);
        return matches[0].i;
    }

    /// <summary>Applies pending migrations (up to <see cref="CliMigrationRunOptions.To"/>).</summary>
    public Task<CliMigrationRun> UpAsync(CliMigrationRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        return ExclusiveAsync(options, (db, loaded) => RunUpAsync(db, loaded, options), ct);
    }

    /// <summary>Rolls back applied migrations, newest first, down to <see cref="CliMigrationRunOptions.To"/> (kept; <c>base</c> = everything).</summary>
    public Task<CliMigrationRun> DownAsync(CliMigrationRunOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(options.To)) throw new CliMigrationException("to is required to roll back (an id, or base for everything)", 400);
        return ExclusiveAsync(options, (db, loaded) => RunDownAsync(db, loaded, options), ct);
    }

    /// <summary>The SQL a run would execute (dry run); nothing is applied.</summary>
    public Task<string> ScriptAsync(string direction, string? to, CancellationToken ct = default)
    {
        if (direction == "down" && string.IsNullOrEmpty(to))
            throw new CliMigrationException("to is required to roll back (an id, or base for everything)", 400);
        return SessionAsync(async db =>
        {
            var loaded = await LoadAsync(db);
            var states = StatesOf(loaded, await HistoryAsync(db));
            var list = direction == "down" ? ToUndo(loaded, states, to!) : Pending(loaded, states, to);
            var parts = new List<string>();
            foreach (var l in list)
            {
                var step = direction == "down" ? l.Migration.Down! : l.Migration.Up;
                var sql = step.SqlFor(db.Dialect, l.Migration.Id) ?? (step.Script != null ? await step.Script(db) : null);
                parts.Add($"-- {l.Source.Name}:{l.Migration.Id} {l.Migration.Name} ({direction})\n{sql ?? "-- (code: runs a function, no SQL to show)"}");
            }
            return string.Join("\n\n", parts);
        }, ct);
    }

    /// <summary>Runs <paramref name="work"/> alone: one run per process at a time, and the database lock across processes.</summary>
    private async Task<CliMigrationRun> ExclusiveAsync(CliMigrationRunOptions options,
        Func<CliSqlSession, List<Loaded>, Task<CliMigrationRun>> work, CancellationToken ct)
    {
        if (!await _running.WaitAsync(options.Wait ? Timeout.Infinite : 0, ct))
            throw new CliMigrationException($"a migration run is in progress on {Name}", 409);
        try
        {
            return await SessionAsync(async db =>
            {
                var key = $"qcli-migrations:{_options.Schema ?? ""}.{_tables.Migrations}";
                if (!await CliSqlSchema.AcquireLockAsync(db, key, options.Wait))
                    throw new CliMigrationException($"a migration run is in progress on {Name}", 409);
                try
                {
                    await CliSqlSchema.EnsureHistoryTablesAsync(db, _tables, _options.Schema);
                    return await work(db, await LoadAsync(db));
                }
                finally
                {
                    try
                    {
                        await CliSqlSchema.ReleaseLockAsync(db, key);
                    }
                    catch (DbException)
                    {
                        // The lock goes with the connection anyway
                    }
                }
            }, ct);
        }
        finally
        {
            _running.Release();
        }
    }

    private static void ThrowIfBlocked(List<CliMigrationState> states, bool force)
    {
        var problems = states.Where(s => s.Status is CliMigrationStatus.Changed or CliMigrationStatus.Missing).ToList();
        if (problems.Count > 0 && !force)
            throw new CliMigrationException(
                $"migrations changed or missing since they were applied: {string.Join(", ", problems.Select(p => $"{p.Source}:{p.Id} ({p.Status})"))}; fix them or run with force",
                409);
    }

    private static List<Loaded> Pending(List<Loaded> loaded, List<CliMigrationState> states, string? to)
    {
        var last = to != null ? Find(loaded, to) : loaded.Count - 1;
        return loaded.Where((_, i) => i <= last && states[i].Status is not (CliMigrationStatus.Applied or CliMigrationStatus.Changed)).ToList();
    }

    private static List<Loaded> ToUndo(List<Loaded> loaded, List<CliMigrationState> states, string to)
    {
        var keep = to == "base" ? -1 : Find(loaded, to);
        var undo = loaded.Where((_, i) => i > keep && states[i].Status is CliMigrationStatus.Applied or CliMigrationStatus.Changed).Reverse().ToList();
        var noDown = undo.Where(l => l.Migration.Down == null).ToList();
        if (noDown.Count > 0)
            throw new CliMigrationException($"cannot roll back: no down step for {string.Join(", ", noDown.Select(l => $"{l.Source.Name}:{l.Migration.Id}"))}", 409);
        return undo;
    }

    private async Task<CliMigrationRun> RunUpAsync(CliSqlSession db, List<Loaded> loaded, CliMigrationRunOptions options)
    {
        var states = StatesOf(loaded, await HistoryAsync(db));
        ThrowIfBlocked(states, options.Force);
        var pending = Pending(loaded, states, options.To);
        return await RecordAsync(db, "up", options, pending, (l, runId) => ApplyAsync(db, l, l.Migration.Up, durationMs =>
            l.Tracked
                ? Task.CompletedTask
                : db.ExecuteAsync(
                    $"INSERT INTO {Table(_tables.Migrations)} ({Cols("Source", "Id", "Name", "Checksum", "AppliedAt", "DurationMs", "AppliedBy", "RunId")}) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    l.Source.Name, l.Migration.Id, l.Migration.Name, l.Checksum, DateTimeOffset.UtcNow, durationMs, options.Actor, runId)));
    }

    private async Task<CliMigrationRun> RunDownAsync(CliSqlSession db, List<Loaded> loaded, CliMigrationRunOptions options)
    {
        var undo = ToUndo(loaded, StatesOf(loaded, await HistoryAsync(db)), options.To!);
        return await RecordAsync(db, "down", options, undo, (l, _) => ApplyAsync(db, l, l.Migration.Down!, _ =>
            l.Tracked
                ? Task.CompletedTask
                : db.ExecuteAsync($"DELETE FROM {Table(_tables.Migrations)} WHERE {Col("Source")} = ? AND {Col("Id")} = ?", l.Source.Name, l.Migration.Id)));
    }

    /// <summary>Runs the step and its history change, in one transaction unless the migration opts out (or its source tracks itself).</summary>
    private static async Task<long> ApplyAsync(CliSqlSession db, Loaded l, CliMigrationStep step, Func<long, Task> bookkeeping)
    {
        var watch = Stopwatch.StartNew();
        var inTransaction = l.Migration.Transaction && !l.Tracked;
        if (inTransaction) db.Transaction = await db.Connection.BeginTransactionAsync(db.CancellationToken);
        try
        {
            var sql = step.SqlFor(db.Dialect, l.Migration.Id);
            if (sql == null) await step.Code!(db);
            else if (!string.IsNullOrWhiteSpace(sql)) await db.ExecuteScriptAsync(sql);
            await bookkeeping(watch.ElapsedMilliseconds);
            if (db.Transaction != null) await db.Transaction.CommitAsync(db.CancellationToken);
        }
        catch
        {
            if (db.Transaction != null)
            {
                try
                {
                    await db.Transaction.RollbackAsync(CancellationToken.None);
                }
                catch (DbException)
                {
                    // The connection may already have dropped it
                }
            }
            throw;
        }
        finally
        {
            if (db.Transaction != null)
            {
                await db.Transaction.DisposeAsync();
                db.Transaction = null;
            }
        }
        return watch.ElapsedMilliseconds;
    }

    /// <summary>Runs the steps, recording the run before and at the end.</summary>
    private async Task<CliMigrationRun> RecordAsync(CliSqlSession db, string direction, CliMigrationRunOptions options, List<Loaded> list,
        Func<Loaded, string, Task<long>> step)
    {
        var run = new CliMigrationRun
        {
            Id = Guid.NewGuid().ToString(),
            Direction = direction,
            Target = options.To,
            Trigger = options.Trigger,
            Actor = options.Actor,
            StartedAt = CliSqlSession.Iso(DateTimeOffset.UtcNow)!,
            Status = "running",
        };
        await db.ExecuteAsync(
            $"INSERT INTO {Table(_tables.Runs)} ({Cols("Id", "Direction", "Target", "Trigger", "Actor", "StartedAt", "Status", "Applied")}) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            run.Id, direction, run.Target, run.Trigger, run.Actor, DateTimeOffset.Parse(run.StartedAt, CultureInfo.InvariantCulture), "running",
            JsonSerializer.Serialize(new RunDetail([], null), Json));
        foreach (var l in list)
        {
            try
            {
                var durationMs = await step(l, run.Id);
                run.Applied.Add(new(l.Source.Name, l.Migration.Id, l.Migration.Name, durationMs));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Status = "failed";
                run.Error = $"{l.Source.Name}:{l.Migration.Id} {l.Migration.Name}: {ex.Message}";
                run.Failed = new(l.Source.Name, l.Migration.Id);
                break;
            }
        }
        if (run.Status == "running") run.Status = "succeeded";
        var finished = DateTimeOffset.UtcNow;
        run.FinishedAt = CliSqlSession.Iso(finished);
        await db.ExecuteAsync(
            $"UPDATE {Table(_tables.Runs)} SET {Col("FinishedAt")} = ?, {Col("Status")} = ?, {Col("Error")} = ?, {Col("Applied")} = ? WHERE {Col("Id")} = ?",
            finished, run.Status, run.Error, JsonSerializer.Serialize(new RunDetail(run.Applied, run.Failed), Json), run.Id);
        return run;
    }
}
