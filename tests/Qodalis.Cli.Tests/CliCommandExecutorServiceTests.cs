using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class CliCommandExecutorServiceTests
{
    private readonly CliCommandRegistry _registry = new();
    private readonly CliCommandExecutorService _executor;

    public CliCommandExecutorServiceTests()
    {
        _executor = new CliCommandExecutorService(_registry, NullLogger<CliCommandExecutorService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_KnownCommand_ReturnsSuccess()
    {
        _registry.Register(new TestProcessor("echo", "Echo", (cmd, _) => Task.FromResult($"Hello {cmd.Value}")));

        var response = await _executor.ExecuteAsync(new CliProcessCommand { Command = "echo", Value = "world" });

        Assert.Equal(0, response.ExitCode);
        Assert.Single(response.Outputs);
        var text = Assert.IsType<TextOutput>(response.Outputs[0]);
        Assert.Equal("Hello world", text.Value);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ReturnsError()
    {
        var response = await _executor.ExecuteAsync(new CliProcessCommand { Command = "unknown" });

        Assert.Equal(1, response.ExitCode);
        var text = Assert.IsType<TextOutput>(response.Outputs[0]);
        Assert.Contains("Unknown command", text.Value);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowingCommand_ReturnsError()
    {
        _registry.Register(new TestProcessor("fail", "Fails", (_, _) => throw new InvalidOperationException("boom")));

        var response = await _executor.ExecuteAsync(new CliProcessCommand { Command = "fail" });

        Assert.Equal(1, response.ExitCode);
        var text = Assert.IsType<TextOutput>(response.Outputs[0]);
        Assert.Contains("boom", text.Value);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResult_ReturnsNoOutput()
    {
        _registry.Register(new TestProcessor("noop", "No-op", (_, _) => Task.FromResult("")));

        var response = await _executor.ExecuteAsync(new CliProcessCommand { Command = "noop" });

        Assert.Equal(0, response.ExitCode);
        Assert.Empty(response.Outputs);
    }
}
