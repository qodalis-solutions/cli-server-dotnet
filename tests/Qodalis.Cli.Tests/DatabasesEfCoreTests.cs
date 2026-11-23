using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Plugin.Audit.EfCore;
using Qodalis.Cli.Plugin.FileSystem.Sqlite;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class MigrationNote
{
    public int Id { get; set; }
}

/// <summary>A host's context with two EF Core migrations (below), as <c>dotnet ef migrations add</c> writes them.</summary>
public class MigrationNotesDbContext(DbContextOptions<MigrationNotesDbContext> options) : DbContext(options)
{
    public DbSet<MigrationNote> Notes => Set<MigrationNote>();
}

[DbContext(typeof(MigrationNotesDbContext))]
[Migration("20260101000000_CreateNotes")]
public class CreateNotesMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("Notes", t => new { Id = t.Column<int>(nullable: false) }, constraints: t => t.PrimaryKey("PK_Notes", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Notes");
}

[DbContext(typeof(MigrationNotesDbContext))]
[Migration("20260102000000_CreateTags")]
public class CreateTagsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("Tags", t => new { Id = t.Column<int>(nullable: false) }, constraints: t => t.PrimaryKey("PK_Tags", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Tags");
}

/// <summary>
/// <c>Qodalis.Cli.Plugin.Databases.EfCore</c>: a DbContext's EF Core migrations in the plugin, the EF Core stores'
/// schemas as sources (renaming the previous release's tables), and filesystem-sqlite's versioned schema.
/// </summary>
public sealed class DatabasesEfCoreTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"qcli-migrations-ef-{Guid.NewGuid():N}.db");
    private UsersTestHost? _h;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_h != null) await _h.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private string ConnectionString => $"Data Source={_file}";

    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private async Task<CliDatabases> StartAsync(Action<CliDatabasesBuilder> configure, Action<Qodalis.Cli.Extensions.CliBuilder>? cli = null)
    {
        _h = await UsersTestHost.StartAsync(
            configureServices: services => services.AddDbContextFactory<MigrationNotesDbContext>(o => o
                .UseSqlite(ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))),
            configureCli: c =>
            {
                cli?.Invoke(c);
                c.AddDatabases(configure);
            });
        return _h.Host.Services.GetRequiredService<CliDatabases>();
    }

    [Fact]
    public async Task ShowsAppliesAndRollsBackADbContextsMigrations_KeepingEfsHistory()
    {
        var migrations = await StartAsync(m => m.AddEfCore<MigrationNotesDbContext>("app"));
        var db = migrations.Require("app");

        var states = await db.StatusAsync();
        Assert.Equal(["ef:20260101000000_CreateNotes CreateNotes pending", "ef:20260102000000_CreateTags CreateTags pending"],
            states.Select(s => $"{s.Source}:{s.Id} {s.Name} {s.Status}"));
        Assert.All(states, s => Assert.True(s.HasDown));

        var script = await db.ScriptAsync("up", null);
        Assert.Contains("CREATE TABLE \"Notes\"", script);
        Assert.Contains("CREATE TABLE \"Tags\"", script);

        var first = await migrations.RunAsync("app", "up", new CliMigrationRunOptions { To = "20260101000000_CreateNotes" });
        Assert.Equal("succeeded", first.Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
        Assert.Equal(["applied", "pending"], (await db.StatusAsync()).Select(s => s.Status));

        Assert.Equal(["20260102000000_CreateTags"], (await migrations.RunAsync("app", "up", new CliMigrationRunOptions())).Applied.Select(a => a.Id));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Tags'"));
        // EF keeps its history; the plugin keeps only its runs
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM \"QcliMigrations\""));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM \"QcliMigrationRuns\""));

        Assert.Contains("DROP TABLE \"Notes\"", await db.ScriptAsync("down", "base"));
        var down = await migrations.RunAsync("app", "down", new CliMigrationRunOptions { To = "base" });
        Assert.Equal("succeeded", down.Status);
        Assert.Equal(["20260102000000_CreateTags", "20260101000000_CreateNotes"], down.Applied.Select(a => a.Id));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task AppliesTheDbContextsMigrationsOnStartup_InsteadOfMigrateAsync()
    {
        var migrations = await StartAsync(m => m.AddEfCore<MigrationNotesDbContext>("app", db => db.ApplyMigrationsOnStartup()));
        Assert.All(await migrations.Require("app").StatusAsync(), s => Assert.Equal("applied", s.Status));
        Assert.Equal("startup", (await migrations.Require("app").RunsAsync()).Items[0].Trigger);
    }

    [Fact]
    public async Task AStoresSchemaSource_RenamesThePreviousReleasesTable_WithItsEntries()
    {
        // The audit table as 1.0.0-beta.29 created it, with one entry
        Sql("""
            CREATE TABLE audit_entries (Id INTEGER NOT NULL CONSTRAINT PK_audit_entries PRIMARY KEY AUTOINCREMENT, EntryId TEXT NOT NULL,
                Timestamp INTEGER NOT NULL, Event TEXT NOT NULL, Username TEXT NOT NULL, UserId TEXT NULL, Details TEXT NULL, Actor TEXT NULL,
                ActorId TEXT NULL, Target TEXT NULL, Properties TEXT NULL, Metadata TEXT NULL);
            CREATE UNIQUE INDEX IX_audit_entries_EntryId ON audit_entries (EntryId);
            INSERT INTO audit_entries (EntryId, Timestamp, Event, Username) VALUES ('e1', 1000, 'auth.login', 'alice');
            """);

        var migrations = await StartAsync(
            m => m.AddAuditStoreSchema("cli", db => db.ApplyMigrationsOnStartup()),
            cli => cli.AddEfCoreAuditStore(o => o.UseSqlite(ConnectionString)));

        Assert.Equal(["qcli-audit:1 applied"], (await migrations.Require("cli").StatusAsync()).Select(s => $"{s.Source}:{s.Id} {s.Status}"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'audit_entries'"));
        var entries = await _h!.Host.Services.GetRequiredService<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Username = "alice" });
        Assert.Equal("e1", Assert.Single(entries).Id);
    }

    [Fact]
    public async Task FilesystemSqlite_RenamesTheFilesTable_AndRecordsItsMigrations()
    {
        Sql("""
            CREATE TABLE files (id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                type TEXT NOT NULL CHECK(type IN ('file', 'directory')), content BLOB, size INTEGER NOT NULL DEFAULT 0,
                permissions TEXT DEFAULT '644', created_at TEXT NOT NULL, modified_at TEXT NOT NULL, parent_path TEXT);
            CREATE INDEX idx_files_parent ON files(parent_path);
            INSERT INTO files (path, name, type, content, size, created_at, modified_at, parent_path)
                VALUES ('/notes', 'notes', 'directory', NULL, 0, '2026-01-01', '2026-01-01', '/'),
                       ('/notes/a.txt', 'a.txt', 'file', X'68656C6C6F', 5, '2026-01-01', '2026-01-01', '/notes');
            """);

        var provider = new SqliteFileStorageProvider(new SqliteProviderOptions { DbPath = _file });

        Assert.Equal("hello", await provider.ReadFileAsync("/notes/a.txt"));
        Assert.Equal(["a.txt"], (await provider.ListAsync("/notes")).Select(f => f.Name));
        await provider.WriteFileAsync("/notes/b.txt", "new");
        Assert.Equal(2, (await provider.ListAsync("/notes")).Count);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('files', 'idx_files_parent')"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_QcliFiles_ParentPath'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('QcliFiles') WHERE name = 'ParentPath'"));
        Assert.Equal("1,2", Scalar("SELECT group_concat(\"Id\") FROM (SELECT \"Id\" FROM \"QcliMigrations\" WHERE \"Source\" = 'qcli-filesystem' ORDER BY \"Id\")"));

        // Restarting applies nothing new
        var again = new SqliteFileStorageProvider(new SqliteProviderOptions { DbPath = _file });
        Assert.Equal("new", await again.ReadFileAsync("/notes/b.txt"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM \"QcliMigrationRuns\" WHERE \"Applied\" LIKE '%qcli-filesystem%'"));
    }

    [Fact]
    public async Task FilesystemSqlite_KeepsTheOldTable_WithLegacyTableNames()
    {
        Sql("""
            CREATE TABLE files (id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                type TEXT NOT NULL, content BLOB, size INTEGER NOT NULL DEFAULT 0, permissions TEXT DEFAULT '644',
                created_at TEXT NOT NULL, modified_at TEXT NOT NULL, parent_path TEXT);
            INSERT INTO files (path, name, type, content, size, created_at, modified_at, parent_path)
                VALUES ('/a.txt', 'a.txt', 'file', X'6F6C64', 3, '2026-01-01', '2026-01-01', '/');
            """);

        var provider = new SqliteFileStorageProvider(new SqliteProviderOptions { DbPath = _file, LegacyTableNames = true });

        Assert.Equal("old", await provider.ReadFileAsync("/a.txt"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'QcliFiles'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('files') WHERE name = 'parent_path'"));
    }

    [Fact]
    public void ResolvesTableNames()
    {
        Assert.Equal("QcliFiles", Abstractions.Helpers.QcliTableNames.Resolve("Files"));
        Assert.Equal("AppFiles", Abstractions.Helpers.QcliTableNames.Resolve("Files", new() { TablePrefix = "App" }));
        Assert.Equal("Files", Abstractions.Helpers.QcliTableNames.Resolve("Files", new() { TablePrefix = "" }));
        Assert.Equal("MyFiles", Abstractions.Helpers.QcliTableNames.Resolve("Files", new() { Tables = { ["files"] = "MyFiles" } }));
        Assert.Equal("files", Abstractions.Helpers.QcliTableNames.Resolve("Files", new() { LegacyTableNames = true }, "files"));
        Assert.Equal("invalid table name: My Files (Parameter 'baseName')",
            Assert.Throws<ArgumentException>(() => Abstractions.Helpers.QcliTableNames.Resolve("Files", new() { Tables = { ["Files"] = "My Files" } })).Message);
    }
}
