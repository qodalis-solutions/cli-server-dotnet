using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Qodalis.Cli.Plugin.Migrations.EfCore;

/// <summary>
/// Creates a CLI EF Core store's tables (users-efcore, jobs-efcore, audit-efcore, filesystem-efcore,
/// public-api-efcore): renames the previous release's tables first (<c>audit_entries</c> → <c>QcliAuditEntries</c>,
/// with their indexes, keeping the data; cli-server-dotnet#99), then creates the database or only the missing tables.
/// The stores run it on first use; with the migrations plugin it is their schema's first migration.
/// </summary>
public static class QcliEfStoreSchema
{
    /// <summary>
    /// Renames the legacy tables (<paramref name="renames"/>: legacy name → an entity's table), then
    /// creates what is missing. A table a host created in its own migrations is kept.
    /// </summary>
    /// <param name="db">The store's context (its connection and transaction are used).</param>
    /// <param name="renames">Previous release's table → the entity's table (<c>audit_entries</c> → <c>QcliAuditEntries</c>); empty to skip.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task EnsureAsync(DbContext db, IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            // EF's in-memory provider and other non-relational ones: nothing to rename, no tables to create
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        if (renames.Count > 0 && await RelationalExistsAsync(db, cancellationToken))
            await AdoptLegacyTablesAsync(db, renames, cancellationToken);

