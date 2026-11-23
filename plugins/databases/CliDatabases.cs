using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>The last run of a database, in the overview.</summary>
public sealed class CliMigrationLastRun
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required string Status { get; init; }
    public required string StartedAt { get; init; }
    public required string Trigger { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>A database in the list (<c>GET /api/v1/qcli/databases</c>).</summary>
public sealed class CliDatabaseSummary
{
    public required string Name { get; init; }
    public required string Dialect { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public int Applied { get; init; }
    public int Pending { get; init; }

    /// <summary>Migrations <c>changed</c>, <c>missing</c> or <c>failed</c>.</summary>
    public int Problems { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliMigrationLastRun? LastRun { get; init; }

    /// <summary>Size in bytes (best effort: left out when it cannot be read).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    /// <summary>Set when the database could not be read (down, wrong credentials).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// The registered databases: their stats, and what happens around a migration run: the
/// <c>databases:migration-run</c> event (audience <c>databases:read</c>) and the audit entry (<c>migrations.up</c> /
/// <c>migrations.down</c>).
/// </summary>
public sealed class CliDatabases
{
    /// <summary>The event published after every migration run.</summary>
    public const string RunEvent = "databases:migration-run";

    private readonly Dictionary<string, CliMigrationRunner> _runners;
    private readonly IServiceProvider _services;
    private readonly ILogger<CliDatabases>? _logger;

    public CliDatabases(IEnumerable<CliMigrationRunner> runners, IServiceProvider services)
    {
        _runners = runners.ToDictionary(r => r.Name);
        _services = services;
        _logger = services.GetService<ILogger<CliDatabases>>();
    }

    public IReadOnlyList<CliMigrationRunner> Databases => _runners.Values.ToList();

    public CliMigrationRunner? Database(string name) => _runners.GetValueOrDefault(name);

    /// <summary>The database, or a 404 <see cref="CliMigrationException"/>.</summary>
    public CliMigrationRunner Require(string name) =>
        Database(name) ?? throw new CliMigrationException($"unknown database: {name}", 404);

    /// <summary>Every database with its counts, last run and size; a database that cannot be read carries its error.</summary>
    public async Task<List<CliDatabaseSummary>> OverviewAsync(CancellationToken ct = default) =>
        (await Task.WhenAll(_runners.Values.Select(db => SummarizeAsync(db, ct)))).ToList();

    /// <summary>One database's counts, last run and size, or its error.</summary>
    public Task<CliDatabaseSummary> SummaryAsync(string name, CancellationToken ct = default) => SummarizeAsync(Require(name), ct);

    /// <summary>A database's stats: size, connections, load counters, largest tables.</summary>
    public Task<CliDatabaseStats> StatsAsync(string name, CancellationToken ct = default) => Require(name).StatsAsync(ct);

    private static async Task<CliDatabaseSummary> SummarizeAsync(CliMigrationRunner db, CancellationToken ct)
    {
        try
        {
            var states = await db.StatusAsync(ct);
            var last = (await db.RunsAsync(1, 0, ct)).Items.FirstOrDefault();
            long? size;
            try
            {
                size = await db.SizeAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                size = null;
            }
            return new CliDatabaseSummary
            {
                Name = db.Name,
                Dialect = db.Dialect,
                Sources = db.SourceNames,
                Applied = states.Count(s => s.Status == CliMigrationStatus.Applied),
                Pending = states.Count(s => s.Status is CliMigrationStatus.Pending or CliMigrationStatus.Failed),
                Problems = states.Count(s => s.Status is CliMigrationStatus.Changed or CliMigrationStatus.Missing or CliMigrationStatus.Failed),
                LastRun = last == null
                    ? null
                    : new CliMigrationLastRun
                    {
                        Id = last.Id,
                        Direction = last.Direction,
                        Status = last.Status,
                        StartedAt = last.StartedAt,
                        Trigger = last.Trigger,
                        FinishedAt = last.FinishedAt,
                        Actor = last.Actor,
                        Error = last.Error,
                    },
                SizeBytes = size,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string dialect;
            try
            {
                dialect = db.Dialect;
            }
            catch (Exception)
            {
                dialect = "unknown";
            }
            return new CliDatabaseSummary
            {
                Name = db.Name,
                Dialect = dialect,
                Sources = db.SourceNames,
                Error = ex.Message,
            };
        }
    }

    /// <summary>Runs up or down on a database, then publishes the event and writes the audit entry.</summary>
    public async Task<CliMigrationRun> RunAsync(string name, string direction, CliMigrationRunOptions options, CancellationToken ct = default)
    {
        var db = Require(name);
        var run = direction == "down" ? await db.DownAsync(options, ct) : await db.UpAsync(options, ct);
        var summary = run.Applied.Count == 0 ? "nothing to do" : string.Join(", ", run.Applied.Select(a => $"{a.Source}:{a.Id}"));

        var events = _services.GetService<ICliEventBus>();
        if (events != null)
        {
            var data = new Dictionary<string, object?>
            {
                ["database"] = name,
                ["run"] = run.Id,
                ["direction"] = direction,
                ["status"] = run.Status,
                ["count"] = run.Applied.Count,
            };
            if (run.Error != null) data["error"] = run.Error;
            await events.PublishAsync(RunEvent, data, CliEventAudience.WithPermission(DatabasesPermissions.Read));
        }

        var audit = _services.GetService<IAuditStore>();
        if (audit != null)
        {
            try
            {
                await audit.LogAsync(new AuditEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Event = $"migrations.{direction}",
                    Username = run.Actor ?? run.Trigger,
                    Actor = run.Actor,
                    Target = $"database:{name}",
                    Details = run.Status == "failed" ? $"{summary}; failed: {run.Error}" : summary,
                    Properties = new()
                    {
                        ["run"] = run.Id,
                        ["status"] = run.Status,
                        ["trigger"] = run.Trigger,
                        ["applied"] = run.Applied.Count,
                    },
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not write the audit entry of migration run {Run}", run.Id);
            }
        }
        return run;
    }

    /// <summary>
    /// Applies the pending migrations of the databases marked <c>ApplyOnStartup</c>, one after another, waiting for
    /// another process's run.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run failed (the first failure).</exception>
    public async Task<List<CliMigrationRun>> ApplyOnStartupAsync(CancellationToken ct = default)
    {
        var runs = new List<CliMigrationRun>();
        foreach (var db in _runners.Values.Where(r => r.ApplyOnStartup))
        {
            var run = await RunAsync(db.Name, "up", new CliMigrationRunOptions { Trigger = "startup", Wait = true }, ct);
            runs.Add(run);
            if (run.Status == "failed") throw new InvalidOperationException($"migrations of {db.Name} failed: {run.Error}");
        }
        return runs;
    }
}

/// <summary>Permissions of the databases plugin.</summary>
public static class DatabasesPermissions
{
    /// <summary>See databases, their stats, migrations, runs and the SQL a run would execute.</summary>
    public const string Read = "databases:read";

    /// <summary>Apply and roll back migrations.</summary>
    public const string Migrate = "databases:migrate";
}
