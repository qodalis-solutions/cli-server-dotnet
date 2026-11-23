using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Migrations;

/// <summary>
/// <c>migrations</c>: the databases; <c>migrations status &lt;db&gt;</c>, <c>migrations up &lt;db&gt; [--to] [--dry-run] [--force]</c>,
/// <c>migrations down &lt;db&gt; --to &lt;id&gt; [--dry-run]</c>.
/// </summary>
public class CliMigrationsCommandProcessor : CliCommandProcessor
{
    private readonly IServiceProvider _services;

    public CliMigrationsCommandProcessor(IServiceProvider services)
    {
        _services = services;
        Processors =
        [
            new Sub(this, "status", "A database's migrations and their status", MigrationsPermissions.Read, StatusAsync),
            new Sub(this, "up", "Apply pending migrations", MigrationsPermissions.Run, (c, ct) => RunAsync(c, "up", ct), Flags(false)),
            new Sub(this, "down", "Roll back migrations", MigrationsPermissions.Run, (c, ct) => RunAsync(c, "down", ct), Flags(true)),
        ];
    }

    public override string Command { get; set; } = "migrations";
    public override string Description { get; set; } = "Database migrations: status, apply, roll back";
    public override string? Module { get; set; } = "migrations";
    public override string? Icon { get; set; } = "🗄️";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [MigrationsPermissions.Read];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("migrations", "The databases with their applied and pending migrations"),
        new("migrations status main", "The migrations of main and their status"),
        new("migrations up main --dry-run", "The SQL the pending migrations would run"),
        new("migrations down main --to 0003", "Roll back what was applied after 0003"),
    ];

    private CliMigrations Migrations => _services.GetRequiredService<CliMigrations>();

    private string? Actor => _services.GetService<IHttpContextAccessor>()?.HttpContext?.GetCliPrincipal()?.UserName;

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var databases = await Migrations.OverviewAsync(cancellationToken);
        if (databases.Count == 0) return "No databases";
        return string.Join('\n', databases.Select(d => d.Error != null
            ? $"{d.Name} ({d.Dialect}): error: {d.Error}"
            : $"{d.Name} ({d.Dialect}): {d.Applied} applied, {d.Pending} pending{(d.Problems > 0 ? $", {d.Problems} problem(s)" : "")}"));
    }

    /// <summary>The database named on the command, or the only one.</summary>
    private string? DatabaseOf(CliProcessCommand command)
    {
        var name = CliValueHelpers.GetPositionalText(command)?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;
        var all = Migrations.Databases;
        return all.Count == 1 ? all[0].Name : null;
    }

    private static string? Arg(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is { Length: > 0 } text && text != "True" ? text : null;

    private static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    private async Task<string> StatusAsync(CliProcessCommand command, CancellationToken ct)
    {
        var name = DatabaseOf(command);
        if (name == null) return "Name the database: migrations status <db>";
        try
        {
            var states = await Migrations.Require(name).StatusAsync(ct);
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
        if (name == null) return $"Name the database: migrations {direction} <db>";
        var to = Arg(command, "to");
        if (direction == "down" && to == null) return "--to is required to roll back (an id, or base for everything)";
        try
        {
            if (Flag(command, "dry-run"))
            {
                var sql = await Migrations.Require(name).ScriptAsync(direction, to, ct);
                return string.IsNullOrEmpty(sql) ? (direction == "up" ? "Nothing pending" : "Nothing to roll back") : sql;
            }
            var run = await Migrations.RunAsync(name, direction, new CliMigrationRunOptions
            {
                To = to, Force = Flag(command, "force"), Actor = Actor, Trigger = "terminal",
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

        public Sub(CliMigrationsCommandProcessor parent, string command, string description, string permission,
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
