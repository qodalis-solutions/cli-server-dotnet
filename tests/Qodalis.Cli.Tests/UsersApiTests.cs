using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Tests;

public class UsersApiTests
{
    private readonly AuthService _authService;
    private readonly IUserStore _userStore;
    private readonly ISessionStore _sessionStore;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditStore _auditStore;
    private readonly IGroupStore _groupStore;

    public UsersApiTests()
    {
        _userStore = new InMemoryUserStore();
        _sessionStore = new InMemorySessionStore();
        _credentialStore = new InMemoryCredentialStore();
        _auditStore = new InMemoryAuditStore();
        _groupStore = new InMemoryGroupStore();
        _authService = new AuthService(_userStore, _sessionStore, _credentialStore, _auditStore);
    }

    private async Task<(User admin, string token)> SeedAdminAndLogin()
    {
        var admin = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "root",
            Email = "root@localhost",
            Groups = ["admin"],
        });
        await _authService.SetPasswordAsync(admin.Id, "root");
        await _groupStore.CreateGroupAsync("admin", "Administrator group");

        var result = await _authService.LoginAsync("root", "root");
        return (admin, result.Token);
    }

    // --- AuthService Tests ---

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenAndSession()
    {
        var (admin, token) = await SeedAdminAndLogin();

        Assert.NotNull(token);
        Assert.NotEmpty(token);

        var session = await _sessionStore.GetSessionAsync(token);
        Assert.NotNull(session);
        Assert.Equal(admin.Id, session.UserId);
        Assert.Equal("root", session.User.Name);
    }

    [Fact]
    public async Task Login_InvalidPassword_Throws()
    {
        await SeedAdminAndLogin();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.LoginAsync("root", "wrong"));
        Assert.Equal("Invalid credentials", ex.Message);
    }

    [Fact]
    public async Task Login_NonexistentUser_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.LoginAsync("nobody", "pass"));
        Assert.Equal("Invalid credentials", ex.Message);
    }

    [Fact]
    public async Task Login_DisabledUser_Throws()
    {
        var (admin, _) = await SeedAdminAndLogin();
        await _userStore.UpdateUserAsync(admin.Id, new UpdateUserDto { Disabled = true });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.LoginAsync("root", "root"));
        Assert.Equal("Account is disabled", ex.Message);
    }

    [Fact]
    public async Task Logout_RemovesSession()
    {
        var (_, token) = await SeedAdminAndLogin();

        await _authService.LogoutAsync(token);

        var session = await _sessionStore.GetSessionAsync(token);
        Assert.Null(session);
    }

    [Fact]
    public async Task VerifyPassword_Valid_ReturnsTrue()
    {
        var (admin, _) = await SeedAdminAndLogin();
        var valid = await _authService.VerifyPasswordAsync(admin.Id, "root");
        Assert.True(valid);
    }

    [Fact]
    public async Task VerifyPassword_Invalid_ReturnsFalse()
    {
        var (admin, _) = await SeedAdminAndLogin();
        var valid = await _authService.VerifyPasswordAsync(admin.Id, "wrong");
        Assert.False(valid);
    }

    // --- User CRUD Tests ---

    [Fact]
    public async Task CreateUser_Success()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "alice",
            Email = "alice@example.com",
            Groups = ["users"],
        });

        Assert.Equal("alice", user.Name);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Contains("users", user.Groups);
        Assert.NotEmpty(user.Id);
        Assert.True(user.CreatedAt > 0);
    }

    [Fact]
    public async Task CreateUser_Duplicate_Throws()
    {
        await _userStore.CreateUserAsync(new CreateUserDto { Name = "bob", Email = "bob@test.com" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userStore.CreateUserAsync(new CreateUserDto { Name = "bob", Email = "bob2@test.com" }));
    }

    [Fact]
    public async Task GetUser_ById()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "charlie", Email = "c@test.com" });
        var found = await _userStore.GetUserAsync(user.Id);
        Assert.NotNull(found);
        Assert.Equal("charlie", found.Name);
    }

    [Fact]
    public async Task GetUser_ByName()
    {
        await _userStore.CreateUserAsync(new CreateUserDto { Name = "diana", Email = "d@test.com" });
        var found = await _userStore.GetUserAsync("diana");
        Assert.NotNull(found);
        Assert.Equal("diana", found.Name);
    }

    [Fact]
    public async Task UpdateUser_ChangesFields()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "eve", Email = "e@test.com" });
        var updated = await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
        {
            Email = "eve@updated.com",
            Disabled = true,
        });

        Assert.Equal("eve@updated.com", updated.Email);
        Assert.True(updated.Disabled);
    }

    [Fact]
    public async Task DeleteUser_Removes()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "frank", Email = "f@test.com" });
        await _userStore.DeleteUserAsync(user.Id);
        var found = await _userStore.GetUserAsync(user.Id);
        Assert.Null(found);
    }

    // --- Session Tests ---

    [Fact]
    public async Task Session_ListAndKick()
    {
        var (admin, token) = await SeedAdminAndLogin();

        var sessions = await _sessionStore.GetActiveSessionsAsync();
        Assert.Single(sessions);
        Assert.Equal(admin.Id, sessions[0].UserId);

        // Kick session by id
        await _sessionStore.DeleteSessionByIdAsync(sessions[0].Id);
        var afterKick = await _sessionStore.GetActiveSessionsAsync();
        Assert.Empty(afterKick);
    }

    [Fact]
    public async Task Session_Cleanup_RemovesExpired()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "test", Email = "t@t.com" });
        var session = await _sessionStore.CreateSessionAsync(user.Id, "tok1", user, new SessionMetadata());

        // Force old lastActivity
        session.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 100_000;

        await _sessionStore.CleanupAsync(50_000);

        var remaining = await _sessionStore.GetActiveSessionsAsync();
        Assert.Empty(remaining);
    }

    // --- Group Tests ---

    [Fact]
    public async Task Group_CreateAndList()
    {
        var group = await _groupStore.CreateGroupAsync("devs", "Developers");
        Assert.Equal("devs", group.Name);

        var groups = await _groupStore.GetGroupsAsync();
        Assert.Single(groups);
    }

    [Fact]
    public async Task Group_Duplicate_Throws()
    {
        await _groupStore.CreateGroupAsync("ops");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _groupStore.CreateGroupAsync("ops"));
    }

    [Fact]
    public async Task Group_AddRemoveMember()
    {
        await _groupStore.CreateGroupAsync("team");
        var user = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "george",
            Email = "g@test.com",
            Groups = [],
        });

        // Add to group
        await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
        {
            Groups = [.. user.Groups, "team"],
        });
        var updated = await _userStore.GetUserAsync(user.Id);
        Assert.Contains("team", updated!.Groups);

        // Remove from group
        await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
        {
            Groups = updated.Groups.Where(g => g != "team").ToList(),
        });
        var removed = await _userStore.GetUserAsync(user.Id);
        Assert.DoesNotContain("team", removed!.Groups);
    }

    // --- Audit Tests ---

    [Fact]
    public async Task Audit_LogAndQuery()
    {
        await _auditStore.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = "login",
            Username = "root",
            Details = "Success",
        });

        await _auditStore.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = "login_failed",
            Username = "hacker",
            Details = "Wrong password",
        });

        var all = await _auditStore.GetEntriesAsync();
        Assert.Equal(2, all.Count);

        var filtered = await _auditStore.GetEntriesAsync(new AuditQueryOptions { Event = "login" });
        Assert.Single(filtered);
        Assert.Equal("root", filtered[0].Username);

        var limited = await _auditStore.GetEntriesAsync(new AuditQueryOptions { Limit = 1 });
        Assert.Single(limited);
    }

    [Fact]
    public async Task Audit_Clear()
    {
        await _auditStore.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = "test",
            Username = "test",
        });

        await _auditStore.ClearAsync();
        var entries = await _auditStore.GetEntriesAsync();
        Assert.Empty(entries);
    }

    // --- Password Change Tests ---

    [Fact]
    public async Task SetPassword_ThenVerify()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "helen", Email = "h@t.com" });
        await _authService.SetPasswordAsync(user.Id, "secret123");

        Assert.True(await _authService.VerifyPasswordAsync(user.Id, "secret123"));
        Assert.False(await _authService.VerifyPasswordAsync(user.Id, "wrongpass"));
    }

    // --- Credential Cascade Tests ---

    [Fact]
    public async Task DeleteUser_CascadesCredentialsAndSessions()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "ivan",
            Email = "i@t.com",
            Groups = ["admin"],
        });
        await _authService.SetPasswordAsync(user.Id, "pass");
        var loginResult = await _authService.LoginAsync("ivan", "pass");

        // Verify creds and session exist
        Assert.NotNull(await _credentialStore.GetCredentialsAsync(user.Id));
        Assert.NotNull(await _sessionStore.GetSessionAsync(loginResult.Token));

        // Delete user + cascade
        await _userStore.DeleteUserAsync(user.Id);
        await _credentialStore.DeleteCredentialsAsync(user.Id);
        await _sessionStore.DeleteUserSessionsAsync(user.Id);

        Assert.Null(await _userStore.GetUserAsync(user.Id));
        Assert.Null(await _credentialStore.GetCredentialsAsync(user.Id));
        Assert.Null(await _sessionStore.GetSessionAsync(loginResult.Token));
    }

    // --- Login Audit Logging Tests ---

    [Fact]
    public async Task Login_Success_CreatesAuditEntry()
    {
        await SeedAdminAndLogin();

        var entries = await _auditStore.GetEntriesAsync(new AuditQueryOptions { Event = "login" });
        Assert.Single(entries);
        Assert.Equal("root", entries[0].Username);
        Assert.Equal("Login successful", entries[0].Details);
    }

    [Fact]
    public async Task Login_Failure_CreatesAuditEntry()
    {
        await SeedAdminAndLogin();

        try { await _authService.LoginAsync("root", "wrong"); } catch { }

        var entries = await _auditStore.GetEntriesAsync(new AuditQueryOptions { Event = "login_failed" });
        Assert.Single(entries);
        Assert.Equal("root", entries[0].Username);
    }
}
