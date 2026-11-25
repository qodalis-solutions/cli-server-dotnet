using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Portal;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The portal plugin's <c>portal</c> config provider and <c>portal</c> command: the options' branding as defaults,
/// stored values overriding them live in /portal.json, validation errors, the read-only site section; the command's
/// text (site found or missing, with and without sections), --json, and config:read on a protected server.
/// </summary>
public sealed class PortalCommandTests
{
    private static readonly string Site = Path.Combine(AppContext.BaseDirectory, "fixtures", "portal-site");
    private const string Hint = "Change the branding with config set portal.branding.<key> <value> (name, logoUrl, themeMode, footerLinks).";
    private static readonly CliConfigContext All = ConfigTestHost.All;

    /// <summary>A server with the portal (stand-in site), optionally the docs and public-api sections.</summary>
    private static async Task<IHost> StartAsync(Action<PortalOptions>? configure = null, bool sections = true, bool named = true,
        string? sitePath = null, ICliConfigStore? store = null, Action<IServiceCollection>? services = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s =>
            {
                services?.Invoke(s);
                s.AddControllers().AddCli(cli =>
                {
                    if (store != null) cli.SetConfigStore(store);
                    if (sections)
                    {
                        cli.AddDocs(o => o.AddFolder(DocsPluginTests.Fixtures));
                        // the public-api plugin's section (the plugin itself needs the users plugin; portal.json only reads its section)
                        cli.Services.AddSingleton(new CliPortalSection(CliPortalSection.Api, "API reference", "/qcli/api"));
                    }
                    cli.AddPortal(o =>
                    {
                        o.SitePath = sitePath ?? Site;
                        if (named)
                        {
                            o.Name = "RateBell API";
                            o.FooterLinks.Add(new PortalLink("Terms", "/terms"));
                        }
                        configure?.Invoke(o);
                    });
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                if (sections) app.UseQodalisDocs();
                app.UseQodalisPortal();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        var host = await builder.StartAsync();
        await host.Services.GetRequiredService<CliConfigService>().ReadyAsync();
        return host;
    }

    private static async Task<JsonElement> PortalJson(IHost host, string path = "/qcli/portal")
    {
        var res = await host.GetTestClient().GetAsync($"{path}/portal.json");
        Assert.Equal(200, (int)res.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
    }

    private static CliConfigService Config(IHost host) => host.Services.GetRequiredService<CliConfigService>();

    private static async Task<CliConfigSectionView> SectionAsync(IHost host, string id) =>
        (await Config(host).GetAsync("portal", All)).Sections.Single(s => s.Id == id);

    private static CliConfigEntryView Entry(CliConfigSectionView section, string key) => section.Entries.Single(e => e.Key == key);

    private static string? Json(JsonNode? node) => node?.ToJsonString();

    private static async Task<(int Status, CliConfigUpdateResponse? Body, IReadOnlyDictionary<string, string>? Errors, string? Error)> PatchAsync(
        IHost host, string changes, string section = "branding")
    {
        try
        {
            var body = await Config(host).UpdateAsync("portal", section, new CliConfigUpdateRequest { Changes = ConfigStoreContractTests.C(changes) }, All);
            return (200, body, null, null);
        }
        catch (CliConfigException ex)
        {
            return (ex.Status, null, ex.Errors, ex.Message);
        }
    }

    private static async Task OkAsync(IHost host, string changes)
    {
        var res = await PatchAsync(host, changes);
        Assert.Null(res.Errors);
        Assert.Equal(200, res.Status);
    }

    private static async Task<string> ErrorsAsync(IHost host, string changes)
    {
        var res = await PatchAsync(host, changes);
        Assert.Equal(400, res.Status);
        return JsonSerializer.Serialize(res.Errors);
    }

    private static Task<(int Status, string Text, JsonElement Body)> Run(IHost host, string line, string? token = null) =>
        ConfigCommands.RunAsync(host.GetTestClient(), host.Services.GetRequiredService<ICliCommandRegistry>(), line, token);

    private static async Task<string> Text(IHost host, string line) => (await Run(host, line)).Text;

    private static int PageCount(IHost host) => host.Services.GetRequiredService<DocsPages>().All.Count;

    // ------------------------------------------------------------------ the config provider

    [Fact]
    public async Task Branding_TheOptionsAreTheDefaults()
    {
        using var host = await StartAsync(o =>
        {
            o.LogoUrl = "/logo.svg";
            o.Theme.Mode = PortalThemeMode.Dark;
        });
        var branding = await SectionAsync(host, "branding");
        Assert.Equal(
            [
                "name string \"RateBell API\" \"RateBell API\" default True",
                "logoUrl string \"/logo.svg\" \"/logo.svg\" default True",
                "themeMode enum \"dark\" \"dark\" default True",
                "footerLinks json [{\"label\":\"Terms\",\"url\":\"/terms\"}] [{\"label\":\"Terms\",\"url\":\"/terms\"}] default True",
            ],
            branding.Entries.Select(e => $"{e.Key} {e.Type} {Json(e.Value)} {Json(e.Default)} {e.Source} {e.Mutable}"));
        Assert.Equal(["system", "light", "dark"], Entry(branding, "themeMode").Validation!.Options!);
        Assert.Equal("0", branding.Version);
    }

    [Fact]
    public async Task Branding_UnsetOptions_DefaultToDocumentationNoLogoSystemNoLinks()
    {
        using var host = await StartAsync(sections: false, named: false);
        var branding = await SectionAsync(host, "branding");
        Assert.Equal(["\"Documentation\"", null, "\"system\"", "[]"], branding.Entries.Select(e => Json(e.Value)));
    }

    [Fact]
    public async Task Site_PathSiteFoundAndSections_ReadOnly()
    {
        using var host = await StartAsync();
        var site = await SectionAsync(host, "site");
        Assert.Equal(["path \"/qcli/portal\" False", "siteFound true False", "sections [\"docs\",\"api\"] False"],
            site.Entries.Select(e => $"{e.Key} {Json(e.Value)} {e.Mutable}"));
        Assert.Null(site.Version);
        var res = await PatchAsync(host, "{\"path\":\"/x\"}", "site");
        Assert.Equal((400, "config section portal.site is read-only"), (res.Status, res.Error));
    }

    [Fact]
    public async Task StoredValues_OverrideTheDefaults_AndApplyLiveToPortalJson()
    {
        using var host = await StartAsync(o => o.Theme.Light.Primary = "#0a7");
        Assert.Equal("RateBell API", (await PortalJson(host)).GetProperty("name").GetString());
        var res = await PatchAsync(host,
            "{\"name\":\"Acme Docs\",\"logoUrl\":\"https://cdn.example.com/logo.png\",\"themeMode\":\"dark\"," +
            "\"footerLinks\":[{\"label\":\"Privacy\",\"url\":\"https://example.com/privacy\"},{\"label\":\"Status\",\"url\":\"/status\"}]}");
        Assert.Equal(200, res.Status);
        Assert.Equal(["name", "logoUrl", "themeMode", "footerLinks"], res.Body!.Applied);
        Assert.False(res.Body.RequiresRestart);
        var json = await PortalJson(host);
        Assert.Equal(
            "{\"name\":\"Acme Docs\",\"logo\":\"https://cdn.example.com/logo.png\",\"theme\":{\"mode\":\"dark\",\"dark\":{},\"light\":{\"primary\":\"#0a7\"}}," +
            "\"footerLinks\":[{\"label\":\"Privacy\",\"url\":\"https://example.com/privacy\"},{\"label\":\"Status\",\"url\":\"/status\"}]," +
            "\"docs\":{\"title\":\"Guides\",\"pages\":\"/qcli/docs/pages\"}," +
            "\"api\":{\"title\":\"API reference\",\"openapi\":\"/qcli/api/openapi.json\",\"swagger\":\"/qcli/api/swagger\"}}",
            json.GetRawText());
        var name = Entry(await SectionAsync(host, "branding"), "name");
        Assert.Equal(("\"Acme Docs\"", "\"RateBell API\"", "runtime"), (Json(name.Value), Json(name.Default), name.Source));
    }

    [Fact]
    public async Task ValuesAlreadyInTheStore_OverrideTheDefaultsFromStartup()
    {
        var store = new InMemoryConfigStore();
        await store.WriteAsync("portal", "branding",
            ConfigStoreContractTests.C("{\"name\":\"Stored Name\",\"themeMode\":\"light\",\"footerLinks\":[{\"label\":\"Blog\",\"url\":\"/blog\"}]}"));
        using var host = await StartAsync(sections: false, store: store);
        var json = await PortalJson(host);
        Assert.Equal(("Stored Name", "light", "[{\"label\":\"Blog\",\"url\":\"/blog\"}]"),
            (json.GetProperty("name").GetString(), json.GetProperty("theme").GetProperty("mode").GetString(), json.GetProperty("footerLinks").GetRawText()));
        var name = Entry(await SectionAsync(host, "branding"), "name");
        Assert.Equal(("\"Stored Name\"", "\"RateBell API\""), (Json(name.Value), Json(name.Default)));
    }

    [Fact]
    public async Task EmptyLogoUrl_RemovesTheLogo_AResetBringsTheOptionsBack()
    {
        using var host = await StartAsync(o => o.LogoUrl = "/logo.svg");
        await OkAsync(host, "{\"logoUrl\":\"\"}");
        Assert.Equal(JsonValueKind.Null, (await PortalJson(host)).GetProperty("logo").ValueKind);
        await OkAsync(host, "{\"logoUrl\":\"data:image/png;base64,iVBORw0KGgo=\"}");
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", (await PortalJson(host)).GetProperty("logo").GetString());
        await OkAsync(host, "{\"logoUrl\":null,\"name\":\"Other\",\"footerLinks\":[]}");
        Assert.Equal("[]", (await PortalJson(host)).GetProperty("footerLinks").GetRawText());
        await OkAsync(host, "{\"name\":null,\"footerLinks\":null}");
        var json = await PortalJson(host);
        Assert.Equal(("RateBell API", "/logo.svg", "[{\"label\":\"Terms\",\"url\":\"/terms\"}]"),
            (json.GetProperty("name").GetString(), json.GetProperty("logo").GetString(), json.GetProperty("footerLinks").GetRawText()));
    }

    [Fact]
    public async Task ConfigSet_ChangesTheSiteLive()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();
        Assert.Equal("Updated portal.branding.name = Acme Docs (version 1)",
            (await ConfigCommands.RunAsync(client, "config set portal.branding.name \"Acme Docs\"")).Text);
        Assert.Equal("Updated portal.branding.themeMode = light (version 2)",
            (await ConfigCommands.RunAsync(client, "config set portal.branding.themeMode light")).Text);
        Assert.StartsWith("Updated portal.branding.footerLinks = ",
            (await ConfigCommands.RunAsync(client, "config set portal.branding.footerLinks '[{\"label\":\"Blog\",\"url\":\"https://example.com/blog\"}]'")).Text);
        var json = await PortalJson(host);
        Assert.Equal(("Acme Docs", "light", "[{\"label\":\"Blog\",\"url\":\"https://example.com/blog\"}]"),
            (json.GetProperty("name").GetString(), json.GetProperty("theme").GetProperty("mode").GetString(), json.GetProperty("footerLinks").GetRawText()));
    }

    [Fact]
    public async Task Validation_PerKeyErrors_NothingApplied()
    {
        using var host = await StartAsync();
        var tooMany = new JsonArray(Enumerable.Range(0, 21).Select(i => (JsonNode)new JsonObject { ["label"] = $"L{i}", ["url"] = "/x" }).ToArray());
        Assert.Equal("{\"name\":\"must be at least 1 characters\"}", await ErrorsAsync(host, "{\"name\":\"\"}"));
        Assert.Equal("{\"name\":\"must be at most 80 characters\"}", await ErrorsAsync(host, $"{{\"name\":\"{new string('x', 81)}\"}}"));
        Assert.Equal("{\"name\":\"portal name must be 1-80 characters without control characters\"}", await ErrorsAsync(host, "{\"name\":\"a\\nb\"}"));
        const string logoError = "{\"logoUrl\":\"portal logo must be an http(s) URL, a root-relative path or a data:image URL\"}";
        Assert.Equal(logoError, await ErrorsAsync(host, "{\"logoUrl\":\"javascript:alert(1)\"}"));
        Assert.Equal(logoError, await ErrorsAsync(host, "{\"logoUrl\":\"//evil.example/logo.png\"}"));
        Assert.Equal("{\"themeMode\":\"must be one of: system, light, dark\"}", await ErrorsAsync(host, "{\"themeMode\":\"blue\"}"));
        var shape = JsonSerializer.Serialize(new Dictionary<string, string> { ["footerLinks"] = "must be a list of { \"label\": ..., \"url\": ... } links" });
        Assert.Equal(shape, await ErrorsAsync(host, "{\"footerLinks\":{\"label\":\"x\",\"url\":\"/x\"}}"));
        Assert.Equal(shape, await ErrorsAsync(host, "{\"footerLinks\":[\"x\"]}"));
        Assert.Equal("{\"footerLinks\":\"must have at most 20 links\"}",
            await ErrorsAsync(host, new JsonObject { ["footerLinks"] = tooMany }.ToJsonString()));
        Assert.Equal("{\"footerLinks\":\"invalid portal footer link: Bad\"}", await ErrorsAsync(host, "{\"footerLinks\":[{\"label\":\"Bad\",\"url\":\"ftp://x\"}]}"));
        Assert.Equal("{\"footerLinks\":\"invalid portal footer link: \"}", await ErrorsAsync(host, "{\"footerLinks\":[{\"label\":\"\",\"url\":\"/x\"}]}"));
        Assert.Equal("Error: validation failed\n  themeMode: must be one of: system, light, dark",
            (await ConfigCommands.RunAsync(host.GetTestClient(), "config set portal.branding.themeMode blue")).Text);
        Assert.Equal("RateBell API", (await PortalJson(host)).GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------ the command

    [Fact]
    public async Task Command_RegisteredUnderThePortalModuleWithStatus_NeedingConfigRead()
    {
        using var host = await StartAsync();
        var processor = host.Services.GetRequiredService<ICliCommandRegistry>().FindProcessor("portal")!;
        Assert.IsType<CliPortalCommandProcessor>(processor);
        Assert.Equal(["config:read"], processor.RequiredPermissions!);
        Assert.Equal(["status"], processor.Processors!.Select(p => p.Command));
        Assert.Equal("portal", processor.Module);
        Assert.Contains("config set portal.branding.<key>", processor.Description);
    }

    [Fact]
    public async Task PortalAndPortalStatus_PathSiteSectionsWithThePageCountAndTheApi_Branding()
    {
        using var host = await StartAsync(o => o.LogoUrl = "/logo.svg");
        var pages = PageCount(host);
        var expected = string.Join('\n',
            "Portal: /qcli/portal",
            $"Site: found, SitePath ({Site})",
            "Sections:",
            $"  docs  Guides, {pages} pages (/qcli/docs/pages)",
            "  api   API reference (/qcli/api/openapi.json)",
            "Branding:",
            "  name         RateBell API",
            "  logoUrl      /logo.svg",
            "  themeMode    system",
            "  footerLinks  Terms (/terms)",
            "",
            Hint);
        Assert.Equal(expected, await Text(host, "portal"));
        Assert.Equal(expected, await Text(host, "portal status"));
    }

    [Fact]
    public async Task ShowsTheCurrentBrandingAfterConfigSet()
    {
        using var host = await StartAsync();
        await ConfigCommands.RunAsync(host.GetTestClient(), "config set portal.branding.name Acme");
        await ConfigCommands.RunAsync(host.GetTestClient(), "config set portal.branding.logoUrl data:image/png;base64,iVBORw0KGgo=");
        var text = await Text(host, "portal");
        Assert.Contains("  name         Acme\n", text);
        Assert.Contains("  logoUrl      data:image/png;base64,… (34 characters)\n", text);
    }

    [Fact]
    public async Task WithoutSectionsOrFooterLinks()
    {
        using var host = await StartAsync(o => o.Path = "/help", sections: false, named: false);
        Assert.Equal(string.Join('\n',
                "Portal: /help",
                $"Site: found, SitePath ({Site})",
                "Sections: none (the site shows \"Nothing published yet\")",
                "Branding:",
                "  name         Documentation",
                "  logoUrl      (none)",
                "  themeMode    system",
                "  footerLinks  (none)",
                "",
                Hint),
            await Text(host, "portal"));
    }

    [Fact]
    public async Task SiteMissing_ThePortalIsNotServed_TheProviderShowsSiteFoundFalse()
    {
        var logs = new CapturingLoggerProvider();
        using var host = await StartAsync(sitePath: Path.Combine(AppContext.BaseDirectory, "fixtures", "no-such-site"),
            services: s => s.AddSingleton<ILoggerProvider>(logs));
        Assert.Contains("[qcli] portal site not found; the portal is not served", logs.Messages);
        var text = await Text(host, "portal");
        Assert.Equal(["Portal: /qcli/portal", "Site: not found, the portal is not served (install @qodalis/cli-server-portal or set SitePath)"],
            text.Split('\n').Take(2));
        Assert.Contains($"  docs  Guides, {PageCount(host)} pages (/qcli/docs/pages)", text);
        Assert.Equal("false", Json(Entry(await SectionAsync(host, "site"), "siteFound").Value));
        var json = JsonSerializer.Deserialize<JsonElement>(await Text(host, "portal --json"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"path\":\"/qcli/portal\",\"found\":false,\"source\":null,\"dir\":null}"),
            JsonNode.Parse(json.GetProperty("site").GetRawText())));
    }

    [Fact]
    public async Task Json_PortalJsonWithThePageCount_AndTheSite()
    {
        using var host = await StartAsync();
        await ConfigCommands.RunAsync(host.GetTestClient(), "config set portal.branding.themeMode dark");
        var text = await Text(host, "portal --json");
        var json = JsonNode.Parse(text)!.AsObject();
        var expected = new JsonObject
        {
            ["name"] = "RateBell API",
            ["logo"] = null,
            ["theme"] = new JsonObject { ["mode"] = "dark", ["dark"] = new JsonObject(), ["light"] = new JsonObject() },
            ["footerLinks"] = new JsonArray(new JsonObject { ["label"] = "Terms", ["url"] = "/terms" }),
            ["docs"] = new JsonObject { ["title"] = "Guides", ["pages"] = "/qcli/docs/pages", ["pageCount"] = PageCount(host) },
            ["api"] = new JsonObject { ["title"] = "API reference", ["openapi"] = "/qcli/api/openapi.json", ["swagger"] = "/qcli/api/swagger" },
            ["site"] = new JsonObject { ["path"] = "/qcli/portal", ["found"] = true, ["source"] = "siteDir", ["dir"] = Site },
        };
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse(await Text(host, "portal status --json"))));
        // the same as /portal.json, but for pageCount and site
        json.Remove("site");
        json["docs"]!.AsObject().Remove("pageCount");
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse((await PortalJson(host)).GetRawText())));
    }
}

