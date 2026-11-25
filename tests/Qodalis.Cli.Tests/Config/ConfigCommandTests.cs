using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Config;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>Runs a command line through <c>POST /api/v1/qcli/execute</c>, the way the web CLI sends it.</summary>
internal static class ConfigCommands
{
    public static readonly HashSet<string> SubCommands = ["list", "get", "set", "reset"];

    /// <summary>The body of <c>/execute</c> for <paramref name="line"/> (words, <c>--name value</c> flags, positionals).</summary>
    public static object Body(string line)
    {
        var words = CliValueHelpers.SplitShellWords(line);
        var chain = new List<string>();
        var index = 1;
        if (words.Count > 1 && SubCommands.Contains(words[1])) chain.Add(words[index++]);
        var args = new Dictionary<string, object>();
        var positionals = new List<string>();
        for (; index < words.Count; index++)
        {
            if (words[index].StartsWith("--"))
            {
                var name = words[index][2..];
                if (index + 1 < words.Count && !words[index + 1].StartsWith("--")) args[name] = words[++index];
                else args[name] = true;
            }
            else positionals.Add(words[index]);
        }
        return new { command = words[0], chainCommands = chain, value = string.Join(' ', positionals), positionals, args, rawCommand = line };
    }

    public static async Task<(int Status, string Text, JsonElement Body)> RunAsync(HttpClient client, string line, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/execute")
        {
            Content = new StringContent(JsonSerializer.Serialize(Body(line)), Encoding.UTF8, "application/json"),
        };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.SendAsync(req);
        var body = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
        var text = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("outputs", out var outputs)
            ? string.Join('\n', outputs.EnumerateArray().Where(o => o.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                .Select(o => o.GetProperty("value").GetString()))
            : string.Empty;
        return ((int)res.StatusCode, text, body);
    }

    public static CliStoredConfigProvider App() => new(new CliStoredConfigProviderOptions
    {
        Id = "app",
        Title = "App",
        Description = "The app",
        Sections =
        [
            new()
            {
                Id = "main",
                Title = "Main",
                Settings =
                [
                    new() { Key = "limit", Type = "number", Default = 5, Validation = new() { Min = 1, Max = 10 } },
                    new() { Key = "apiKey", Type = "secret" },
                    new() { Key = "tags", Type = "string[]", Default = new JsonArray() },
                    new() { Key = "beta", Type = "boolean", Default = false },
                    new() { Key = "mode", Type = "enum", Default = "a", Validation = new() { Options = ["a", "b"] } },
                    new() { Key = "fixed", Type = "string", Mutable = false, Default = "x" },
                    new() { Key = "timeout", Type = "duration", Default = "30s", RequiresRestart = true },
                ],
            },
            new() { Id = "main.extra", Settings = [new() { Key = "deep.key", Type = "string", Default = "d" }] },
        ],
    });
}

/// <summary>The <c>config</c> command on an open server.</summary>
public sealed class ConfigCommandTests : IAsyncLifetime
{
    private ConfigTestHost _host = null!;

    public async Task InitializeAsync() => _host = await ConfigTestHost.StartAsync(cli => cli.AddConfigProvider(ConfigCommands.App()));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> Run(string line) => (await ConfigCommands.RunAsync(_host.Client, line)).Text;

    [Fact]
    public void IsRegisteredWithItsSubCommandsAndPermissions()
    {
        var processor = _host.Service<ICliCommandRegistry>().FindProcessor("config");
        var config = Assert.IsType<CliConfigCommandProcessor>(processor);
        Assert.Equal(["config:read"], config.RequiredPermissions!);
        Assert.Equal(["list config:read", "get config:read", "set config:write", "reset config:write"],
            config.Processors!.Select(p => $"{p.Command} {string.Join(",", p.RequiredPermissions!)}"));
    }

    [Fact]
    public async Task ConfigList_ProvidersAndSections()
    {
        var text = await Run("config list");
        Assert.Contains("server  Server", text);
        Assert.Matches(@"app  App\n  main +7 setting\(s\)  version 0\n  main\.extra +1 setting\(s\)  version 0", text);
        Assert.Equal(text, await Run("config"));
    }

