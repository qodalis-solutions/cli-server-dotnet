using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Portal;

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>Registers and serves the docs plugin.</summary>
public static class CliBuilderDocsExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Adds the docs plugin: markdown guides from folders (<see cref="DocsOptions.AddFolder"/>) or code
    /// (<see cref="DocsOptions.AddPage"/>), served as JSON by <see cref="UseQodalisDocs"/> and listed on the portal as
    /// its guides section. Throws <see cref="ArgumentException"/> for an invalid base path or variable.
    /// </summary>
    public static CliBuilder AddDocs(this CliBuilder builder, Action<DocsOptions>? configure = null)
    {
        var options = new DocsOptions();
        configure?.Invoke(options);
        if (!IsValidBasePath(options.BasePath)) throw new ArgumentException($"invalid docs base path: {options.BasePath}");
        DocsVariables.Validate(options.Variables.Keys);

        builder.AddInstalledPlugin(typeof(DocsOptions).Assembly);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new CliPortalSection(CliPortalSection.Docs, options.Title, options.BasePath));
        return builder;
    }

    /// <summary>
    /// Reads the pages (folders are read now, once; any page error throws <see cref="ArgumentException"/>) and serves
    /// <c>GET &lt;BasePath&gt;/pages</c> and <c>GET &lt;BasePath&gt;/pages/&lt;slug&gt;</c> (and <c>HEAD</c>): no sign-in,
    /// credentials ignored, <c>Cache-Control: no-cache</c>, 404 <c>page not found</c> for an unknown slug. Other paths
    /// fall through.
    /// </summary>
    public static IApplicationBuilder UseQodalisDocs(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var options = services.GetService<DocsOptions>()
                      ?? throw new InvalidOperationException("docs: call cli.AddDocs() before UseQodalisDocs()");
        var contentRoot = services.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
        var pages = DocsPageLoader.Load(options, contentRoot);
        var bySlug = pages.ToDictionary(p => p.Slug, StringComparer.Ordinal);
        var listPath = options.BasePath + "/pages";
        var pagePrefix = listPath + "/";

        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? string.Empty;
            if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) ||
                !(path == listPath || path.StartsWith(pagePrefix, StringComparison.Ordinal)))
            {
                await next();
                return;
            }

            var variables = Variables(options, context.Request);
            object body;
            var status = StatusCodes.Status200OK;
            if (path == listPath)
            {
                body = pages.Select(p => new PageSummary(p.Slug, DocsVariables.Replace(p.Title, variables), p.Group, p.Order)).ToList();
            }
            else if (bySlug.TryGetValue(path[pagePrefix.Length..], out var page))
            {
                body = new PageView(page.Slug, DocsVariables.Replace(page.Title, variables), page.Group, page.Order,
                    DocsVariables.Replace(page.Markdown, variables));
            }
            else
            {
                status = StatusCodes.Status404NotFound;
                body = new ErrorBody("page not found");
            }

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            if (HttpMethods.IsGet(method))
                await context.Response.WriteAsync(JsonSerializer.Serialize(body, body.GetType(), Json), context.RequestAborted);
        });
        return app;
    }

    private static Dictionary<string, string> Variables(DocsOptions options, HttpRequest request)
    {
        var values = new Dictionary<string, string>(options.Variables, StringComparer.Ordinal)
        {
            [DocsVariables.BaseUrl] = (options.BaseUrl ?? $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}").TrimEnd('/'),
            [DocsVariables.Name] = options.Name,
        };
        return values;
    }

    private static bool IsValidBasePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && path.Length > 1 && path[^1] != '/' &&
        !path.Contains("//", StringComparison.Ordinal) && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '#' or '\\');

    private sealed record PageSummary(string Slug, string Title, string Group, int Order);

    private sealed record PageView(string Slug, string Title, string Group, int Order, string Markdown);

    private sealed record ErrorBody(string Error);
}
