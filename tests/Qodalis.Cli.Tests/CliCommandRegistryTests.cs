using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class CliCommandRegistryTests
{
    private readonly CliCommandRegistry _registry = new(NullLogger<CliCommandRegistry>.Instance);

    [Fact]
    public void Register_And_FindProcessor_ByName()
    {
        var proc = new TestProcessor("echo", "Echo command");
        _registry.Register(proc);

        var found = _registry.FindProcessor("echo");
        Assert.NotNull(found);
        Assert.Equal("echo", found.Command);
    }

    [Fact]
    public void FindProcessor_IsCaseInsensitive()
    {
        _registry.Register(new TestProcessor("Echo", "Echo"));

        Assert.NotNull(_registry.FindProcessor("echo"));
        Assert.NotNull(_registry.FindProcessor("ECHO"));
        Assert.NotNull(_registry.FindProcessor("Echo"));
    }

    [Fact]
    public void FindProcessor_ReturnsNull_ForUnknownCommand()
    {
        Assert.Null(_registry.FindProcessor("nonexistent"));
    }

    [Fact]
    public void Processors_ListsAllRegistered()
    {
        _registry.Register(new TestProcessor("a", "A"));
        _registry.Register(new TestProcessor("b", "B"));

        Assert.Equal(2, _registry.Processors.Count);
    }

    [Fact]
    public void Register_OverwritesDuplicate()
    {
        _registry.Register(new TestProcessor("echo", "Old"));
        _registry.Register(new TestProcessor("echo", "New"));

        Assert.Single(_registry.Processors);
        Assert.Equal("New", _registry.Processors[0].Description);
    }

    [Fact]
    public void FindProcessor_ResolvesChainCommands()
    {
        var child = new TestProcessor("sub", "Sub command");
        var parent = new TestProcessor("parent", "Parent")
        {
            Processors = new[] { child }
        };
        _registry.Register(parent);

        var found = _registry.FindProcessor("parent", new[] { "sub" });
        Assert.NotNull(found);
        Assert.Equal("sub", found.Command);
    }

    [Fact]
    public void FindProcessor_ChainReturnsParent_WhenChildNotFound()
    {
        var parent = new TestProcessor("parent", "Parent")
        {
            Processors = Array.Empty<ICliCommandProcessor>()
        };
        _registry.Register(parent);

        var found = _registry.FindProcessor("parent", new[] { "unknown" });
        Assert.NotNull(found);
        Assert.Equal("parent", found.Command);
    }
}
