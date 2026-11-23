using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Qodalis.Cli.Plugin.Databases;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The migrations runner on every dialect: SQLite always; PostgreSQL, MySQL and SQL Server when
/// <c>QCLI_TEST_POSTGRES_CS</c>, <c>QCLI_TEST_MYSQL_CS</c> and <c>QCLI_TEST_MSSQL_CS</c> hold ADO.NET connection strings to
/// databases the tests may write to (each test uses its own table names).
/// </summary>
public sealed class MigrationsRunnerTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files) File.Delete(file);
    }

    public static IEnumerable<object[]> Dialects()
    {
        yield return ["sqlite"];
        if (Environment.GetEnvironmentVariable("QCLI_TEST_POSTGRES_CS") is { Length: > 0 }) yield return ["postgres"];
        if (Environment.GetEnvironmentVariable("QCLI_TEST_MYSQL_CS") is { Length: > 0 }) yield return ["mysql"];
        if (Environment.GetEnvironmentVariable("QCLI_TEST_MSSQL_CS") is { Length: > 0 }) yield return ["mssql"];
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>A connection factory for the dialect and a unique name for the test's tables.</summary>
    private (Func<IServiceProvider, DbConnection> Connect, string Prefix) Database(string dialect)
    {
        var prefix = "T" + Guid.NewGuid().ToString("N")[..10];
        switch (dialect)
        {
            case "postgres":
            {
                var cs = Environment.GetEnvironmentVariable("QCLI_TEST_POSTGRES_CS")!;
                return (_ => new NpgsqlConnection(cs), prefix);
            }
            case "mysql":
            {
                var cs = Environment.GetEnvironmentVariable("QCLI_TEST_MYSQL_CS")!;
                return (_ => new MySqlConnection(cs), prefix);
            }
            case "mssql":
            {
                var cs = Environment.GetEnvironmentVariable("QCLI_TEST_MSSQL_CS")!;
                return (_ => new SqlConnection(cs), prefix);
            }
            default:
            {
                var file = Path.Combine(Path.GetTempPath(), $"qcli-migrations-{Guid.NewGuid():N}.db");
                _files.Add(file);
                return (_ => new SqliteConnection($"Data Source={file}"), prefix);
            }
        }
    }

    private static List<CliMigration> Migrations(string p) =>
    [
        new() { Id = "1", Name = "create notes", Up = $"CREATE TABLE {p}Notes (Id INT PRIMARY KEY)", Down = CliMigrationStep.FromSql($"DROP TABLE {p}Notes") },
        new() { Id = "2", Name = "create tags", Up = $"CREATE TABLE {p}Tags (Id INT PRIMARY KEY)", Down = CliMigrationStep.FromSql($"DROP TABLE {p}Tags") },
        new()
        {
            Id = "10", Name = "seed notes",
            Up = $"INSERT INTO {p}Notes (Id) VALUES (1)",
            Down = CliMigrationStep.FromCode(db => db.ExecuteAsync($"DELETE FROM {p}Notes WHERE Id = ?", 1)),
        },
    ];

    private static CliMigrationRunner Runner(Func<IServiceProvider, DbConnection> connect, string prefix, params ICliMigrationSource[] sources)
    {
        var options = new CliMigrationDatabaseOptions { Connection = connect, TablePrefix = "Qcli" + prefix };
        foreach (var source in sources) options.WithSource(source);
        return new CliMigrationRunner("main", options, new NoServices());
    }

    private static async Task<int> CountAsync(Func<IServiceProvider, DbConnection> connect, string table)
    {
        await using var connection = connect(new NoServices());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ExistsAsync(Func<IServiceProvider, DbConnection> connect, string table)
    {
        try
        {
            await CountAsync(connect, table);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static async Task DropAsync(Func<IServiceProvider, DbConnection> connect, params string[] tables)
    {
        foreach (var table in tables)
        {
            await using var connection = connect(new NoServices());
            await connection.OpenAsync();
            // Created unquoted (folded to lowercase by PostgreSQL) or quoted (the history tables)
            foreach (var name in new[] { table, CliSqlSchema.QuoteIdent(CliSqlDialects.Of(connection), table) })
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"DROP TABLE {name}";
                    await command.ExecuteNonQueryAsync();
                }
                catch (DbException)
                {
                    // Not there
                }
            }
        }
    }

    private static string[] Tables(string p) => [$"{p}Notes", $"{p}Tags", $"{p}Things", $"Qcli{p}Migrations", $"Qcli{p}MigrationRuns"];

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task AppliesInIdOrder_RecordsHistoryAndRuns_ThenHasNothingToDo(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p)));
            Assert.Equal(["1:pending", "2:pending", "10:pending"], (await runner.StatusAsync()).Select(s => $"{s.Id}:{s.Status}"));

            var run = await runner.UpAsync(new CliMigrationRunOptions { Actor = "alice", Trigger = "api" });

            Assert.Equal("succeeded", run.Status);
            Assert.Equal(["1", "2", "10"], run.Applied.Select(a => a.Id));
            var states = await runner.StatusAsync();
            Assert.All(states, s => Assert.Equal("applied", s.Status));
            Assert.All(states, s => Assert.Equal("alice", s.AppliedBy));
            Assert.All(states, s => Assert.NotNull(s.AppliedAt));
            Assert.Equal(1, await CountAsync(connect, $"{p}Notes"));

            var again = await runner.UpAsync();
            Assert.Empty(again.Applied);
            var runs = await runner.RunsAsync();
            Assert.Equal(2, runs.Total);
            Assert.Equal(run.Id, runs.Items[1].Id);
            Assert.Equal("alice", runs.Items[1].Actor);
            Assert.Equal(["1", "2", "10"], runs.Items[1].Applied.Select(a => a.Id));
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task UpTo_DownTo_AndDownToBase(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p)));
            Assert.Equal(["1"], (await runner.UpAsync(new CliMigrationRunOptions { To = "1" })).Applied.Select(a => a.Id));
            Assert.Equal(["2", "10"], (await runner.UpAsync(new CliMigrationRunOptions { To = "app:10" })).Applied.Select(a => a.Id));

            var down = await runner.DownAsync(new CliMigrationRunOptions { To = "1" });
            Assert.Equal(["10", "2"], down.Applied.Select(a => a.Id));
            Assert.Equal(0, await CountAsync(connect, $"{p}Notes"));
            Assert.False(await ExistsAsync(connect, $"{p}Tags"));

            Assert.Equal(["1"], (await runner.DownAsync(new CliMigrationRunOptions { To = "base" })).Applied.Select(a => a.Id));
            Assert.All(await runner.StatusAsync(), s => Assert.Equal("pending", s.Status));

            var unknown = await Assert.ThrowsAsync<CliMigrationException>(() => runner.UpAsync(new CliMigrationRunOptions { To = "7" }));
            Assert.Equal(404, unknown.StatusCode);
            Assert.Equal(400, (await Assert.ThrowsAsync<CliMigrationException>(() => runner.DownAsync(new CliMigrationRunOptions()))).StatusCode);
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task AFailingMigrationStopsTheRun_RollsBackItsStep_AndShowsAsFailed(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var list = Migrations(p).Take(1).ToList();
            // A step that creates a table, then fails: its transaction takes the table back (MySQL commits DDL itself)
            list.Add(new CliMigration
            {
                Id = "2", Name = "broken",
                Up = CliMigrationStep.FromCode(async db =>
                {
                    await db.ExecuteAsync($"CREATE TABLE {p}Things (Id INT PRIMARY KEY)");
                    await db.ExecuteAsync("SELECT * FROM no_such_table_here");
                }),
            });
            var runner = Runner(connect, p, new CliCodeMigrationSource("app", list));

            var run = await runner.UpAsync();

            Assert.Equal("failed", run.Status);
            Assert.Equal(["1"], run.Applied.Select(a => a.Id));
            Assert.StartsWith("app:2 broken: ", run.Error);
            Assert.Equal(new CliMigrationRef("app", "2"), run.Failed);
            Assert.Equal(["1:applied", "2:failed"], (await runner.StatusAsync()).Select(s => $"{s.Id}:{s.Status}"));
            if (dialect != "mysql") Assert.False(await ExistsAsync(connect, $"{p}Things"));
            var last = (await runner.RunsAsync(1)).Items[0];
            Assert.Equal("failed", last.Status);
            Assert.Equal(run.Error, last.Error);
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ChangedOrMissingMigrations_BlockRuns_UnlessForced(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            await Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p).Take(2).ToList())).UpAsync();

            var edited = Migrations(p).Take(2).ToList();
            edited[1] = new CliMigration { Id = "2", Name = "create tags", Up = $"CREATE TABLE {p}Tags (Id INT PRIMARY KEY, Name VARCHAR(20))" };
            var changed = Runner(connect, p, new CliCodeMigrationSource("app", edited.Append(Migrations(p)[2]).ToList()));
            Assert.Equal(["applied", "changed", "pending"], (await changed.StatusAsync()).Select(s => s.Status));
            var blocked = await Assert.ThrowsAsync<CliMigrationException>(() => changed.UpAsync());
            Assert.Equal(409, blocked.StatusCode);
            Assert.Contains("app:2 (changed)", blocked.Message);
            Assert.Equal(["10"], (await changed.UpAsync(new CliMigrationRunOptions { Force = true })).Applied.Select(a => a.Id));

            var missing = Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p).Where(m => m.Id != "2").ToList()));
            Assert.Contains(await missing.StatusAsync(), s => s.Id == "2" && s.Status == "missing");
            Assert.Equal(409, (await Assert.ThrowsAsync<CliMigrationException>(() => missing.UpAsync())).StatusCode);
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task OneRunAtATime_AcrossRunners(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var gate = new TaskCompletionSource();
            var started = new TaskCompletionSource();
            var slow = new List<CliMigration>
            {
                new()
                {
                    Id = "1", Name = "slow", Transaction = dialect != "sqlite",
                    Up = CliMigrationStep.FromCode(async _ =>
                    {
                        started.SetResult();
                        await gate.Task;
                    }),
                },
            };
            var first = Runner(connect, p, new CliCodeMigrationSource("app", slow));
            var running = first.UpAsync();
            await started.Task;

            // The same runner: busy at once
            Assert.Equal(409, (await Assert.ThrowsAsync<CliMigrationException>(() => first.UpAsync())).StatusCode);
            // Another process (runner): the database lock answers busy, or makes it wait
            var second = Runner(connect, p, new CliCodeMigrationSource("app", slow));
            if (dialect != "sqlite")
            {
                Assert.Equal(409, (await Assert.ThrowsAsync<CliMigrationException>(() => second.UpAsync())).StatusCode);
                var waiting = second.UpAsync(new CliMigrationRunOptions { Wait = true });
                await Task.Delay(300);
                Assert.False(waiting.IsCompleted);
                gate.SetResult();
                Assert.Equal("succeeded", (await running).Status);
                Assert.Empty((await waiting).Applied);
            }
            else
            {
                gate.SetResult();
                Assert.Equal("succeeded", (await running).Status);
            }
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task DryRunShowsTheSql_AndRunsNothing(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p)));
            var sql = await runner.ScriptAsync("up", "2");
            Assert.Contains($"-- app:1 create notes (up)\nCREATE TABLE {p}Notes", sql);
            Assert.Contains($"CREATE TABLE {p}Tags", sql);
            Assert.DoesNotContain("INSERT", sql);
            Assert.False(await ExistsAsync(connect, $"{p}Notes"));

            await runner.UpAsync();
            var down = await runner.ScriptAsync("down", "1");
            Assert.Contains("-- app:10 seed notes (down)\n-- (code: runs a function, no SQL to show)", down);
            Assert.Contains($"DROP TABLE {p}Tags", down);
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task AdoptsALegacyTable_WithItsColumnsAndData(string dialect)
    {
        var (connect, prefix) = Database(dialect);
        // Unquoted names, which PostgreSQL folds to lowercase: lowercase all along
        var p = prefix.ToLowerInvariant();
        var legacy = $"{p}_old_things";
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app",
            [
                new CliMigration { Id = "0", Name = "the old release", Up = $"CREATE TABLE {legacy} (id INT PRIMARY KEY, created_at VARCHAR(30)); INSERT INTO {legacy} (id, created_at) VALUES (7, 'then')" },
                new CliMigration
                {
                    Id = "1", Name = "adopt",
                    Up = CliMigrationStep.FromCode(async db =>
                    {
                        Assert.True(await CliSqlSchema.AdoptLegacyTableAsync(db, legacy, $"{p}things",
                            new Dictionary<string, string> { ["id"] = "Id", ["created_at"] = "CreatedAt" }));
                        Assert.False(await CliSqlSchema.AdoptLegacyTableAsync(db, legacy, $"{p}things"));
                        Assert.True(await CliSqlSchema.ColumnExistsAsync(db, $"{p}things", "CreatedAt"));
                        Assert.False(await CliSqlSchema.ColumnExistsAsync(db, $"{p}things", "created_at"));
                    }),
                },
            ]));
            var run = await runner.UpAsync();
            Assert.True(run.Status == "succeeded", run.Error);
            Assert.Equal(1, await CountAsync(connect, $"{p}things"));
            Assert.False(await ExistsAsync(connect, legacy));
        }
        finally
        {
            await DropAsync(connect, [.. Tables(prefix), $"{p}things", legacy]);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task IndexExists_FindsAnIndexOfTheTable_ByName(string dialect)
    {
        var (connect, prefix) = Database(dialect);
        var p = prefix.ToLowerInvariant();
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app",
            [
                new CliMigration { Id = "1", Name = "things", Up = $"CREATE TABLE {p}things (id INT PRIMARY KEY, started VARCHAR(30))" },
                new CliMigration
                {
                    Id = "2", Name = "index",
                    Up = CliMigrationStep.FromCode(async db =>
                    {
                        Assert.False(await CliSqlSchema.IndexExistsAsync(db, $"{p}things", $"ix_{p}_started"));
                        await db.ExecuteAsync($"CREATE INDEX ix_{p}_started ON {p}things (started)");
                        Assert.True(await CliSqlSchema.IndexExistsAsync(db, $"{p}things", $"ix_{p}_started"));
                        Assert.False(await CliSqlSchema.IndexExistsAsync(db, $"{p}things", $"ix_{p}_other"));
                    }),
                },
            ]));
            var run = await runner.UpAsync();
            Assert.True(run.Status == "succeeded", run.Error);
        }
        finally
        {
            await DropAsync(connect, [.. Tables(prefix), $"{p}things"]);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task SqlFolder_ReadsUpAndDownFiles(string dialect)
    {
        var (connect, p) = Database(dialect);
        var dir = Directory.CreateTempSubdirectory("qcli-sql-folder-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "0001_create_notes.up.sql"), $"CREATE TABLE {p}Notes (Id INT PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(dir, "0001_create_notes.down.sql"), $"DROP TABLE {p}Notes;");
            await File.WriteAllTextAsync(Path.Combine(dir, "0002_seed-notes.up.sql"),
                $"-- qcli:no-transaction\nINSERT INTO {p}Notes (Id) VALUES (1);\nINSERT INTO {p}Notes (Id) VALUES (2);");
            await File.WriteAllTextAsync(Path.Combine(dir, "readme.txt"), "not a migration");
            var runner = Runner(connect, p, new CliSqlFolderMigrationSource(dir));

            var states = await runner.StatusAsync();
            Assert.Equal(["0001 create notes", "0002 seed notes"], states.Select(s => $"{s.Id} {s.Name}"));
            Assert.Equal([true, false], states.Select(s => s.HasDown));
            Assert.Equal(2, (await runner.UpAsync()).Applied.Count);
            Assert.Equal(2, await CountAsync(connect, $"{p}Notes"));
        }
        finally
        {
            Directory.Delete(dir, true);
            await DropAsync(connect, Tables(p));
        }
    }

    [Fact]
    public void ComparesIdsNumerically()
    {
        Assert.Equal(["1", "2", "10", "20261120111649_PublicApi", "a"],
            new[] { "10", "a", "2", "20261120111649_PublicApi", "1" }.OrderBy(x => x, CliMigrationIdComparer.Instance));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Stats_ReadSizeServerConnectionsCountersAndTables(string dialect)
    {
        var (connect, p) = Database(dialect);
        try
        {
            var runner = Runner(connect, p, new CliCodeMigrationSource("app", Migrations(p)));
            await runner.UpAsync();

            var stats = await runner.StatsAsync();
            Assert.Equal("main", stats.Name);
            Assert.Equal(dialect, stats.Dialect);
            Assert.True(DateTimeOffset.TryParse(stats.CollectedAt, out _));
            Assert.Empty(stats.Unavailable.Select(u => $"{u.Metric}: {u.Error}"));
            Assert.StartsWith(dialect switch
            {
                "postgres" => "PostgreSQL ",
                "mysql" => "M",
                "mssql" => "SQL Server ",
                _ => "SQLite ",
            }, stats.Server!.Version);
            Assert.True(stats.Size!.Bytes > 0);
            Assert.True(await runner.SizeAsync() > 0);
            // The shared test databases may hold larger tables of other tests: the test's own are listed when among the 10
            Assert.InRange(stats.Tables!.Count, 1, 10);
            if (dialect != "sqlite") Assert.Equal(stats.Tables.Select(t => t.Bytes).OrderByDescending(b => b), stats.Tables.Select(t => t.Bytes));
            var notes = stats.Tables.FirstOrDefault(t => string.Equals(t.Name, $"{p}Notes", StringComparison.OrdinalIgnoreCase));
            if (dialect == "sqlite") Assert.NotNull(notes);
            if (notes != null && dialect is "sqlite" or "mssql") Assert.Equal(1, notes.Rows);

            if (dialect == "sqlite")
            {
                Assert.Null(stats.Connections);
                Assert.Null(stats.Counters);
            }
            else
            {
                Assert.True(stats.Server.UptimeSeconds > 0);
                Assert.NotNull(stats.Server.StartedAt);
                Assert.True(stats.Connections!.Open >= 1);
                Assert.True(stats.Connections.Max > 0);
                Assert.True(stats.Counters!.Transactions > 0);
                Assert.NotNull(stats.CountersScope);
                Assert.InRange(stats.CacheHitRatio!.Value, 0, 1);
            }
        }
        finally
        {
            await DropAsync(connect, Tables(p));
        }
    }

    [Fact]
    public void NumbersPlaceholders_OutsideQuotes()
    {
        Assert.Equal("SELECT @p0, '?', \"a?\", @p1", CliSqlSession.NumberPlaceholders("SELECT ?, '?', \"a?\", ?", 2));
    }
}
