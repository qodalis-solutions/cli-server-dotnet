using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Dashboard plugins from the marketplace (<c>/api/v1/qcli/dashboard/marketplace</c>): reading needs
/// <c>admin:access</c>, installing, updating and removing <c>dashboard:install</c>. 404 when the marketplace is off.
/// <list type="bullet">
/// <item><c>GET /</c>: <c>{ marketplaceUrl, install, dashboardApi }</c></item>
/// <item><c>GET /plugins?query=&amp;page=&amp;pageSize=</c>: the marketplace's dashboard plugins, annotated (<c>installed</c>, <c>updateAvailable</c>, <c>allowed</c>, <c>blockedReason</c>, <c>requires</c>)</item>
/// <item><c>GET /plugins/{name}</c>: one plugin with its versions</item>
/// <item><c>GET /installed</c>: the installed plugins (<c>updateAvailable</c> when the marketplace answers)</item>
/// <item><c>POST /installed</c> <c>{ name, version? }</c>: 201 with the installed plugin</item>
/// <item><c>PUT /installed/{name}</c> <c>{ version? }</c>: the updated plugin</item>
/// <item><c>DELETE /installed/{name}</c>: the removed plugin</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/qcli/dashboard/marketplace")]
public class DashboardMarketplaceController(DashboardMarketplace marketplace, ILogger<DashboardMarketplaceController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ContentResult Send(int status, object? body)
    {
        Response.Headers.CacheControl = "no-store";
        var json = body is JsonNode node ? node.ToJsonString(Json) : JsonSerializer.Serialize(body, Json);
        return new ContentResult { StatusCode = status, Content = json, ContentType = "application/json; charset=utf-8" };
    }

    private ContentResult Error(int status, string message) => Send(status, new JsonObject { ["error"] = message });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        if (!marketplace.Enabled) return Error(404, "not found");
        try
        {
            return await action();
        }
        catch (DashboardMarketplaceException ex)
        {
            return Error(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[qcli-admin] dashboard marketplace request failed");
            return Error(500, "internal server error");
        }
    }

    /// <summary>The caller: the server's principal, else the dashboard session (the static admin may do everything).</summary>
    private (DashboardMarketplaceActor Actor, bool CanInstall) Caller()
    {
        if (HttpContext.GetCliPrincipal() is { } principal)
            return (new DashboardMarketplaceActor(principal.UserId, principal.UserName), principal.Can(CliRoutePolicy.DashboardInstall));
        var user = HttpContext.Items["AdminUser"] as ClaimsPrincipal;
        return (new DashboardMarketplaceActor(null, user?.Identity?.Name ?? DashboardMarketplaceActor.Anonymous.UserName), true);
    }

    /// <summary>The installer, or null with 403 <c>forbidden: missing permission dashboard:install</c>.</summary>
    private DashboardMarketplaceActor? Installer(out IActionResult? denied)
    {
        var (actor, can) = Caller();
        denied = can ? null : Error(403, $"forbidden: missing permission {CliRoutePolicy.DashboardInstall}");
        return can ? actor : null;
    }

    /// <summary>A whole number query parameter from 1 to <paramref name="max"/>; null when absent, 400 when invalid.</summary>
    private int? IntParam(string name, int max)
    {
        if (!Request.Query.TryGetValue(name, out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0])) return null;
        if (values.Count > 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > max)
            throw new DashboardMarketplaceException(400, $"invalid {name}");
        return n;
    }

    /// <summary>The package name of a route (<c>@acme/tickets</c> or <c>%40acme%2Ftickets</c>).</summary>
    private static string NameOf(string? raw)
    {
        try
        {
            return Uri.UnescapeDataString(raw ?? string.Empty);
        }
        catch (UriFormatException)
        {
            throw new DashboardMarketplaceException(400, "invalid package name");
        }
    }

    /// <summary>The request's JSON object; an empty body is <c>{}</c>, anything else 400.</summary>
    private async Task<JsonObject> BodyAsync()
    {
        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new DashboardMarketplaceException(400, "invalid body");
        }
        catch (JsonException)
        {
            throw new DashboardMarketplaceException(400, "invalid body");
        }
    }

    /// <summary>A string field; absent or null is null, another type an invalid value ("").</summary>
    private static string? Field(JsonObject body, string key) => body[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => string.Empty,
    };

    [HttpGet]
    public Task<IActionResult> Info() => Run(() => Task.FromResult<IActionResult>(Send(200, marketplace.Info())));

    [HttpGet("plugins")]
    public Task<IActionResult> Search(CancellationToken cancellationToken) => Run(async () =>
    {
        var query = Request.Query.TryGetValue("query", out var q) ? q.ToString() : null;
        var page = IntParam("page", 1_000_000);
        var pageSize = IntParam("pageSize", 100);
        return Send(200, await marketplace.SearchAsync(query, page, pageSize, cancellationToken));
    });

    [HttpGet("plugins/{**name}")]
    public Task<IActionResult> Details(string name, CancellationToken cancellationToken) => Run(async () =>
        Send(200, await marketplace.DetailsAsync(NameOf(name), cancellationToken)));

    [HttpGet("installed")]
    public Task<IActionResult> Installed(CancellationToken cancellationToken) => Run(async () =>
        Send(200, await marketplace.InstalledAsync(checkUpdates: true, cancellationToken)));

    [HttpPost("installed")]
    public Task<IActionResult> Install() => Run(async () =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        var body = await BodyAsync();
        var name = body["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
        return Send(201, await marketplace.InstallPluginAsync(name, Field(body, "version"), actor, HttpContext.RequestAborted));
    });

    [HttpPut("installed/{**name}")]
    public Task<IActionResult> Update(string name) => Run(async () =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        var package = NameOf(name);
        var body = await BodyAsync();
        var result = await marketplace.UpdatePluginAsync(package, Field(body, "version"), actor, HttpContext.RequestAborted);
        return Send(200, result.Plugin);
    });

    [HttpDelete("installed/{**name}")]
    public Task<IActionResult> Remove(string name) => Run(async () =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        return Send(200, await marketplace.RemovePluginAsync(NameOf(name), actor));
    });
}
