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
    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenRegistration _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (checker == null) return Task.CompletedTask;
        if (bus != null)
        {
            foreach (var type in new[] { ServerPluginMarketplace.ChangedEvent, DashboardMarketplace.ExtensionsChangedEvent })
            {
                _subscriptions.Add(bus.Subscribe(type, (_, _) =>
                {
                    checker.RefreshInstalled();
                    return Task.CompletedTask;
                }));
            }
        }
        if (lifetime != null) _started = lifetime.ApplicationStarted.Register(checker.Start);
        else checker.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        checker?.Stop();
        return Task.CompletedTask;
    }
}
