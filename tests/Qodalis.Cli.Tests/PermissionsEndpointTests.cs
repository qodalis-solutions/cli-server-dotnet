using System.Text.Json;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>GET /api/v1/qcli/permissions</c>: the permission catalog, filtered to what the caller holds
/// (<c>?all=true</c> with <c>users:read</c>: every entry).
/// </summary>
public class PermissionsEndpointTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(configureCli: cli =>
            cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates")));
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<List<JsonElement>> GetEntriesAsync(string token, string query = "")
    {
        var res = await _h.GetAsync("permissions" + query, token);
        Assert.Equal(200, (int)res.StatusCode);
        return (await UsersTestHost.ReadJsonAsync(res)).EnumerateArray().ToList();
    }

    private static List<string> Names(IEnumerable<JsonElement> entries) =>
        entries.Select(e => e.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task Root_GetsEveryEntryItHolds()
    {
        var entries = await GetEntriesAsync(_root);

        Assert.Equal(29, entries.Count);
        Assert.Equal("commands:execute", entries[0].GetProperty("name").GetString());
        var last = entries[^1];
        Assert.Equal("rates:read", last.GetProperty("name").GetString());
        Assert.Equal("host", last.GetProperty("surface").GetString());
        Assert.Equal("Rates", last.GetProperty("group").GetString());
        Assert.All(entries, e => Assert.Equal(
            ["name", "description", "group", "surface"],
            e.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task Root_GetsTheUsersPluginEntriesInOrder()
    {
        var names = Names(await GetEntriesAsync(_root));

        Assert.Equal(
            [
                "users:read", "users:create", "users:update", "users:delete",
                "groups:read", "groups:create", "groups:update", "groups:delete",
                "sessions:read", "sessions:revoke", "tokens:read", "tokens:revoke",
            ],
            names.Skip(16).Take(12));
    }

    [Fact]
    public async Task PlainUser_GetsOnlyWhatItHolds()
    {
        var alice = await _h.UserWithPermissionsAsync(_root, "alice", "files:read", "rates:*");

        Assert.Equal(["files:read", "rates:read"], Names(await GetEntriesAsync(alice)));
        Assert.Equal(["files:read", "rates:read"], Names(await GetEntriesAsync(alice, "?all=false")));
    }

    [Fact]
    public async Task All_RequiresUsersRead()
    {
        var alice = await _h.UserWithPermissionsAsync(_root, "alice", "files:read", "rates:*");

        var res = await _h.GetAsync("permissions?all=true", alice);
        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal("""{"error":"permission denied: users:read"}""", await res.Content.ReadAsStringAsync());

        Assert.Equal(29, (await GetEntriesAsync(_root, "?all=true")).Count);
    }

    [Theory]
    [InlineData("?all=yes")]
    [InlineData("?all=")]
    [InlineData("?all=TRUE")]
    public async Task All_InvalidValue_Returns400(string query)
    {
        var res = await _h.GetAsync("permissions" + query, _root);

        Assert.Equal("all must be true or false", await UsersTestHost.AssertErrorAsync(res, 400));
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var res = await _h.GetAsync("permissions");

        Assert.Equal("unauthorized", await UsersTestHost.AssertErrorAsync(res, 401));
    }

    [Fact]
    public async Task ScopedCliToken_SeesItsScope()
    {
        var res = await _h.PostAsync("tokens", _root, new { name = "scoped", permissions = new[] { "files:read" } });
        Assert.Equal(201, (int)res.StatusCode);
        var token = (await UsersTestHost.ReadJsonAsync(res)).GetProperty("token").GetString()!;

        Assert.Equal(["files:read"], Names(await GetEntriesAsync(token)));
    }
}
