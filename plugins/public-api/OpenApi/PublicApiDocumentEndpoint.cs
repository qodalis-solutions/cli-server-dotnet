using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// Serves <c>&lt;base&gt;/openapi.json</c> (anonymous, credentials ignored, CORS <c>*</c>) and Swagger UI at
/// <c>&lt;base&gt;/swagger</c> for that document only. Every other path falls through.
/// </summary>
internal sealed class PublicApiDocumentEndpoint
{
    private readonly PublicApiOptions _options;
    private readonly IOpenApiDocumentProvider _provider;
    private readonly IServer? _server;
    private readonly string _documentPath;
    private readonly string _swaggerPath;

    public PublicApiDocumentEndpoint(PublicApiOptions options, IOpenApiDocumentProvider provider, IServer? server)
    {
        _options = options;
        _provider = provider;
        _server = server;
        _documentPath = options.BasePath + "/openapi.json";
        _swaggerPath = options.BasePath + "/swagger";
    }

    public string DocumentPath => _documentPath;

    /// <summary>The document as the request should see it (servers filled in), serialized as OpenAPI 3.1 JSON.</summary>
    public async Task<string> RenderAsync(HttpRequest request, CancellationToken ct)
    {
        var document = await _provider.GetOpenApiDocumentAsync(ct);
        if (!HasHostServers(document))
        {
            var url = _options.ServerUrl ?? $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}";
            document.Servers = [new OpenApiServer { Url = url.TrimEnd('/') }];
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.SerializeAsV31(new OpenApiJsonWriter(writer));
        return writer.ToString();
    }

    public async Task<string> TitleAsync(CancellationToken ct) =>
        (await _provider.GetOpenApiDocumentAsync(ct)).Info?.Title ?? "API";

    /// <summary>
    /// The host's own servers. Without a request the framework fills <c>servers</c> from the server's listening
    /// addresses (e.g. <c>http://[::]:8080</c>); those are not the host's choice and are replaced like an empty list.
    /// </summary>
    private bool HasHostServers(OpenApiDocument document)
    {
        if (document.Servers is not { Count: > 0 } servers) return false;
        var addresses = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses == null || addresses.Count == 0) return true;
        var listening = addresses.Select(a => a.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !servers.All(s => s.Url != null && listening.Contains(s.Url.TrimEnd('/')));
    }

    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value;
        var method = context.Request.Method;
        var isDocument = path == _documentPath;
        var isPage = path == _swaggerPath || path == _swaggerPath + "/" || path == _swaggerPath + "/index.html";
        var isScript = path == _swaggerPath + "/index.js";
        if (!isDocument && !isPage && !isScript)
        {
            await next();
            return;
        }

        // The page, its script and the document's servers carry the request's path base; a protocol-relative one
        // (//host or /\host) would point the browser at another origin.
        var pathBase = context.Request.PathBase.Value ?? "";
        if (pathBase.StartsWith("//", StringComparison.Ordinal) || pathBase.StartsWith("/\\", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"invalid path base\"}", context.RequestAborted);
            return;
        }

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await next();
            return;
        }

        string body;
        string contentType;
        if (isDocument)
        {
            body = await RenderAsync(context.Request, context.RequestAborted);
            contentType = "application/json; charset=utf-8";
            context.Response.Headers.AccessControlAllowOrigin = "*";
        }
        else if (isPage)
        {
            body = RenderPage(pathBase, await CachedTitleAsync(context.RequestAborted));
            contentType = "text/html; charset=utf-8";
        }
        else
        {
            body = RenderScript(pathBase, await CachedTitleAsync(context.RequestAborted));
            contentType = "application/javascript; charset=utf-8";
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.Headers.CacheControl = "no-cache";
        if (HttpMethods.IsGet(method)) await response.WriteAsync(body, Encoding.UTF8, context.RequestAborted);
    }

    private volatile string? _title;

    /// <summary>The document's <c>info.title</c>, read on the first Swagger UI request (the document's endpoints are
    /// only known once the app runs) and kept; concurrent first reads compute the same value.</summary>
    private async Task<string> CachedTitleAsync(CancellationToken ct) => _title ??= await TitleAsync(ct);

    /// <summary>Swagger UI's index page with its placeholders filled and a <c>&lt;base&gt;</c> of
    /// <c>{PathBase}{BasePath}/swagger/</c>, so the relative asset URLs resolve under the page's own path.</summary>
    private string RenderPage(string pathBase, string title)
    {
        var html = ReadResource("index.html")
            .Replace("%(DocumentTitle)", WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("%(StylesPath)", "./swagger-ui.css", StringComparison.Ordinal)
            .Replace("%(HeadContent)", "", StringComparison.Ordinal)
            .Replace("%(ScriptBundlePath)", "./swagger-ui-bundle.js", StringComparison.Ordinal)
            .Replace("%(ScriptPresetsPath)", "./swagger-ui-standalone-preset.js", StringComparison.Ordinal);
        var baseHref = WebUtility.HtmlEncode(pathBase + _swaggerPath + "/");
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? html : html.Insert(head + "<head>".Length, $"\n    <base href=\"{baseHref}\" />");
    }

    /// <summary>Swagger UI's script with one document (<c>{PathBase}{BasePath}/openapi.json</c>), no persisted
    /// authorization and "try it out" on. The JSON encoder escapes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and quotes,
    /// so the values cannot leave the script's object literal.</summary>
    private string RenderScript(string pathBase, string title)
    {
        var config = new JsonObject
        {
            ["urls"] = new JsonArray(new JsonObject { ["url"] = pathBase + _documentPath, ["name"] = title }),
            ["deepLinking"] = false,
            ["persistAuthorization"] = false,
            ["displayOperationId"] = false,
            ["defaultModelsExpandDepth"] = 1,
            ["defaultModelExpandDepth"] = 1,
            ["defaultModelRendering"] = "example",
            ["displayRequestDuration"] = false,
            ["docExpansion"] = "list",
            ["showExtensions"] = false,
            ["showCommonExtensions"] = false,
            ["supportedSubmitMethods"] = new JsonArray("get", "put", "post", "delete", "options", "head", "patch", "trace"),
            ["tryItOutEnabled"] = true,
        };
        return ReadResource("index.js")
            .Replace("%(ConfigObject)", config.ToJsonString(), StringComparison.Ordinal)
            .Replace("%(OAuthConfigObject)", "{}", StringComparison.Ordinal)
            .Replace("%(Interceptors)", "{}", StringComparison.Ordinal);
    }

    private static readonly ConcurrentDictionary<string, string> Resources = new();

    private static string ReadResource(string name) => Resources.GetOrAdd(name, static n =>
    {
        using var source = typeof(SwaggerUIOptions).GetTypeInfo().Assembly
            .GetManifestResourceStream("Swashbuckle.AspNetCore.SwaggerUI." + n)
            ?? throw new InvalidOperationException($"public-api: Swagger UI's {n} was not found");
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    });

    /// <summary>Swagger UI's options, used only for its static assets (the page and its script are served above).</summary>
    public SwaggerUIOptions CreateSwaggerUIOptions() => new() { RoutePrefix = _swaggerPath.TrimStart('/') };
}
