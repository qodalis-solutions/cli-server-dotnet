using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Aws;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Processors;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>GET /commands</c> lists each command's own <c>requiredPermissions</c>, sub-commands included (the parents' and
/// <c>commands:execute</c> are not repeated); a command without any has no <c>requiredPermissions</c> field.
/// </summary>
public sealed class CommandPermissionsTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"qcli-permissions-{Guid.NewGuid():N}.db");
    private ConfigTestHost _host = null!;
    private JsonElement _commands;

    public async Task InitializeAsync()
    {
        _host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddModule(new AwsModule())
                .AddProcessor<CliHttpCommandProcessor>()
                .AddProcessor(new TestProcessor("plain", "No permissions"));
            cli.AddDatabases(d => d.AddDatabase("main", db => db.UseConnection(_ => new SqliteConnection($"Data Source={_file}"))));
        }, admin: true);
        _commands = await _host.Client.GetFromJsonAsync<JsonElement>("/api/v1/qcli/commands");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    /// <summary>The descriptor at a command path, e.g. <c>At("aws", "s3", "rm")</c>.</summary>
    private JsonElement At(params string[] path)
    {
        var level = _commands;
        JsonElement current = default;
        foreach (var name in path)
        {
            current = level.EnumerateArray().Single(c => c.GetProperty("command").GetString() == name);
            current.TryGetProperty("processors", out level);
        }
        return current;
    }

    private string[]? Permissions(params string[] path) =>
        At(path).TryGetProperty("requiredPermissions", out var p) ? p.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;

    [Fact]
    public void RootCommands_ListTheirOwnPermissions()
    {
        Assert.Equal(["aws:read"], Permissions("aws"));
        Assert.Equal(["databases:read"], Permissions("databases"));
        Assert.Equal(["http:request"], Permissions("http"));
        Assert.Equal(["admin:access"], Permissions("admin"));
    }

    [Fact]
    public void SubCommands_ListTheirOwnPermissions_WithoutTheParents()
    {
        Assert.Equal(["aws:write"], Permissions("aws", "s3", "rm"));
        Assert.Equal(["aws:write"], Permissions("aws", "configure", "set"));
        Assert.Equal(["databases:migrate"], Permissions("databases", "up"));
        Assert.Equal(["logs:read"], Permissions("admin", "logs"));
        // read-only sub-commands declare nothing of their own: aws:read comes from the root and is not repeated
        Assert.Null(Permissions("aws", "s3", "ls"));
        Assert.Null(Permissions("admin", "status"));
    }

    [Fact]
    public void ACommandWithoutPermissions_HasNoField()
    {
        Assert.Null(Permissions("plain"));
        Assert.DoesNotContain("commands:execute", _commands.GetRawText());
    }
}
