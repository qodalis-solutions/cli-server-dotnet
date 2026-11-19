using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Portal;

namespace Qodalis.Cli.Tests;

/// <summary>C7/C9: markdown guides from folders or code, served as JSON at <c>&lt;base&gt;/pages</c>.</summary>
public class DocsPluginTests
{
    internal static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "docs");

    internal static async Task<IHost> StartAsync(Action<DocsOptions>? configure = null, bool fixtures = true)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddControllers().AddCli(cli => cli.AddDocs(o =>
            {
                o.Name = "RateBell API";
                o.Variables["support"] = "help@ratebell.app";
                if (fixtures) o.AddFolder(Fixtures);
                configure?.Invoke(o);
            })));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseQodalisDocs();
                app.UseEndpoints(e => e.MapControllers());
            });
        });
        return await builder.StartAsync();
    }

    private static async Task<(HttpResponseMessage Res, string Body)> GetAsync(IHost host, string path, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await host.GetTestClient().SendAsync(req);
        return (res, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pages_ListInOrder()
    {
        using var host = await StartAsync();

        var (res, body) = await GetAsync(host, "/qcli/docs/pages");

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Equal(
            """[{"slug":"limits","title":"Limits and quotas","group":"Guides","order":0},{"slug":"index","title":"Welcome to RateBell API","group":"Guides","order":1},{"slug":"auth","title":"Authentication","group":"Basics","order":2}]""",
            body);
    }

    [Fact]
    public async Task Page_ReplacesVariables()
    {
        using var host = await StartAsync();

        var (res, body) = await GetAsync(host, "/qcli/docs/pages/index");

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Equal(JsonSerializer.Serialize(new
        {
            slug = "index",
            title = "Welcome to RateBell API",
            group = "Guides",
            order = 1,
            markdown = "Call http://localhost/v1/rates with help@ratebell.app and {{unknown}}.\n",
        }), body);
    }

    [Fact]
    public async Task Page_FrontMatterRemoved_HeadingKept()
    {
        using var host = await StartAsync();

        var page = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages/auth")).Body);

        Assert.Equal("# Ignored heading\nSend `Authorization: Bearer <token>`.\n", page.GetProperty("markdown").GetString());
        Assert.Equal("Authentication", page.GetProperty("title").GetString());
        Assert.Equal("Basics", page.GetProperty("group").GetString());
    }

    [Fact]
    public async Task Page_NoFrontMatter_TitleFromHeading()
    {
        using var host = await StartAsync();

        var page = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages/limits")).Body);

        Assert.Equal("# Limits and quotas\nRequests per day.\n", page.GetProperty("markdown").GetString());
        Assert.Equal("Limits and quotas", page.GetProperty("title").GetString());
    }

    [Fact]
    public async Task BaseUrl_Option()
    {
        using var host = await StartAsync(o => o.BaseUrl = "https://api.ratebell.app");

        var page = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages/index")).Body);

        Assert.StartsWith("Call https://api.ratebell.app/v1/rates", page.GetProperty("markdown").GetString());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("INDEX")]
    [InlineData("..%2Fsecret")]
    [InlineData("index.md")]
    [InlineData("index/extra")]
    public async Task UnknownSlug_404(string slug)
    {
        using var host = await StartAsync();

        var (res, body) = await GetAsync(host, "/qcli/docs/pages/" + slug);

        Assert.Equal(404, (int)res.StatusCode);
        Assert.Equal("""{"error":"page not found"}""", body);
    }

    [Fact]
    public async Task CodePages()
    {
        using var host = await StartAsync(o => o.AddPage("errors", "# Errors\nBody"));

        var pages = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages")).Body);

        var errors = pages.EnumerateArray().Single(p => p.GetProperty("slug").GetString() == "errors");
        Assert.Equal("Errors", errors.GetProperty("title").GetString());
        Assert.Equal("Guides", errors.GetProperty("group").GetString());
        Assert.Equal(0, errors.GetProperty("order").GetInt32());
        // order 0, then title: "Errors" before "Limits and quotas"
        Assert.Equal("errors", pages[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task SameOrderAndTitle_SortedBySlug()
    {
        using var host = await StartAsync(o =>
        {
            o.AddPage("b-page", "# same");
            o.AddPage("a-page", "# Same");
        }, fixtures: false);

        var pages = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages")).Body);

        Assert.Equal(["a-page", "b-page"], pages.EnumerateArray().Select(p => p.GetProperty("slug").GetString()));
    }

    public static IEnumerable<object[]> StartupErrorCases()
    {
        yield return ["slug", "invalid docs page slug: Bad Slug"];
        yield return ["file", "invalid docs page name: Read Me.md"];
        yield return ["duplicate", "duplicate docs page: index"];
        yield return ["folder", "docs folder not found: "];
        yield return ["order", "invalid order in docs page: x-page"];
        yield return ["builtin", "docs variable is built in: baseUrl"];
        yield return ["builtin-name", "docs variable is built in: name"];
        yield return ["variable", "invalid docs variable: 1x"];
        yield return ["base", "invalid docs base path: /"];
        yield return ["base-trailing", "invalid docs base path: /docs/"];
    }

    [Theory]
    [MemberData(nameof(StartupErrorCases))]
    public async Task Startup_Errors(string kind, string message)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Read Me.md"), "# Read me");
            var missing = Path.Combine(dir, "missing");
            Action<DocsOptions> configure = kind switch
            {
                "slug" => o => o.AddPage("Bad Slug", "x"),
                "file" => o => o.AddFolder(dir),
                "duplicate" => o => o.AddPage("index", "x"),
                "folder" => o => o.AddFolder(missing),
                "order" => o => o.AddPage("x-page", "---\norder: x\n---\nbody"),
                "builtin" => o => o.Variables["baseUrl"] = "x",
                "builtin-name" => o => o.Variables["name"] = "x",
                "variable" => o => o.Variables["1x"] = "x",
                "base" => o => o.BasePath = "/",
                "base-trailing" => o => o.BasePath = "/docs/",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(configure));

            Assert.Equal(kind == "folder" ? message + missing : message, ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Folder_IgnoresHiddenUnderscoredAndOtherFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Intro.md"), "# Intro");
            await File.WriteAllTextAsync(Path.Combine(dir, ".hidden.md"), "# Hidden");
            await File.WriteAllTextAsync(Path.Combine(dir, "_draft.md"), "# Draft");
            await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "text");
            await File.WriteAllTextAsync(Path.Combine(dir, "sub", "nested.md"), "# Nested");
            using var host = await StartAsync(o => o.AddFolder(dir), fixtures: false);

            var pages = JsonSerializer.Deserialize<JsonElement>((await GetAsync(host, "/qcli/docs/pages")).Body);

            Assert.Equal(["intro"], pages.EnumerateArray().Select(p => p.GetProperty("slug").GetString()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FrontMatter_Unclosed_IsBody()
    {
        var page = DocsPageLoader.Parse("draft", "---\ntitle: x\nbody");

        Assert.Equal("draft", page.Title);
        Assert.Equal("---\ntitle: x\nbody", page.Markdown);
        Assert.Equal("Guides", page.Group);
        Assert.Equal(0, page.Order);
    }

    [Fact]
    public void Parse_QuotesAndUnknownKeys()
    {
        var page = DocsPageLoader.Parse("quoted", "---\ntitle: 'A: B'\nauthor: x\ngroup: \"Basics\"\norder: -3\n---\n# Heading\n");

        Assert.Equal("A: B", page.Title);
        Assert.Equal("Basics", page.Group);
        Assert.Equal(-3, page.Order);
        Assert.Equal("# Heading\n", page.Markdown);
    }

    [Fact]
    public void Parse_MismatchedQuotesKept()
    {
        Assert.Equal("\"A'", DocsPageLoader.Parse("q", "---\ntitle: \"A'\n---\n").Title);
    }

    [Fact]
    public async Task IgnoresCredentials()
    {
        using var host = await StartAsync();

        Assert.Equal(200, (int)(await GetAsync(host, "/qcli/docs/pages", "garbage")).Res.StatusCode);
        Assert.Equal(200, (int)(await GetAsync(host, "/qcli/docs/pages/index", "qcli_pat_garbage")).Res.StatusCode);
    }

    [Fact]
    public async Task Head_AnswersWithoutBody()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, "/qcli/docs/pages/index"));

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OtherPathsAndMethods_FallThrough()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        Assert.Equal(404, (int)(await client.GetAsync("/qcli/docs")).StatusCode);
        Assert.Equal(404, (int)(await client.GetAsync("/qcli/docs/other")).StatusCode);
        Assert.Equal(404, (int)(await client.PostAsync("/qcli/docs/pages", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task CustomBasePath()
    {
        using var host = await StartAsync(o => o.BasePath = "/developers/docs");

        Assert.Equal(200, (int)(await GetAsync(host, "/developers/docs/pages")).Res.StatusCode);
        Assert.Equal(404, (int)(await GetAsync(host, "/qcli/docs/pages")).Res.StatusCode);
    }

    [Fact]
    public async Task PortalSection_Registered()
    {
        using var host = await StartAsync();

        var section = Assert.Single(host.Services.GetServices<CliPortalSection>());

        Assert.Equal(new CliPortalSection("docs", "Guides", "/qcli/docs"), section);
    }
}
