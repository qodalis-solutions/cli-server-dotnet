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
/// Server plugins from the marketplace (<c>/api/v1/qcli/server/marketplace</c>): reading needs <c>admin:access</c>,
/// installing, updating and removing <c>plugins:install</c>. Every change takes effect after a restart: 202 with
/// <c>restartRequired</c>. 404 when the marketplace is off.
/// <list type="bullet">
/// <item><c>GET /</c>: <c>{ marketplaceUrl, install, platform: 'dotnet', serverVersion, enabled }</c></item>
/// <item><c>GET /plugins?query=&amp;page=&amp;pageSize=&amp;platform=</c>: the marketplace's server plugins, annotated (<c>platform</c>, <c>otherPlatforms</c>, <c>autoInstall</c>, <c>updateAvailable</c>, <c>allowed</c>, <c>blockedReason</c>); <c>platform</c>: <c>this</c> (default: the ones with a .NET package), <c>all</c>, <c>node</c>, <c>python</c> or <c>dotnet</c> (400 for anything else). Plugins without a .NET package are not installable here.</item>
/// <item><c>GET /plugins/{name}</c>: one plugin with its platforms and versions</item>
/// <item><c>GET /installed</c>: the installed plugins and how they run (<c>status</c>, <c>loadedVersion</c>, <c>error</c>, <c>updateAvailable</c>)</item>
/// <item><c>POST /installed</c> <c>{ name, version? }</c>: 202, the installed plugin with <c>restartRequired</c></item>
/// <item><c>PUT /installed/{name}</c> <c>{ version? }</c>: 202 (200 with <c>changed: false</c> when it already is that version)</item>
/// <item><c>DELETE /installed/{name}</c>: 202, the removed plugin</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/qcli/server/marketplace")]
public class ServerMarketplaceController(IServiceProvider services, ILogger<ServerMarketplaceController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ContentResult Send(int status, object? body)
    {
        Response.Headers.CacheControl = "no-store";
        var json = body is JsonNode node ? node.ToJsonString(Json) : JsonSerializer.Serialize(body, Json);
        return new ContentResult { StatusCode = status, Content = json, ContentType = "application/json; charset=utf-8" };
    }

    private ContentResult Error(int status, string message) => Send(status, new JsonObject { ["error"] = message });

    private async Task<IActionResult> Run(Func<ServerPluginMarketplace, Task<IActionResult>> action)
    {
        if (services.GetService(typeof(ServerPluginMarketplace)) is not ServerPluginMarketplace marketplace) return Error(404, "not found");
        try
        {
            return await action(marketplace);
        }
        catch (DashboardMarketplaceException ex)
        {
            return Error(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[qcli-admin] server marketplace request failed");
            return Error(500, "internal server error");
        }
    }

    /// <summary>The installer, or null with 403 <c>forbidden: missing permission plugins:install</c> (the static admin may do everything).</summary>
    private DashboardMarketplaceActor? Installer(out IActionResult? denied)
    {
        denied = null;
        if (HttpContext.GetCliPrincipal() is { } principal)
        {
            if (principal.Can(CliRoutePolicy.PluginsInstall)) return new DashboardMarketplaceActor(principal.UserId, principal.UserName);
            denied = Error(403, $"forbidden: missing permission {CliRoutePolicy.PluginsInstall}");
            return null;
        }
        var user = HttpContext.Items["AdminUser"] as ClaimsPrincipal;
        return new DashboardMarketplaceActor(null, user?.Identity?.Name ?? DashboardMarketplaceActor.Anonymous.UserName);
    }

    private int? IntParam(string name, int max)
    {
        if (!Request.Query.TryGetValue(name, out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0])) return null;
        if (values.Count > 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > max)
            throw new DashboardMarketplaceException(400, $"invalid {name}");
        return n;
    }

    private static string NameOf(string? raw)
    {
        try
        {
            return Uri.UnescapeDataString(raw ?? string.Empty);
        }
        catch (UriFormatException)
        {
            throw new DashboardMarketplaceException(400, "invalid plugin name");
        }
    }

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
    public Task<IActionResult> Info() => Run(m => Task.FromResult<IActionResult>(Send(200, m.Info())));

    [HttpGet("plugins")]
    public Task<IActionResult> Search(CancellationToken cancellationToken) => Run(async m =>
    {
        var query = Request.Query.TryGetValue("query", out var q) ? q.ToString() : null;
        // platform=all&platform=node is "all,node": invalid
        var platform = Request.Query.TryGetValue("platform", out var p) ? p.ToString() : null;
        return Send(200, await m.SearchAsync(query, IntParam("page", 1_000_000), IntParam("pageSize", 100), platform, cancellationToken));
    });

    [HttpGet("plugins/{**name}")]
    public Task<IActionResult> Details(string name, CancellationToken cancellationToken) => Run(async m =>
        Send(200, await m.DetailsAsync(NameOf(name), cancellationToken)));

    [HttpGet("installed")]
    public Task<IActionResult> Installed(CancellationToken cancellationToken) => Run(async m =>
        Send(200, await m.InstalledAsync(checkUpdates: true, cancellationToken)));

    [HttpPost("installed")]
    public Task<IActionResult> Install() => Run(async m =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        var body = await BodyAsync();
        return Send(202, await m.InstallPluginAsync(Field(body, "name"), Field(body, "version"), actor, HttpContext.RequestAborted));
    });

    [HttpPut("installed/{**name}")]
    public Task<IActionResult> Update(string name) => Run(async m =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        var body = await BodyAsync();
        var result = await m.UpdatePluginAsync(NameOf(name), Field(body, "version"), actor, HttpContext.RequestAborted);
        return Send(result.Changed == false ? 200 : 202, result);
    });

    [HttpDelete("installed/{**name}")]
    public Task<IActionResult> Remove(string name) => Run(async m =>
    {
        if (Installer(out var denied) is not { } actor) return denied!;
        return Send(202, await m.RemovePluginAsync(NameOf(name), actor));
    });
}
