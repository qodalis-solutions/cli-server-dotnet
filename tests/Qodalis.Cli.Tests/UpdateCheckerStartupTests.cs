using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The update checker's hosted service and timers when the host stops while they start, or stops twice at once
/// (WebApplicationFactory disposing parallel test hosts).
/// </summary>
public sealed class UpdateCheckerStartupTests
{
    /// <summary>A bus whose subscriptions count their disposals; the first disposal waits for <see cref="Release"/>.</summary>
    private sealed class GatedBus : ICliEventBus
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(false);
        private int _first;

        public List<GatedSubscription> Subscriptions { get; } = [];

        /// <summary>Completes when the first disposal started (and waits for <see cref="Release"/>).</summary>
        public Task FirstDisposeEntered => _entered.Task;

        public void Release() => _gate.Set();

        public Task PublishAsync(string type, object? data = null, CliEventAudience? audience = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IDisposable Subscribe(string pattern, Func<CliEvent, CancellationToken, Task> handler)
        {
            var subscription = new GatedSubscription(this);
            Subscriptions.Add(subscription);
            return subscription;
        }

        public sealed class GatedSubscription(GatedBus bus) : IDisposable
        {
            private int _disposals;

            public int Disposals => Volatile.Read(ref _disposals);

            public void Dispose()
            {
                Interlocked.Increment(ref _disposals);
                if (Interlocked.Exchange(ref bus._first, 1) == 0)
                {
                    bus._entered.TrySetResult();
                    bus._gate.Wait(TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    private static UpdateChecker Checker(TimeProvider? time = null, bool enabled = false) =>
        new(new UpdateCheckerSources { ServerVersion = () => null, Time = time }, new AdminUpdateCheckOptions { Enabled = enabled });

    [Fact]
    public async Task StopAsync_TwiceAtOnce_DisposesEachSubscriptionOnceWithoutThrowing()
    {
        using var checker = Checker();
        var bus = new GatedBus();
        var startup = new UpdateCheckerStartup(checker, bus, lifetime: null);
        await startup.StartAsync(CancellationToken.None);
        Assert.Equal(2, bus.Subscriptions.Count);

        // The first stop is held inside its first disposal while a second stop runs to the end
        var first = Task.Run(() => startup.StopAsync(CancellationToken.None));
        await bus.FirstDisposeEntered.WaitAsync(TimeSpan.FromSeconds(30));
        await startup.StopAsync(CancellationToken.None);
        bus.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(bus.Subscriptions, s => Assert.Equal(1, s.Disposals));
    }

    [Fact]
    public async Task StartAsync_AfterStop_KeepsNoSubscription()
    {
        using var checker = Checker();
        var bus = new GatedBus();
        bus.Release();
        var startup = new UpdateCheckerStartup(checker, bus, lifetime: null);
        await startup.StopAsync(CancellationToken.None);

        await startup.StartAsync(CancellationToken.None);

        Assert.Equal(2, bus.Subscriptions.Count);
        Assert.All(bus.Subscriptions, s => Assert.Equal(1, s.Disposals));
    }

    /// <summary>A clock whose first timer stops the checker before it is handed back (Stop() during Start()).</summary>
    private sealed class StopOnFirstTimerClock : TimeProvider
    {
        public UpdateChecker? Checker { get; set; }
        public List<RecordingTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecordingTimer();
            Timers.Add(timer);
            if (Timers.Count == 1) Checker!.Stop();
            return timer;
        }

        public sealed class RecordingTimer : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void Checker_StoppedWhileStarting_KeepsNoTimer()
    {
        var clock = new StopOnFirstTimerClock();
        using var checker = Checker(clock, enabled: true);
        clock.Checker = checker;

        checker.Start();

        Assert.NotEmpty(clock.Timers);
        Assert.True(clock.Timers[0].Disposed);
    }
}
