using System.Text.Json;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>The MCP tools config_list, config_get and config_set, and the endpoint's read-only option.</summary>
public sealed class ConfigMcpTests : IAsyncLifetime
{
    private ConfigTestHost _host = null!;

    internal static CliStoredConfigProvider App() => new(new CliStoredConfigProviderOptions
    {
        Id = "app",
        Title = "App",
        Sections =
        [
            new()
            {
                Id = "main",
                Settings =
                [
                    new() { Key = "limit", Type = "number", Default = 5, Validation = new() { Min = 1, Max = 10 } },
                    new() { Key = "apiKey", Type = "secret" },
                ],
            },
        ],
    });

    public async Task InitializeAsync() => _host = await ConfigTestHost.StartAsync(cli =>
    {
        cli.AddMcp();
        cli.AddConfigProvider(App());
    });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<JsonElement> Call(string name, object arguments) => Mcp.CallAsync(_host.Client, name, arguments);

    [Fact]
    public async Task ListsTheThreeTools_WithSchemasAndReadOnlyHints()
    {
        var tools = await Mcp.ToolsAsync(_host.Client);
        Assert.Equal(["config_list", "config_get", "config_set"],
            tools.Select(t => t.GetProperty("name").GetString()!).Where(n => n.StartsWith("config")));
        JsonElement Tool(string name) => tools.Single(t => t.GetProperty("name").GetString() == name);
        Assert.Equal("{\"readOnlyHint\":true}", Tool("config_list").GetProperty("annotations").GetRawText());
        Assert.Equal("{\"readOnlyHint\":true}", Tool("config_get").GetProperty("annotations").GetRawText());
        Assert.False(Tool("config_set").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("[\"provider\"]", Tool("config_get").GetProperty("inputSchema").GetProperty("required").GetRawText());
        Assert.Equal("[\"provider\",\"section\",\"changes\"]", Tool("config_set").GetProperty("inputSchema").GetProperty("required").GetRawText());
        Assert.Equal("object", Tool("config_set").GetProperty("inputSchema").GetProperty("properties").GetProperty("changes").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheConfigCommand_IsNotAlsoServedAsCommandTools()
    {
        var names = await Mcp.ToolNamesAsync(_host.Client);
        Assert.DoesNotContain(names, n => n.StartsWith("config_") && n is not ("config_list" or "config_get" or "config_set"));
    }

    [Fact]
    public async Task ConfigList_ProvidersAndSectionSummaries()
    {
        var res = await Call("config_list", new { });
        Assert.False(res.GetProperty("isError").GetBoolean());
        Assert.Equal("{\"kind\":\"memory\",\"title\":\"In memory\",\"detail\":\"\",\"persistent\":false}",
            res.GetProperty("structuredContent").GetProperty("store").GetRawText());
        var providers = res.GetProperty("structuredContent").GetProperty("providers");
        Assert.Equal(["server", "host", "cors", "mcp", "app", "custom"], providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(
            "{\"id\":\"app\",\"title\":\"App\",\"description\":\"\",\"writable\":true,\"storage\":{\"kind\":\"store\",\"description\":\"Changes are saved to the config store\",\"sources\":[]},\"sections\":[{\"id\":\"main\",\"title\":\"main\",\"mutable\":true,\"version\":\"0\",\"keys\":2}]}",
            ConfigApi.Find(providers, "id", "app").GetRawText());
    }

    [Fact]
    public async Task ConfigGet_AProviderASectionAnEntry_UnknownOnesAreErrors()
    {
        var provider = (await Call("config_get", new { provider = "app" })).GetProperty("structuredContent").GetProperty("provider");
        Assert.Equal("app", provider.GetProperty("id").GetString());
        var section = (await Call("config_get", new { provider = "app", section = "main" })).GetProperty("structuredContent");
        Assert.Equal(["limit", "apiKey"], section.GetProperty("section").GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()));
        var entry = (await Call("config_get", new { provider = "app", section = "main", key = "limit" })).GetProperty("structuredContent");
        Assert.Equal(("app", "main", "0", "limit", 5),
            (entry.GetProperty("provider").GetString(), entry.GetProperty("section").GetString(), entry.GetProperty("version").GetString(),
             entry.GetProperty("entry").GetProperty("key").GetString(), entry.GetProperty("entry").GetProperty("value").GetInt32()));
        var unknown = await Call("config_get", new { provider = "nope" });
        Assert.True(unknown.GetProperty("isError").GetBoolean());
        Assert.Equal("{\"error\":\"unknown config provider: nope\"}", unknown.GetProperty("structuredContent").GetRawText());
        var noSection = await Call("config_get", new { provider = "app", section = "nope" });
        Assert.Equal((true, "unknown config section: app.nope"), (noSection.GetProperty("isError").GetBoolean(), Mcp.Text(noSection)));
        Assert.True((await Call("config_get", new { provider = "app", section = "main", key = "nope" })).GetProperty("isError").GetBoolean());
        var (_, message) = await Mcp.ErrorAsync(_host.Client, "tools/call", new { name = "config_get", arguments = new { } });
        Assert.Contains("missing required argument 'provider'", message);
    }

    [Fact]
    public async Task ConfigSet_AppliesAnswersErrorsAndConflicts_NeverReturnsSecrets()
    {
        var ok = await Call("config_set", new { provider = "app", section = "main", changes = new { limit = 7, apiKey = "sk_mcp_secret" } });
        Assert.False(ok.GetProperty("isError").GetBoolean());
        var result = ok.GetProperty("structuredContent");
        Assert.Equal(("app", "[\"limit\",\"apiKey\"]", "1"),
            (result.GetProperty("provider").GetString(), result.GetProperty("applied").GetRawText(), result.GetProperty("version").GetString()));
        Assert.DoesNotContain("sk_mcp_secret", ok.GetRawText());
        var invalid = await Call("config_set", new { provider = "app", section = "main", changes = new { limit = 50 } });
        Assert.True(invalid.GetProperty("isError").GetBoolean());
        Assert.Equal("{\"error\":\"validation failed\",\"errors\":{\"limit\":\"must be at most 10\"}}", invalid.GetProperty("structuredContent").GetRawText());
        var stale = await Call("config_set", new { provider = "app", section = "main", changes = new { limit = 3 }, version = "0" });
        Assert.Equal("{\"error\":\"version conflict\",\"version\":\"1\"}", stale.GetProperty("structuredContent").GetRawText());
        var got = await Call("config_get", new { provider = "app", section = "main", key = "apiKey" });
        var entry = got.GetProperty("structuredContent").GetProperty("entry");
        Assert.Equal((JsonValueKind.Null, true), (entry.GetProperty("value").ValueKind, entry.GetProperty("isSet").GetBoolean()));
        Assert.DoesNotContain("sk_mcp_secret", got.GetRawText());
        var (_, message) = await Mcp.ErrorAsync(_host.Client, "tools/call",
            new { name = "config_set", arguments = new { provider = "app", section = "main", changes = "x" } });
        Assert.Contains("argument 'changes' must be an object", message);
    }

    [Fact]
    public async Task ConfigSet_IsRefusedWhenTheEndpointIsReadOnly()
    {
        await using var ro = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddMcp(o => o.ReadOnly = true);
            cli.AddConfigProvider(App());
        });
        var names = await Mcp.ToolNamesAsync(ro.Client);
        Assert.Contains("config_list", names);
        Assert.Contains("config_get", names);
        Assert.DoesNotContain("config_set", names);
        var (code, message) = await Mcp.ErrorAsync(ro.Client, "tools/call",
            new { name = "config_set", arguments = new { provider = "app", section = "main", changes = new { limit = 2 } } });
        Assert.Equal((-32602, "Tool config_set is not available: the MCP endpoint is read-only"), (code, message));
        var limit = await Mcp.CallAsync(ro.Client, "config_get", new { provider = "app", section = "main", key = "limit" });
        Assert.Equal(5, limit.GetProperty("structuredContent").GetProperty("entry").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ServerInfo_ListsTheConfigFeature()
    {
        var info = await Mcp.CallAsync(_host.Client, "server_info");
        Assert.Contains("config", info.GetProperty("structuredContent").GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }
}

/// <summary>The config tools on a server protected by the users plugin.</summary>
public sealed class ConfigMcpProtectedTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!, _runner = null!, _reader = null!, _writer = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: cli =>
        {
            cli.AddMcp();
            cli.AddConfigProvider(ConfigMcpTests.App());
            cli.AddConfigProvider(new CliStoredConfigProvider(new CliStoredConfigProviderOptions
            {
                Id = "vault", Title = "Vault", RequiredPermission = "vault:admin",
                Sections = [new() { Id = "s", Settings = [new() { Key = "k", Type = "string" }] }],
            }));
        });
        _root = await _h.RootTokenAsync();
        _runner = await _h.UserWithPermissionsAsync(_root, "runner", "commands:execute");
        _reader = await _h.UserWithPermissionsAsync(_root, "reader", "commands:execute", "config:read");
        _writer = await _h.UserWithPermissionsAsync(_root, "writer", "commands:execute", "config:read", "config:write");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<List<string>> Names(string token) =>
        (await Mcp.ToolNamesAsync(_h.Client, token)).Where(n => n.StartsWith("config")).ToList();

    [Fact]
    public async Task ToolsFollowConfigReadAndConfigWrite()
    {
        Assert.Empty(await Names(_runner));
        Assert.Equal(["config_list", "config_get"], await Names(_reader));
        Assert.Equal(["config_list", "config_get", "config_set"], await Names(_writer));
        var (_, message) = await Mcp.ErrorAsync(_h.Client, "tools/call",
            new { name = "config_set", arguments = new { provider = "app", section = "main", changes = new { limit = 2 } } }, _reader);
        Assert.Equal("Unknown tool: config_set", message);
    }

    [Fact]
    public async Task AProvidersRequiredPermission_HidesItFromTheToolsToo()
    {
        static IEnumerable<string?> Ids(JsonElement res) =>
            res.GetProperty("structuredContent").GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString());
        Assert.DoesNotContain("vault", Ids(await Mcp.CallAsync(_h.Client, "config_list", new { }, _writer)));
        var res = await Mcp.CallAsync(_h.Client, "config_get", new { provider = "vault" }, _writer);
        Assert.True(res.GetProperty("isError").GetBoolean());
        Assert.Equal("{\"error\":\"forbidden: missing permission vault:admin\"}", res.GetProperty("structuredContent").GetRawText());
        Assert.Contains("vault", Ids(await Mcp.CallAsync(_h.Client, "config_list", new { }, _root)));
    }

    [Fact]
    public async Task Changes_AreAuditedWithViaMcpAndTheCaller()
    {
        await Mcp.CallAsync(_h.Client, "config_set", new { provider = "app", section = "main", changes = new { limit = 3 } }, _writer);
        var entry = Assert.Single(await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = "config.update" }));
        Assert.Equal(("writer", "config:app.main"), (entry.Username, entry.Target));
        Assert.Contains("\"via\":\"mcp\"", JsonSerializer.Serialize(entry.Properties));
    }
}
