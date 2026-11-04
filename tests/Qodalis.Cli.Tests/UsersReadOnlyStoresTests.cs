using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <see cref="ExternalAuthResult.Permissions"/> and read-only user/group stores (issue #27).
/// </summary>
public class UsersReadOnlyStoresTests
{
    private const string UsersMessage = "Users are managed by the host application";
    private const string GroupsMessage = "Groups are managed by the host application";

    /// <summary>A store that records every call and fails the test on writes.</summary>
    private sealed class RecordingUserStore : IUserStore
    {
        public List<User> Users { get; } =
        [
            new() { Id = "ext-alice", Name = "alice", Email = "alice@external.com", Groups = ["admin"] },
            new() { Id = "ext-bob", Name = "bob", Email = "bob@external.com", Groups = [] },
        ];

        public int Writes;

        public Task<List<User>> GetUsersAsync() => Task.FromResult(Users.Select(u => u.Clone()).ToList());

        public Task<User?> GetUserAsync(string idOrName) => Task.FromResult(Users.FirstOrDefault(u =>
            u.Id == idOrName || string.Equals(u.Name, idOrName, StringComparison.OrdinalIgnoreCase))?.Clone());

        public Task<User> CreateUserAsync(CreateUserDto data) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
        public Task<User> UpdateUserAsync(string id, UpdateUserDto data) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
        public Task DeleteUserAsync(string id) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
    }

    private sealed class RecordingGroupStore : IGroupStore
    {
        private readonly List<Group> _groups =
        [
            new() { Id = "admin", Name = "admin", Permissions = ["*"], BuiltIn = true },
            new() { Id = "users", Name = "users", Permissions = [], BuiltIn = true },
            new() { Id = "ops", Name = "ops", Permissions = ["jobs:read"] },
        ];

        public int Writes;

        public Task<List<Group>> GetGroupsAsync() => Task.FromResult(_groups.Select(g => g.Clone()).ToList());

        public Task<Group?> GetGroupAsync(string name) => Task.FromResult(_groups.FirstOrDefault(g =>
            string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.Clone());

        public Task<Group> CreateGroupAsync(Group group) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
        public Task<Group> UpdateGroupAsync(string name, UpdateGroupDto data) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
        public Task DeleteGroupAsync(string name) { Interlocked.Increment(ref Writes); throw new InvalidOperationException("write"); }
    }

    private static ExternalAuthResult? Adapter(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var name = context.Request.Headers["X-Test-User"].FirstOrDefault();
        if (string.IsNullOrEmpty(name)) return null;
        var groups = context.Request.Headers["X-Test-Groups"].FirstOrDefault()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
        var permissions = context.Request.Headers["X-Test-Permissions"].FirstOrDefault()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new ExternalAuthResult
        {
            User = new User { Id = $"ext-{name}", Name = name, Email = $"{name}@external.com", Groups = groups },
            IsAdmin = context.Request.Headers["X-Test-Admin"].FirstOrDefault() == "true",
            Permissions = permissions,
        };
    }

    private static Task<HttpResponseMessage> SendAsync(UsersTestHost h, HttpMethod method, string path,
        string user = "alice", bool admin = true, string? groups = null, string? permissions = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, path.StartsWith('/') ? path : $"{Base}/{path}");
        req.Headers.Add("X-Test-User", user);
        if (admin) req.Headers.Add("X-Test-Admin", "true");
        if (groups != null) req.Headers.Add("X-Test-Groups", groups);
        if (permissions != null) req.Headers.Add("X-Test-Permissions", permissions);
        if (method != HttpMethod.Get) req.Headers.Add("Origin", "http://localhost");
        if (body != null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return h.Client.SendAsync(req);
    }

    // =========================================================================
    // ExternalAuthResult.Permissions
    // =========================================================================

    [Fact]
    public async Task Permissions_AreAddedToTheGroupsPermissions()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(Adapter));

        // 'users' grants commands:execute; the result adds logs:read and jobs:read; 'nope' does not exist.
        var current = await ReadJsonAsync(await SendAsync(h, HttpMethod.Get, "sessions/current", "bob", admin: false,
            groups: "users,nope", permissions: "logs:read,jobs:read, ,LOGS:READ"));
        var permissions = current.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(new string?[] { "commands:execute", "logs:read", "jobs:read" }, permissions);
        Assert.Equal(new string?[] { "users", "nope" }, current.GetProperty("user").GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
    }

    [Fact]
    public async Task Permissions_AloneAuthorizeServerRoutes()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(Adapter));

        await AssertErrorAsync(await SendAsync(h, HttpMethod.Get, "audit", "carol", admin: false), 403);
        Assert.Equal(200, (int)(await SendAsync(h, HttpMethod.Get, "audit", "carol", admin: false,
            permissions: "audit:read")).StatusCode);
    }

    [Fact]
    public async Task Permissions_WithIsAdmin_KeepEverything()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(Adapter));

        var current = await ReadJsonAsync(await SendAsync(h, HttpMethod.Get, "sessions/current", "dana",
            permissions: "logs:read"));
        var permissions = current.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(new string?[] { "*", "logs:read" }, permissions);
    }

    [Fact]
    public async Task Permissions_OpenTheLogsSocket()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(Adapter), realEventSockets: true);

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/logs", r => r.Headers["X-Test-User"] = "erin"));
        Assert.Contains("403", denied.Message);

        var socket = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/logs", r =>
        {
            r.Headers["X-Test-User"] = "erin";
            r.Headers["X-Test-Permissions"] = "logs:read";
        });
        Assert.Contains("connected", await ReceiveTextAsync(socket));
        await CloseAsync(socket);
    }

    // =========================================================================
    // Read-only stores
    // =========================================================================

    private static async Task<(UsersTestHost Host, RecordingUserStore Users, RecordingGroupStore Groups)> StartReadOnlyAsync(
        bool users, bool groups)
    {
        var userStore = new RecordingUserStore();
        var groupStore = new RecordingGroupStore();
        var h = await UsersTestHost.StartAsync(o =>
        {
            o.UseExternalAuth(Adapter);
            o.UserStore = userStore;
            o.GroupStore = groupStore;
            if (users) o.ReadOnlyUsers();
            if (groups) o.ReadOnlyGroups();
        });
        return (h, userStore, groupStore);
    }

    public static TheoryData<string, string, object?> UserWrites => new()
    {
        { "POST", "users", new { name = "new-user", email = "new@test.com" } },
        { "PUT", "users/bob", new { email = "bob2@test.com" } },
        { "PUT", "users/alice", new { data = new { theme = "dark" } } }, // self update
        { "DELETE", "users/bob", null },
        { "POST", "groups/ops/members/bob", null },
        { "DELETE", "groups/admin/members/alice", null },
    };

    [Theory]
    [MemberData(nameof(UserWrites))]
    public async Task ReadOnlyUsers_UserWritesAnswer501WithoutTouchingTheStore(string method, string path, object? body)
    {
        var (h, users, groups) = await StartReadOnlyAsync(users: true, groups: false);
        await using var _ = h;

        Assert.Equal(UsersMessage, await AssertErrorAsync(await SendAsync(h, new HttpMethod(method), path, body: body), 501));
        Assert.Equal(0, users.Writes);
        Assert.Equal(0, groups.Writes);
    }

    [Fact]
    public async Task ReadOnlyUsers_PasswordChangesAnswer501_WithoutExternalAuth()
    {
        await using var h = await UsersTestHost.StartAsync();
        var token = await h.RootTokenAsync();
        h.Service<UsersModuleOptions>().UsersReadOnly = true; // read on every request

        var self = await h.PutAsync("users/root/password", token, new { password = "new-password", currentPassword = "root" });
        Assert.Equal(UsersMessage, await AssertErrorAsync(self, 501));
        await AssertErrorAsync(await h.PostAsync("users", token, new { name = "x", email = "x@test.com" }), 501);

        // Nothing changed: the old password still signs in.
        await h.LoginAsync("root", "root");
    }

    [Fact]
    public async Task ReadOnlyUsers_RootIsNotSeeded()
    {
        var userStore = new RecordingUserStore();
        userStore.Users.Clear();
        await using var h = await UsersTestHost.StartAsync(o =>
        {
            o.UserStore = userStore;
            o.ReadOnlyUsers();
        });

        // Without an adapter nobody can sign in: no root user was created in the host's store.
        await AssertErrorAsync(await h.PostAsync("sessions", null, new { username = "root", password = "root" }), 401);
        Assert.Equal(0, userStore.Writes);
    }

    [Fact]
    public async Task ReadOnlyUsers_ReadsStillWork()
    {
        var (h, users, _) = await StartReadOnlyAsync(users: true, groups: false);
        await using var __ = h;

        var list = await ReadJsonAsync(await SendAsync(h, HttpMethod.Get, "users"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(200, (int)(await SendAsync(h, HttpMethod.Get, "users/bob")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(h, HttpMethod.Get, "groups/admin/members")).StatusCode);
        Assert.Equal(0, users.Writes);
    }

    public static TheoryData<string, string, object?> GroupWrites => new()
    {
        { "POST", "groups", new { name = "new-group", permissions = new[] { "jobs:read" } } },
        { "PUT", "groups/ops", new { description = "x" } },
        { "DELETE", "groups/ops", null },
    };

    [Theory]
    [MemberData(nameof(GroupWrites))]
    public async Task ReadOnlyGroups_GroupWritesAnswer501WithoutTouchingTheStore(string method, string path, object? body)
    {
        var (h, users, groups) = await StartReadOnlyAsync(users: false, groups: true);
        await using var _ = h;

        Assert.Equal(GroupsMessage, await AssertErrorAsync(await SendAsync(h, new HttpMethod(method), path, body: body), 501));
        Assert.Equal(0, groups.Writes);
        Assert.Equal(0, users.Writes);
    }

    [Fact]
    public async Task ReadOnlyGroups_AreNotSeeded_AndReadsWork()
    {
        var groupStore = new RecordingGroupStore();
        await using var h = await UsersTestHost.StartAsync(o =>
        {
            o.UseExternalAuth(Adapter);
            o.GroupStore = groupStore;
            o.ReadOnlyGroups();
        });

        var list = await ReadJsonAsync(await SendAsync(h, HttpMethod.Get, "groups"));
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal(0, groupStore.Writes);
    }

    [Fact]
    public async Task ReadOnlyGroups_WithWritableUsers_KeepUserWritesAndMemberships()
    {
        var groupStore = new RecordingGroupStore();
        await using var h = await UsersTestHost.StartAsync(o =>
        {
            o.UseExternalAuth(Adapter);
            o.GroupStore = groupStore;
            o.ReadOnlyGroups();
        });

        Assert.Equal(201, (int)(await SendAsync(h, HttpMethod.Post, "users",
            body: new { name = "new-user", email = "new@test.com" })).StatusCode);
        Assert.Equal(204, (int)(await SendAsync(h, HttpMethod.Post, "groups/ops/members/new-user")).StatusCode);
        Assert.Equal(0, groupStore.Writes);
    }

    [Fact]
    public async Task WritableStores_AreUnaffected()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(Adapter));

        Assert.Equal(201, (int)(await SendAsync(h, HttpMethod.Post, "groups",
            body: new { name = "auditors", permissions = new[] { "audit:read" } })).StatusCode);
        Assert.Equal(201, (int)(await SendAsync(h, HttpMethod.Post, "users",
            body: new { name = "new-user", email = "new@test.com" })).StatusCode);
    }

    // =========================================================================
    // Flags published to clients
    // =========================================================================

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CurrentSession_PublishesTheReadOnlyFlags(bool users, bool groups)
    {
        var (h, _, _) = await StartReadOnlyAsync(users, groups);
        await using var __ = h;

        var current = await ReadJsonAsync(await SendAsync(h, HttpMethod.Get, "sessions/current"));
        Assert.Equal(users, current.GetProperty("usersReadOnly").GetBoolean());
        Assert.Equal(groups, current.GetProperty("groupsReadOnly").GetBoolean());
    }

    [Fact]
    public async Task Login_PublishesTheReadOnlyFlags()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.ReadOnlyGroups());

        var (_, session) = await h.LoginAsync("root", "root");
        Assert.False(session.GetProperty("usersReadOnly").GetBoolean());
        Assert.True(session.GetProperty("groupsReadOnly").GetBoolean());
    }

    [Fact]
    public void Options_FluentAndProperties()
    {
        var options = new UsersModuleOptions().ReadOnlyUsers().ReadOnlyGroups();
        Assert.True(options.UsersReadOnly);
        Assert.True(options.GroupsReadOnly);
        options.ReadOnlyUsers(false);
        Assert.False(options.UsersReadOnly);
    }
}
