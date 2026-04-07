using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Hosted service that seeds the default admin user and group,
/// and periodically cleans up expired sessions.
/// </summary>
public class UsersBootstrapService : IHostedService, IDisposable
{
    private readonly IUserStore _userStore;
    private readonly IGroupStore _groupStore;
    private readonly ISessionStore _sessionStore;
    private readonly AuthService _authService;
    private readonly UsersModuleOptions _options;
    private readonly ILogger<UsersBootstrapService> _logger;
    private Timer? _cleanupTimer;

    private const long DefaultSessionTimeoutMs = 30 * 60 * 1000; // 30 minutes
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    public UsersBootstrapService(
        IUserStore userStore,
        IGroupStore groupStore,
        ISessionStore sessionStore,
        AuthService authService,
        UsersModuleOptions options,
        ILogger<UsersBootstrapService> logger)
    {
        _userStore = userStore;
        _groupStore = groupStore;
        _sessionStore = sessionStore;
        _authService = authService;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed default admin user if no users exist
        var users = await _userStore.GetUsersAsync();
        if (users.Count == 0)
        {
            var adminPassword = _options.DefaultAdminPassword ?? "root";
            var adminUser = await _userStore.CreateUserAsync(new CreateUserDto
            {
                Name = "root",
                Email = "root@localhost",
                Groups = ["admin"],
            });
            await _authService.SetPasswordAsync(adminUser.Id, adminPassword);
            _logger.LogInformation("Seeded default admin user 'root'");
        }

        // Seed admin group if not present
        var adminGroup = await _groupStore.GetGroupAsync("admin");
        if (adminGroup == null)
        {
            await _groupStore.CreateGroupAsync("admin", "Administrator group");
            _logger.LogInformation("Seeded 'admin' group");
        }

        // Start cleanup timer
        var timeoutMs = _options.SessionTimeoutMs ?? DefaultSessionTimeoutMs;
        _cleanupTimer = new Timer(async _ =>
        {
            try
            {
                await _sessionStore.CleanupAsync(timeoutMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }, null, CleanupInterval, CleanupInterval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}
