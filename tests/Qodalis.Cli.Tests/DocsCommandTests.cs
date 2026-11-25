using System.Text.Json;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The docs plugin's <c>docs</c> terminal command through <c>POST /execute</c>: list (grouped, --group, --json), read
/// (markdown with variables, --json, heading from the title), no pages, unknown or missing slugs, and a protected
/// server (no permission beyond commands:execute, like the public pages).
/// </summary>
public sealed class DocsCommandTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
    }

    private static void Docs(DocsOptions o, Action<DocsOptions>? configure)
    {
        o.Name = "RateBell API";
        o.Variables["support"] = "help@ratebell.app";
        (configure ?? WithPages)(o);
    }

    private static void WithPages(DocsOptions o) => o
        .AddPage("intro", "---\ntitle: Welcome to {{name}}\norder: 1\n---\nWrite to {{support}}. API at {{baseUrl}}/api.")
        .AddPage("auth", "---\ntitle: Authentication\norder: 2\n---\n# Sign in\n\nUse a token.")
        .AddPage("limits", "---\ntitle: Rate limits\ngroup: Reference\norder: 10\n---\n")
        .AddPage("errors", "---\ntitle: Errors\ngroup: Reference\norder: 10\n---\n{{unknown}} stays.");

    private async Task<ConfigTestHost> StartAsync(Action<DocsOptions>? configure = null)
    {
        var host = await ConfigTestHost.StartAsync(cli => cli.AddDocs(o => Docs(o, configure)));
        _hosts.Add(host);
        return host;
    }

    private static async Task<string> Run(ConfigTestHost h, string line) =>
        (await ConfigCommands.RunAsync(h.Client, h.Service<ICliCommandRegistry>(), line)).Text;

    [Fact]
    public async Task IsRegisteredWithListAndRead_NeedingNoPermissionBeyondCommandsExecute()
    {
        var h = await StartAsync();
        var docs = h.Service<ICliCommandRegistry>().FindProcessor("docs")!;
        Assert.Null(docs.RequiredPermissions);
        Assert.Equal(["list", "read"], docs.Processors!.Select(p => p.Command));
        Assert.All(docs.Processors!, p => Assert.Null(p.RequiredPermissions));
    }

    [Fact]
    public async Task DocsAndDocsList_ThePagesByGroup_SlugAndTitle_WithVariablesReplaced()
    {
        var h = await StartAsync();
        var expected = string.Join('\n', "Guides", "  intro   Welcome to RateBell API", "  auth    Authentication", "", "Reference",
            "  errors  Errors", "  limits  Rate limits");
        Assert.Equal(expected, await Run(h, "docs"));
        Assert.Equal(expected, await Run(h, "docs list"));
        Assert.Equal(expected, await Run(h, "docs ls"));
    }

    [Fact]
    public async Task GroupFilters_CaseInsensitive_AndJsonPrintsTheList()
    {
        var h = await StartAsync();
        Assert.Equal("Reference\n  errors  Errors\n  limits  Rate limits", await Run(h, "docs list --group reference"));
        Assert.Equal("No documentation pages in the group Nope.", await Run(h, "docs list --group Nope"));
        Assert.Equal("Error: --group needs a group name", await Run(h, "docs list --group"));
        var json = JsonDocument.Parse(await Run(h, "docs list --json")).RootElement;
        Assert.Equal(4, json.GetArrayLength());
        Assert.Equal("""{"slug":"intro","title":"Welcome to RateBell API","group":"Guides","order":1}""",
            JsonSerializer.Serialize(json[0]));
    }

    [Fact]
    public async Task ListsAGroupOnce_EvenWhenItsPagesAreNotNextToEachOther()
    {
        var h = await StartAsync(o => o
            .AddPage("a", "---\ntitle: A\norder: 1\n---\n")
            .AddPage("b", "---\ntitle: B\ngroup: Other\norder: 2\n---\n")
            .AddPage("c", "---\ntitle: C\norder: 3\n---\n"));
        Assert.Equal("Guides\n  a  A\n  c  C\n\nOther\n  b  B", await Run(h, "docs"));
    }

    [Fact]
    public async Task SaysSo_WhenThereAreNoPages()
    {
        var h = await StartAsync(_ => { });
        Assert.Equal("No documentation pages.", await Run(h, "docs"));
        Assert.Equal("[]", await Run(h, "docs list --json"));
    }

    [Fact]
    public async Task Read_TheMarkdownWithVariables_TheTitleAsAHeadingUnlessThePageHasItsOwn()
    {
        var h = await StartAsync();
        Assert.Equal("# Welcome to RateBell API\n\nWrite to help@ratebell.app. API at /api.", await Run(h, "docs read intro"));
        Assert.Equal("# Sign in\n\nUse a token.", await Run(h, "docs read auth"));
        Assert.Equal("# Rate limits", await Run(h, "docs read limits"));
        Assert.Equal("# Errors\n\n{{unknown}} stays.", await Run(h, "docs show errors"));
        Assert.Equal("# Sign in\n\nUse a token.", await Run(h, "docs cat auth"));
    }

    [Fact]
    public async Task Read_UsesTheConfiguredBaseUrl()
    {
        var h = await StartAsync(o =>
        {
            WithPages(o);
            o.BaseUrl = "https://api.example.com/";
        });
        Assert.Contains("API at https://api.example.com/api.", await Run(h, "docs read intro"));
    }

    [Fact]
    public async Task ReadJson_ThePageAsTheRouteServesIt()
    {
        var h = await StartAsync();
        var json = JsonDocument.Parse(await Run(h, "docs read intro --json")).RootElement;
        Assert.Equal(
            """{"slug":"intro","title":"Welcome to RateBell API","group":"Guides","order":1,"markdown":"Write to help@ratebell.app. API at /api."}""",
            JsonSerializer.Serialize(json));
    }

    [Fact]
    public async Task Read_MissingUnknownOrSeveralSlugs()
    {
        var h = await StartAsync();
        Assert.Equal("Error: name the page: docs read <slug> (docs list shows them)", await Run(h, "docs read"));
        Assert.Equal("Error: page not found: nope (docs list shows the pages)", await Run(h, "docs read nope"));
        Assert.Equal("Error: one page at a time: docs read <slug>", await Run(h, "docs read intro auth"));
    }

    [Fact]
    public async Task OnAProtectedServer_CommandsExecuteIsEnough_AnonymousIsRefused()
    {
        var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: cli => cli.AddDocs(o => Docs(o, null)));
        _hosts.Add(h);
        var registry = h.Service<ICliCommandRegistry>();
        var runner = await h.UserWithPermissionsAsync(await h.RootTokenAsync(), "runner", "commands:execute");
        Assert.Equal(401, (await ConfigCommands.RunAsync(h.Client, registry, "docs list")).Status);
        Assert.Equal("# Sign in\n\nUse a token.", (await ConfigCommands.RunAsync(h.Client, registry, "docs read auth", runner)).Text);
    }
}
