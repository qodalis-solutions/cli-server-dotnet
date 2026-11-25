extern alias demo;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws;
using Qodalis.Cli.Processors;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>GET /commands</c> lists a command added by a module under the module's name (<c>module</c>), unless the command
/// names a module itself: the aws module, the demo's own commands (demo module) and the built-in commands (core).
/// </summary>
public class ModuleCommandsTests
{
    private sealed class ShopModule : CliModule
    {
        public override string Name => "shop";
        public override string Version => "1.0.0";
        public override string Description => "Shop";

        public override IEnumerable<ICliCommandProcessor> Processors { get; } =
        [
            new TestProcessor("orders", "Orders"),
            new TestProcessor("stats", "Stats") { Module = "reports" },
        ];
    }

    private static async Task<Dictionary<string, string?>> ModulesAsync(HttpClient client)
    {
        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/qcli/commands");
        return all.EnumerateArray().ToDictionary(
            c => c.GetProperty("command").GetString()!,
            c => c.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
    }

    [Fact]
    public async Task ModuleCommands_CarryItsName_OwnModuleWins_StandaloneHaveNone()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
            cli.AddModule(new ShopModule()).AddProcessor(new TestProcessor("loose", "Loose")));

        var modules = await ModulesAsync(h.Client);

        Assert.Equal("shop", modules["orders"]);
        Assert.Equal("reports", modules["stats"]);
        Assert.Null(modules["loose"]);
    }

    [Fact]
    public async Task Aws_IsListedUnderTheAwsModule()
    {
        await using var h = await McpPlainHost.StartAsync(cli => cli.AddModule(new AwsModule()));

        Assert.Equal("aws", (await ModulesAsync(h.Client))["aws"]);
    }

    [Fact]
    public async Task Http_IsListedUnderCore()
    {
        await using var h = await McpPlainHost.StartAsync(cli => cli.AddProcessor<CliHttpCommandProcessor>());

        Assert.Equal("core", (await ModulesAsync(h.Client))["http"]);
    }

    [Fact]
    public void Demo_ExampleCommandsAreTheDemoModule_HttpIsCore()
    {
        using var factory = new WebApplicationFactory<demo::Program>();
        var services = factory.Services;
        var info = services.GetRequiredService<ICliServerInfoService>();
        var modules = services.GetServices<ICliCommandProcessor>()
            .GroupBy(p => p.Command)
            .ToDictionary(g => g.Key, g => info.MapToDescriptor(g.Last()).Module);

        foreach (var command in new[] { "echo", "status", "time", "hello", "math", "system", "hash", "base64" })
            Assert.Equal("demo", modules[command]);
        Assert.Equal("core", modules["http"]);
        Assert.Equal("aws", modules["aws"]);
    }
}
