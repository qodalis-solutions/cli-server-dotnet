using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Events;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Starts the update checker's periodic checks once the server started (after every hosted service, the jobs plugin's
/// scheduler included), and drops the updates an install, update or removal applied (<c>server:plugins-changed</c>,
/// <c>dashboard:extensions-changed</c>) without asking the marketplace. Nothing when the marketplace is off.
/// </summary>
internal sealed class UpdateCheckerStartup(UpdateChecker? checker, ICliEventBus? bus, IHostApplicationLifetime? lifetime) : IHostedService
{
    // The host may stop this service while it starts, or twice at once (WebApplicationFactory disposing a host): the
    // subscriptions are swapped out under the lock and disposed outside it, never enumerated while another call changes them
    private readonly object _lock = new();
    private List<IDisposable> _subscriptions = [];
    private CancellationTokenRegistration _started;
    private bool _stopped;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (checker == null) return Task.CompletedTask;
        var subscriptions = new List<IDisposable>();
        if (bus != null)
        {
            foreach (var type in new[] { ServerPluginMarketplace.ChangedEvent, DashboardMarketplace.ExtensionsChangedEvent })
            {
                subscriptions.Add(bus.Subscribe(type, (_, _) =>
                {
                    checker.RefreshInstalled();
                    return Task.CompletedTask;
                }));
            }
        }
        lock (_lock)
        {
            if (!_stopped)
            {
                _subscriptions.AddRange(subscriptions);
                subscriptions.Clear();
                if (lifetime != null) _started = lifetime.ApplicationStarted.Register(checker.Start);
                else checker.Start();
            }
        }
        // Stopped before it started: drop what it subscribed
        foreach (var subscription in subscriptions) subscription.Dispose();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        List<IDisposable> subscriptions;
        CancellationTokenRegistration started;
        lock (_lock)
        {
            _stopped = true;
            subscriptions = _subscriptions;
            _subscriptions = [];
            started = _started;
            _started = default;
        }
        started.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
        checker?.Stop();
        return Task.CompletedTask;
    }
}
