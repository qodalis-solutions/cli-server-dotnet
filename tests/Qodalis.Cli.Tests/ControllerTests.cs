using System.Text.Json;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Qodalis.Cli.Tests;

public class ControllerTests
{
    private readonly CliCommandRegistry _registry;
    private readonly CliCommandExecutorService _executor;
    private readonly ICliServerInfoService _serverInfo;

    public ControllerTests()
    {
        _registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        _registry.Register(new TestProcessor("echo", "Echo command", apiVersion: 1));
        _registry.Register(new TestProcessor("v2cmd", "V2 only command", apiVersion: 2));
        _executor = new CliCommandExecutorService(_registry, NullLogger<CliCommandExecutorService>.Instance, Array.Empty<ICliProcessorFilter>());
        _serverInfo = new CliServerInfoService();
    }

    // --- V1 Controller ---

    [Fact]
    public void V1_GetVersion_Returns100()
    {
        var controller = new CliController(_registry, _executor, _serverInfo, NullLogger<CliController>.Instance);
        var result = controller.GetVersion() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("1.0.0", json);
    }

    [Fact]
    public void V1_GetCommands_ReturnsAllCommands()
    {
        var controller = new CliController(_registry, _executor, _serverInfo, NullLogger<CliController>.Instance);
        var result = controller.GetCommands() as OkObjectResult;

        Assert.NotNull(result);
        var commands = result.Value as System.Collections.IList;
        Assert.NotNull(commands);
        Assert.Equal(2, commands.Count);
    }

    [Fact]
    public async Task V1_Execute_KnownCommand_ReturnsSuccess()
    {
        var controller = new CliController(_registry, _executor, _serverInfo, NullLogger<CliController>.Instance);
        var result = await controller.ExecuteAsync(
            new CliProcessCommand { Command = "echo" },
            CancellationToken.None) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as Qodalis.Cli.Models.CliServerResponse;
        Assert.NotNull(response);
        Assert.Equal(0, response.ExitCode);
    }

    [Fact]
    public async Task V1_Execute_UnknownCommand_ReturnsError()
    {
        var controller = new CliController(_registry, _executor, _serverInfo, NullLogger<CliController>.Instance);
        var result = await controller.ExecuteAsync(
            new CliProcessCommand { Command = "nonexistent" },
            CancellationToken.None) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as Qodalis.Cli.Models.CliServerResponse;
        Assert.NotNull(response);
        Assert.Equal(1, response.ExitCode);
    }

    // --- V2 Controller ---

    [Fact]
    public void V2_GetVersion_ReturnsApiVersion2()
    {
        var controller = new CliControllerV2(_registry, _executor, _serverInfo, NullLogger<CliControllerV2>.Instance);
        var result = controller.GetVersion() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"ApiVersion\":2", json);
        Assert.Contains("\"ServerVersion\":\"1.0.0\"", json);
    }

    [Fact]
    public void V2_GetCommands_ReturnsOnlyV2Plus()
    {
        var controller = new CliControllerV2(_registry, _executor, _serverInfo, NullLogger<CliControllerV2>.Instance);
        var result = controller.GetCommands() as OkObjectResult;

        Assert.NotNull(result);
        var commands = result.Value as System.Collections.IList;
        Assert.NotNull(commands);
        Assert.Single(commands); // only v2cmd, not echo (apiVersion 1)
    }

    // --- Version Discovery ---

    [Fact]
    public void VersionDiscovery_ReturnsSupportedVersions()
    {
        var controller = new CliVersionController();
        var result = controller.GetVersions() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"SupportedVersions\":[1]", json);
        Assert.Contains("\"PreferredVersion\":1", json);
        Assert.Contains("\"ServerVersion\":\"1.0.0\"", json);
    }

    // --- V1 Streaming ---

    private CliController CreateControllerWithHttpContext(CliCommandRegistry registry, CliCommandExecutorService executor)
    {
        var controller = new CliController(registry, executor, _serverInfo, NullLogger<CliController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        return controller;
    }

    private static List<(string EventType, string Data)> ParseSseEvents(string text)
    {
        var events = new List<(string, string)>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var eventType = "message";
            var data = "";
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ")) eventType = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
            }
            if (!string.IsNullOrEmpty(data))
                events.Add((eventType, data));
        }
        return events;
    }

    [Fact]
    public async Task V1_ExecuteStream_KnownCommand_ReturnsSseEvents()
    {
        var controller = CreateControllerWithHttpContext(_registry, _executor);

        await controller.ExecuteStream(new CliProcessCommand { Command = "echo" });

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        Assert.Contains(events, e => e.EventType == "output");
        Assert.Contains(events, e => e.EventType == "done" && e.Data.Contains("\"exitCode\":0"));
    }

    [Fact]
    public async Task V1_ExecuteStream_UnknownCommand_ReturnsSseError()
    {
        var controller = CreateControllerWithHttpContext(_registry, _executor);

        await controller.ExecuteStream(new CliProcessCommand { Command = "nonexistent" });

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        Assert.Contains(events, e => e.EventType == "error" && e.Data.Contains("Unknown command"));
    }

    [Fact]
    public async Task V1_ExecuteStream_StreamCapableProcessor_EmitsChunks()
    {
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new TestStreamProcessor());
        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, Array.Empty<ICliProcessorFilter>());

        var controller = CreateControllerWithHttpContext(registry, executor);

        await controller.ExecuteStream(new CliProcessCommand { Command = "stream-test" });

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        var outputEvents = events.Where(e => e.EventType == "output").ToList();
        Assert.Equal(3, outputEvents.Count);

        var doneEvents = events.Where(e => e.EventType == "done").ToList();
        Assert.Single(doneEvents);
        Assert.Contains("\"exitCode\":0", doneEvents[0].Data);
    }
}
