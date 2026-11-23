using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// <c>databases</c> (alias <c>db</c>): <c>databases list</c>, <c>databases stats &lt;db&gt;</c>,
/// <c>databases migrations &lt;db&gt;</c>, <c>databases up &lt;db&gt; [--to] [--dry-run] [--force]</c>,
/// <c>databases down &lt;db&gt; --to &lt;id&gt; [--dry-run]</c>.
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
    ];

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
