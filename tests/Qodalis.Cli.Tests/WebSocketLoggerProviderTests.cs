using Microsoft.Extensions.Logging;

using Qodalis.Cli.Logging;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

public class WebSocketLoggerProviderTests
{
    [Fact]
    public void CreateLogger_ReturnsNonNull()
    {
        using var manager = new CliLogSocketManager();
        using var provider = new WebSocketLoggerProvider(manager);

        var logger = provider.CreateLogger("TestCategory");

        Assert.NotNull(logger);
    }

    [Fact]
    public void CreateLogger_ReturnsDifferentInstancesForDifferentCategories()
    {
        using var manager = new CliLogSocketManager();
        using var provider = new WebSocketLoggerProvider(manager);

        var logger1 = provider.CreateLogger("Category1");
        var logger2 = provider.CreateLogger("Category2");

        Assert.NotSame(logger1, logger2);
    }

    [Theory]
    [InlineData(LogLevel.Trace, true)]
    [InlineData(LogLevel.Debug, true)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void IsEnabled_ReturnsExpected(LogLevel level, bool expected)
    {
        using var manager = new CliLogSocketManager();
        var logger = new WebSocketLogger("Test", manager);

        Assert.Equal(expected, logger.IsEnabled(level));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "verbose")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "information")]
    [InlineData(LogLevel.Warning, "warning")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "fatal")]
    public void MapLogLevel_MapsCorrectly(LogLevel input, string expected)
    {
        Assert.Equal(expected, WebSocketLogger.MapLogLevel(input));
    }

    [Fact]
    public void BeginScope_ReturnsNull()
    {
        using var manager = new CliLogSocketManager();
        var logger = new WebSocketLogger("Test", manager);

        var scope = logger.BeginScope("scope");

        Assert.Null(scope);
    }
}
