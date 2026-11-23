using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Migrations;

/// <summary>Body of <c>POST /{db}/up</c> and <c>POST /{db}/down</c>.</summary>
public sealed class CliMigrationRunRequest
{
    public string? To { get; set; }
    public bool Force { get; set; }
}

/// <summary>
/// The migrations API: databases, their migrations, run history, dry runs, up and down. Errors answer
/// <c>{ "error": "..." }</c>: 400 bad input, 404 unknown database or migration, 409 busy or blocked. On a protected
/// server reading needs <c>migrations:read</c> and running <c>migrations:run</c>.
/// </summary>
[ApiController]
[Route("api/v1/qcli/migrations")]
[CliMigrationErrors]
public class CliMigrationsController(CliMigrations migrations) : ControllerBase
{
    /// <summary>Every database: applied, pending, problems, last run.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { databases = await migrations.OverviewAsync(ct) });

    /// <summary>A database's migrations and their status.</summary>
    [HttpGet("{db}")]
    public async Task<IActionResult> Get(string db, CancellationToken ct)
    {
        var runner = migrations.Require(db);
        return Ok(new { name = runner.Name, dialect = runner.Dialect, sources = runner.SourceNames, migrations = await runner.StatusAsync(ct) });
    }

    /// <summary>Run history, newest first.</summary>
    [HttpGet("{db}/runs")]
    public async Task<IActionResult> Runs(string db, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var take = Math.Clamp(size ?? 20, 1, 100);
        var skip = (Math.Max(page ?? 1, 1) - 1) * take;
        var result = await migrations.Require(db).RunsAsync(take, skip, ct);
        return Ok(new { items = result.Items, total = result.Total });
    }

    /// <summary>The SQL a run would execute (dry run).</summary>
    [HttpGet("{db}/sql")]
    public async Task<IActionResult> Sql(string db, [FromQuery] string? direction, [FromQuery] string? to, CancellationToken ct) =>
        Ok(new { sql = await migrations.Require(db).ScriptAsync(direction == "down" ? "down" : "up", Id(to), ct) });

    /// <summary>Applies the pending migrations (up to <c>to</c>).</summary>
    [HttpPost("{db}/up")]
    public Task<IActionResult> Up(string db, [FromBody] CliMigrationRunRequest? body, CancellationToken ct) => RunAsync(db, "up", body, ct);

    /// <summary>Rolls back to <c>to</c> (kept; <c>base</c> for everything).</summary>
    [HttpPost("{db}/down")]
    public Task<IActionResult> Down(string db, [FromBody] CliMigrationRunRequest? body, CancellationToken ct) => RunAsync(db, "down", body, ct);

    private async Task<IActionResult> RunAsync(string db, string direction, CliMigrationRunRequest? body, CancellationToken ct)
    {
        var options = new CliMigrationRunOptions
        {
            To = Id(body?.To),
            Force = body?.Force == true,
            Actor = HttpContext.GetCliPrincipal()?.UserName,
            Trigger = "api",
        };
        return Ok(await migrations.RunAsync(db, direction, options, ct));
    }

    private static string? Id(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 200) throw new CliMigrationException("to must be a migration id", 400);
        return value;
    }
}

/// <summary>Answers a <see cref="CliMigrationException"/> with its status and <c>{ "error": message }</c>.</summary>
internal sealed class CliMigrationErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not CliMigrationException ex) return;
        context.Result = new JsonResult(new { error = ex.Message }) { StatusCode = ex.StatusCode };
        context.ExceptionHandled = true;
    }
}