    [Fact]
    public async Task ConfigGet_ProviderSectionAndKey()
    {
        var provider = await Run("config get app");
        Assert.Contains("app  App  The app", provider);
        Assert.Contains("app.main  Main  (version 0)", provider);
        Assert.Matches(@"limit +5  \[number, default\]", provider);
        Assert.Matches(@"apiKey +\(not set\)  \[secret, default\]", provider);
        Assert.Matches(@"fixed +x  \[string, default, read-only\]", provider);
        Assert.Matches(@"timeout +30s  \[duration, default, restart\]", provider);
        var section = await Run("config get app.main.extra");
        Assert.Contains("app.main.extra", section);
        Assert.Contains("deep.key", section);
        Assert.Equal(string.Join('\n',
            "app.main.limit",
            "  Label         limit",
            "  Type          number",
            "  Value         5",
            "  Default       5",
            "  Source        default",
            "  Mutable       yes",
            "  Validation    min 1; max 10",
            "  Version       0"), await Run("config get app.main.limit"));
        Assert.Contains("Value         d", await Run("config get app.main.extra.deep.key"));
    }

    [Fact]
    public async Task Get_ErrorsForUnknownProvidersSectionsAndKeys()
    {
        Assert.Equal("Error: unknown config provider: nope", await Run("config get nope"));
        Assert.Equal("Error: unknown config section: app.nope", await Run("config get app.nope"));
        Assert.Equal("Error: unknown setting: app.main.nope", await Run("config get app.main.nope"));
    }

    [Fact]
    public async Task Set_ValuesParsedByType()
    {
        Assert.Equal("Updated app.main.limit = 7 (version 1)", await Run("config set app.main.limit 7"));
        Assert.Equal("Updated app.main.tags = [\"a\",\"b\",\"c\"] (version 2)", await Run("config set app.main.tags a,b, c"));
        Assert.Equal("Updated app.main.beta = true (version 3)", await Run("config set app.main.beta yes"));
        Assert.Equal("Updated app.main.mode = b (version 4)", await Run("config set app.main.mode b"));
        Assert.Contains("Value         [\"a\",\"b\",\"c\"]", await Run("config get app.main.tags"));
    }

    [Fact]
    public async Task Set_AChangeThatNeedsARestartSaysSo() =>
        Assert.Matches(@"^Updated app\.main\.timeout = 1m \(version \d+\)\nRestart the server for the change to take effect\.$",
            await Run("config set app.main.timeout 1m"));

    [Fact]
    public async Task Set_ValidationParseReadOnlyAndUnknownErrors()
    {
        Assert.Equal("Error: validation failed\n  limit: must be at most 10", await Run("config set app.main.limit 50"));
        Assert.Equal("Error: validation failed\n  limit: must be a number", await Run("config set app.main.limit lots"));
        Assert.Equal("Error: validation failed\n  beta: must be true or false", await Run("config set app.main.beta maybe"));
        Assert.Equal("Error: validation failed\n  mode: must be one of: a, b", await Run("config set app.main.mode z"));
        Assert.Equal("Error: validation failed\n  fixed: read-only", await Run("config set app.main.fixed y"));
        Assert.Equal("Error: validation failed\n  ghost: unknown setting", await Run("config set app.main.ghost 1"));
        Assert.Equal("Error: config provider server is read-only", await Run("config set server.runtime.pid 1"));
        Assert.Equal("Error: name a setting: <provider>.<section>.<key>", await Run("config set app.main 1"));
        Assert.Contains("usage: config set", await Run("config set app.main.limit"));
    }

    [Fact]
    public async Task Set_ExpectedVersion_RefusedWhenStale()
    {
        await Run("config set app.main.limit 2");
        Assert.Equal("Error: version conflict (current version 1)", await Run("config set app.main.limit 3 --expected-version 0"));
        Assert.Equal("Error: version conflict (current version 1)", await Run("config set app.main.limit 3 --version 0"));
        Assert.Equal("Updated app.main.limit = 3 (version 2)", await Run("config set app.main.limit 3 --expected-version 1"));
    }

    [Fact]
    public async Task Secrets_AreAcceptedNeverPrinted()
    {
        Assert.Matches(@"^Updated app\.main\.apiKey = \(set\) \(version \d+\)$", await Run("config set app.main.apiKey sk_cmd_secret"));
        var all = await Run("config get app") + await Run("config get app.main.apiKey");
        Assert.DoesNotContain("sk_cmd_secret", all);
        var entry = await Run("config get app.main.apiKey");
        Assert.Contains("Value         (set)", entry);
        Assert.DoesNotContain("Default", entry);
        Assert.Matches(@"^Reset app\.main\.apiKey = \(not set\)", await Run("config reset app.main.apiKey"));
    }

