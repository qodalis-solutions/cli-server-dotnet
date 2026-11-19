using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Tests;

public class PermissionCatalogTests
{
    private static readonly string[] CoreNames =
    [
        "commands:execute", "shell:execute", "files:read", "files:write", "data:query", "jobs:read",
        "jobs:manage", "logs:read", "admin:access", "audit:read", "audit:clear",
    ];

    private readonly ServiceCollection _services = new();
    private readonly CliBuilder _builder;

    public PermissionCatalogTests()
    {
        _builder = new CliBuilder(_services);
    }

    private ICliPermissionCatalog Resolve() =>
        _services.BuildServiceProvider().GetRequiredService<ICliPermissionCatalog>();

    [Fact]
    public void CoreEntries_AreRegisteredInOrder()
    {
        var catalog = Resolve();

        Assert.Equal(CoreNames, catalog.All.Select(p => p.Name));
        Assert.All(catalog.All, p => Assert.Equal("cli", p.Surface));
        var files = catalog.Find("FILES:READ")!;
        Assert.Equal("List, read and download files", files.Description);
        Assert.Equal("Files", files.Group);
    }

    [Fact]
    public void AddPermissions_AddsHostEntries()
    {
        _builder.AddPermissions(p => p.Group("Rates")
            .Add("rates:read", "Official exchange rates")
            .Add("banks:read", "Banks and their buy and sell rates"));

        var all = Resolve().All;
        Assert.Equal(13, all.Count);
        Assert.Equal(new CliPermission("rates:read", "Official exchange rates", "Rates", "host"), all[11]);
        Assert.Equal(new CliPermission("banks:read", "Banks and their buy and sell rates", "Rates", "host"), all[12]);
    }

    [Fact]
    public void AddPermissions_WithCliSurface()
    {
        _builder.AddPermissions(p => p.Group("Things").Add("things:read", "Read things"), CliPermissionSurface.Cli);

        Assert.Equal("cli", Resolve().Find("things:read")!.Surface);
    }

    [Theory]
    [InlineData("rates", "Rates", "Read rates")]
    [InlineData("rates:*", "Rates", "Read rates")]
    [InlineData("*", "Rates", "Read rates")]
    [InlineData("Rates:Read", "Rates", "Read rates")]
    [InlineData("a:b:c", "Rates", "Read rates")]
    [InlineData("", "Rates", "Read rates")]
    [InlineData("rates:read", "Rates", "")]
    [InlineData("rates:read", "Rates", "LONG")]
    [InlineData("rates:read", "Rates", "two\nlines")]
    [InlineData("rates:read", "", "Read rates")]
    public void InvalidEntries_Throw(string name, string group, string description)
    {
        if (description == "LONG") description = new string('x', 201);

        var ex = Assert.Throws<ArgumentException>(() =>
            _builder.AddPermissions(p => p.Group(group).Add(name, description)));
        Assert.StartsWith("invalid permission entry: ", ex.Message);
    }

    [Fact]
    public void AddBeforeGroup_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => _builder.AddPermissions(p => p.Add("rates:read", "Read rates")));
        Assert.Equal("invalid permission entry: rates:read", ex.Message);
    }

    [Theory]
    [InlineData("files:read")]
    [InlineData("FILES:READ")]
    public void Duplicate_Throws(string name)
    {
        // The duplicate check (case-insensitive) runs before the syntax check, so FILES:READ is a duplicate too.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _builder.AddPermissions(p => p.Group("Files").Add(name, "x")));
        Assert.Equal($"permission already registered: {name}", ex.Message);
    }

    [Fact]
    public void Duplicate_WithinOneCall_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _builder.AddPermissions(p => p.Group("Rates")
            .Add("rates:read", "a").Add("rates:read", "b")));
        Assert.Equal("permission already registered: rates:read", ex.Message);
    }

    [Fact]
    public void AddPermissions_AfterResolve_Throws()
    {
        Resolve();

        Assert.Throws<InvalidOperationException>(() =>
            _builder.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates")));
    }

    [Fact]
    public void JsonShape()
    {
        var json = JsonSerializer.Serialize(Resolve().Find("files:read"));

        Assert.Equal(
            """{"name":"files:read","description":"List, read and download files","group":"Files","surface":"cli"}""",
            json);
    }
}
