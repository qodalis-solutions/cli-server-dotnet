using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class ApiVersionTests
{
    [Fact]
    public void DefaultApiVersion_IsOne()
    {
        var proc = new TestProcessor("test", "Test");
        Assert.Equal(1, proc.ApiVersion);
    }

    [Fact]
    public void ApiVersion_CanBeOverridden()
    {
        var proc = new TestProcessor("test", "Test", apiVersion: 2);
        Assert.Equal(2, proc.ApiVersion);
    }

    [Fact]
    public void DefaultVersion_Is100()
    {
        var proc = new TestProcessor("test", "Test");
        Assert.Equal("1.0.0", proc.Version);
    }
}
