using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>Options of <see cref="EfCorePublicApiUsageStore"/> (<c>AddEfCorePublicApiUsageStore(..., o =&gt; ...)</c>).</summary>
public sealed class EfCorePublicApiUsageStoreOptions : QcliTableNameOptions
{
    /// <summary>
    /// Creates the <c>QcliApiUsage</c> table on first use when it does not exist (renaming the <c>qcli_api_usage</c> of
    /// 1.0.0-beta.29 unless <see cref="QcliTableNameOptions.LegacyTableNames"/>). On by default; turn it off when the
    /// host's own migrations create the table (<c>modelBuilder.ApplyPublicApiUsageModel()</c>).
    /// </summary>
    public bool EnsureCreated { get; set; } = true;
}

/// <summary>
/// <see cref="IPublicApiUsageStore"/> backed by EF Core (table <c>QcliApiUsage</c>): the counters are shared by every
/// process that serves the API, so the quotas hold across them (the guard's increment-then-check-then-compensate never
/// admits more than a limit; under contention a request may be refused while the count is just below it).
/// </summary>
public class EfCorePublicApiUsageStore : IPublicApiUsageStore
{
    private readonly IDbContextFactory<PublicApiUsageDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<string, string> _renames;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCorePublicApiUsageStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    /// <param name="options">Store options (defaults when not given).</param>
    /// <param name="renames">The previous release's table to rename on first use (registered by <c>AddEfCorePublicApiUsageStore</c>).</param>
    public EfCorePublicApiUsageStore(IDbContextFactory<PublicApiUsageDbContext> dbFactory,
        EfCorePublicApiUsageStoreOptions? options = null, QcliEfStoreRenames<PublicApiUsageDbContext>? renames = null)
    {
        _dbFactory = dbFactory;
        Options = options ?? new EfCorePublicApiUsageStoreOptions();
        _renames = renames?.Renames ?? new Dictionary<string, string>();
    }

    /// <summary>The store's options.</summary>
    public EfCorePublicApiUsageStoreOptions Options { get; }

    /// <inheritdoc />
    public async Task<long> IncrementAsync(string tokenId, string userId, string window, CancellationToken ct)
    {
        await using var db = await CreateDbContextAsync(ct);
        if (await AddOneAsync(db, tokenId, window, ct) == 0)
        {
            db.Usage.Add(new ApiUsageEntity { TokenId = tokenId, UserId = userId, Window = window, Count = 1 });
            try
            {
                await db.SaveChangesAsync(ct);
                return 1;
            }
            catch (DbUpdateException)
            {
                // Another process inserted the row first: count on it.
                db.ChangeTracker.Clear();
                if (await AddOneAsync(db, tokenId, window, ct) == 0) throw;
            }
        }
        return await db.Usage.AsNoTracking()
            .Where(u => u.TokenId == tokenId && u.Window == window)
            .Select(u => u.Count)
            .FirstOrDefaultAsync(ct);
    }

    private static Task<int> AddOneAsync(PublicApiUsageDbContext db, string tokenId, string window, CancellationToken ct) =>
        db.Usage.Where(u => u.TokenId == tokenId && u.Window == window)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + 1), ct);

    /// <inheritdoc />
    public async Task DecrementAsync(string tokenId, string window, CancellationToken ct)
    {
        await using var db = await CreateDbContextAsync(ct);
        await db.Usage.Where(u => u.TokenId == tokenId && u.Window == window && u.Count > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count - 1), ct);
    }

    /// <inheritdoc />
    public async Task<long> GetAccountCountAsync(string userId, string window, CancellationToken ct)
    {
        await using var db = await CreateDbContextAsync(ct);
        return await db.Usage.AsNoTracking()
            .Where(u => u.UserId == userId && u.Window == window)
            .SumAsync(u => u.Count, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>> GetTokenDaysAsync(
        IReadOnlyCollection<string> tokenIds, string fromDay, string toDay, CancellationToken ct)
    {
        if (tokenIds.Count == 0) return new Dictionary<string, IReadOnlyDictionary<string, long>>();
        var ids = tokenIds.Distinct().ToList();
        await using var db = await CreateDbContextAsync(ct);
        var rows = await db.Usage.AsNoTracking()
            .Where(u => ids.Contains(u.TokenId) && u.Window.Length == PublicApiWindows.DayLength &&
                        string.Compare(u.Window, fromDay) >= 0 && string.Compare(u.Window, toDay) <= 0)
            .Select(u => new { u.TokenId, u.Window, u.Count })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TokenId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, long>)g.ToDictionary(r => r.Window, r => r.Count));
    }

    /// <inheritdoc />
    public async Task PruneAsync(string beforeDay, string beforeMinute, CancellationToken ct)
    {
        await using var db = await CreateDbContextAsync(ct);
        await db.Usage
            .Where(u => (u.Window.Length == PublicApiWindows.DayLength && string.Compare(u.Window, beforeDay) < 0) ||
                        (u.Window.Length == PublicApiWindows.MinuteLength && string.Compare(u.Window, beforeMinute) < 0))
            .ExecuteDeleteAsync(ct);
    }

    private async Task<PublicApiUsageDbContext> CreateDbContextAsync(CancellationToken ct)
    {
        var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!_initialized)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_initialized)
                {
                    if (Options.EnsureCreated) await EnsureTableAsync(db, ct);
                    _initialized = true;
                }
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
            finally
            {
                _initLock.Release();
            }
        }
        return db;
    }

    /// <summary>
    /// Renames the table of 1.0.0-beta.29 (<c>qcli_api_usage</c>), then creates the database with the table, or only
    /// the table in a database that already exists (the host's own, whose other tables make <c>EnsureCreated</c> do
    /// nothing). An existing table is kept.
    /// </summary>
    private Task EnsureTableAsync(PublicApiUsageDbContext db, CancellationToken ct) => QcliEfStoreSchema.EnsureAsync(db, _renames, ct);
}
