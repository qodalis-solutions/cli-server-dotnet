using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// Entity Framework Core implementation of <see cref="IAuditStore"/>: keeps the audit log in a relational database,
/// so it survives restarts and is shared by every instance of the server. Entries are kept until cleared or pruned
/// (<see cref="PruneAsync"/>).
/// </summary>
public class EfCoreAuditStore : IAuditStore
{
    private readonly IDbContextFactory<AuditStorageDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<string, string> _renames;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreAuditStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    /// <param name="renames">The previous release's table to rename on first use (registered by <c>AddEfCoreAuditStore</c>).</param>
    public EfCoreAuditStore(IDbContextFactory<AuditStorageDbContext> dbFactory, QcliEfStoreRenames<AuditStorageDbContext>? renames = null)
    {
        _dbFactory = dbFactory;
        _renames = renames?.Renames ?? new Dictionary<string, string>();
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();

        await using var db = await CreateDbContextAsync(CancellationToken.None);
        db.Entries.Add(new AuditEntryEntity
        {
            EntryId = entry.Id,
            Timestamp = entry.Timestamp,
            Event = entry.Event,
            Username = entry.Username,
            UserId = entry.UserId,
            Details = entry.Details,
            Actor = entry.Actor,
            ActorId = entry.ActorId,
            Target = entry.Target,
            Properties = entry.Properties is { Count: > 0 } ? JsonSerializer.Serialize(entry.Properties) : null,
            Metadata = entry.Metadata == null ? null : JsonSerializer.Serialize(entry.Metadata),
        });
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        IQueryable<AuditEntryEntity> query = db.Entries;

        if (!string.IsNullOrEmpty(options?.Event))
        {
            var value = options.Event.ToLowerInvariant();
            query = query.Where(e => e.Event.ToLower() == value);
        }

        if (!string.IsNullOrEmpty(options?.Username))
        {
            var value = options.Username.ToLowerInvariant();
            query = query.Where(e => e.Username.ToLower() == value);
        }

        if (!string.IsNullOrEmpty(options?.Actor))
        {
            var value = options.Actor.ToLowerInvariant();
            query = query.Where(e => e.Actor != null && e.Actor.ToLower() == value);
        }

        if (options?.From is { } from)
        {
            var fromMs = from.ToUnixTimeMilliseconds();
            query = query.Where(e => e.Timestamp >= fromMs);
        }

        if (options?.To is { } to)
        {
            var toMs = to.ToUnixTimeMilliseconds();
            query = query.Where(e => e.Timestamp < toMs);
        }

        // Newest first; the key breaks timestamp ties in insertion order.
        query = query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id);

        if (options?.Offset is int offset and > 0)
            query = query.Skip(offset);

        if (options?.Limit is int limit)
            query = query.Take(limit);

        var entities = await query.AsNoTracking().ToListAsync();
        return entities.Select(ToEntry).ToList();
    }

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        await db.Entries.ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var cutoff = before.ToUnixTimeMilliseconds();
        await using var db = await CreateDbContextAsync(cancellationToken);
        return await db.Entries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<AuditStorageDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!_initialized)
        {
            // Once per store, even when the first calls come together (jobs at startup, a burst of sign-ins)
            await _initLock.WaitAsync(cancellationToken);
            try
            {
                if (!_initialized)
                {
                    await EnsureTableAsync(db, cancellationToken);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }
        return db;
    }

    /// <summary>
    /// Renames the table of 1.0.0-beta.29 and older (<c>audit_entries</c>) to its new name, keeping the entries, then
    /// creates the database with the table, or only the table in a database that already exists (the host's own,
    /// whose other tables make <c>EnsureCreated</c> do nothing). A table the host created in its migrations is kept.
    /// </summary>
    private Task EnsureTableAsync(AuditStorageDbContext db, CancellationToken cancellationToken) =>
        QcliEfStoreSchema.EnsureAsync(db, _renames, cancellationToken);

    private static AuditEntry ToEntry(AuditEntryEntity entity) => new()
    {
        Id = entity.EntryId,
        Timestamp = entity.Timestamp,
        Event = entity.Event,
        Username = entity.Username,
        UserId = entity.UserId,
        Details = entity.Details,
        Actor = entity.Actor,
        ActorId = entity.ActorId,
        Target = entity.Target,
        Properties = entity.Properties == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.Properties),
        Metadata = entity.Metadata == null ? null : JsonSerializer.Deserialize<AuditMetadata>(entity.Metadata),
    };
}
