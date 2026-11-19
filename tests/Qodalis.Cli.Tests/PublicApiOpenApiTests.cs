using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>C6: the host's public OpenAPI document at <c>&lt;base&gt;/openapi.json</c> and Swagger UI at <c>&lt;base&gt;/swagger</c>.</summary>
public class PublicApiOpenApiTests
{
    private static void Endpoints(IEndpointRouteBuilder e)
    {
        e.MapGet("/v1/ping", () => "pong").WithGroupName("public");
        e.MapGet("/v1/banks", () => "banks").RequireQcliAuthorization("banks:read", hostTokenOnly: true).WithGroupName("public");
        e.MapGet("/api/v1/internal", () => "internal");
    }

    private static Task<UsersTestHost> StartAsync(Action<PublicApiOptions>? configure = null,
        Action<OpenApiOptions>? configureDocument = null, string? pathBase = null, string? forcedPathBase = null)
    {
        return PublicApiTestHost.StartAsync(configure,
            configureServices: s =>
            {
                s.AddOpenApi("public", o =>
                {
                    o.ShouldInclude = d => d.GroupName == "public";
                    configureDocument?.Invoke(o);
                });
                s.AddOpenApi("v1");
            },
            configureApp: app =>
            {
                if (pathBase != null) app.UsePathBase(pathBase);
                if (forcedPathBase != null)
                    app.Use((context, next) =>
                    {
                        context.Request.PathBase = forcedPathBase;
                        return next();
                    });
                app.UseQodalisPublicApi();
            },
            endpoints: Endpoints);
    }

    private static async Task<JsonObject> DocumentAsync(UsersTestHost h, string path = "/qcli/api/openapi.json", string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await h.Client.SendAsync(req);
        Assert.Equal(200, (int)res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
    }

    [Fact]
    public async Task Document_HasOnlyPublicRoutes()
    {
        await using var h = await StartAsync();

        var res = await h.Client.GetAsync("/qcli/api/openapi.json");

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Equal("*", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.StartsWith("3.1", doc["openapi"]!.GetValue<string>());
        Assert.Equal(new[] { "/v1/banks", "/v1/ping", "/v1/rates" }.Order(),
            doc["paths"]!.AsObject().Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Document_SecuritySchemeAndRequirement()
    {
        await using var h = await StartAsync();

        var doc = await DocumentAsync(h);

        var scheme = doc["components"]!["securitySchemes"]!["qcliToken"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            { "type": "http", "description": "API token (qcli_pat_…), sent as Authorization: Bearer <token>", "scheme": "bearer", "bearerFormat": "qcli_pat" }
            """), scheme), scheme.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "qcliToken": [] } ]"""), doc["security"]), doc["security"]?.ToJsonString());
    }

    [Fact]
    public async Task Document_RequiredPermission()
    {
        await using var h = await StartAsync();

        var paths = (await DocumentAsync(h))["paths"]!;

        Assert.Equal("rates:read", paths["/v1/rates"]!["get"]!["x-required-permission"]!.GetValue<string>());
        Assert.True(paths["/v1/rates"]!["get"]!["x-host-token-only"]!.GetValue<bool>());
        Assert.Equal("banks:read", paths["/v1/banks"]!["get"]!["x-required-permission"]!.GetValue<string>());
        Assert.True(paths["/v1/banks"]!["get"]!["x-host-token-only"]!.GetValue<bool>());
        Assert.Null(paths["/v1/ping"]!["get"]!["x-required-permission"]);
        Assert.Null(paths["/v1/ping"]!["get"]!["x-host-token-only"]);
    }

    [Fact]
    public async Task Document_HostValuesWin()
    {
        await using var h = await StartAsync(configureDocument: o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "https://api.example.com" }];
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes["qcliToken"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey, Name = "X-Api-Key", In = ParameterLocation.Header,
                };
                doc.Paths["/v1/ping"]!.Operations![HttpMethod.Get].Extensions ??= new Dictionary<string, IOpenApiExtension>();
                doc.Paths["/v1/ping"]!.Operations![HttpMethod.Get].Extensions!["x-required-permission"] =
                    new JsonNodeExtension(JsonValue.Create("custom"));
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((op, ctx, _) =>
            {
                if (ctx.Description.RelativePath == "v1/rates")
                {
                    op.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    op.Extensions["x-required-permission"] = new JsonNodeExtension(JsonValue.Create("rates:custom"));
                }
                return Task.CompletedTask;
            });
        });

