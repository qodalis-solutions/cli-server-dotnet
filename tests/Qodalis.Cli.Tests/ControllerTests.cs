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

        await controller.ExecuteStream(new CliProcessCommand { Command = "echo" }, CancellationToken.None);

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

        await controller.ExecuteStream(new CliProcessCommand { Command = "nonexistent" }, CancellationToken.None);

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

        await controller.ExecuteStream(new CliProcessCommand { Command = "stream-test" }, CancellationToken.None);

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        var outputEvents = events.Where(e => e.EventType == "output").ToList();
        Assert.Equal(3, outputEvents.Count);

        var doneEvents = events.Where(e => e.EventType == "done").ToList();
        Assert.Single(doneEvents);
        Assert.Contains("\"exitCode\":0", doneEvents[0].Data);
    }

    // --- Cancellation Tests ---

    [Fact]
    public async Task SlowStreamProcessor_WhenCancelled_StopsEmittingChunks()
    {
        var processor = new SlowStreamProcessor { TotalChunks = 10, ChunkDelayMs = 50 };
        using var cts = new CancellationTokenSource();

        var chunksReceived = 0;
        async Task Emit(object _)
        {
            chunksReceived++;
            // Cancel after 3 chunks
            if (chunksReceived == 3)
                cts.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.HandleStreamAsync(new CliProcessCommand { Command = "slow-stream" }, Emit, cts.Token));

        Assert.True(processor.ChunksEmitted <= 3,
            $"Expected at most 3 chunks before cancellation but got {processor.ChunksEmitted}");
    }

    [Fact]
    public async Task SlowStreamProcessor_ReceivesCancellationToken()
    {
        var processor = new SlowStreamProcessor { TotalChunks = 3, ChunkDelayMs = 0 };
        using var cts = new CancellationTokenSource();

        await processor.HandleStreamAsync(
            new CliProcessCommand { Command = "slow-stream" },
            _ => Task.CompletedTask,
            cts.Token);

        Assert.Equal(cts.Token, processor.ReceivedCancellationToken);
    }

    [Fact]
    public async Task SlowStreamProcessor_WhenAlreadyCancelled_ThrowsImmediately()
    {
        var processor = new SlowStreamProcessor { TotalChunks = 10, ChunkDelayMs = 0 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.HandleStreamAsync(
                new CliProcessCommand { Command = "slow-stream" },
                _ => Task.CompletedTask,
                cts.Token));

        Assert.Equal(0, processor.ChunksEmitted);
    }

    [Fact]
    public async Task V1_ExecuteStream_WhenCancelled_DoesNotEmitDoneEvent()
    {
        var slowProcessor = new SlowStreamProcessor { TotalChunks = 10, ChunkDelayMs = 50 };
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(slowProcessor);
        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, Array.Empty<ICliProcessorFilter>());

        var controller = CreateControllerWithHttpContext(registry, executor);

        using var cts = new CancellationTokenSource();

        // Cancel after a short delay to interrupt the slow processor mid-stream
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            cts.Cancel();
        });

        // ExecuteStream should complete without throwing (OperationCanceledException is caught internally)
        await controller.ExecuteStream(new CliProcessCommand { Command = "slow-stream" }, cts.Token);

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        // No "done" event should be emitted when cancelled
        Assert.DoesNotContain(events, e => e.EventType == "done");
    }

    [Fact]
    public async Task V1_ExecuteStream_WhenCancelledMidStream_EmitsPartialOutput()
    {
        var slowProcessor = new SlowStreamProcessor { TotalChunks = 10, ChunkDelayMs = 50 };
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(slowProcessor);
        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, Array.Empty<ICliProcessorFilter>());

        var controller = CreateControllerWithHttpContext(registry, executor);

        using var cts = new CancellationTokenSource();

        // Cancel after 80 ms — enough time for at least 1 chunk (50 ms apart) but not all 10
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            cts.Cancel();
        });

        await controller.ExecuteStream(new CliProcessCommand { Command = "slow-stream" }, cts.Token);

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);
        var outputEvents = events.Where(e => e.EventType == "output").ToList();

        // Some output was emitted before cancellation, but not all 10 chunks
        Assert.True(outputEvents.Count > 0, "Expected at least one output event before cancellation");
        Assert.True(outputEvents.Count < 10, $"Expected fewer than 10 output events but got {outputEvents.Count}");
    }

    [Fact]
    public async Task V1_ExecuteStream_WithPreCancelledToken_EmitsNoOutputAndNoDone()
    {
        var slowProcessor = new SlowStreamProcessor { TotalChunks = 10, ChunkDelayMs = 0 };
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(slowProcessor);
        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, Array.Empty<ICliProcessorFilter>());

        var controller = CreateControllerWithHttpContext(registry, executor);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await controller.ExecuteStream(new CliProcessCommand { Command = "slow-stream" }, cts.Token);

        controller.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        var events = ParseSseEvents(body);

        Assert.DoesNotContain(events, e => e.EventType == "done");
        Assert.DoesNotContain(events, e => e.EventType == "output");
    }
}
