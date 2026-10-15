using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Unit tests for the users plugin services and in-memory stores.
/// </summary>
public class UsersApiTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeEventSocketManager _events = new();
    private readonly IUserStore _userStore = new InMemoryUserStore();
    private readonly ISessionStore _sessionStore = new InMemorySessionStore();
    private readonly ICredentialStore _credentialStore = new InMemoryCredentialStore();
    private readonly IAuditStore _auditStore = new InMemoryAuditStore();
    private readonly IGroupStore _groupStore = new InMemoryGroupStore();
    private readonly AuthService _authService;

    public UsersApiTests()
    {
        _authService = new AuthService(_userStore, _sessionStore, _credentialStore, _auditStore, _groupStore,
            new UsersModuleOptions(), _events, _time);
    }

    private async Task<(User admin, string token)> SeedAdminAndLogin()
    {
        await _groupStore.CreateGroupAsync(new Group { Name = "admin", Permissions = ["*"], BuiltIn = true });
        var admin = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "root",
            Email = "root@localhost",
            Groups = ["admin"],
        });
        await _authService.SetPasswordAsync(admin.Id, "root");

        var result = await _authService.LoginAsync("root", "root");
        return (admin, result.Token);
    }

    // --- AuthService ---

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenSessionAndPermissions()
    {
        var (admin, token) = await SeedAdminAndLogin();

        var session = await _sessionStore.GetSessionAsync(AuthService.HashToken(token));
        Assert.NotNull(session);
        Assert.Equal(admin.Id, session.UserId);
        Assert.Equal("root", session.UserName);

        var auth = await _authService.AuthenticateTokenAsync(token);
        Assert.Equal(["*"], auth.Permissions);
        Assert.True(auth.Can("users:delete"));
    }

    [Fact]
    public async Task Login_InvalidPassword_Throws401()
    {
        await SeedAdminAndLogin();
        var ex = await Assert.ThrowsAsync<UsersApiException>(() => _authService.LoginAsync("root", "wrong"));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("invalid credentials", ex.Message);
    }

    [Fact]
    public async Task Login_NonexistentUser_Throws401()
    {
        var ex = await Assert.ThrowsAsync<UsersApiException>(() => _authService.LoginAsync("nobody", "pass"));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Login_DisabledUser_Throws403()
    {
        var (admin, _) = await SeedAdminAndLogin();
        await _userStore.UpdateUserAsync(admin.Id, new UpdateUserDto { Disabled = true });

        var ex = await Assert.ThrowsAsync<UsersApiException>(() => _authService.LoginAsync("root", "root"));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("account disabled", ex.Message);
    }

    [Fact]
    public async Task Login_PersistsLastLoginAtThroughStore()
    {
        var spy = new SpyUserStore();
        var auth = new AuthService(spy, _sessionStore, _credentialStore, _auditStore, _groupStore, timeProvider: _time);
        var user = await spy.CreateUserAsync(new CreateUserDto { Name = "spy", Email = "s@s.s" });
        await auth.SetPasswordAsync(user.Id, "pass");

        await auth.LoginAsync("spy", "pass");

        Assert.Contains(spy.Updates, u => u.LastLoginAt == _time.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), (await spy.GetUserAsync(user.Id))!.LastLoginAt);
    }

    [Fact]
    public async Task Lockout_IsConfigurable()
    {
        var options = new UsersModuleOptions { MaxLoginAttempts = 2, LockoutMs = 1000 };
        var auth = new AuthService(_userStore, _sessionStore, _credentialStore, _auditStore, _groupStore, options, null, _time);
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "lk", Email = "lk@x" });
        await auth.SetPasswordAsync(user.Id, "pass");

        await Assert.ThrowsAsync<UsersApiException>(() => auth.LoginAsync("lk", "bad"));
        await Assert.ThrowsAsync<UsersApiException>(() => auth.LoginAsync("lk", "bad"));
        var locked = await Assert.ThrowsAsync<UsersApiException>(() => auth.LoginAsync("lk", "pass"));
        Assert.Equal(429, locked.StatusCode);

        _time.Advance(TimeSpan.FromMilliseconds(1001));
        await auth.LoginAsync("lk", "pass");
    }

    private async Task<AuthService> LockoutServiceAsync(string userName, int maxTracked = 10_000)
    {
        var options = new UsersModuleOptions { MaxLoginAttempts = 3, LockoutMs = 60_000 };
        var auth = new AuthService(_userStore, _sessionStore, _credentialStore, _auditStore, _groupStore, options, null, _time)
        {
            MaxTrackedLockouts = maxTracked,
        };
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = userName, Email = $"{userName}@x" });
        await auth.SetPasswordAsync(user.Id, "pass");
        return auth;
    }

    private static async Task<int> LoginStatusAsync(AuthService auth, string name, string password)
    {
        try
        {
            await auth.LoginAsync(name, password);
            return 200;
        }
        catch (UsersApiException ex)
        {
            return ex.StatusCode;
        }
    }

    [Fact]
    public async Task Lockout_FailureCountsExpireAfterTheLockoutWindow()
    {
        var auth = await LockoutServiceAsync("slow");
        Assert.Equal(401, await LoginStatusAsync(auth, "slow", "bad"));
        Assert.Equal(401, await LoginStatusAsync(auth, "slow", "bad"));

        _time.Advance(TimeSpan.FromMilliseconds(60_001)); // older failures no longer count
        Assert.Equal(401, await LoginStatusAsync(auth, "slow", "bad"));
        Assert.Equal(401, await LoginStatusAsync(auth, "slow", "bad"));
        Assert.Equal(200, await LoginStatusAsync(auth, "slow", "pass"));
    }

    [Fact]
    public async Task Lockout_PruningKeepsPartialFailureCountsOfActiveEntries()
    {
        var auth = await LockoutServiceAsync("victim", maxTracked: 5);
        Assert.Equal(401, await LoginStatusAsync(auth, "victim", "bad"));
        Assert.Equal(401, await LoginStatusAsync(auth, "victim", "bad"));

        // Many tracked names push the table over its cap: pruning runs, but nothing has expired.
        for (var i = 0; i < 8; i++)
            await LoginStatusAsync(auth, $"ghost-{i}", "x");

        Assert.Equal(401, await LoginStatusAsync(auth, "victim", "bad")); // 3rd failure locks
        Assert.Equal(429, await LoginStatusAsync(auth, "victim", "pass"));
    }

    [Fact]
    public async Task Lockout_PruningRemovesOnlyExpiredEntries()
    {
        var auth = await LockoutServiceAsync("someone", maxTracked: 3);
        for (var i = 0; i < 4; i++)
            await LoginStatusAsync(auth, $"old-{i}", "x");
        Assert.Equal(4, auth.TrackedLockouts);

        _time.Advance(TimeSpan.FromMilliseconds(60_001));
        await LoginStatusAsync(auth, "fresh", "x"); // over the cap: the expired entries go
        Assert.Equal(1, auth.TrackedLockouts);
    }

    [Fact]
    public async Task Lockout_ConcurrentAttemptsWhilePruning_NeverLoseTheLock()
    {
        var auth = await LockoutServiceAsync("racer", maxTracked: 0); // prune on every attempt
        var statuses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
            LoginStatusAsync(auth, i % 2 == 0 ? "racer" : $"noise-{i}", "bad"))));

        var racer = statuses.Where((_, i) => i % 2 == 0).ToList();
        Assert.Equal(3, racer.Count(s => s == 401));
        Assert.Equal(12, racer.Count(s => s == 429));
        Assert.Equal(429, await LoginStatusAsync(auth, "racer", "pass"));
    }

    [Fact]
    public async Task Logout_RemovesSession()
    {
        var (_, token) = await SeedAdminAndLogin();
        await _authService.LogoutAsync(token);
        Assert.Null(await _sessionStore.GetSessionAsync(AuthService.HashToken(token)));
    }

    [Fact]
    public async Task AuthenticateToken_IdleExpired_ThrowsAndDeletes()
    {
        var (_, token) = await SeedAdminAndLogin();
        _time.Advance(TimeSpan.FromMinutes(30).Add(TimeSpan.FromMilliseconds(1)));

        var ex = await Assert.ThrowsAsync<UsersApiException>(() => _authService.AuthenticateTokenAsync(token));
        Assert.Equal(401, ex.StatusCode);
        Assert.Null(await _sessionStore.GetSessionAsync(AuthService.HashToken(token)));
    }

    [Fact]
    public async Task RevokeUserSessions_BroadcastsSessionKickedWithReason()
    {
        var (admin, token) = await SeedAdminAndLogin();
        var session = await _sessionStore.GetSessionAsync(AuthService.HashToken(token));

        await _authService.RevokeUserSessionsAsync(admin.Id, AuthService.RevokeReasons.UserDisabled, "someone");

        var kicked = Assert.Single(_events.SessionKicked());
        Assert.Equal(session!.Id, kicked.GetProperty("sessionId").GetString());
        Assert.Equal(admin.Id, kicked.GetProperty("userId").GetString());
        Assert.Equal("user_disabled", kicked.GetProperty("reason").GetString());
        Assert.Null(await _sessionStore.GetSessionAsync(AuthService.HashToken(token)));
    }

    [Fact]
    public async Task SweepIdleSessions_RemovesExpired()
    {
        var (_, token) = await SeedAdminAndLogin();
        _time.Advance(TimeSpan.FromMinutes(31));
        await _authService.SweepIdleSessionsAsync();
        Assert.Null(await _sessionStore.GetSessionAsync(AuthService.HashToken(token)));
    }

    // --- Password hashing ---

    [Fact]
    public async Task SetPassword_StoresPbkdf2Format_AndVerifies()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "helen", Email = "h@t.com" });
        await _authService.SetPasswordAsync(user.Id, "secret123");

        var creds = await _credentialStore.GetCredentialsAsync(user.Id);
        Assert.Matches(@"^pbkdf2\$100000\$[A-Za-z0-9+/=]{24}\$[A-Za-z0-9+/=]{44}$", creds!.PasswordHash);

        Assert.True(await _authService.VerifyPasswordAsync(user.Id, "secret123"));
        Assert.False(await _authService.VerifyPasswordAsync(user.Id, "wrongpass"));
    }

    [Fact]
    public void PasswordHasher_UsesRandomSalt_AndKnownVector()
    {
        var (a, _) = PasswordHasher.Hash("same");
        var (b, _) = PasswordHasher.Hash("same");
        Assert.NotEqual(a, b);

        var salt = new byte[16];
        var key = Rfc2898DeriveBytes.Pbkdf2("pw", salt, 100_000, HashAlgorithmName.SHA256, 32);
        var encoded = $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        Assert.True(PasswordHasher.Verify("pw", encoded));
        Assert.False(PasswordHasher.Verify("pW", encoded));
        Assert.False(PasswordHasher.Verify("pw", "pbkdf2$abc$###$###"));
    }

    [Fact]
    public async Task LegacySha256Hash_IsVerifiedAndUpgraded()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "legacy", Email = "l@t.com" });
        var salt = "abcd";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + "oldpass"))).ToLowerInvariant();
        await _credentialStore.SetPasswordAsync(user.Id, hash, salt);

        Assert.False(await _authService.VerifyPasswordAsync(user.Id, "nope"));
        Assert.True(await _authService.VerifyPasswordAsync(user.Id, "oldpass"));
        Assert.StartsWith("pbkdf2$", (await _credentialStore.GetCredentialsAsync(user.Id))!.PasswordHash);
        Assert.True(await _authService.VerifyPasswordAsync(user.Id, "oldpass"));
    }

    // --- Permissions ---

    [Theory]
    [InlineData("*", "users:read", true)]
    [InlineData("users:*", "users:delete", true)]
    [InlineData("USERS:READ", "users:read", true)]
    [InlineData("users:read", "users:create", false)]
    [InlineData("users:read", "users", false)]
    [InlineData("users", "users:read", false)]
    [InlineData("groups:*", "users:read", false)]
    [InlineData("users:read:*", "users:read", true)] // same as cliPermissionMatches
    // A non-last '*' matches exactly one segment; only a last '*' grants everything below.
    [InlineData("*:read", "users:read", true)]
    [InlineData("*:read", "users:delete", false)]
    [InlineData("*:read", "users:read:x", false)]
    [InlineData("*:read", "*", false)]
    [InlineData("users:*:read", "users:x:read", true)]
    [InlineData("users:*:read", "users:x:delete", false)]
    [InlineData("users:*:read", "shell:execute", false)]
    [InlineData("*:*", "users:read:x", true)]
    public void PermissionMatcher_MatchesCliCoreSemantics(string granted, string required, bool expected)
    {
        Assert.Equal(expected, PermissionMatcher.Matches(granted, required));
    }

    [Fact]
    public async Task EffectivePermissions_AreUnionOfGroups_CaseInsensitive()
    {
        await _groupStore.CreateGroupAsync(new Group { Name = "Readers", Permissions = ["users:read", "audit:read"] });
        await _groupStore.CreateGroupAsync(new Group { Name = "auditors", Permissions = ["AUDIT:READ", "audit:clear"] });
        var user = new User { Id = "u", Groups = ["readers", "AUDITORS", "missing"] };

        var perms = await _authService.GetEffectivePermissionsAsync(user);
        Assert.Equal(["users:read", "audit:read", "audit:clear"], perms);
    }

    // --- Stores ---

    [Fact]
    public async Task UserStore_CrudAndCopies()
    {
        var user = await _userStore.CreateUserAsync(new CreateUserDto { Name = "alice", Email = "a@e.com", Groups = ["users"] });
        Assert.NotEmpty(user.Id);
        Assert.False(user.Disabled);

        user.Groups.Add("admin");
        user.Name = "mutated";
        var reloaded = await _userStore.GetUserAsync("ALICE");
        Assert.Equal(["users"], reloaded!.Groups);

        var updated = await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto { Email = "x@y.z", Disabled = true });
        Assert.Equal("x@y.z", updated.Email);
        Assert.True(updated.Disabled);

        await _userStore.DeleteUserAsync(user.Id);
        Assert.Null(await _userStore.GetUserAsync(user.Id));
    }

    [Fact]
    public async Task UserStore_DuplicateName_Throws()
    {
        await _userStore.CreateUserAsync(new CreateUserDto { Name = "bob", Email = "bob@test.com" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userStore.CreateUserAsync(new CreateUserDto { Name = "BOB", Email = "bob2@test.com" }));
    }

    [Fact]
    public async Task GroupStore_CaseInsensitive_UpdateAndDuplicate()
    {
        var group = await _groupStore.CreateGroupAsync(new Group { Name = "Devs", Description = "Developers" });
        Assert.NotEmpty(group.Id);
        Assert.True(group.CreatedAt > 0);
        Assert.Equal("Devs", (await _groupStore.GetGroupAsync("DEVS"))!.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _groupStore.CreateGroupAsync(new Group { Name = "devs" }));

        var updated = await _groupStore.UpdateGroupAsync("devs", new UpdateGroupDto { Permissions = ["code:*"] });
        Assert.Equal(["code:*"], updated.Permissions);
        Assert.Equal("Developers", updated.Description);

        await _groupStore.DeleteGroupAsync("DEVS");
        Assert.Empty(await _groupStore.GetGroupsAsync());
    }

    [Fact]
    public async Task AuditStore_FiltersLimitsAndAssignsIds()
    {
        await _auditStore.LogAsync(new AuditEntry { Timestamp = 1, Event = "login", Username = "root" });
        await _auditStore.LogAsync(new AuditEntry { Timestamp = 2, Event = "login_failed", Username = "hacker" });

        var all = await _auditStore.GetEntriesAsync();
        Assert.Equal(["login_failed", "login"], all.Select(e => e.Event));
        Assert.All(all, e => Assert.False(string.IsNullOrEmpty(e.Id)));

        Assert.Single(await _auditStore.GetEntriesAsync(new AuditQueryOptions { Event = "login" }));
        Assert.Single(await _auditStore.GetEntriesAsync(new AuditQueryOptions { Username = "HACKER" }));
        Assert.Single(await _auditStore.GetEntriesAsync(new AuditQueryOptions { Limit = 1 }));

        await _auditStore.ClearAsync();
        Assert.Empty(await _auditStore.GetEntriesAsync());
    }

    [Fact]
    public async Task AuditStore_KeepsLast1000Entries()
    {
        var store = new InMemoryAuditStore();
        for (var i = 0; i < 1100; i++)
            await store.LogAsync(new AuditEntry { Timestamp = i, Event = "e", Username = $"u{i}" });

        var entries = await store.GetEntriesAsync();
        Assert.Equal(1000, entries.Count);
        Assert.Equal("u1099", entries[0].Username);
        Assert.Equal("u100", entries[^1].Username);
    }

    // --- Registration ---

    [Fact]
    public void AddUsers_SetStoreGeneric_ResolvesThroughDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StoreDependency>();
        services.AddControllers().AddCli(cli => cli.AddUsers(o => o
            .SetUserStore<DiUserStore>()
            .SetAuditStore(_ => new InMemoryAuditStore(10))));

        using var provider = services.BuildServiceProvider();
        var store = Assert.IsType<DiUserStore>(provider.GetRequiredService<IUserStore>());
        Assert.Same(provider.GetRequiredService<StoreDependency>(), store.Dependency);
        Assert.Same(store, provider.GetRequiredService<IUserStore>());
        Assert.Equal(10, Assert.IsType<InMemoryAuditStore>(provider.GetRequiredService<IAuditStore>()).MaxEntries);
        Assert.IsType<InMemoryGroupStore>(provider.GetRequiredService<IGroupStore>());
        Assert.NotNull(provider.GetRequiredService<UsersService>());
    }

    [Fact]
    public void AddUsers_StoreInstanceProperty_IsUsed()
    {
        var sessions = new InMemorySessionStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli(cli => cli.AddUsers(o => o.SessionStore = sessions));

        using var provider = services.BuildServiceProvider();
        Assert.Same(sessions, provider.GetRequiredService<ISessionStore>());
    }

    public sealed class StoreDependency;

    public sealed class DiUserStore(StoreDependency dependency) : InMemoryUserStore
    {
        public StoreDependency Dependency { get; } = dependency;
    }

    private sealed class SpyUserStore : InMemoryUserStore, IUserStore
    {
        public List<UpdateUserDto> Updates { get; } = [];

        Task<User> IUserStore.UpdateUserAsync(string id, UpdateUserDto data)
        {
            Updates.Add(data);
            return UpdateUserAsync(id, data);
        }
    }
}