        var doc = await DocumentAsync(h);

        Assert.Equal("https://api.example.com", Assert.Single(doc["servers"]!.AsArray())!["url"]!.GetValue<string>());
        Assert.Equal("apiKey", doc["components"]!["securitySchemes"]!["qcliToken"]!["type"]!.GetValue<string>());
        Assert.Equal("custom", doc["paths"]!["/v1/ping"]!["get"]!["x-required-permission"]!.GetValue<string>());
        Assert.Equal("rates:custom", doc["paths"]!["/v1/rates"]!["get"]!["x-required-permission"]!.GetValue<string>());
    }

    [Fact]
    public async Task Document_HostSecurityWins()
    {
        await using var h = await StartAsync(configureDocument: o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, Name = "X-Api-Key", In = ParameterLocation.Header,
            };
            doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("apiKey", doc)] = [] }];
            return Task.CompletedTask;
        }));

        var doc = await DocumentAsync(h);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "apiKey": [] } ]"""), doc["security"]), doc["security"]?.ToJsonString());
        Assert.NotNull(doc["components"]!["securitySchemes"]!["qcliToken"]);
    }

    [Fact]
    public async Task Document_ServersFromRequest()
    {
        await using (var h = await StartAsync())
        {
            var doc = await DocumentAsync(h);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "url": "http://localhost" } ]"""), doc["servers"]),
                doc["servers"]?.ToJsonString());
        }

        await using (var h = await StartAsync(o => o.ServerUrl = "https://api.ratebell.app"))
        {
            var doc = await DocumentAsync(h);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "url": "https://api.ratebell.app" } ]"""), doc["servers"]),
                doc["servers"]?.ToJsonString());
        }
    }

    [Fact]
    public async Task Document_IgnoresCredentials()
    {
        await using var h = await StartAsync();
        var (_, token) = await h.HostTokenAsync("alice");

        await DocumentAsync(h, bearer: token);
        await DocumentAsync(h, bearer: "qcli_pat_garbage");
        await DocumentAsync(h, bearer: "garbage");
    }

    [Fact]
    public async Task Swagger_ServesUi()
    {
        await using var h = await StartAsync(configureDocument: o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "RateBell API";
            return Task.CompletedTask;
        }));

        foreach (var path in new[] { "/qcli/api/swagger", "/qcli/api/swagger/", "/qcli/api/swagger/index.html" })
        {
            var res = await h.Client.GetAsync(path);
            Assert.Equal(200, (int)res.StatusCode);
            Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
            var html = await res.Content.ReadAsStringAsync();
            Assert.Contains("<title>RateBell API</title>", html);
            Assert.Contains("<base href=\"/qcli/api/swagger/\"", html);
        }

        var js = await h.Client.GetAsync("/qcli/api/swagger/index.js");
        Assert.Equal(200, (int)js.StatusCode);
        var script = await js.Content.ReadAsStringAsync();
        Assert.Contains("\"/qcli/api/openapi.json\"", script);
        Assert.Contains("\"persistAuthorization\":false", script);
        Assert.Contains("\"tryItOutEnabled\":true", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "\"url\":"));
        Assert.Equal(200, (int)(await h.Client.GetAsync("/qcli/api/swagger/swagger-ui-bundle.js")).StatusCode);
        Assert.Equal(200, (int)(await h.Client.GetAsync("/qcli/api/swagger/index.css")).StatusCode);
    }

    [Fact]
    public async Task SwaggerAndDocument_UnderPathBase()
    {
        await using var h = await StartAsync(pathBase: "/x");

        foreach (var path in new[] { "/x/qcli/api/swagger", "/x/qcli/api/swagger/", "/x/qcli/api/swagger/index.html" })
        {
            var res = await h.Client.GetAsync(path);
            Assert.Equal(200, (int)res.StatusCode);
            Assert.Contains("<base href=\"/x/qcli/api/swagger/\" />", await res.Content.ReadAsStringAsync());
        }
        var script = await h.Client.GetStringAsync("/x/qcli/api/swagger/index.js");
        Assert.Contains("\"/x/qcli/api/openapi.json\"", script);
        Assert.Equal(200, (int)(await h.Client.GetAsync("/x/qcli/api/swagger/swagger-ui-bundle.js")).StatusCode);
        Assert.Equal(200, (int)(await h.Client.GetAsync("/x/qcli/api/swagger/index.css")).StatusCode);

        var doc = await DocumentAsync(h, "/x/qcli/api/openapi.json");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "url": "http://localhost/x" } ]"""), doc["servers"]),
            doc["servers"]?.ToJsonString());
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task ProtocolRelativePathBase_Refused(string pathBase)
    {
        await using var h = await StartAsync(forcedPathBase: pathBase);

        foreach (var path in new[] { "/qcli/api/swagger/", "/qcli/api/swagger/index.html", "/qcli/api/swagger/index.js", "/qcli/api/openapi.json" })
        {
            var res = await h.Client.GetAsync(path);
            Assert.Equal(400, (int)res.StatusCode);
            Assert.Equal("""{"error":"invalid path base"}""", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ExplicitlyEmptySecurityAndServers_AreFilled()
    {
        await using var h = await StartAsync(configureDocument: o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Security = [];
            doc.Servers = [];
            return Task.CompletedTask;
        }));

        var doc = await DocumentAsync(h);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "qcliToken": [] } ]"""), doc["security"]), doc["security"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "url": "http://localhost" } ]"""), doc["servers"]), doc["servers"]?.ToJsonString());
    }

    [Fact]
    public async Task OtherPathsUnderBase_NotHandled()
    {
        await using var h = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/qcli/api/other")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/qcli/api")).StatusCode);
    }

    [Fact]
    public async Task CustomBasePath()
    {
        await using var h = await StartAsync(o => o.BasePath = "/developers/api");

        Assert.Equal(200, (int)(await h.Client.GetAsync("/developers/api/openapi.json")).StatusCode);
        Assert.Equal(404, (int)(await h.Client.GetAsync("/qcli/api/openapi.json")).StatusCode);
        Assert.Equal(200, (int)(await h.Client.GetAsync("/developers/api/swagger/")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("api")]
    [InlineData("/api/")]
    public async Task InvalidBasePath_Throws(string path)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(o => o.BasePath = path));

        Assert.Equal($"invalid public-api base path: {path}", ex.Message);
    }

    [Fact]
    public async Task MissingDocument_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(o => o.DocumentName = "nope"));

        Assert.Equal("public-api: no OpenAPI document named 'nope'; call services.AddOpenApi(\"nope\")", ex.Message);
    }

    [Fact]
    public async Task XCodeSamples_PassThrough()
    {
        await using var h = await StartAsync(configureDocument: o => o.AddOperationTransformer((op, ctx, _) =>
        {
            if (ctx.Description.RelativePath == "v1/rates")
            {
                op.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                op.Extensions["x-codeSamples"] = new JsonNodeExtension(JsonNode.Parse("""[ { "lang": "curl", "source": "curl …" } ]""")!);
            }
            return Task.CompletedTask;
        }));

        var samples = (await DocumentAsync(h))["paths"]!["/v1/rates"]!["get"]!["x-codeSamples"];

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[ { "lang": "curl", "source": "curl …" } ]"""), samples), samples?.ToJsonString());
    }

    [Fact]
    public async Task OtherDocuments_Untouched()
    {
        await using var h = await StartAsync();

        var provider = h.Host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var doc = await provider.GetOpenApiDocumentAsync();

        Assert.True(doc.Components?.SecuritySchemes == null || !doc.Components.SecuritySchemes.ContainsKey("qcliToken"));
    }
}
