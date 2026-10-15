using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

// ---------------------------------------------------------------------------
// Test processors
// ---------------------------------------------------------------------------

internal sealed class McpEchoProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "echo";
    public override string Description { get; set; } = "Echoes the input";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult($"echo:{command.Value}|raw:{command.RawCommand}");
}

internal sealed class McpCalcProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "calc";
    public override string Description { get; set; } = "Calculator";
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = [new McpCalcAddProcessor()];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("usage");
}

internal sealed class McpCalcAddProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "add";
    public override string Description { get; set; } = "";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor { Name = "a", Description = "First", Required = true, Type = CommandParameterType.Number },
        new CliCommandParameterDescriptor { Name = "b", Description = "Second", Required = true, Type = CommandParameterType.Number },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var a = double.Parse(command.Args["a"].ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        var b = double.Parse(command.Args["b"].ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        return Task.FromResult($"{a + b}|chain:{string.Join(",", command.ChainCommands)}");
    }
}

/// <summary>Every parameter type, with defaults.</summary>
internal sealed class McpTypesProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "types";
    public override string Description { get; set; } = "All parameter types";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor { Name = "text", Description = "A string", Type = CommandParameterType.String, DefaultValue = "hi" },
        new CliCommandParameterDescriptor { Name = "count", Description = "A number", Type = CommandParameterType.Number, DefaultValue = 3 },
        new CliCommandParameterDescriptor { Name = "flag", Description = "A boolean", Type = CommandParameterType.Boolean, DefaultValue = false },
        new CliCommandParameterDescriptor { Name = "tags", Description = "An array", Type = CommandParameterType.Array },
        new CliCommandParameterDescriptor { Name = "obj", Description = "An object", Type = CommandParameterType.Object },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Join(";", command.Args.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")) + "|raw:" + command.RawCommand);
}

internal sealed class McpRichProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "rich";
    public override string Description { get; set; } = "Rich outputs";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("");

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken)
    {
        var response = new CliServerResponse
        {
            ExitCode = 2,
            Outputs =
            [
                new TextOutput { Value = "title" },
                new TableOutput { Headers = ["A", "B"], Rows = [["1", "2"], ["3", "4"]] },
                new ListOutput { Items = ["x", "y"] },
                new KeyValueOutput { Entries = [new KeyValueEntry { Key = "k", Value = "v" }] },
                new JsonOutput { Value = new[] { 1, 2 } },
            ],
        };
        return Task.FromResult<ICliStructuredResponse?>(response);
    }
}

internal sealed class McpInfoStructuredProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "info";
    public override string Description { get; set; } = "Structured info";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("");

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken) =>
        Task.FromResult<ICliStructuredResponse?>(new CliServerResponse
        {
            Outputs = [new JsonOutput { Value = new { name = "demo", count = 2 } }],
        });
}

internal sealed class McpThrowProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "boom";
    public override string Description { get; set; } = "Throws";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("kaboom");
}

internal sealed class McpSecretProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "secret";
    public override string Description { get; set; } = "Needs secret:read";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = ["secret:read"];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult("the secret");
}

internal sealed class McpNamedProcessor(string command, params ICliCommandProcessor[] children) : CliCommandProcessor
{
    public override string Command { get; set; } = command;
    public override string Description { get; set; } = $"cmd {command}";
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = children.Length == 0 ? null : children;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult($"ran {Command}");
}

internal sealed class FakeDataExplorerProvider : IDataExplorerProvider
{
    public Task<DataExplorerResult> ExecuteAsync(DataExplorerExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Query == "fail") throw new InvalidOperationException("syntax error");
        return Task.FromResult(new DataExplorerResult
        {
            Success = true,
            Source = "",
            Columns = ["id", "name"],
            Rows = [new object[] { 1, "alice" }, new object[] { 2, "bob" }, new object[] { 3, "carol" }],
        });
    }

    public Task<DataExplorerSchemaResult?> GetSchemaAsync(DataExplorerProviderOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<DataExplorerSchemaResult?>(new DataExplorerSchemaResult
        {
            Source = options.Name,
            Tables = [new DataExplorerSchemaTable { Name = "users", Type = "table", Columns = [new DataExplorerSchemaColumn { Name = "id", Type = "int", PrimaryKey = true }] }],
        });
}

