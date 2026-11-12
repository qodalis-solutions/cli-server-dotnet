using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

internal sealed class ShowThingProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "show";
    public override string Description { get; set; } = "Shows a thing";
    public override IEnumerable<string>? Aliases { get; set; } = ["sh"];
    public override bool? AcceptsRawInput { get; set; } = true;
    public override IEnumerable<string>? RequiredPermissions { get; set; } = ["things:read"];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult($"show {command.Value}".Trim());
}

internal sealed class ThingsProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "things";
    public override string Description { get; set; } = "Manages things";
    public override IEnumerable<string>? Aliases { get; set; } = ["th"];
    public override string? Module { get; set; } = "inventory";
    public override string? Icon { get; set; } = "📦";
    public override string? Documentation { get; set; } = "First paragraph.\n\nSecond paragraph.";
    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [new("things show 1", "Shows thing 1"), new("things")];
    public override IEnumerable<string>? RequiredPermissions { get; set; } = ["things:use"];
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "format", Aliases = ["f"], Description = "Output format", Type = CommandParameterType.String,
            DefaultValue = "table", AllowedValues = ["table", "json"],
        },
        new CliCommandParameterDescriptor { Name = "verbose", Description = "More output", Type = CommandParameterType.Boolean },
    ];
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = [new ShowThingProcessor()];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("things");
}

/// <summary>Command metadata: aliases, examples, documentation, module, icon and allowed values.</summary>
public class CommandMetadataTests
{
    private static Task<McpPlainHost> StartAsync() => McpPlainHost.StartAsync(cli =>
    {
        cli.AddProcessor(new ThingsProcessor());
        cli.AddMcp();
    });

    private static async Task<JsonElement> ExecuteAsync(HttpClient client, object body)
    {
        var res = await client.PostAsync("/api/v1/qcli/execute",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Commands_SendTheMetadata()
    {
        await using var h = await StartAsync();
        var all = await h.Client.GetFromJsonAsync<JsonElement>("/api/v1/qcli/commands");
        var things = all.EnumerateArray().Single(c => c.GetProperty("command").GetString() == "things");
        Assert.Equal("th", things.GetProperty("aliases")[0].GetString());
        Assert.Equal("inventory", things.GetProperty("module").GetString());
        Assert.Equal("📦", things.GetProperty("icon").GetString());
        Assert.Equal("First paragraph.\n\nSecond paragraph.", things.GetProperty("documentation").GetString());
        var examples = things.GetProperty("examples");
        Assert.Equal("things show 1", examples[0].GetProperty("command").GetString());
        Assert.Equal("Shows thing 1", examples[0].GetProperty("description").GetString());
        Assert.False(examples[1].TryGetProperty("description", out _));
        var parameters = things.GetProperty("parameters");
        Assert.Equal(["table", "json"], parameters[0].GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()));
        Assert.False(parameters[1].TryGetProperty("allowedValues", out _));
        var show = things.GetProperty("processors")[0];
        Assert.Equal("sh", show.GetProperty("aliases")[0].GetString());
        Assert.False(show.TryGetProperty("examples", out _));
        Assert.False(show.TryGetProperty("module", out _));
    }

    [Fact]
    public async Task Execute_ResolvesAliases()
    {
        await using var h = await StartAsync();
        var root = await ExecuteAsync(h.Client, new { command = "TH", chainCommands = Array.Empty<string>(), args = new { } });
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("things", root.GetProperty("outputs")[0].GetProperty("value").GetString());
        var sub = await ExecuteAsync(h.Client, new { command = "th", chainCommands = new[] { "sh" }, value = "7", args = new { } });
        Assert.Equal("show 7", sub.GetProperty("outputs")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Registry_PrefersACommandNameOverAnAlias_AndAliasesCarryPermissions()
    {
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new ThingsProcessor());
        Assert.Equal(["things:use", "things:read"], CliCommandPermissions.RequiredFor(registry, "th", ["sh"]));
        registry.Register(new ShowThingProcessor { Command = "th" });
        Assert.Equal("Shows a thing", registry.FindProcessor("th")!.Description);
    }

    [Fact]
    public async Task Mcp_ListsAllowedValuesAsEnum_AndRefusesOthers()
    {
        await using var h = await StartAsync();
        var tools = await Mcp.ToolsAsync(h.Client);
        var things = tools.Single(t => t.GetProperty("name").GetString() == "things");
        var format = things.GetProperty("inputSchema").GetProperty("properties").GetProperty("format");
        Assert.Equal(["table", "json"], format.GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        var bad = await Mcp.RpcAsync(h.Client, "tools/call", new { name = "things", arguments = new { format = "xml" } });
        Assert.Contains("must be one of: table, json", bad.ToString());
    }
}