/// <summary>The <c>portal</c> command on a server protected by the users plugin.</summary>
public sealed class PortalCommandProtectedTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!, _runner = null!, _reader = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli => cli.AddPortal(o =>
            {
                o.SitePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "portal-site");
                o.Name = "RateBell API";
            }),
            configureApp: app => app.UseQodalisPortal());
        _root = await _h.RootTokenAsync();
        _runner = await _h.UserWithPermissionsAsync(_root, "runner", "commands:execute");
        _reader = await _h.UserWithPermissionsAsync(_root, "reader", "commands:execute", "config:read");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<(int Status, string Text, JsonElement Body)> Run(string line, string? token = null) =>
        ConfigCommands.RunAsync(_h.Client, _h.Service<ICliCommandRegistry>(), line, token);

    [Fact]
    public async Task ConfigReadRunsIt_CommandsExecuteAloneIsRefused_ThePortalStaysPublic()
    {
        Assert.Equal(401, (await Run("portal")).Status);
        Assert.Equal(403, (await Run("portal", _runner)).Status);
        var ok = await Run("portal status", _reader);
        Assert.Equal(200, ok.Status);
        Assert.Equal("Portal: /qcli/portal", ok.Text.Split('\n')[0]);
        Assert.Contains("  name         RateBell API", (await Run("portal", _root)).Text);
        // changing the branding needs config:write
        Assert.Equal(403, (await ConfigCommands.RunAsync(_h.Client, "config set portal.branding.name Nope", _reader)).Status);
        Assert.Equal("Updated portal.branding.name = Acme (version 1)",
            (await ConfigCommands.RunAsync(_h.Client, "config set portal.branding.name Acme", _root)).Text);
        _h.Client.DefaultRequestHeaders.Authorization = null;
        var res = await _h.Client.GetAsync("/qcli/portal/portal.json");
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("Acme", JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync()).GetProperty("name").GetString());
    }
}
