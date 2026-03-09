using System.Text.Json;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

public class CliLogSocketManagerTests
{
    [Fact]
    public void Constructor_CreatesInstance()
    {
        using var manager = new CliLogSocketManager();
        Assert.NotNull(manager);
    }

    [Theory]
    [InlineData(null, "information", true)]
    [InlineData("", "information", true)]
    [InlineData("verbose", "verbose", true)]
    [InlineData("verbose", "debug", true)]
    [InlineData("verbose", "information", true)]
    [InlineData("verbose", "warning", true)]
    [InlineData("verbose", "error", true)]
    [InlineData("verbose", "fatal", true)]
    [InlineData("debug", "verbose", false)]
    [InlineData("debug", "debug", true)]
    [InlineData("debug", "information", true)]
    [InlineData("information", "verbose", false)]
    [InlineData("information", "debug", false)]
    [InlineData("information", "information", true)]
    [InlineData("information", "warning", true)]
    [InlineData("warning", "information", false)]
    [InlineData("warning", "warning", true)]
    [InlineData("warning", "error", true)]
    [InlineData("error", "warning", false)]
    [InlineData("error", "error", true)]
    [InlineData("error", "fatal", true)]
    [InlineData("fatal", "error", false)]
    [InlineData("fatal", "fatal", true)]
    public void ShouldSendLog_FiltersCorrectly(string? filterLevel, string logLevel, bool expected)
    {
        var result = CliLogSocketManager.ShouldSendLog(filterLevel, logLevel);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldSendLog_UnknownFilterLevel_ReturnsTrue()
    {
        Assert.True(CliLogSocketManager.ShouldSendLog("unknown", "information"));
    }

    [Fact]
    public void ShouldSendLog_UnknownLogLevel_ReturnsTrue()
    {
        Assert.True(CliLogSocketManager.ShouldSendLog("information", "unknown"));
    }

    [Fact]
    public void ShouldSendLog_BothUnknown_ReturnsTrue()
    {
        Assert.True(CliLogSocketManager.ShouldSendLog("foo", "bar"));
    }

    [Fact]
    public void FormatLogMessage_ReturnsValidJson()
    {
        var json = CliLogSocketManager.FormatLogMessage("information", "Test message", "TestCategory");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("log", root.GetProperty("type").GetString());
        Assert.Equal("information", root.GetProperty("level").GetString());
        Assert.Equal("Test message", root.GetProperty("message").GetString());
        Assert.Equal("TestCategory", root.GetProperty("category").GetString());
        Assert.True(root.TryGetProperty("timestamp", out var timestamp));
        Assert.False(string.IsNullOrEmpty(timestamp.GetString()));
    }

    [Fact]
    public void FormatLogMessage_TimestampIsIso8601()
    {
        var json = CliLogSocketManager.FormatLogMessage("debug", "msg", "cat");
        var doc = JsonDocument.Parse(json);
        var timestampStr = doc.RootElement.GetProperty("timestamp").GetString();

        Assert.NotNull(timestampStr);
        Assert.True(DateTime.TryParse(timestampStr, out _));
    }
}