internal sealed class McpNoopJob : ICliJob
{
    public Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A plugin-style provider (registered with <c>AddMcpToolProvider</c>).</summary>
internal sealed class CustomMcpProvider : IMcpToolProvider
{
    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<McpToolDefinition>>(
        [
            new McpToolDefinition
            {
                Name = "custom_hello",
                Description = "Custom tool",
                InputSchema = McpSchema.Object(new System.Text.Json.Nodes.JsonObject { ["who"] = McpSchema.Property("string") }, "who"),
                CallAsync = (args, ctx, _) => Task.FromResult(McpToolResult.Ok($"hello {McpSchema.GetString(args, "who")}")),
            },
            // Collides with the "echo" command tool: plugin tools win over command tools.
            new McpToolDefinition
            {
                Name = "echo",
                Description = "Plugin echo",
                InputSchema = McpSchema.Object(),
                CallAsync = (_, _, _) => Task.FromResult(McpToolResult.Ok("plugin echo")),
            },
        ]);
}

// ---------------------------------------------------------------------------
// Hosts and helpers
// ---------------------------------------------------------------------------

/// <summary>A CLI server without the users plugin (no authentication provider).</summary>
internal sealed class McpPlainHost : IAsyncDisposable
{
    private McpPlainHost(IHost host)
    {
        Host = host;
        Client = host.GetTestClient();
    }

    public IHost Host { get; }
    public HttpClient Client { get; }

    public static async Task<McpPlainHost> StartAsync(Action<CliBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s =>
            {
                s.AddControllers().AddCli(configure);
                services?.Invoke(s);
            });
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        return new McpPlainHost(await builder.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }
}

internal static class Mcp
{
    public const string Path = "/api/qcli/mcp";

    public static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    public static string Request(string method, object? @params = null, object? id = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? 1,
            ["method"] = method,
            ["params"] = @params,
        }.Where(kv => kv.Key != "params" || kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? token = null, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Path) { Content = Body(body) };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin != null) req.Headers.Add("Origin", origin);
        return await client.SendAsync(req);
    }

    /// <summary>Posts a JSON-RPC request and returns the parsed JSON-RPC response (asserting HTTP 200 JSON).</summary>
    public static async Task<JsonElement> RpcAsync(HttpClient client, string method, object? @params = null, string? token = null)
    {
        var res = await PostAsync(client, Request(method, @params), token);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        var json = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
        Assert.Equal("2.0", json.GetProperty("jsonrpc").GetString());
        return json;
    }

    public static async Task<JsonElement> ResultAsync(HttpClient client, string method, object? @params = null, string? token = null)
    {
        var json = await RpcAsync(client, method, @params, token);
        Assert.True(json.TryGetProperty("result", out var result), $"expected a result, got {json}");
        return result;
    }

    public static async Task<(int Code, string Message)> ErrorAsync(HttpClient client, string method, object? @params = null, string? token = null)
    {
        var json = await RpcAsync(client, method, @params, token);
        Assert.True(json.TryGetProperty("error", out var error), $"expected an error, got {json}");
        return (error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!);
    }

    public static async Task<List<JsonElement>> ToolsAsync(HttpClient client, string? token = null) =>
        (await ResultAsync(client, "tools/list", null, token)).GetProperty("tools").EnumerateArray().ToList();

    public static async Task<List<string>> ToolNamesAsync(HttpClient client, string? token = null) =>
        (await ToolsAsync(client, token)).Select(t => t.GetProperty("name").GetString()!).ToList();

    public static Task<JsonElement> CallAsync(HttpClient client, string name, object? arguments = null, string? token = null) =>
        ResultAsync(client, "tools/call", arguments == null ? new { name } : new { name, arguments }, token);

    public static string Text(JsonElement callResult)
    {
        var content = Assert.Single(callResult.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return content.GetProperty("text").GetString()!;
    }

    public static void AddTestProcessors(CliBuilder cli)
    {
        cli.AddProcessor(new McpEchoProcessor());
        cli.AddProcessor(new McpCalcProcessor());
        cli.AddProcessor(new McpTypesProcessor());
        cli.AddProcessor(new McpInfoStructuredProcessor());
        cli.AddProcessor(new McpRichProcessor());
        cli.AddProcessor(new McpThrowProcessor());
        cli.AddProcessor(new McpSecretProcessor());
    }
}

// ---------------------------------------------------------------------------
// Protocol, tools and calls (no authentication provider)
// ---------------------------------------------------------------------------

public class McpProtocolTests : IAsyncLifetime
{
    private McpPlainHost _h = null!;
    private HttpClient Client => _h.Client;

