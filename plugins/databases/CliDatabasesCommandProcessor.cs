using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// <c>databases</c> (alias <c>db</c>): <c>databases list</c>, <c>databases stats &lt;db&gt;</c>,
/// <c>databases migrations &lt;db&gt;</c>, <c>databases up &lt;db&gt; [--to] [--dry-run] [--force]</c>,
/// <c>databases down &lt;db&gt; --to &lt;id&gt; [--dry-run]</c>, <c>databases runs [&lt;db&gt;] [-n N] [--json]</c>.
/// </summary>
public class CliDatabasesCommandProcessor : CliCommandProcessor
{
    private readonly IServiceProvider _services;

    public CliDatabasesCommandProcessor(IServiceProvider services)
    {
        _services = services;
        Processors =
        [
            new Sub(this, "list", "The databases: size, applied and pending migrations, last run", DatabasesPermissions.Read, (c, ct) => ListAsync(ct)),
            new Sub(this, "stats", "A database's size, connections, load and largest tables", DatabasesPermissions.Read, StatsAsync),
            new Sub(this, "migrations", "A database's migrations and their status", DatabasesPermissions.Read, StatusAsync),
            new Sub(this, "up", "Apply pending migrations", DatabasesPermissions.Migrate, (c, ct) => RunAsync(c, "up", ct), Flags(false)),
            new Sub(this, "down", "Roll back migrations", DatabasesPermissions.Migrate, (c, ct) => RunAsync(c, "down", ct), Flags(true)),
            new Sub(this, "runs", "Shows the migration run history of a database: time, direction, status, count, who ran it, error",
                DatabasesPermissions.Read, RunsAsync, RunsFlags())
            {
                Examples =
                [
                    new("databases runs main", "The last 20 runs of the database main"),
                    new("databases runs main -n 5 --json", "The last 5 runs as JSON"),
                ],
            },
        ];
    }

    public override string Command { get; set; } = "databases";
    public override IEnumerable<string>? Aliases { get; set; } = ["db"];
    public override string Description { get; set; } = "Databases: size, connections and load; migrations status, apply, roll back";
    public override string? Module { get; set; } = "databases";
    public override string? Icon { get; set; } = "🗄️";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [DatabasesPermissions.Read];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("databases", "The databases with their size and applied and pending migrations"),
        new("databases stats main", "Size, connections, load and the largest tables of main"),
        new("databases migrations main", "The migrations of main and their status"),
        new("databases up main --dry-run", "The SQL the pending migrations would run"),
        new("databases down main --to 0003", "Roll back what was applied after 0003"),
        new("databases runs main", "The migration run history of main"),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private CliDatabases Databases => _services.GetRequiredService<CliDatabases>();

    private string? Actor => _services.GetService<IHttpContextAccessor>()?.HttpContext?.GetCliPrincipal()?.UserName;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        ListAsync(cancellationToken);

    private async Task<string> ListAsync(CancellationToken ct)
    {
        var databases = await Databases.OverviewAsync(ct);
        if (databases.Count == 0) return "No databases";
        return string.Join('\n', databases.Select(d => d.Error != null
            ? $"{d.Name} ({d.Dialect}): error: {d.Error}"
            : $"{d.Name} ({d.Dialect}{(d.SizeBytes is { } size ? $", {CliDatabaseFormat.Bytes(size)}" : "")}): {d.Applied} applied, {d.Pending} pending{(d.Problems > 0 ? $", {d.Problems} problem(s)" : "")}"));
    }