    [Fact]
    public async Task Reset_BackToTheDefault_FreeKeysRemoved()
    {
        await Run("config set app.main.limit 9");
        Assert.Matches(@"^Reset app\.main\.limit = 5 \(version \d+\)$", await Run("config reset app.main.limit"));
        Assert.Equal("Updated custom.custom.motd = Hello there (version 1)", await Run("config set custom.custom.motd Hello there"));
        Assert.Equal("Updated custom.custom.retries = 3 (version 2)", await Run("config set custom.custom.retries 3"));
        Assert.Contains("Type          number", await Run("config get custom.custom.retries"));
        Assert.Equal("Reset custom.custom.motd (version 3)", await Run("config reset custom.custom.motd"));
        Assert.StartsWith("Error: version conflict", await Run("config reset app.main.limit --expected-version 0"));
        Assert.Contains("usage: config reset", await Run("config reset"));
    }

    [Fact]
    public void FormatValue()
    {
        Assert.Equal("x", CliConfigCommandProcessor.FormatValue("x"));
        Assert.Equal("(none)", CliConfigCommandProcessor.FormatValue((JsonNode?)null));
        Assert.Equal("{\"a\":1}", CliConfigCommandProcessor.FormatValue(JsonNode.Parse("{\"a\":1}")));
        Assert.Equal("(set)", CliConfigCommandProcessor.FormatValue((JsonNode?)null, true, true));
        Assert.Equal("(not set)", CliConfigCommandProcessor.FormatValue((JsonNode?)null, true, false));
    }

    [Fact]
    public async Task CanBeLeftOut()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.ConfigureConfig(o => o.Command = false));
        Assert.Null(host.Service<ICliCommandRegistry>().FindProcessor("config"));
    }

    [Fact]
    public async Task AHostCommandNamedConfig_Wins()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddProcessor(new McpEchoNamed("config")));
        Assert.IsType<McpEchoNamed>(host.Service<ICliCommandRegistry>().FindProcessor("config"));
    }
}

internal sealed class McpEchoNamed(string command) : CliCommandProcessor
{
    public override string Command { get; set; } = command;
    public override string Description { get; set; } = "mine";

    public override Task<string> HandleAsync(Qodalis.Cli.Abstractions.CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("mine");
}

/// <summary>The <c>config</c> command on a server protected by the users plugin.</summary>
public sealed class ConfigCommandProtectedTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _runner = null!, _reader = null!, _writer = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: cli => cli.AddConfigProvider(ConfigCommands.App()));
        var root = await _h.RootTokenAsync();
        _runner = await _h.UserWithPermissionsAsync(root, "runner", "commands:execute");
        _reader = await _h.UserWithPermissionsAsync(root, "reader", "commands:execute", "config:read");
        _writer = await _h.UserWithPermissionsAsync(root, "writer", "commands:execute", "config:read", "config:write");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task ConfigRead_ForListAndGet()
    {
        Assert.Equal(403, (await ConfigCommands.RunAsync(_h.Client, "config list", _runner)).Status);
        var ok = await ConfigCommands.RunAsync(_h.Client, "config get app.main.limit", _reader);
        Assert.Equal(200, ok.Status);
        Assert.Contains("Value         5", ok.Text);
    }

    [Fact]
    public async Task ConfigWrite_ForSetAndReset()
    {
        var denied = await ConfigCommands.RunAsync(_h.Client, "config set app.main.limit 2", _reader);
        Assert.Equal(403, denied.Status);
        Assert.Contains("config:write", denied.Body.GetRawText());
        Assert.Equal(403, (await ConfigCommands.RunAsync(_h.Client, "config reset app.main.limit", _reader)).Status);
        Assert.Equal("Updated app.main.limit = 2 (version 1)", (await ConfigCommands.RunAsync(_h.Client, "config set app.main.limit 2", _writer)).Text);
    }

    [Fact]
    public async Task Changes_AreAuditedWithViaTerminalAndTheSignedInUser()
    {
        await ConfigCommands.RunAsync(_h.Client, "config set app.main.apiKey terminal-secret", _writer);
        var entries = await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = "config.update" });
        var entry = Assert.Single(entries);
        Assert.Equal(("writer", "config:app.main"), (entry.Username, entry.Target));
        var properties = JsonSerializer.Serialize(entry.Properties);
        Assert.Contains("\"via\":\"terminal\"", properties);
        Assert.Contains("\"changes\":[{\"key\":\"apiKey\",\"old\":null,\"new\":\"***\"}]", properties);
        Assert.DoesNotContain("terminal-secret", JsonSerializer.Serialize(entries));
    }
}
