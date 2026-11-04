using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Seeds the built-in groups (<c>admin</c> = <c>*</c>, <c>users</c> = <c>commands:execute</c>) and —
/// unless external auth is configured — the <c>root</c> user (random password with
/// <c>mustChangePassword</c> when none is configured, printed to standard error and never logged), and periodically sweeps idle sessions.
/// With an external auth adapter it instead re-checks the events and logs sockets opened through the adapter every
/// <see cref="Qodalis.Cli.Security.CliShellOptions.SessionCheckInterval"/> (see <see cref="AuthService.RecheckExternalSocketsAsync"/>).
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
    private Timer? _externalSocketTimer;
    private readonly IServiceProvider? _services;
    private readonly CancellationTokenSource _stopping = new();
    private int _externalCheckRunning;

    private static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromMinutes(5);

    public UsersBootstrapService(
        IUserStore userStore,
        IGroupStore groupStore,
        AuthService authService,
        UsersModuleOptions options,
        ILogger<UsersBootstrapService> logger,
        IServiceProvider? services = null)
    {
        _services = services;
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
        // Read-only stores belong to the host application: nothing is seeded into them.
        if (!_options.GroupsReadOnly)
        {
            await EnsureGroupAsync(Group.AdminGroup, "Administrators", ["*"]);
            await EnsureGroupAsync(Group.UsersGroup, "Default group for new accounts", ["commands:execute"]);
        }

        if (_options.UsesExternalAuth || _options.UsersReadOnly) return;

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
            // The password goes to stderr only: ILogger output is broadcast on /ws/v1/qcli/logs and kept
            // in the admin plugin's log buffer, where any log reader could pick it up.
            var output = _options.SecretOutput ?? Console.Error;
            output.WriteLine(
                $"[qodalis-cli] Seeded admin user 'root' with generated password: {password} — it must be changed on first login. " +
                "This is the only time it is shown.");
            output.Flush();
            _logger.LogWarning(
                "Seeded admin user 'root' with a generated password (printed to standard error, not logged). " +
                "It must be changed on first login.");
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

        if (_options.UsesExternalAuth)
        {
            StartExternalSocketChecks();
            return;
        }

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

    /// <summary>
    /// The interval at which open shells re-check their session (<c>SessionCheckInterval</c>, capped at 60 s),
    /// also used for the events and logs sockets opened through the external adapter.
    /// </summary>
    internal TimeSpan ExternalSocketCheckInterval
    {
        get
        {
            var configured = _services?.GetService<Qodalis.Cli.Security.CliServerOptions>()?.Shell.SessionCheckInterval
                ?? TimeSpan.FromSeconds(30);
            var max = Qodalis.Cli.Security.CliShellOptions.MaxSessionCheckInterval;
            return configured <= TimeSpan.Zero || configured > max ? max : configured;
        }
    }

    private void StartExternalSocketChecks()
    {
        if (_services?.GetService<IExternalAuthAdapter>() is not { } adapter) return;
        var interval = ExternalSocketCheckInterval;
        _externalSocketTimer = new Timer(async _ =>
        {
            // A slow adapter must not stack checks.
            if (Interlocked.Exchange(ref _externalCheckRunning, 1) == 1) return;
            try
            {
                await _authService.RecheckExternalSocketsAsync(adapter, _services, _stopping.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during the external-auth socket check");
            }
            finally
            {
                Volatile.Write(ref _externalCheckRunning, 0);
            }
        }, null, interval, interval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        _externalSocketTimer?.Change(Timeout.Infinite, 0);
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _externalSocketTimer?.Dispose();
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }
}