    private async Task<string> StatsAsync(CliProcessCommand command, CancellationToken ct)
    {
        var name = DatabaseOf(command);
        if (name == null) return "Name the database: databases stats <db>";
        try
        {
            return CliDatabaseFormat.Stats(await Databases.StatsAsync(name, ct));
        }
        catch (CliMigrationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The database named on the command, or the only one.</summary>
    private string? DatabaseOf(CliProcessCommand command)
    {
        var name = CliValueHelpers.GetPositionalText(command)?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;
        var all = Databases.Databases;
        return all.Count == 1 ? all[0].Name : null;
    }

    private static string? Arg(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is { Length: > 0 } text && text != "True" ? text : null;

    private static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    private async Task<string> StatusAsync(CliProcessCommand command, CancellationToken ct)
    {
        var name = DatabaseOf(command);
        if (name == null) return "Name the database: databases migrations <db>";
        try
        {
            var states = await Databases.Require(name).StatusAsync(ct);
            if (states.Count == 0) return "No migrations";
            return string.Join('\n', states.Select(s =>
                $"{s.Status,-8} {s.Source}:{s.Id} {s.Name}{(s.AppliedAt != null ? $"  ({s.AppliedAt}{(s.AppliedBy != null ? $", {s.AppliedBy}" : "")})" : "")}"));
        }
        catch (CliMigrationException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> RunAsync(CliProcessCommand command, string direction, CancellationToken ct)
    {
        var name = DatabaseOf(command);
        if (name == null) return $"Name the database: databases {direction} <db>";
        var to = Arg(command, "to");
        if (direction == "down" && to == null) return "--to is required to roll back (an id, or base for everything)";
        try
        {
            if (Flag(command, "dry-run"))
            {
                var sql = await Databases.Require(name).ScriptAsync(direction, to, ct);
                return string.IsNullOrEmpty(sql) ? (direction == "up" ? "Nothing pending" : "Nothing to roll back") : sql;
            }
            var run = await Databases.RunAsync(name, direction, new CliMigrationRunOptions
            {
                To = to,
                Force = Flag(command, "force"),
                Actor = Actor,
                Trigger = "terminal",
            }, ct);
            var text = new StringBuilder($"{(direction == "up" ? "Applied" : "Rolled back")} {run.Applied.Count} migration(s) ({run.Status})");
            foreach (var a in run.Applied) text.Append($"\n  {a.Source}:{a.Id} {a.Name} ({a.DurationMs} ms)");
            if (run.Error != null) text.Append($"\nError: {run.Error}");
            return text.ToString();
        }
        catch (CliMigrationException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> RunsAsync(CliProcessCommand command, CancellationToken ct)
    {
        try
        {
            var limit = LimitOf(command, 20, 100);
            if (limit.Error != null) return limit.Error;
            var name = DatabaseOf(command);
            if (name == null) return $"Error: name the database: {string.Join(", ", Databases.Databases.Select(d => d.Name))}";
            var db = Databases.Database(name);
            if (db == null) return $"Error: unknown database: {name}";
            var page = await db.RunsAsync(limit.Value, 0, ct);
            if (Flag(command, "json")) return JsonSerializer.Serialize(new { database = db.Name, items = page.Items, total = page.Total }, Json);
            if (page.Items.Count == 0) return $"{db.Name}: no migration runs yet";
            return string.Join('\n', new[]
            {
                $"{db.Name} ({db.Dialect}): {page.Items.Count} of {page.Total} run(s)",
                $"{"Started",-19} Dir  Status    Count Trigger",
            }.Concat(page.Items.Select(FormatRunLine)));
        }
        catch (CliMigrationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// <c>--limit</c> / <c>-n</c>: a whole number from 1 to <paramref name="max"/>; <paramref name="fallback"/> when not
    /// given; an error text when invalid.
    /// </summary>
    internal static (int Value, string? Error) LimitOf(CliProcessCommand command, int fallback, int max)
    {
        if (!command.Args.TryGetValue("limit", out var raw) && !command.Args.TryGetValue("n", out raw)) return (fallback, null);
        var text = raw?.ToString();
        if (raw == null || text is "True" or "true") return (fallback, null);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max
            ? (n, null)
            : (0, $"Error: --limit must be a whole number from 1 to {max}");
    }

    /// <summary><c>2026-11-25T10:04:05.123Z</c> -> <c>2026-11-25 10:04:05</c>.</summary>
    private static string ShortTime(string? iso) =>
        string.IsNullOrEmpty(iso) ? string.Empty : Regex.Replace(iso.Replace('T', ' '), @"\.\d+Z$|Z$", string.Empty);

    /// <summary>One run as a line of <c>databases runs</c>: time, direction, status, count, trigger/actor, target, error.</summary>
    public static string FormatRunLine(CliMigrationRun run)
    {
        var who = run.Actor != null ? $"{run.Trigger} ({run.Actor})" : run.Trigger;
        var columns = string.Join(' ', ShortTime(run.StartedAt).PadRight(19), run.Direction.PadRight(4), run.Status.PadRight(9),
            run.Applied.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5), who);
        return string.Join("  ", new[] { columns, run.Target != null ? $"to {run.Target}" : "", run.Error ?? "" }.Where(s => s.Length > 0));
    }

    private static List<ICliCommandParameterDescriptor> RunsFlags() =>
    [
        new CliCommandParameterDescriptor
        {
            Name = "limit",
            Aliases = ["n"],
            Description = "How many runs to show (1-100, default 20)",
            Type = CommandParameterType.Number,
            DefaultValue = 20,
        },
        new CliCommandParameterDescriptor { Name = "json", Description = "Print the runs as JSON", Type = CommandParameterType.Boolean },
    ];

    private static List<ICliCommandParameterDescriptor> Flags(bool down) =>
    [
        new CliCommandParameterDescriptor
        {
            Name = "to",
            Description = down ? "Roll back to this migration, keeping it (id, source:id, or base)" : "Stop after this migration (id or source:id)",
            Type = CommandParameterType.String,
            Required = down,
        },
        new CliCommandParameterDescriptor { Name = "dry-run", Description = "Show the SQL without running it", Type = CommandParameterType.Boolean },
        new CliCommandParameterDescriptor { Name = "force", Description = "Run although a migration is changed or missing", Type = CommandParameterType.Boolean },
    ];

    private sealed class Sub : CliCommandProcessor
    {
        private readonly Func<CliProcessCommand, CancellationToken, Task<string>> _handle;

        public Sub(CliDatabasesCommandProcessor parent, string command, string description, string permission,
            Func<CliProcessCommand, CancellationToken, Task<string>> handle, List<ICliCommandParameterDescriptor>? parameters = null)
        {
            _handle = handle;
            Command = command;
            Description = description;
            RequiredPermissions = [permission];
            Parameters = parameters;
            Module = parent.Module;
        }

        public override string Command { get; set; }
        public override string Description { get; set; }

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
            _handle(command, cancellationToken);
    }
}
