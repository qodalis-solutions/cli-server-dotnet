using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>Options of <see cref="EfCoreApiTokenStore"/> (<c>AddEfCoreApiTokenStore(..., o =&gt; ...)</c>).</summary>
public sealed class EfCoreApiTokenStoreOptions : QcliTableNameOptions
{
    /// <summary>
    /// Brings an existing table up to date on first use: renames the <c>api_tokens</c> of 1.0.0-beta.29 and older to
    /// <c>QcliApiTokens</c> (unless <see cref="QcliTableNameOptions.LegacyTableNames"/>) and adds columns newer
    /// versions need (<c>Audience</c>, 1.0.0-beta.29). On by default; turn it off when the host's own migrations own
    /// the table: the store then refuses to start (<see cref="InvalidOperationException"/>) until the migration that
    /// adds the column has run.
    /// </summary>
    public bool UpgradeSchema { get; set; } = true;
}

/// <summary>
/// <see cref="IApiTokenStore"/> backed by EF Core (table <c>QcliApiTokens</c>): API tokens and the tokens of linked
/// terminals survive restarts and are shared by every instance of the server. Expired tokens are deleted as they are
/// listed (they are refused anyway).
/// </summary>
public class EfCoreApiTokenStore : IApiTokenStore
{
    private readonly IDbContextFactory<ApiTokenStorageDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly IReadOnlyDictionary<string, string> _renames;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreApiTokenStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    /// <param name="time">The clock for pruning expired tokens (the system clock when not given).</param>
    /// <param name="options">Schema options (defaults when not given).</param>
    /// <param name="renames">The previous release's table to rename on first use (registered by <c>AddEfCoreApiTokenStore</c>).</param>
    public EfCoreApiTokenStore(IDbContextFactory<ApiTokenStorageDbContext> dbFactory, TimeProvider? time = null,
        EfCoreApiTokenStoreOptions? options = null, QcliEfStoreRenames<ApiTokenStorageDbContext>? renames = null)
    {
        _dbFactory = dbFactory;
        _time = time ?? TimeProvider.System;
        Options = options ?? new EfCoreApiTokenStoreOptions();
        _renames = renames?.Renames ?? new Dictionary<string, string>();
    }

    /// <summary>The store's options.</summary>
    public EfCoreApiTokenStoreOptions Options { get; }

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
            Audience = token.Audience,
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
                    await EnsureTableAsync(db, Options.UpgradeSchema, Options.UpgradeSchema ? _renames : new Dictionary<string, string>(), cancellationToken);
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
    /// Renames the table of 1.0.0-beta.29 and older (<paramref name="renames"/>), then creates the database with the
    /// table, or only the table in a database that already exists (the host's own, whose other tables make
    /// <c>EnsureCreated</c> do nothing). A table the host created in its migrations is kept. A table created before
    /// tokens had an audience (1.0.0-beta.28 and older) gets the <c>Audience</c> column, unless
    /// <paramref name="upgradeSchema"/> is off: then a missing column stops the store with a clear error.
    /// </summary>
    private static async Task EnsureTableAsync(ApiTokenStorageDbContext db, bool upgradeSchema,
        IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken)
    {
        await QcliEfStoreSchema.EnsureAsync(db, renames, cancellationToken);

        if (upgradeSchema)
            await EnsureAudienceColumnAsync(db, cancellationToken);
        else if (!await HasAudienceColumnAsync(db, cancellationToken))
            throw new InvalidOperationException(
                $"The {db.Model.FindEntityType(typeof(ApiTokenEntity))!.GetTableName()} table has no Audience column (added in 1.0.0-beta.29): apply the host migration that " +
                "adds it, or leave EfCoreApiTokenStoreOptions.UpgradeSchema on so the store adds it.");
    }

    /// <summary>
    /// Adds the nullable <c>Audience</c> column to an existing table that lacks it (existing rows keep null, read as
    /// <c>cli</c>). Identifiers are quoted by the provider and the column type is the model's for the provider
    /// (<c>nvarchar(16)</c> on SQL Server, <c>character varying(16)</c> on PostgreSQL, ...), as migrations create it.
    /// </summary>
    private static async Task EnsureAudienceColumnAsync(ApiTokenStorageDbContext db, CancellationToken cancellationToken)
    {
        if (await HasAudienceColumnAsync(db, cancellationToken))
            return;

        var entityType = db.Model.FindEntityType(typeof(ApiTokenEntity))!;
        var tableName = entityType.GetTableName()!;
        var store = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var property = entityType.FindProperty(nameof(ApiTokenEntity.Audience))!;
        var columnName = property.GetColumnName(store)!;
        var columnType = property.GetColumnType(store);
        var sql = db.GetService<ISqlGenerationHelper>();
        var alter = $"ALTER TABLE {sql.DelimitIdentifier(tableName, entityType.GetSchema())} " +
                    $"ADD {sql.DelimitIdentifier(columnName)} {columnType} NULL";
        try
        {
            await db.Database.ExecuteSqlRawAsync(alter, cancellationToken);
        }
        catch (DbException)
        {
            // Another instance of the server may have added it meanwhile
            if (!await HasAudienceColumnAsync(db, cancellationToken))
                throw;
        }
    }

    private static async Task<bool> HasAudienceColumnAsync(ApiTokenStorageDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            _ = await db.Tokens.AsNoTracking().Select(t => t.Audience).Take(1).ToListAsync(cancellationToken);
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
        Audience = entity.Audience ?? ApiTokenAudience.Cli,
    };
}
