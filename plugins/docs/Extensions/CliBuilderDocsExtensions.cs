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
        return builder.AddDocs(options);
    }

    /// <summary>Adds the docs plugin with these options (read when the server starts serving).</summary>
    internal static CliBuilder AddDocs(this CliBuilder builder, DocsOptions options)
    {
        if (!IsValidBasePath(options.BasePath)) throw new ArgumentException($"invalid docs base path: {options.BasePath}");
        DocsVariables.Validate(options.Variables.Keys);

        builder.AddInstalledPlugin(typeof(DocsOptions).Assembly);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new DocsPages(options, sp.GetService<IHostEnvironment>()));
        // The docs command (docs list, docs read <slug>): the same pages and views as the routes
        builder.AddProcessor<CliDocsCommandProcessor>();
        builder.Services.AddSingleton(sp => new CliPortalSection(CliPortalSection.Docs, options.Title, options.BasePath)
        {
            // the page count, shown by the portal command
            PageCount = () => sp.GetRequiredService<DocsPages>().All.Count,
        });
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
        var docs = services.GetRequiredService<DocsPages>();
        _ = docs.All; // read now, so a page error stops the startup
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

            if (CliPathBase.IsUnsafe(context.Request.PathBase.Value))
            {
                // {{ baseUrl }} carries the path base; //evil.example would make the guides' links point elsewhere.
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json; charset=utf-8";
                if (HttpMethods.IsGet(method)) await context.Response.WriteAsync(CliPathBase.InvalidBody, context.RequestAborted);
                return;
            }

            var variables = docs.Variables($"{context.Request.Scheme}://{context.Request.Host.Value}{context.Request.PathBase.Value}");
            object body;
            var status = StatusCodes.Status200OK;
            if (path == listPath)
            {
                body = docs.List(variables);
            }
            else if (docs.Find(path[pagePrefix.Length..]) is { } page)
            {
                body = DocsPages.View(page, variables);
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

    internal static bool IsValidBasePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && path.Length > 1 && path[^1] != '/' &&
        !path.Contains("//", StringComparison.Ordinal) && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '#' or '\\');

    private sealed record ErrorBody(string Error);
}
