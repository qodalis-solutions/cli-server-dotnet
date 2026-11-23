using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Users.Extensions;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Dashboard extensions (cli-server-dashboard <c>docs/extensions.md</c>): <c>CliBuilder.AddDashboardExtension</c>
/// and <c>GET /api/v1/qcli/dashboard/extensions</c> (admin plugin).
/// </summary>
public class DashboardExtensionsTests
{
    private const string AdminUser = "dashadmin";
    private const string AdminPass = "dashpass";

    private static CliDashboardExtension Marketplace() => new()
    {
        Id = "marketplace",
        Title = "Marketplace",
        Titles = new Dictionary<string, string> { ["de"] = "Marktplatz", ["pt-BR"] = "Mercado" },
        Icon = "🧩",
        Group = "data",
        Order = 10,
        Permission = "marketplace:moderate",
        Entry = "/qcli/marketplace/admin/extension.js",
        Styles = ["/qcli/marketplace/admin/extension.css"],
        Version = "0.1.0",
    };

    private static CliBuilder Builder() => new(new ServiceCollection());

    [Fact]
    public void KeepsTheRegistrationOrder()
    {
        var builder = Builder()
            .AddDashboardExtension(Marketplace())
            .AddDashboardExtension(new CliDashboardExtension { Id = "billing", Title = "Billing", Entry = "https://cdn.example.com/billing.js" });
        Assert.Equal(["marketplace", "billing"], builder.DashboardExtensions.Select(e => e.Id));
    }

    [Fact]
    public void RefusesAnIdRegisteredTwice()
    {
        var builder = Builder().AddDashboardExtension(Marketplace());
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddDashboardExtension(Marketplace()));
        Assert.Equal("dashboard extension already registered: marketplace", error.Message);
    }

    [Fact]
    public void TurnsAnUnknownGroupIntoSystem()
    {
        var builder = Builder().AddDashboardExtension(new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Group = "elsewhere" });
        Assert.Equal("system", builder.DashboardExtensions[0].Group);
    }

    public static IEnumerable<object[]> Invalid() =>
    [
        [new CliDashboardExtension { Id = "Bad Id", Title = "X", Entry = "/x.js" }, "id must match"],
        [new CliDashboardExtension { Id = "-x", Title = "X", Entry = "/x.js" }, "id must match"],
        [new CliDashboardExtension { Id = new string('x', 41), Title = "X", Entry = "/x.js" }, "id must match"],
        [new CliDashboardExtension { Id = "x", Title = "", Entry = "/x.js" }, "title must be 1-40 characters"],
        [new CliDashboardExtension { Id = "x", Title = new string('y', 41), Entry = "/x.js" }, "title must be 1-40 characters"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "//evil.example/x.js" }, "entry must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "javascript:alert(1)" }, "entry must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "x.js" }, "entry must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x\t.js" }, "entry must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Titles = new Dictionary<string, string> { ["not a code"] = "X" } }, "titles must map"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Icon = "<img>" }, "icon must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Icon = "ab" }, "icon must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Permission = "" }, "permission must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Styles = ["/a.css", "/b.css", "/c.css", "/d.css", "/e.css", "/f.css"] }, "styles must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Styles = ["//evil/a.css"] }, "styles must be"],
        [new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Version = "" }, "version must be"],
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RefusesAnInvalidExtension(CliDashboardExtension extension, string reason)
    {
        var error = Assert.Throws<ArgumentException>(() => Builder().AddDashboardExtension(extension));
        Assert.StartsWith("invalid dashboard extension: ", error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData("🧩")]
    [InlineData("🇷🇴")]
    [InlineData("👨‍👩‍👧")]
    [InlineData("/icons/billing.svg")]
    [InlineData("https://cdn.example.com/i.png")]
    public void AcceptsEmojiAndImageUrlsAsIcons(string icon)
    {
        var builder = Builder().AddDashboardExtension(new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Icon = icon });
        Assert.Equal(icon, builder.DashboardExtensions[0].Icon);
    }

    private static async Task<IHost> StartAsync(bool users, Action<CliBuilder> configure)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddCors();
                    services
                        .AddControllers()
                        .AddCli(cli =>
                        {
                            if (users) cli.AddUsers(o => o.DefaultAdminPassword = "root-pass-123");
                            cli.AddAdmin(o =>
                            {
                                o.Username = AdminUser;
                                o.Password = AdminPass;
                                o.JwtSecret = "dashboard-extensions-test-secret-long-enough-for-hmac";
                            });
                            configure(cli);
                        })
                        .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    if (users) app.UseQodalisUsers();
                    app.UseQodalisAdmin();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                    app.UseCli();
                });
            });
        return await builder.StartAsync();
    }

    private static async Task<string> TokenAsync(HttpClient client, string path, object body)
    {
        var res = await client.PostAsJsonAsync(path, body);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task ListsTheExtensionsInRegistrationOrder()
    {
        using var host = await StartAsync(false, cli => cli
            .AddDashboardExtension(Marketplace())
            .AddDashboardExtension(new CliDashboardExtension { Id = "billing", Title = "Billing", Entry = "/billing/admin.js" }));
        var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/qcli/dashboard/extensions")).StatusCode);

        var token = await TokenAsync(client, "/api/v1/qcli/auth/login", new { username = AdminUser, password = AdminPass });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/qcli/dashboard/extensions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("extensions");
        Assert.Equal(2, json.GetArrayLength());
        var first = json[0];
        Assert.Equal("marketplace", first.GetProperty("id").GetString());
        Assert.Equal("Marktplatz", first.GetProperty("titles").GetProperty("de").GetString());
        Assert.Equal("data", first.GetProperty("group").GetString());
        Assert.Equal(10, first.GetProperty("order").GetInt32());
        Assert.Equal("marketplace:moderate", first.GetProperty("permission").GetString());
        Assert.Equal("/qcli/marketplace/admin/extension.css", first.GetProperty("styles")[0].GetString());
        // Optional fields that are not set are left out
        var billing = json[1];
        Assert.Equal(["id", "title", "entry"], billing.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task AnswersAnEmptyListWithoutExtensions()
    {
        using var host = await StartAsync(false, _ => { });
        var client = host.GetTestClient();
        var token = await TokenAsync(client, "/api/v1/qcli/auth/login", new { username = AdminUser, password = AdminPass });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await client.GetStringAsync("/api/v1/qcli/dashboard/extensions");
        Assert.Equal("{\"extensions\":[]}", body);
    }

    [Fact]
    public async Task NeedsASignInOnAProtectedServer()
    {
        using var host = await StartAsync(true, cli => cli.AddDashboardExtension(Marketplace()));
        var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/qcli/dashboard/extensions")).StatusCode);

        var token = await TokenAsync(client, "/api/v1/qcli/sessions", new { username = "root", password = "root-pass-123" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/qcli/dashboard/extensions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ids = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("extensions")
            .EnumerateArray().Select(e => e.GetProperty("id").GetString());
        Assert.Equal(["marketplace"], ids);
    }
}