        try
        {
            if (await db.Database.EnsureCreatedAsync(cancellationToken)) return;
            if (!await AnyTableMissingAsync(db, cancellationToken)) return;
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
        }
        catch (DbException)
        {
            // Another instance of the server may have created them meanwhile
            if (await AnyTableMissingAsync(db, cancellationToken)) throw;
        }
    }

    private static async Task<bool> AnyTableMissingAsync(DbContext db, CancellationToken ct)
    {
        var tables = db.Model.GetEntityTypes().Select(e => (Table: e.GetTableName(), Schema: e.GetSchema())).Where(t => t.Table != null).Distinct().ToList();
        var missing = false;
        await WithSessionAsync(db, ct, async session =>
        {
            foreach (var (table, schema) in tables)
            {
                if (await CliSqlSchema.TableExistsAsync(session, table!, schema)) continue;
                missing = true;
                break;
            }
        });
        return missing;
    }

    private static async Task<bool> RelationalExistsAsync(DbContext db, CancellationToken ct) =>
        await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct);

    /// <summary>Renames each legacy table that exists to its entity's table (when that one does not), and its keys and indexes.</summary>
    private static Task AdoptLegacyTablesAsync(DbContext db, IReadOnlyDictionary<string, string> renames, CancellationToken ct) =>
        WithSessionAsync(db, ct, async session =>
        {
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table == null) continue;
                var schema = entity.GetSchema();
                foreach (var (legacy, _) in renames.Where(r => r.Value == table))
                {
                    try
                    {
                        if (!await CliSqlSchema.AdoptLegacyTableAsync(session, legacy, table, null, schema)) continue;
                    }
                    catch (DbException)
                    {
                        // Another instance of the server renamed it meanwhile
                        if (!await CliSqlSchema.TableExistsAsync(session, table, schema)) throw;
                        continue;
                    }
                    await RenameIndexesAsync(session, entity, legacy, table, schema);
                }
            }
        });

    /// <summary>
    /// Renames the primary key and indexes EF named after the old table (<c>PK_audit_entries</c> → <c>PK_QcliAuditEntries</c>).
    /// Only names change; on SQLite (no index renames) and when a name is not found they stay as they are.
    /// </summary>
    private static async Task RenameIndexesAsync(CliSqlSession session, IEntityType entity, string legacy, string table, string? schema)
    {
        var store = StoreObjectIdentifier.Table(table, schema);
        var names = entity.GetIndexes().Select(i => i.GetDatabaseName(store)).Where(n => n != null).Select(n => n!).ToList();
        var key = entity.FindPrimaryKey()?.GetName(store);
        foreach (var newName in names.Append(key).Where(n => n != null && n.Contains(table, StringComparison.Ordinal)))
        {
            var oldName = newName!.Replace(table, legacy, StringComparison.Ordinal);
            try
            {
                switch (session.Dialect)
                {
                    case CliSqlDialects.Postgres:
                        await session.ExecuteAsync(
                            $"ALTER INDEX IF EXISTS {CliSqlSchema.QuoteTable(session.Dialect, oldName, schema)} RENAME TO {CliSqlSchema.QuoteIdent(session.Dialect, newName)}");
                        break;
                    case CliSqlDialects.SqlServer:
                        var prefix = schema != null ? $"{schema}." : "";
                        if (newName == key)
                            await session.ExecuteAsync("IF OBJECT_ID(?) IS NOT NULL EXEC sp_rename ?, ?, 'OBJECT'", prefix + oldName, prefix + oldName, newName);
                        else
                            await session.ExecuteAsync("IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = ? AND object_id = OBJECT_ID(?)) EXEC sp_rename ?, ?, 'INDEX'",
                                oldName, prefix + table, $"{prefix}{table}.{oldName}", newName);
                        break;
                    case CliSqlDialects.MySql when newName != key:
                        await session.ExecuteAsync(
                            $"ALTER TABLE {CliSqlSchema.QuoteIdent(session.Dialect, table)} RENAME INDEX {CliSqlSchema.QuoteIdent(session.Dialect, oldName)} TO {CliSqlSchema.QuoteIdent(session.Dialect, newName)}");
                        break;
                }
            }
            catch (DbException)
            {
                // A name the old table did not have: nothing to rename
            }
            catch (ArgumentException)
            {
                // Not a plain identifier
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the context's connection (opened and closed around it when needed).</summary>
    private static async Task WithSessionAsync(DbContext db, CancellationToken ct, Func<CliSqlSession, Task> work)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            var session = new CliSqlSession(connection, CliSqlDialects.Of(connection), db.GetInfrastructure(), ct)
            {
                Transaction = db.Database.CurrentTransaction?.GetDbTransaction(),
            };
            await work(session);
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// A store's schema as a migration source for the migrations plugin: migration <c>1</c> runs
    /// <see cref="EnsureAsync"/> on <typeparamref name="TContext"/> with the store's renames
    /// (<see cref="QcliEfStoreRenames{TContext}"/>), then creates what is missing.
    /// </summary>
    public static ICliMigrationSource Source<TContext>(string name, string description) where TContext : DbContext =>
        new CliCodeMigrationSource(name,
        [
            new CliMigration
            {
                Id = "1",
                Name = description,
                Transaction = false,
                Up = CliMigrationStep.FromCode(async session =>
                {
                    await using var lease = await CliEfCoreContexts.CreateAsync<TContext>(session);
                    var renames = session.Services.GetService(typeof(QcliEfStoreRenames<TContext>)) as QcliEfStoreRenames<TContext>;
                    await EnsureAsync(lease.Context, renames?.Renames ?? new Dictionary<string, string>(), session.CancellationToken);
                }),
            },
        ]);
}

/// <summary>
/// The previous release's tables of a store and their new names (<c>audit_entries</c> → <c>QcliAuditEntries</c>),
/// registered by the store; empty with <c>LegacyTableNames</c>.
/// </summary>
public sealed class QcliEfStoreRenames<TContext>(IReadOnlyDictionary<string, string> renames) where TContext : DbContext
{
    public IReadOnlyDictionary<string, string> Renames { get; } = renames;

    /// <summary>
    /// Legacy name → resolved name for each table of <paramref name="tables"/>, or none with
    /// <see cref="Qodalis.Cli.Abstractions.Helpers.QcliTableNameOptions.LegacyTableNames"/>.
    /// </summary>
    public static QcliEfStoreRenames<TContext> For(QcliEfTables tables, Qodalis.Cli.Abstractions.Helpers.QcliTableNameOptions options,
        IReadOnlyDictionary<string, string> legacyByBase) =>
        new(options.LegacyTableNames
            ? new Dictionary<string, string>()
            : legacyByBase.Where(l => l.Value != tables.Table(l.Key)).ToDictionary(l => l.Value, l => tables.Table(l.Key)));
}