    public async Task InitializeAsync()
    {
        _h = await McpPlainHost.StartAsync(cli =>
        {
            cli.SetAllowedOrigins("http://allowed.example");
            Mcp.AddTestProcessors(cli);
            cli.AddMcp(o => o.Instructions = "Use the tools.");
        });
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    // --- initialize / ping / notifications ---------------------------------

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2024-01-01", "2025-11-25")]
    public async Task Initialize_NegotiatesVersion(string requested, string expected)
    {
        var result = await Mcp.ResultAsync(Client, "initialize", new
        {
            protocolVersion = requested,
            capabilities = new { },
            clientInfo = new { name = "test", version = "1" },
        });
        Assert.Equal(expected, result.GetProperty("protocolVersion").GetString());
        Assert.False(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        Assert.Equal("qodalis-cli-server", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("serverInfo").GetProperty("version").GetString()));
        Assert.Equal("Use the tools.", result.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Initialize_NoSessionHeader()
    {
        var res = await Mcp.PostAsync(Client, Mcp.Request("initialize", new { protocolVersion = "2025-06-18" }));
        Assert.Equal(200, (int)res.StatusCode);
        Assert.False(res.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task Initialize_MissingProtocolVersion_InvalidParams()
    {
        var (code, _) = await Mcp.ErrorAsync(Client, "initialize", new { });
        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task Ping_ReturnsEmptyObject()
    {
        var json = await Mcp.RpcAsync(Client, "ping");
        Assert.Equal(1, json.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Object, json.GetProperty("result").ValueKind);
        Assert.Empty(json.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task Id_IsEchoed_StringOrNumber()
    {
        var res = await Mcp.PostAsync(Client, "{\"jsonrpc\":\"2.0\",\"id\":\"abc-1\",\"method\":\"ping\"}");
        var json = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
        Assert.Equal("abc-1", json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Notification_Returns202WithEmptyBody()
    {
        var res = await Mcp.PostAsync(Client, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Equal(202, (int)res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());

        // Unknown notifications are accepted too.
        res = await Mcp.PostAsync(Client, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/whatever\"}");
        Assert.Equal(202, (int)res.StatusCode);
    }

    [Fact]
    public async Task ClientResponse_Returns202()
    {
        var res = await Mcp.PostAsync(Client, "{\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{}}");
        Assert.Equal(202, (int)res.StatusCode);
    }

    // --- JSON-RPC errors ----------------------------------------------------

    private async Task<JsonElement> RawAsync(string body)
    {
        var res = await Mcp.PostAsync(Client, body);
        Assert.Equal(200, (int)res.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MalformedJson_ParseError()
    {
        var json = await RawAsync("{not json");
        Assert.Equal(-32700, json.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task EmptyBody_ParseError()
    {
        var json = await RawAsync("");
        Assert.Equal(-32700, json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{\"id\":1,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{\"x\":1},\"method\":\"ping\"}")]
    public async Task InvalidRequest(string body)
    {
        var json = await RawAsync(body);
        Assert.Equal(-32600, json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UnknownMethod_MethodNotFound()
    {
        var (code, message) = await Mcp.ErrorAsync(Client, "resources/list");
        Assert.Equal(-32601, code);
        Assert.Contains("resources/list", message);
    }

    [Fact]
    public async Task ParamsNotObject_InvalidParams()
    {
        var json = await RawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":[1]}");
        Assert.Equal(-32602, json.GetProperty("error").GetProperty("code").GetInt32());
    }

    // --- HTTP ---------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task GetAndDelete_Return405WithAllowPost(string method)
    {
        var res = await Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Mcp.Path));
        Assert.Equal(405, (int)res.StatusCode);
        Assert.Contains("POST", res.Content.Headers.Allow);
    }

    [Fact]
    public async Task DisallowedOrigin_Returns403()
    {
        var res = await Mcp.PostAsync(Client, Mcp.Request("ping"), origin: "http://evil.example");
        Assert.Equal(403, (int)res.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, body.GetProperty("error").ValueKind);
    }

    [Theory]
    [InlineData("http://allowed.example")]
    [InlineData("http://localhost")]
    public async Task AllowedOrSameOrigin_IsAccepted(string origin)
    {
        var res = await Mcp.PostAsync(Client, Mcp.Request("ping"), origin: origin);
        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task NoOrigin_IsAccepted()
    {
        Assert.Equal(200, (int)(await Mcp.PostAsync(Client, Mcp.Request("ping"))).StatusCode);
    }

    [Fact]
    public async Task OpenWithoutProviderAndToken()
    {
        var res = await Mcp.PostAsync(Client, Mcp.Request("tools/list"));
        Assert.Equal(200, (int)res.StatusCode);
    }

    // --- tools/list ---------------------------------------------------------

    [Fact]
    public async Task ToolsList_ContainsCommandTools()
    {
        var names = await Mcp.ToolNamesAsync(Client);
        Assert.Contains("server_info", names);
        Assert.Contains("echo", names);
        Assert.Contains("calc_add", names);
        Assert.Contains("types", names);
        Assert.Contains("info", names);
        Assert.Contains("secret", names); // no auth provider: no permission filtering
        Assert.DoesNotContain("calc", names); // has sub-processors, no parameters, no value
        // Features not enabled
        Assert.DoesNotContain(names, n => n.StartsWith("fs_"));
        Assert.DoesNotContain(names, n => n.StartsWith("data_"));
        Assert.DoesNotContain(names, n => n.StartsWith("jobs_"));
    }

    [Fact]
    public async Task ToolsList_IgnoresCursor()
    {
        var result = await Mcp.ResultAsync(Client, "tools/list", new { cursor = "abc" });
        Assert.NotEmpty(result.GetProperty("tools").EnumerateArray());
        Assert.False(result.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task ToolsList_DescriptionsAndFallback()
    {
        var tools = await Mcp.ToolsAsync(Client);
        Assert.Equal("Echoes the input", tools.Single(t => t.GetProperty("name").GetString() == "echo").GetProperty("description").GetString());
        Assert.Equal("Run 'calc add'", tools.Single(t => t.GetProperty("name").GetString() == "calc_add").GetProperty("description").GetString());
    }

    [Fact]
    public async Task InputSchema_ValueTool()
    {
        var schema = (await Mcp.ToolsAsync(Client)).Single(t => t.GetProperty("name").GetString() == "echo").GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal(["value"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
    }

    [Fact]
    public async Task InputSchema_ParameterTypes()
    {
        var schema = (await Mcp.ToolsAsync(Client)).Single(t => t.GetProperty("name").GetString() == "types").GetProperty("inputSchema");
        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("text").GetProperty("type").GetString());
        Assert.Equal("hi", props.GetProperty("text").GetProperty("default").GetString());
        Assert.Equal("A string", props.GetProperty("text").GetProperty("description").GetString());
        Assert.Equal("number", props.GetProperty("count").GetProperty("type").GetString());
        Assert.Equal(3, props.GetProperty("count").GetProperty("default").GetInt32());
        Assert.Equal("boolean", props.GetProperty("flag").GetProperty("type").GetString());
        Assert.False(props.GetProperty("flag").GetProperty("default").GetBoolean());
        Assert.Equal("array", props.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("obj").GetProperty("type").GetString()); // anything else -> string
        Assert.False(props.TryGetProperty("value", out _));
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
    }

    [Fact]
    public async Task InputSchema_RequiredParameters()
    {
        var schema = (await Mcp.ToolsAsync(Client)).Single(t => t.GetProperty("name").GetString() == "calc_add").GetProperty("inputSchema");
        Assert.Equal(["a", "b"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("a").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ServerInfo_HasReadOnlyAnnotation()
    {
        var tool = (await Mcp.ToolsAsync(Client)).Single(t => t.GetProperty("name").GetString() == "server_info");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // --- tools/call ---------------------------------------------------------

    [Fact]
    public async Task Call_ValueTool()
    {
        var result = await Mcp.CallAsync(Client, "echo", new { value = "hello world" });
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("echo:hello world|raw:echo \"hello world\"", Mcp.Text(result));
        Assert.False(result.TryGetProperty("structuredContent", out _));
    }

    [Fact]
    public async Task Call_SubCommandWithArgs()
    {
        var result = await Mcp.CallAsync(Client, "calc_add", new { a = 2, b = 3.5 });
        Assert.Equal("5.5|chain:add", Mcp.Text(result));
    }

    [Fact]
    public async Task Call_AllTypes_RawCommand()
    {
        var result = await Mcp.CallAsync(Client, "types", new { text = "a b", count = 4, flag = true, tags = new[] { "x", "y" } });
        var text = Mcp.Text(result);
        Assert.Contains("count=4", text);
        Assert.Contains("text=a b", text);
        Assert.Contains("|raw:types --text=\"a b\" --count=4 --flag --tags=x,y", text);
    }

    [Fact]
    public async Task Call_StructuredContent_ForSingleJsonObject()
    {
        var result = await Mcp.CallAsync(Client, "info");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.Equal("demo", structured.GetProperty("name").GetString());
        Assert.Equal(2, structured.GetProperty("count").GetInt32());
        Assert.Contains("\"name\": \"demo\"", Mcp.Text(result)); // pretty-printed
    }

    [Fact]
    public async Task Call_RendersOutputsAsText_AndExitCodeIsError()
    {
        var result = await Mcp.CallAsync(Client, "rich");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("title\nA | B\n1 | 2\n3 | 4\nx\ny\nk: v\n[\n  1,\n  2\n]", Mcp.Text(result));
        Assert.False(result.TryGetProperty("structuredContent", out _)); // json array, not object
    }

    [Fact]
    public async Task Call_ProcessorException_IsErrorResult()
    {
        var result = await Mcp.CallAsync(Client, "boom");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("kaboom", Mcp.Text(result));
    }

    [Fact]
    public async Task Call_ServerInfo()
    {
        var result = await Mcp.CallAsync(Client, "server_info");
        var s = result.GetProperty("structuredContent");
        Assert.Equal("qodalis-cli-server", s.GetProperty("name").GetString());
        var features = s.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("mcp", features);
        Assert.Contains("commands", features);
        Assert.DoesNotContain("filesystem", features);
        var commands = s.GetProperty("commands").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("echo", commands);
        Assert.Contains("calc", commands);
        Assert.True(s.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task Call_UnknownTool_InvalidParams()
    {
        var (code, message) = await Mcp.ErrorAsync(Client, "tools/call", new { name = "nope" });
        Assert.Equal(-32602, code);
        Assert.Equal("Unknown tool: nope", message);
    }

    [Fact]
    public async Task Call_ParentWithoutTool_IsUnknown()
    {
        var (code, _) = await Mcp.ErrorAsync(Client, "tools/call", new { name = "calc" });
        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task Call_MissingName_InvalidParams()
    {
        var (code, _) = await Mcp.ErrorAsync(Client, "tools/call", new { arguments = new { } });
        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task Call_ArgumentsNotObject_InvalidParams()
    {
        var (code, _) = await Mcp.ErrorAsync(Client, "tools/call", new { name = "echo", arguments = new[] { 1 } });
        Assert.Equal(-32602, code);
    }

    [Theory]
    [InlineData("{\"a\":\"2\",\"b\":3}", "must be a number")]
    [InlineData("{\"a\":2}", "missing required argument 'b'")]
    [InlineData("{\"a\":2,\"b\":3,\"c\":4}", "unknown argument 'c'")]
    [InlineData("{\"a\":null,\"b\":3}", "missing required argument 'a'")]
    public async Task Call_InvalidArguments_InvalidParams(string args, string reason)
    {
        var body = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"calc_add\",\"arguments\":{args}}}}}";
        var json = await RawAsync(body);
        Assert.Equal(-32602, json.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(reason, json.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("{\"flag\":\"yes\"}", "must be a boolean")]
    [InlineData("{\"tags\":[1,2]}", "must be an array of strings")]
    [InlineData("{\"text\":5}", "must be a string")]
    public async Task Call_WrongTypes_InvalidParams(string args, string reason)
    {
        var body = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"types\",\"arguments\":{args}}}}}";
        var json = await RawAsync(body);
        Assert.Equal(-32602, json.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(reason, json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Call_MissingValue_InvalidParams()
    {
        var (code, message) = await Mcp.ErrorAsync(Client, "tools/call", new { name = "echo", arguments = new { } });
        Assert.Equal(-32602, code);
        Assert.Contains("value", message);
    }
}

// ---------------------------------------------------------------------------
// Naming, options, static token, features
// ---------------------------------------------------------------------------

public class McpToolNamingTests
{
    [Theory]
    [InlineData(new[] { "weather" }, "weather")]
    [InlineData(new[] { "weather", "current" }, "weather_current")]
    [InlineData(new[] { "Weird Name!", "Sub.Cmd" }, "weird-name-_sub-cmd")]
    [InlineData(new[] { "UPPER", "a-b" }, "upper_a-b")]
    public void ToolName_Rules(string[] path, string expected) =>
        Assert.Equal(expected, CommandsMcpToolProvider.ToolName(path));

    [Fact]
    public void ToolName_TruncatedTo64() =>
        Assert.Equal(64, CommandsMcpToolProvider.ToolName([new string('a', 50), new string('b', 50)]).Length);

    [Theory]
    [InlineData("weather_current", "weather_*", true)]
    [InlineData("weather", "weather_*", false)]
    [InlineData("weather", "weather", true)]
    [InlineData("weather", "*", true)]
    [InlineData("other", "weather*", false)]
    public void Patterns(string name, string pattern, bool matches) =>
        Assert.Equal(matches, CliMcpServer.MatchesPattern(name, pattern));

    [Fact]
    public async Task Collision_FirstWins_AndNodesWithParametersOrValueAreTools()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddProcessor(new McpNamedProcessor("a_b"));
            cli.AddProcessor(new McpNamedProcessor("a", new McpNamedProcessor("b")));
            cli.AddProcessor(new McpEchoProcessor());
            cli.AddMcp();
        });
        var tools = await Mcp.ToolsAsync(h.Client);
        var ab = tools.Where(t => t.GetProperty("name").GetString() == "a_b").ToList();
        Assert.Single(ab);
        Assert.Equal("cmd a_b", ab[0].GetProperty("description").GetString());
        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString() == "a");
        Assert.Equal("ran a_b", Mcp.Text(await Mcp.CallAsync(h.Client, "a_b")));
    }

    [Fact]
    public async Task AllowAndDenyLists()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            Mcp.AddTestProcessors(cli);
            cli.AddMcp(o =>
            {
                o.Tools = ["calc_*", "echo", "types", "server_info"];
                o.Exclude = ["types"];
            });
        });
        var names = await Mcp.ToolNamesAsync(h.Client);
        Assert.Equal(["calc_add", "echo", "server_info"], names.OrderBy(n => n).ToArray());

        var (code, message) = await Mcp.ErrorAsync(h.Client, "tools/call", new { name = "types" });
        Assert.Equal(-32602, code);
        Assert.Equal("Unknown tool: types", message);
    }

    [Fact]
    public async Task NotEnabled_Returns404()
    {
        await using var h = await McpPlainHost.StartAsync(Mcp.AddTestProcessors);
        Assert.Equal(404, (int)(await Mcp.PostAsync(h.Client, Mcp.Request("ping"))).StatusCode);
    }

    [Fact]
    public async Task Disabled_Returns404()
    {
        await using var h = await McpPlainHost.StartAsync(cli => cli.AddMcp(o => o.Enabled = false));
        Assert.Equal(404, (int)(await Mcp.PostAsync(h.Client, Mcp.Request("ping"))).StatusCode);
    }

    [Fact]
    public async Task StaticToken_WithoutUsersPlugin()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            Mcp.AddTestProcessors(cli);
            cli.AddMcp(o => o.Token = "s3cret-token");
        });
        var missing = await Mcp.PostAsync(h.Client, Mcp.Request("ping"));
        Assert.Equal(401, (int)missing.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await missing.Content.ReadAsStringAsync());
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());

        Assert.Equal(401, (int)(await Mcp.PostAsync(h.Client, Mcp.Request("ping"), "wrong")).StatusCode);
        Assert.Equal(401, (int)(await Mcp.PostAsync(h.Client, Mcp.Request("ping"), "s3cret-token-longer")).StatusCode);
        Assert.Equal(200, (int)(await Mcp.PostAsync(h.Client, Mcp.Request("ping"), "s3cret-token")).StatusCode);
        Assert.Equal("echo:x|raw:echo x", Mcp.Text(await Mcp.CallAsync(h.Client, "echo", new { value = "x" }, "s3cret-token")));
    }

    [Fact]
    public async Task CustomProvider_ToolsAndCollisionPriority()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            Mcp.AddTestProcessors(cli);
            cli.Services.AddMcpToolProvider<CustomMcpProvider>();
            cli.AddMcp();
        });
        var tools = await Mcp.ToolsAsync(h.Client);
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "custom_hello");
        var echo = Assert.Single(tools, t => t.GetProperty("name").GetString() == "echo");
        Assert.Equal("Plugin echo", echo.GetProperty("description").GetString());

        Assert.Equal("hello bob", Mcp.Text(await Mcp.CallAsync(h.Client, "custom_hello", new { who = "bob" })));
        Assert.Equal("plugin echo", Mcp.Text(await Mcp.CallAsync(h.Client, "echo")));

        // Arguments are validated against the provider's schema first.
        var (code, _) = await Mcp.ErrorAsync(h.Client, "tools/call", new { name = "custom_hello", arguments = new { } });
        Assert.Equal(-32602, code);
    }
}

// ---------------------------------------------------------------------------
// Feature providers: filesystem, data explorer, jobs
// ---------------------------------------------------------------------------

public class McpFeatureToolsTests
{
    private static Task<McpPlainHost> FsHostAsync() => McpPlainHost.StartAsync(cli =>
    {
        cli.AddFileSystem(o => o.UseInMemory());
        cli.AddMcp();
    });

    [Fact]
    public async Task FileSystemTools_ListedWithAnnotations()
    {
        await using var h = await FsHostAsync();
        var tools = await Mcp.ToolsAsync(h.Client);
        string[] expected = ["fs_list", "fs_stat", "fs_read", "fs_write", "fs_mkdir", "fs_delete"];
        foreach (var name in expected) Assert.Contains(tools, t => t.GetProperty("name").GetString() == name);
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "fs_read").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "fs_delete").GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        var readSchema = tools.Single(t => t.GetProperty("name").GetString() == "fs_read").GetProperty("inputSchema");
        Assert.Equal(262144, readSchema.GetProperty("properties").GetProperty("maxBytes").GetProperty("default").GetInt32());

        var features = (await Mcp.CallAsync(h.Client, "server_info")).GetProperty("structuredContent").GetProperty("features")
            .EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("filesystem", features);
    }

    [Fact]
    public async Task FileSystemTools_RoundTrip()
    {
        await using var h = await FsHostAsync();
        Assert.False((await Mcp.CallAsync(h.Client, "fs_mkdir", new { path = "/work/docs" })).GetProperty("isError").GetBoolean());
        var write = await Mcp.CallAsync(h.Client, "fs_write", new { path = "/work/docs/a.txt", content = "héllo" });
        Assert.False(write.GetProperty("isError").GetBoolean());

        var read = await Mcp.CallAsync(h.Client, "fs_read", new { path = "/work/docs/a.txt" });
        Assert.False(read.GetProperty("isError").GetBoolean());
        Assert.Equal("héllo", Mcp.Text(read));

        var truncated = await Mcp.CallAsync(h.Client, "fs_read", new { path = "/work/docs/a.txt", maxBytes = 2 });
        Assert.StartsWith("h\n[truncated", Mcp.Text(truncated)); // the cut 2-byte 'é' is dropped

        var list = await Mcp.CallAsync(h.Client, "fs_list", new { path = "/work/docs" });
        Assert.Contains("a.txt", Mcp.Text(list));
        Assert.Equal("a.txt", list.GetProperty("structuredContent").GetProperty("entries")[0].GetProperty("name").GetString());

        var stat = await Mcp.CallAsync(h.Client, "fs_stat", new { path = "/work/docs/a.txt" });
        Assert.Contains("type: file", Mcp.Text(stat));
        Assert.Equal("file", stat.GetProperty("structuredContent").GetProperty("type").GetString());

        var delete = await Mcp.CallAsync(h.Client, "fs_delete", new { path = "/work/docs/a.txt" });
        Assert.False(delete.GetProperty("isError").GetBoolean());
        var missing = await Mcp.CallAsync(h.Client, "fs_read", new { path = "/work/docs/a.txt" });
        Assert.True(missing.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task FileSystemTools_BinaryFile_IsError()
    {
        await using var h = await FsHostAsync();
        var storage = h.Host.Services.GetRequiredService<Qodalis.Cli.Plugin.FileSystem.IFileStorageProvider>();
        await storage.UploadFileAsync("/bin.dat", [0x00, 0x01, 0xFF, 0x02]);
        var result = await Mcp.CallAsync(h.Client, "fs_read", new { path = "/bin.dat" });
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("binary file, 4 bytes", Mcp.Text(result));
    }

    [Fact]
    public async Task FileSystemTools_InvalidArguments()
    {
        await using var h = await FsHostAsync();
        var (code, _) = await Mcp.ErrorAsync(h.Client, "tools/call", new { name = "fs_read", arguments = new { path = "/x", maxBytes = 1.5 } });
        Assert.Equal(-32602, code);
        (code, _) = await Mcp.ErrorAsync(h.Client, "tools/call", new { name = "fs_write", arguments = new { path = "/x" } });
        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task DataExplorerTools()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddDataExplorerProvider(new FakeDataExplorerProvider(), o =>
            {
                o.Name = "db";
                o.Description = "Test database";
                o.MaxRows = 2;
            });
            cli.AddMcp();
        });
        var names = await Mcp.ToolNamesAsync(h.Client);
        Assert.Contains("data_sources", names);
        Assert.Contains("data_schema", names);
        Assert.Contains("data_query", names);

        Assert.Contains("db (sql): Test database", Mcp.Text(await Mcp.CallAsync(h.Client, "data_sources")));
        Assert.Contains("users (table): id int PK", Mcp.Text(await Mcp.CallAsync(h.Client, "data_schema", new { source = "db" })));

        var query = await Mcp.CallAsync(h.Client, "data_query", new { source = "db", query = "select" });
        Assert.False(query.GetProperty("isError").GetBoolean());
        // Same executor limits as the HTTP route: MaxRows = 2.
        Assert.Equal("id | name\n1 | alice\n2 | bob\n(2 rows, truncated)", Mcp.Text(query));
        Assert.True(query.GetProperty("structuredContent").GetProperty("truncated").GetBoolean());

        var failed = await Mcp.CallAsync(h.Client, "data_query", new { source = "db", query = "fail" });
        Assert.True(failed.GetProperty("isError").GetBoolean());
        Assert.Contains("syntax error", Mcp.Text(failed));

        var unknown = await Mcp.CallAsync(h.Client, "data_schema", new { source = "nope" });
        Assert.True(unknown.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task JobsTools()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddJob(new McpNoopJob(), o =>
            {
                o.Name = "nightly";
                o.Interval = TimeSpan.FromHours(1);
            });
            cli.AddMcp();
        });
        var tools = await Mcp.ToolsAsync(h.Client);
        string[] expected = ["jobs_list", "jobs_get", "jobs_history", "jobs_trigger", "jobs_pause", "jobs_resume", "jobs_stop", "jobs_cancel"];
        foreach (var name in expected) Assert.Contains(tools, t => t.GetProperty("name").GetString() == name);
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "jobs_cancel").GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());

        var list = await Mcp.CallAsync(h.Client, "jobs_list");
        var job = list.GetProperty("structuredContent").GetProperty("jobs")[0];
        var id = job.GetProperty("id").GetString()!;
        Assert.Equal("nightly", job.GetProperty("name").GetString());

        Assert.False((await Mcp.CallAsync(h.Client, "jobs_pause", new { id })).GetProperty("isError").GetBoolean());
        Assert.Equal("paused", (await Mcp.CallAsync(h.Client, "jobs_get", new { id })).GetProperty("structuredContent").GetProperty("status").GetString());
        var again = await Mcp.CallAsync(h.Client, "jobs_pause", new { id });
        Assert.True(again.GetProperty("isError").GetBoolean());
        Assert.False((await Mcp.CallAsync(h.Client, "jobs_resume", new { id })).GetProperty("isError").GetBoolean());

        var history = await Mcp.CallAsync(h.Client, "jobs_history", new { id, limit = 5 });
        Assert.False(history.GetProperty("isError").GetBoolean());

        var missing = await Mcp.CallAsync(h.Client, "jobs_get", new { id = "nope" });
        Assert.True(missing.GetProperty("isError").GetBoolean());

        var features = (await Mcp.CallAsync(h.Client, "server_info")).GetProperty("structuredContent").GetProperty("features")
            .EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("jobs", features);
    }
}

// ---------------------------------------------------------------------------
// With the users plugin (authentication provider)
// ---------------------------------------------------------------------------

public class McpUsersTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(configureCli: cli =>
        {
            Mcp.AddTestProcessors(cli);
            cli.AddFileSystem(o => o.UseInMemory());
            cli.AddMcp(o => o.Token = "ignored-with-users");
        });
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<string> ApiTokenAsync(string session, object body)
    {
        var res = await _h.PostAsync("tokens", session, body);
        Assert.Equal(201, (int)res.StatusCode);
        return (await UsersTestHost.ReadJsonAsync(res)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var res = await Mcp.PostAsync(_h.Client, Mcp.Request("ping"));
        await UsersTestHost.AssertErrorAsync(res, 401);
        // The static token is not used when an authentication provider protects the server.
        await UsersTestHost.AssertErrorAsync(await Mcp.PostAsync(_h.Client, Mcp.Request("ping"), "ignored-with-users"), 401);
    }

    [Fact]
    public async Task WithoutCommandsExecute_Returns403()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "no-exec", "users:read");
        await UsersTestHost.AssertErrorAsync(await Mcp.PostAsync(_h.Client, Mcp.Request("ping"), user), 403);
    }

    [Fact]
    public async Task GetAndDelete_Return405()
    {
        var res = await _h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Mcp.Path));
        Assert.Equal(405, (int)res.StatusCode);
        Assert.Contains("POST", res.Content.Headers.Allow);
        Assert.Equal(405, (int)(await _h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, Mcp.Path))).StatusCode);
    }

    [Fact]
    public async Task Session_ListsAllToolsForAdmin()
    {
        var names = await Mcp.ToolNamesAsync(_h.Client, _root);
        Assert.Contains("secret", names);
        Assert.Contains("echo", names);
        Assert.Contains("fs_read", names);
        Assert.Contains("fs_write", names);
        Assert.Contains("server_info", names);
    }

    [Fact]
    public async Task ToolsFilteredByPermissions()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "plain", "commands:execute", "files:read");
        var names = await Mcp.ToolNamesAsync(_h.Client, user);
        Assert.Contains("echo", names);
        Assert.Contains("fs_read", names);
        Assert.Contains("server_info", names);
        Assert.DoesNotContain("secret", names);
        Assert.DoesNotContain("fs_write", names);
        Assert.DoesNotContain("fs_delete", names);

        var (code, message) = await Mcp.ErrorAsync(_h.Client, "tools/call", new { name = "secret" }, user);
        Assert.Equal(-32602, code);
        Assert.Equal("Unknown tool: secret", message);

        // Granting the permission makes the tool visible and callable.
        Assert.Equal(200, (int)(await _h.PutAsync("groups/plain-group", _root,
            new { permissions = new[] { "commands:execute", "files:read", "secret:read" } })).StatusCode);
        Assert.Equal("the secret", Mcp.Text(await Mcp.CallAsync(_h.Client, "secret", null, user)));
    }

    [Fact]
    public async Task ApiToken_Authenticates_AndScopeFiltersTools()
    {
        var full = await ApiTokenAsync(_root, new { name = "mcp" });
        Assert.Contains("secret", await Mcp.ToolNamesAsync(_h.Client, full));
        Assert.Equal("echo:hi|raw:echo hi", Mcp.Text(await Mcp.CallAsync(_h.Client, "echo", new { value = "hi" }, full)));

        var scoped = await ApiTokenAsync(_root, new { name = "mcp-scoped", permissions = new[] { "commands:execute" } });
        var names = await Mcp.ToolNamesAsync(_h.Client, scoped);
        Assert.Contains("echo", names);
        Assert.DoesNotContain("secret", names);
        Assert.DoesNotContain("fs_read", names);
    }

    [Fact]
    public async Task RevokedApiToken_Returns401()
    {
        var res = await _h.PostAsync("tokens", _root, new { name = "gone" });
        var body = await UsersTestHost.ReadJsonAsync(res);
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(200, (int)(await Mcp.PostAsync(_h.Client, Mcp.Request("ping"), token)).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{body.GetProperty("apiToken").GetProperty("id").GetString()}", _root)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await Mcp.PostAsync(_h.Client, Mcp.Request("ping"), token), 401);
    }

    [Fact]
    public async Task Initialize_WithSession()
    {
        var result = await Mcp.ResultAsync(_h.Client, "initialize", new { protocolVersion = "2025-06-18" }, _root);
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
    }
}
