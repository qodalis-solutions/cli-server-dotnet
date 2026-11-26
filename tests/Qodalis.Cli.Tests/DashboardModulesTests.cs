using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Audit.Extensions;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Users.Extensions;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Dashboard modules (<c>CliBuilder.AddDashboardModule</c>): the dashboard's plugin pages, embedded in the packages
/// that own their APIs, served outside the API and registered as extensions with their files' hash.
/// </summary>
public sealed class DashboardModulesTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs) Directory.Delete(dir, true);
    }

    /// <summary>A module build in a temp folder (what the packages embed under <c>wwwroot/&lt;id&gt;</c>).</summary>
    private PhysicalFileProvider Build(string js = "export default {};", string? css = ".x{}")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-module-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "extension.js"), js);
        if (css != null) File.WriteAllText(Path.Combine(dir, "extension.css"), css);
        return new PhysicalFileProvider(dir);
    }

    private static readonly CliDashboardModule Billing = new()
    {
        Id = "billing",
        Title = "Billing",
        Icon = "💳",
        Group = "data",
        Order = 5,
        Permission = "billing:read",
        Widgets = ["overview"],
        Search = "/?q=",
    };

    [Fact]
    public void RegistersTheModuleWithHashedUrlsUnderItsPrefix()
    {
        var builder = new CliBuilder(new ServiceCollection()).AddDashboardModule(Billing, Build());
        var extension = Assert.Single(builder.DashboardExtensions);
        Assert.Equal("billing", extension.Id);
        Assert.Matches(@"^/qcli/modules/billing/extension\.js\?v=[0-9a-f]{12}$", extension.Entry);
        Assert.Matches(@"^/qcli/modules/billing/extension\.css\?v=[0-9a-f]{12}$", Assert.Single(extension.Styles!));
        Assert.Equal(["overview"], extension.Widgets);
        Assert.Equal("/?q=", extension.Search);
        Assert.Equal("billing:read", extension.Permission);

        // Another build, another hash; no stylesheet, no styles
        var moved = new CliDashboardModule { Id = Billing.Id, Title = Billing.Title, Prefix = "/billing/admin/" };
        var other = new CliBuilder(new ServiceCollection()).AddDashboardModule(moved, Build("export default 1;", css: null));
        Assert.StartsWith("/billing/admin/extension.js?v=", other.DashboardExtensions[0].Entry);
        Assert.NotEqual(extension.Entry[^12..], other.DashboardExtensions[0].Entry[^12..]);
        Assert.Null(other.DashboardExtensions[0].Styles);
    }

    [Fact]
    public void ThePackageAndVersionOfABuild_ComeFromItsBanner_OrItsPackageJson()
    {
        var builder = new CliBuilder(new ServiceCollection())
            .AddDashboardModule(Billing, Build("/*! @qodalis/cli-dashboard-module-billing 1.0.0-beta.29 */\nexport default {};"));
        var extension = Assert.Single(builder.DashboardExtensions);
        Assert.Equal(("@qodalis/cli-dashboard-module-billing", "1.0.0-beta.29"), (extension.Package, extension.Version));
        Assert.Contains("\"package\":\"@qodalis/cli-dashboard-module-billing\"", JsonSerializer.Serialize(extension));

        var npm = Build();
        File.WriteAllText(Path.Combine(npm.Root, "package.json"), "{\"name\":\"@qodalis/cli-dashboard-module-audit\",\"version\":\"1.0.0-beta.28\"}");
        Assert.Equal(("@qodalis/cli-dashboard-module-audit", "1.0.0-beta.28"), CliBuilder.DashboardModuleIdentity(npm));
        Assert.Null(CliBuilder.DashboardModuleIdentity(Build()));
        var invalid = new CliDashboardExtension { Id = "x", Title = "X", Entry = "/x.js", Package = "Not A Package" };
        Assert.Contains("package must be an npm package name", Assert.Throws<ArgumentException>(() => invalid.Validate()).Message);
    }

    [Fact]
    public void LeavesThePageOutWithoutABuild_WithoutCliRoutes_AndRegistersItOnce()
    {
        Assert.Empty(new CliBuilder(new ServiceCollection()).AddDashboardModule(Billing, (IFileProvider?)null).DashboardExtensions);
        Assert.Empty(new CliBuilder(new ServiceCollection()).AddDashboardModule(Billing, new NullFileProvider()).DashboardExtensions);
        Assert.Empty(new CliBuilder(new ServiceCollection()).WithoutCliRoutes().AddDashboardModule(Billing, Build()).DashboardExtensions);
        // An assembly without an embedded build
        Assert.Empty(new CliBuilder(new ServiceCollection()).AddDashboardModule(Billing, typeof(DashboardModulesTests).Assembly).DashboardExtensions);

        var builder = new CliBuilder(new ServiceCollection()).AddDashboardModule(Billing, Build()).AddDashboardModule(Billing, Build());
        Assert.Single(builder.DashboardExtensions);
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IStartupFilter));
    }

    [Fact]
    public void ConfigureDashboardModule_ReplacesOrRemovesTheBuild_BeforeOrAfter()
    {
        var replacement = Build("export default 'other';");
        var before = new CliBuilder(new ServiceCollection())
            .ConfigureDashboardModule("billing", replacement)
            .AddDashboardModule(Billing, Build());
        var expected = CliBuilder.DashboardModuleExtension(Billing, replacement).Entry;
        Assert.Equal(expected, Assert.Single(before.DashboardExtensions).Entry);

        var after = new CliBuilder(new ServiceCollection())
            .AddDashboardExtension(new CliDashboardExtension { Id = "first", Title = "First", Entry = "/first.js" })
            .AddDashboardModule(Billing, Build())
            .AddDashboardExtension(new CliDashboardExtension { Id = "last", Title = "Last", Entry = "/last.js" })
            .ConfigureDashboardModule("billing", replacement);
        Assert.Equal(["first", "billing", "last"], after.DashboardExtensions.Select(e => e.Id));
        Assert.Equal(expected, after.DashboardExtensions[1].Entry);

        after.ConfigureDashboardModule("billing", null);
        Assert.Equal(["first", "last"], after.DashboardExtensions.Select(e => e.Id));
        Assert.Empty(new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("billing", null).AddDashboardModule(Billing, Build()).DashboardExtensions);
    }

    [Fact]
    public void ValidatesWidgetsAndSearch()
    {
        var builder = new CliBuilder(new ServiceCollection());
        Assert.Contains("widgets must be among overview, palette",
            Assert.Throws<ArgumentException>(() => builder.AddDashboardExtension(new CliDashboardExtension { Id = "a", Title = "A", Entry = "/a.js", Widgets = ["sidebar"] })).Message);
        Assert.Contains("search must be a path",
            Assert.Throws<ArgumentException>(() => builder.AddDashboardExtension(new CliDashboardExtension { Id = "a", Title = "A", Entry = "/a.js", Search = "?q=" })).Message);
        builder.AddDashboardExtension(new CliDashboardExtension { Id = "b", Title = "B", Entry = "/b.js", Widgets = [], Search = "/search?text=" });
        Assert.Null(builder.DashboardExtensions[0].Widgets);
        Assert.Equal("/search?text=", builder.DashboardExtensions[0].Search);
    }

    public static IEnumerable<object?[]> Contract() =>
    [
        [CliDashboardModules.Jobs, "jobs", "Jobs", "⏱", "operations", 10, "jobs:read", new[] { "overview", "palette" }, "/?q=", "Joburi"],
        [CliDashboardModules.Audit, "audit", "Audit", "🛡", "operations", 30, "audit:read", null, "/?q=", "Audit"],
        [CliDashboardModules.FileSystem, "filesystem", "Filesystem", "📁", "data", 10, "files:read", null, null, "Fișiere"],
        [CliDashboardModules.DataExplorer, "data-explorer", "Data explorer", "⛁", "data", 15, "data:query", null, null, "Explorator de date"],
        [CliDashboardModules.Tokens, "tokens", "API tokens", "🔑", "access", 10, null, null, null, "Tokenuri API"],
        [CliDashboardModules.Users, "users", "Users", "👥", "access", 5, "users:read", null, "/?q=", "Utilizatori"],
        [CliDashboardModules.Account, "account", "Account", "👤", "account", 0, null, null, null, "Cont"],
    ];

    [Theory]
    [MemberData(nameof(Contract))]
    public void TheBuiltInModulesMatchTheContract(CliDashboardModule module, string id, string title, string icon, string group, int order,
        string? permission, string[]? widgets, string? search, string romanian)
    {
        Assert.Equal(id, module.Id);
        Assert.Equal(title, module.Title);
        Assert.Equal(icon, module.Icon);
        Assert.Equal(group, module.Group);
        Assert.Equal(order, module.Order);
        Assert.Equal(permission, module.Permission);
        Assert.Equal(widgets, module.Widgets?.ToArray());
        Assert.Equal(search, module.Search);
        Assert.Equal($"/qcli/modules/{id}", module.ResolvedPrefix);
        Assert.Equal(["de", "es", "fr", "it", "ja", "ko", "pt", "ro", "ru", "zh"], module.Titles!.Keys.Order());
        Assert.Equal(romanian, module.Titles!["ro"]);
        // A valid extension
        new CliBuilder(new ServiceCollection()).AddDashboardModule(module, Build());
    }

    [Fact]
    public void ThePluginsRegisterTheirPages_Once()
    {
        var builder = new CliBuilder(new ServiceCollection());
        foreach (var id in new[] { "jobs", "audit", "filesystem", "data-explorer", "users", "tokens" }) builder.ConfigureDashboardModule(id, Build());
        builder
            .AddJob(new NoopJob())
            .AddJob(new NoopJob(), o => o.Name = "second")
            .AddAudit()
            .AddFileSystem()
            .AddDataExplorerProvider(new FakeDataExplorerProvider(), o => o.Name = "one")
            .AddDataExplorerProvider(new FakeDataExplorerProvider(), o => o.Name = "two")
            .AddUsers();
        Assert.Equal(["jobs", "audit", "filesystem", "data-explorer", "users", "account", "tokens"], builder.DashboardExtensions.Select(e => e.Id));
    }

    [Fact]
    public void TheAccountPageUsesTheUsersBuild_AndFollowsItsOverride()
    {
        Assert.Equal("users", CliDashboardModules.Account.BuildId);
        Assert.Equal("users", CliDashboardModules.Users.BuildId);
        Assert.Equal("account", CliBuilder.DashboardModuleExtension(CliDashboardModules.Account, Build()).Validate().Group);

        // Before the plugin adds the pages: both from the users build, each at its own prefix
        var users = Build("export default 'users';");
        var before = new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("users", users).ConfigureDashboardModule("tokens", null).ConfigureDashboardModule("audit", null).AddUsers();
        Assert.Equal(["users", "account"], before.DashboardExtensions.Select(e => e.Id));
        Assert.Matches(@"^/qcli/modules/users/extension\.js\?v=[0-9a-f]{12}$", before.DashboardExtensions[0].Entry);
        Assert.Matches(@"^/qcli/modules/account/extension\.js\?v=[0-9a-f]{12}$", before.DashboardExtensions[1].Entry);
        Assert.Equal(before.DashboardExtensions[0].Entry[^12..], before.DashboardExtensions[1].Entry[^12..]);
        Assert.Null(before.DashboardExtensions[1].Permission);

        // After: replacing or removing the users build applies to the account page too
        var after = new CliBuilder(new ServiceCollection()).AddDashboardModule(CliDashboardModules.Users, Build()).AddDashboardModule(CliDashboardModules.Account, Build());
        after.ConfigureDashboardModule("users", users);
        Assert.Equal(CliBuilder.DashboardModuleExtension(CliDashboardModules.Account, users).Entry, after.DashboardExtensions[1].Entry);
        after.ConfigureDashboardModule("users", null);
        Assert.Empty(after.DashboardExtensions);
        // Its own override wins
        var own = new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("account", null).ConfigureDashboardModule("users", users).ConfigureDashboardModule("tokens", null).ConfigureDashboardModule("audit", null).AddUsers();
        Assert.Equal(["users"], own.DashboardExtensions.Select(e => e.Id));
        Assert.Empty(new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("users", null).ConfigureDashboardModule("tokens", null).ConfigureDashboardModule("audit", null).AddUsers().DashboardExtensions);
        // The host application signs users in: the pages stay (the Account page hides the password change)
        Assert.Equal(["users", "account"], new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("users", users).ConfigureDashboardModule("tokens", null).ConfigureDashboardModule("audit", null)
            .AddUsers(o => o.UseExternalAuth(_ => null)).DashboardExtensions.Select(e => e.Id));
        // Guard-only: no pages
        Assert.DoesNotContain(new CliBuilder(new ServiceCollection()).ConfigureDashboardModule("users", users).AddUsers(o => o.GuardOnly = true).DashboardExtensions,
            e => e.Id is "users" or "account" or "tokens");
    }

    private sealed class NoopJob : ICliJob
    {
        public Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ServesThePagesToAnyoneOnAProtectedServer_AndListsWidgetsAndSearch()
    {
        var jobs = Build("export default 'jobs';");
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddCors();
                    services.AddControllers()
                        .AddCli(cli =>
                        {
                            cli.ConfigureDashboardModule("jobs", jobs).ConfigureDashboardModule("tokens", Build("export default 'tokens';"))
                                .ConfigureDashboardModule("users", Build("export default 'users';"));
                            cli.AddUsers(o => { o.DefaultAdminPassword = "root-pass-123"; o.ProtectServer = true; });
                            cli.AddAdmin(o => o.JwtSecret = "dashboard-modules-test-secret-long-enough-for-hmac");
                            cli.AddJob(new NoopJob());
                        })
                        .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    app.UseQodalisUsers();
                    app.UseQodalisAdmin();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                    app.UseCli();
                });
            })
            .StartAsync();
        var client = host.GetTestClient();

        var js = await client.GetAsync("/qcli/modules/jobs/extension.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("no-cache", js.Headers.CacheControl?.ToString());
        Assert.Equal("export default 'jobs';", await js.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/qcli/modules/tokens/extension.css")).StatusCode);
        // One build, two prefixes
        Assert.Equal("export default 'users';", await client.GetStringAsync("/qcli/modules/users/extension.js"));
        Assert.Equal("export default 'users';", await client.GetStringAsync("/qcli/modules/account/extension.js"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/qcli/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/qcli/dashboard/extensions")).StatusCode);

        var login = await client.PostAsJsonAsync("/api/v1/qcli/sessions", new { username = "root", password = "root-pass-123" });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/qcli/dashboard/extensions")).RootElement.GetProperty("extensions");
        var byId = list.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(["overview", "palette"], byId["jobs"].GetProperty("widgets").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal("/?q=", byId["jobs"].GetProperty("search").GetString());
        Assert.Equal("Joburi", byId["jobs"].GetProperty("titles").GetProperty("ro").GetString());
        Assert.False(byId["tokens"].TryGetProperty("widgets", out _));
        Assert.False(byId["tokens"].TryGetProperty("search", out _));
        Assert.False(byId["tokens"].TryGetProperty("permission", out _));
        Assert.Equal("users:read", byId["users"].GetProperty("permission").GetString());
        Assert.Equal("account", byId["account"].GetProperty("group").GetString());
        Assert.False(byId["account"].TryGetProperty("permission", out _));
    }
}
