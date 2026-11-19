using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
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
        if (path == _documentPath && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            var json = await RenderAsync(context.Request, context.RequestAborted);
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers.CacheControl = "no-cache";
            response.Headers.AccessControlAllowOrigin = "*";
            if (HttpMethods.IsGet(method)) await response.WriteAsync(json, Encoding.UTF8, context.RequestAborted);
            return;
        }

        // Swagger UI answers its own page for /swagger and /swagger/ (no redirect); the page's base tag keeps the
        // relative asset URLs under /swagger/.
        if (path == _swaggerPath || path == _swaggerPath + "/")
            context.Request.Path = _swaggerPath + "/index.html";
        await next();
    }

    /// <summary>Swagger UI's options: one document, no persisted authorization, "try it out" on.</summary>
    public SwaggerUIOptions CreateSwaggerUIOptions()
    {
        var ui = new SwaggerUIOptions { RoutePrefix = _swaggerPath.TrimStart('/') };
        ui.SwaggerEndpoint(_documentPath, "API");
        ui.ConfigObject.PersistAuthorization = false;
        ui.ConfigObject.TryItOutEnabled = true;
        var baseHref = WebUtility.HtmlEncode(_swaggerPath + "/");
        ui.IndexStream = () => IndexWithBase(baseHref);
        return ui;
    }

    private static Stream IndexWithBase(string baseHref)
    {
        using var source = typeof(SwaggerUIOptions).GetTypeInfo().Assembly
            .GetManifestResourceStream("Swashbuckle.AspNetCore.SwaggerUI.index.html")
            ?? throw new InvalidOperationException("public-api: Swagger UI's index page was not found");
        using var reader = new StreamReader(source);
        var html = reader.ReadToEnd();
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        html = head < 0
            ? html
            : html.Insert(head + "<head>".Length, $"\n    <base href=\"{baseHref}\" />");
        return new MemoryStream(Encoding.UTF8.GetBytes(html));
    }
}
