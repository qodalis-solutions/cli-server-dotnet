using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Portal;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>C8: the portal site at one mount, answering only its own paths, with <c>portal.json</c>.</summary>
public class PortalPluginTests
{
    private static readonly string Site = Path.Combine(AppContext.BaseDirectory, "fixtures", "portal-site");

    private static async Task<IHost> StartAsync(Action<PortalOptions>? configure = null, bool docs = true, bool api = true,
        Action<IEndpointRouteBuilder>? endpoints = null, bool portalAfterEndpoints = false,
        Action<IServiceCollection>? configureServices = null, Action<CliBuilder>? configureCli = null,
        string? pathBase = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                configureServices?.Invoke(services);
                if (api) services.AddOpenApi("public");
                services.AddControllers().AddCli(cli =>
                {
                    if (docs) cli.AddDocs(o => o.AddFolder(DocsPluginTests.Fixtures));
                    if (api) cli.AddPublicApi();
                    configureCli?.Invoke(cli);
                    cli.AddPortal(o =>
                    {
                        o.SitePath = Site;
                        o.Name = "RateBell API";
                        o.FooterLinks.Add(new PortalLink("Terms", "/terms"));
                        o.Theme.Light.Primary = "#0a7";
                        configure?.Invoke(o);
                    });
                });
            });
            web.Configure(app =>
            {
                if (pathBase != null)
                    app.Use((context, next) =>
                    {
                        context.Request.PathBase = pathBase;
                        return next();
                    });
                app.UseRouting();
                if (docs) app.UseQodalisDocs();
                if (api) app.UseQodalisPublicApi();
                if (!portalAfterEndpoints) app.UseQodalisPortal();
                app.UseEndpoints(e =>
                {
                    endpoints?.Invoke(e);
                    e.MapControllers();
                });
                if (portalAfterEndpoints) app.UseQodalisPortal();
            });
        });
        return await builder.StartAsync();
    }

    private static async Task<(int Status, string Body, HttpResponseMessage Res)> SendAsync(IHost host, string path,
        HttpMethod? method = null, string? accept = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (accept != null) req.Headers.Accept.ParseAdd(accept);
        var res = await host.GetTestClient().SendAsync(req);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(), res);
    }

    [Fact]
    public async Task PortalJson_BothSections()
    {
        using var host = await StartAsync();

        var (status, body, res) = await SendAsync(host, "/qcli/portal/portal.json");

        Assert.Equal(200, status);
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Equal(
            """{"name":"RateBell API","logo":null,"theme":{"mode":"system","dark":{},"light":{"primary":"#0a7"}},"footerLinks":[{"label":"Terms","url":"/terms"}],"docs":{"title":"Guides","pages":"/qcli/docs/pages"},"api":{"title":"API reference","openapi":"/qcli/api/openapi.json","swagger":"/qcli/api/swagger"}}""",
            body);
    }

    [Fact]
    public async Task PortalJson_DocsOnly()
    {
        using var host = await StartAsync(api: false);

        var (_, body, _) = await SendAsync(host, "/qcli/portal/portal.json");

        Assert.EndsWith(""","docs":{"title":"Guides","pages":"/qcli/docs/pages"},"api":null}""", body);
    }

    [Fact]
    public async Task PortalJson_ApiOnly()
    {
        using var host = await StartAsync(docs: false);

        var (_, body, _) = await SendAsync(host, "/qcli/portal/portal.json");

        Assert.EndsWith(""","docs":null,"api":{"title":"API reference","openapi":"/qcli/api/openapi.json","swagger":"/qcli/api/swagger"}}""", body);
    }

    [Fact]
    public async Task PortalJson_NothingPublished_DefaultsAndOptions()
    {
        using var host = await StartAsync(o =>
        {
            o.Name = "Documentation";
            o.LogoUrl = "https://ratebell.app/logo.svg";
            o.FooterLinks.Clear();
            o.Theme.Mode = PortalThemeMode.Dark;
            o.Theme.Light.Primary = null;
            o.Theme.Dark.Background = " #000 ";
        }, docs: false, api: false);

        var (_, body, _) = await SendAsync(host, "/qcli/portal/portal.json");

        Assert.Equal(
            """{"name":"Documentation","logo":"https://ratebell.app/logo.svg","theme":{"mode":"dark","dark":{"background":"#000"},"light":{}},"footerLinks":[],"docs":null,"api":null}""",
            body);
    }

    [Fact]
    public async Task PortalJson_CustomTitlesAndPaths()
    {
        using var host = await StartAsync(docs: false, api: false, configureCli: cli =>
        {
            cli.AddDocs(o => { o.Title = "Handbook"; o.BasePath = "/developers/docs"; });
        });

        var (_, body, _) = await SendAsync(host, "/qcli/portal/portal.json");

        Assert.Contains("\"docs\":{\"title\":\"Handbook\",\"pages\":\"/developers/docs/pages\"}", body);
    }

    [Theory]
    [InlineData("/qcli/portal")]
    [InlineData("/qcli/portal/")]
    [InlineData("/qcli/portal/guides/auth")]
    [InlineData("/qcli/portal/reference")]
    [InlineData("/qcli/portal/reference/Rates")]
    public async Task Index_ForSiteRoutes(string path)
    {
        using var host = await StartAsync();

        var (status, body, res) = await SendAsync(host, path);

        Assert.Equal(200, status);
        Assert.Equal("text/html; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Contains("<base href=\"/qcli/portal/\" />", body);
        Assert.DoesNotContain("<base href=\"/\" />", body);

        var head = await SendAsync(host, path, HttpMethod.Head);
        Assert.Equal(200, head.Status);
        Assert.Empty(head.Body);
    }

    [Fact]
    public async Task Assets_Served()
    {
        using var host = await StartAsync();

        var (status, body, res) = await SendAsync(host, "/qcli/portal/assets/app.js");
        Assert.Equal(200, status);
        Assert.Contains("portal", body);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers.CacheControl!.ToString());
        Assert.Equal("text/javascript", res.Content.Headers.ContentType!.MediaType);

        var favicon = await SendAsync(host, "/qcli/portal/favicon.svg");
        Assert.Equal(200, favicon.Status);
        Assert.Equal("image/svg+xml", favicon.Res.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("GET", "/qcli/portal/other")]
    [InlineData("GET", "/qcli/portal/assets/missing.js")]
    [InlineData("GET", "/qcli/portal/assets/../index.html")]
    [InlineData("GET", "/qcli/portal/guides/a/b")]
    [InlineData("GET", "/qcli/portal/guides/")]
    [InlineData("GET", "/qcli/portal/index.html")]
    [InlineData("GET", "/qcli/portalx")]
    [InlineData("POST", "/qcli/portal/")]
    [InlineData("POST", "/qcli/portal/portal.json")]
    public async Task OtherPaths_FallThrough(string method, string path)
    {
        using var host = await StartAsync();

        var (status, body, _) = await SendAsync(host, path, new HttpMethod(method));

        Assert.Equal(404, status);
        Assert.DoesNotContain("portal", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootMount_DoesNotShadowHostRoutes(bool portalAfterEndpoints)
    {
        using var host = await StartAsync(o => o.Path = "/", portalAfterEndpoints: portalAfterEndpoints, endpoints: e =>
        {
            e.MapGet("/v1/rates", () => "rates");
            e.MapGet("/health", () => "healthy");
        });

        foreach (var accept in new string?[] { null, "text/html" })
        {
            Assert.Equal((200, "rates"), Take(await SendAsync(host, "/v1/rates", accept: accept)));
            Assert.Equal((200, "healthy"), Take(await SendAsync(host, "/health", accept: accept)));
        }

        foreach (var path in new[] { "/", "/guides/x", "/reference", "/reference/Rates" })
        {
            var (status, body, _) = await SendAsync(host, path);
            Assert.Equal(200, status);
            Assert.Contains("<base href=\"/\" />", body);
        }
        Assert.Equal(200, (await SendAsync(host, "/portal.json")).Status);
        Assert.Equal(200, (await SendAsync(host, "/assets/app.js")).Status);
        Assert.Equal(404, (await SendAsync(host, "/v1/missing")).Status);
        Assert.Equal(404, (await SendAsync(host, "/assets/missing.js")).Status);

        static (int, string) Take((int Status, string Body, HttpResponseMessage _) r) => (r.Status, r.Body);
    }

    [Fact]
    public async Task NoSite_ServesNothing()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"qcli-portal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        var logs = new CapturingLoggerProvider();
        try
        {
            using var host = await StartAsync(o => o.SitePath = empty,
                configureServices: s => s.AddSingleton<ILoggerProvider>(logs));

            Assert.Equal(404, (await SendAsync(host, "/qcli/portal/")).Status);
            Assert.Equal(404, (await SendAsync(host, "/qcli/portal/portal.json")).Status);
            Assert.Single(logs.Messages, m => m == "[qcli] portal site not found; the portal is not served");
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    public static IEnumerable<object[]> InvalidOptionCases()
    {
        yield return ["name", "portal name must be 1-80 characters without control characters"];
        yield return ["name-empty", "portal name must be 1-80 characters without control characters"];
        yield return ["name-control", "portal name must be 1-80 characters without control characters"];
        yield return ["logo", "portal logo must be an http(s) URL, a root-relative path or a data:image URL"];
        yield return ["logo-protocol-relative", "portal logo must be an http(s) URL, a root-relative path or a data:image URL"];
        yield return ["colour", "invalid portal colour light.primary: red;}"];
        yield return ["colour-dark", "invalid portal colour dark.surface: url(x)"];
        yield return ["links", "invalid portal footer link: link 21"];
        yield return ["link-url", "invalid portal footer link: Terms"];
        yield return ["link-label", "invalid portal footer link: "];
        yield return ["path", "invalid portal path: qcli"];
        yield return ["path-trailing", "invalid portal path: /docs/"];
    }

    [Theory]
    [MemberData(nameof(InvalidOptionCases))]
    public async Task Invalid_Options_Throw(string kind, string message)
    {
        Action<PortalOptions> configure = kind switch
        {
            "name" => o => o.Name = new string('n', 81),
            "name-empty" => o => o.Name = "",
            "name-control" => o => o.Name = "Rate\nBell",
            "logo" => o => o.LogoUrl = "javascript:alert(1)",
            "logo-protocol-relative" => o => o.LogoUrl = "//evil.example/logo.png",
            "colour" => o => o.Theme.Light.Primary = "red;}",
            "colour-dark" => o => o.Theme.Dark.Surface = "url(x)",
            "links" => o =>
            {
                o.FooterLinks.Clear();
                for (var i = 1; i <= 21; i++) o.FooterLinks.Add(new PortalLink($"link {i}", "/x"));
            },
            "link-url" => o => o.FooterLinks[0] = new PortalLink("Terms", "ftp://x"),
            "link-label" => o => o.FooterLinks[0] = new PortalLink("", "/terms"),
            "path" => o => o.Path = "qcli",
            "path-trailing" => o => o.Path = "/docs/",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(configure));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task ValidLogos_Accepted()
    {
        foreach (var logo in new[] { "/logo.svg", "http://x.example/l.png", "data:image/png;base64,iVBORw0KGgo=" })
        {
            using var host = await StartAsync(o => o.LogoUrl = logo);
            Assert.Contains($"\"logo\":\"{logo}\"", (await SendAsync(host, "/qcli/portal/portal.json")).Body);
        }
    }

    [Fact]
    public async Task DuplicateSection_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            configureServices: s => s.AddSingleton(new CliPortalSection(CliPortalSection.Docs, "More", "/more"))));

        Assert.Equal("portal section already registered: docs", ex.Message);
    }

    [Fact]
    public async Task CustomPath()
    {
        using var host = await StartAsync(o => o.Path = "/developers");

        Assert.Equal(200, (await SendAsync(host, "/developers/portal.json")).Status);
        Assert.Contains("<base href=\"/developers/\" />", (await SendAsync(host, "/developers/guides/auth")).Body);
        Assert.Equal(404, (await SendAsync(host, "/qcli/portal/portal.json")).Status);
    }

    [Fact]
    public async Task PathBase_PrefixesUrlsAndBase()
    {
        using var host = await StartAsync(pathBase: "/app");

        Assert.Contains("<base href=\"/app/qcli/portal/\" />", (await SendAsync(host, "/qcli/portal/")).Body);
        var json = (await SendAsync(host, "/qcli/portal/portal.json")).Body;
        Assert.Contains("\"pages\":\"/app/qcli/docs/pages\"", json);
        Assert.Contains("\"openapi\":\"/app/qcli/api/openapi.json\"", json);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task ProtocolRelativePathBase_Refused(string pathBase)
    {
        using var host = await StartAsync(pathBase: pathBase);

        foreach (var path in new[] { "/qcli/portal/", "/qcli/portal/portal.json", "/qcli/portal/guides/auth" })
        {
            var (status, body, _) = await SendAsync(host, path);
            Assert.Equal(400, status);
            Assert.Equal("""{"error":"invalid path base"}""", body);
        }
    }

    [Fact]
    public void AdminTheme_StillValidates()
    {
        Assert.False(AdminTheme.IsColor("red;}"));
        Assert.True(AdminTheme.IsColor("#0a7"));
        Assert.True(Qodalis.Cli.Appearance.CliColor.IsValid("oklch(0.7 0.1 200)"));
    }
}
