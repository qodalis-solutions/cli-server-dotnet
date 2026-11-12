using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// <see cref="IApiTokenStore"/> backed by EF Core (table <c>api_tokens</c>): API tokens and the tokens of linked
/// terminals survive restarts and are shared by every instance of the server. Expired tokens are deleted as they are
/// listed (they are refused anyway).
/// </summary>
public class EfCoreApiTokenStore : IApiTokenStore
{
    private readonly IDbContextFactory<ApiTokenStorageDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreApiTokenStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    /// <param name="time">The clock for pruning expired tokens (the system clock when not given).</param>
    public EfCoreApiTokenStore(IDbContextFactory<ApiTokenStorageDbContext> dbFactory, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ApiToken> CreateTokenAsync(ApiToken token)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        db.Tokens.Add(new ApiTokenEntity
        {
            Id = token.Id,
            UserId = token.UserId,
            Name = token.Name,
            TokenHash = token.TokenHash,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt,
            LastUsedAt = token.LastUsedAt,
            Permissions = token.Permissions == null ? null : JsonSerializer.Serialize(token.Permissions),
        });
        await db.SaveChangesAsync();
        return token.Clone();
    }

    /// <inheritdoc />
    public async Task<ApiToken?> GetTokenByHashAsync(string tokenHash)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        var entity = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        return entity == null ? null : ToToken(entity);
    }

    /// <inheritdoc />
    public async Task<ApiToken?> GetTokenAsync(string id)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        var entity = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        return entity == null ? null : ToToken(entity);
    }

    /// <inheritdoc />
    public async Task<List<ApiToken>> GetTokensAsync(string? userId = null)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        // Expired tokens are refused anyway: forget them here, so the table does not grow
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await db.Tokens.Where(t => t.ExpiresAt != null && t.ExpiresAt <= now).ExecuteDeleteAsync();

        var query = db.Tokens.AsNoTracking();
        if (userId != null) query = query.Where(t => t.UserId == userId);
        return (await query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync()).Select(ToToken).ToList();
    }

    /// <inheritdoc />
    public async Task DeleteTokenAsync(string id)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        await db.Tokens.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteUserTokensAsync(string userId)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        await db.Tokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task UpdateLastUsedAsync(string id, long lastUsedAt)
    {
        await using var db = await CreateDbContextAsync(CancellationToken.None);
        await db.Tokens.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, lastUsedAt));
    }

    private async Task<ApiTokenStorageDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!_initialized)
        {
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
    /// Creates the database with the table, or only the table in a database that already exists (the host's own,
    /// whose other tables make <c>EnsureCreated</c> do nothing). A table the host created in its migrations is kept.
    /// </summary>
    private static async Task EnsureTableAsync(ApiTokenStorageDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Database.EnsureCreatedAsync(cancellationToken))
            return;

        if (!await ExistsAsync(db, cancellationToken))
        {
            try
            {
                await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
            }
            catch (DbException)
            {
                // Another instance of the server may have created it meanwhile
                if (!await ExistsAsync(db, cancellationToken))
                    throw;
            }
        }
    }

    private static async Task<bool> ExistsAsync(ApiTokenStorageDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            _ = await db.Tokens.AnyAsync(cancellationToken);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static ApiToken ToToken(ApiTokenEntity entity) => new()
    {
        Id = entity.Id,
        UserId = entity.UserId,
        Name = entity.Name,
        TokenHash = entity.TokenHash,
        CreatedAt = entity.CreatedAt,
        ExpiresAt = entity.ExpiresAt,
        LastUsedAt = entity.LastUsedAt,
        Permissions = entity.Permissions == null ? null : JsonSerializer.Deserialize<List<string>>(entity.Permissions),
    };
}
