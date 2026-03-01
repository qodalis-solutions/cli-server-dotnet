using System.Text.Json;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Qodalis.Cli.Tests;

public class ControllerTests
{
    private readonly CliCommandRegistry _registry;
    private readonly CliCommandExecutorService _executor;

    public ControllerTests()
    {
        _registry = new CliCommandRegistry();
        _registry.Register(new TestProcessor("echo", "Echo command", apiVersion: 1));
        _registry.Register(new TestProcessor("v2cmd", "V2 only command", apiVersion: 2));
        _executor = new CliCommandExecutorService(_registry);
    }

    // --- V1 Controller ---

    [Fact]
    public void V1_GetVersion_Returns100()
    {
        var controller = new CliController(_registry, _executor);
        var result = controller.GetVersion() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("1.0.0", json);
    }

    [Fact]
    public void V1_GetCommands_ReturnsAllCommands()
    {
        var controller = new CliController(_registry, _executor);
        var result = controller.GetCommands() as OkObjectResult;

        Assert.NotNull(result);
        var commands = result.Value as System.Collections.IList;
        Assert.NotNull(commands);
        Assert.Equal(2, commands.Count);
    }

    [Fact]
    public async Task V1_Execute_KnownCommand_ReturnsSuccess()
    {
        var controller = new CliController(_registry, _executor);
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
        var controller = new CliController(_registry, _executor);
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
        var controller = new CliControllerV2(_registry, _executor);
        var result = controller.GetVersion() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("2", json);
        Assert.Contains("2.0.0", json);
    }

    [Fact]
    public void V2_GetCommands_ReturnsOnlyV2Plus()
    {
        var controller = new CliControllerV2(_registry, _executor);
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
        Assert.Contains("1", json);
        Assert.Contains("2", json);
        Assert.Contains("2.0.0", json);
    }
}
