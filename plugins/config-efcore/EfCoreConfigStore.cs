using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Databases.EfCore;

namespace Qodalis.Cli.Plugin.Config.EfCore;

/// <summary>
/// Keeps the config providers' writable settings in a database through EF Core (table <c>QcliConfig</c>, source
/// <c>database</c>): one row per provider section holding its values as JSON and its version. A change is written with
/// the version as a concurrency token, so two servers sharing the database never lose a change and a stale version is
/// refused. The table is created on first use, by the databases plugin (<c>AddConfigStoreSchema</c>), or by the host's
/// migrations with <c>modelBuilder.ApplyConfigStorageModel()</c>.
/// </summary>
public class EfCoreConfigStore : ICliConfigStore
{
    private const int MaxAttempts = 5;

    private readonly IDbContextFactory<ConfigStorageDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<string, string> _renames;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public EfCoreConfigStore(IDbContextFactory<ConfigStorageDbContext> dbFactory,
        QcliEfStoreRenames<ConfigStorageDbContext>? renames = null, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _renames = renames?.Renames ?? new Dictionary<string, string>();
        _time = time ?? TimeProvider.System;
    }

    public string Source => CliConfigSources.Database;

    private async Task<ConfigStorageDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (_initialized) return db;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await QcliEfStoreSchema.EnsureAsync(db, _renames, cancellationToken);
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
        return db;
    }

    private static CliConfigStoredSection Parse(ConfigSectionEntity? row)
    {
        if (row == null) return CliConfigStoredSection.Empty;
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(row.Data);
        }
        catch (JsonException)
        {
            json = null;
        }
        return new CliConfigStoredSection(CliConfigStoredSection.ValuesFromJson(json), row.Version);
    }

    public async Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var row = await db.Sections.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Provider == provider && s.Section == section, cancellationToken);
        return Parse(row);
    }

    public async Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await CreateDbContextAsync(cancellationToken);
            var row = await db.Sections.FirstOrDefaultAsync(s => s.Provider == provider && s.Section == section, cancellationToken);
            var current = Parse(row);
            // Throws CliConfigConflictException on a stale expected version
            var next = CliConfigStoreHelpers.Next(current, changes, expectedVersion);
            var data = new CliConfigStoredSection(next.Values, next.Version).ValuesToJson().ToJsonString();
            var now = _time.GetUtcNow().UtcDateTime;
            if (row == null)
            {
                db.Sections.Add(new ConfigSectionEntity { Provider = provider, Section = section, Version = next.Version, Data = data, UpdatedAt = now });
            }
            else
            {
                row.Version = next.Version;
                row.Data = data;
                row.UpdatedAt = now;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return next.Clone();
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Another server wrote the section meanwhile (version changed, or its first row was inserted): read again.
                // With an expected version the next attempt answers the conflict.
            }
            catch (DbUpdateConcurrencyException)
            {
                var latest = await ReadAsync(provider, section, cancellationToken);
                throw new CliConfigConflictException(latest.Version.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
