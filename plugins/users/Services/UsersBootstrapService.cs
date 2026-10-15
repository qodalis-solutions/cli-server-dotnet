using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Seeds the built-in groups (<c>admin</c> = <c>*</c>, <c>users</c> = <c>commands:execute</c>) and —
/// unless external auth is configured — the <c>root</c> user (random password with
/// <c>mustChangePassword</c> when none is configured), and periodically sweeps idle sessions.
/// Seeding is idempotent and also awaited by the auth middleware, so requests that arrive
/// before the hosted service has started still see the seeded state.
/// </summary>
public class UsersBootstrapService : IHostedService, IDisposable
{
    private readonly IUserStore _userStore;
    private readonly IGroupStore _groupStore;
    private readonly AuthService _authService;
    private readonly UsersModuleOptions _options;
    private readonly ILogger<UsersBootstrapService> _logger;
    private readonly object _seedLock = new();
    private Task? _seedTask;
    private Timer? _cleanupTimer;

    private static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromMinutes(5);

    public UsersBootstrapService(
        IUserStore userStore,
        IGroupStore groupStore,
        AuthService authService,
        UsersModuleOptions options,
        ILogger<UsersBootstrapService> logger)
    {
        _userStore = userStore;
        _groupStore = groupStore;
        _authService = authService;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs seeding once; subsequent calls return the same task.</summary>
    public Task EnsureSeededAsync()
    {
        lock (_seedLock)
        {
            if (_seedTask == null || _seedTask.IsFaulted || _seedTask.IsCanceled)
                _seedTask = SeedAsync();
            return _seedTask;
        }
    }

    /// <summary>Passwords that trigger a startup warning when configured explicitly.</summary>
    private static readonly string[] WeakPasswords = ["root", "admin", "password"];

    private async Task SeedAsync()
    {
        await EnsureGroupAsync(Group.AdminGroup, "Administrators", ["*"]);
        await EnsureGroupAsync(Group.UsersGroup, "Default group for new accounts", ["commands:execute"]);

        if (_options.UsesExternalAuth) return;

        var users = await _userStore.GetUsersAsync();
        if (users.Count > 0) return;

        var configured = _options.DefaultAdminPassword;
        var password = string.IsNullOrEmpty(configured) ? GeneratePassword(16) : configured;

        var adminUser = await _userStore.CreateUserAsync(new CreateUserDto
        {
            Name = "root",
            Email = "root@localhost",
            Groups = [Group.AdminGroup],
        });
        await _authService.SetPasswordAsync(adminUser.Id, password);

        if (string.IsNullOrEmpty(configured))
        {
            await _userStore.UpdateUserAsync(adminUser.Id, new UpdateUserDto { MustChangePassword = true });
            _logger.LogWarning(
                "Seeded admin user 'root' with generated password: {Password} — it must be changed on first login. " +
                "This is the only time it is shown.", password);
        }
        else
        {
            _logger.LogInformation("Seeded default admin user 'root'");
            if (WeakPasswords.Contains(configured, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "The seeded 'root' user uses a well-known default password. Change it before exposing this server.");
            }
        }
    }

    /// <summary>Random password from an unambiguous alphabet.</summary>
    internal static string GeneratePassword(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    private async Task EnsureGroupAsync(string name, string description, List<string> permissions)
    {
        if (await _groupStore.GetGroupAsync(name) != null) return;
        try
        {
            await _groupStore.CreateGroupAsync(new Group
            {
                Name = name,
                Description = description,
                Permissions = permissions,
                BuiltIn = true,
            });
            _logger.LogInformation("Seeded '{Group}' group", name);
        }
        catch (InvalidOperationException)
        {
            // Created concurrently — fine.
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureSeededAsync();

        if (_options.UsesExternalAuth) return;

        var timeout = TimeSpan.FromMilliseconds(_authService.SessionTimeoutMs);
        var interval = timeout < MaxCleanupInterval ? timeout : MaxCleanupInterval;
        _cleanupTimer = new Timer(async _ =>
        {
            try
            {
                await _authService.SweepIdleSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }, null, interval, interval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
