using System.Threading.Channels;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class ContractorPollingWorkerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PollingStartsAtLaunchAndKeepsItsWindowAfterAFailedRead()
    {
        var interval = TimeSpan.FromMinutes(5);
        var clock = new PollingClock(Start);
        var megaplan = new PollingClient(attempt => attempt switch
        {
            1 => throw new SyncException("megaplan_http_503", transient: true),
            2 => [new ContractorUpdate("42", "ContractorHuman")],
            _ => []
        });
        var options = Options.Create(new IntegrationOptions
        {
            Sync = new() { Enabled = true, PollingIntervalMinutes = 5 }
        });
        var runtime = new SyncRuntime(options, clock);
        using var services = new ServiceCollection().AddSingleton<IMegaplanClient>(megaplan).BuildServiceProvider();
        using var worker = new ContractorPollingWorker(runtime, services.GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<ContractorPollingWorker>.Instance, clock);
        var initializationTime = TimeSpan.FromSeconds(15);
        clock.Advance(initializationTime);

        await worker.StartAsync(default);
        try
        {
            await clock.WaitForTimerAsync(interval);
            Assert.False(megaplan.Requests.TryRead(out _));

            await clock.TickAsync(interval);
            var firstCheck = Start + initializationTime + interval;
            Assert.Equal((Start, firstCheck), await megaplan.ReadRequestAsync());
            Assert.False(runtime.Reader.TryRead(out _));

            await clock.TickAsync(interval);
            var secondCheck = firstCheck + interval;
            Assert.Equal((Start, secondCheck), await megaplan.ReadRequestAsync());
            var queued = await runtime.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.Equal("42", queued.ContractorId);
            Assert.Equal("ContractorHuman", queued.Model);
            Assert.Equal("on_after_update", queued.Event);

            await clock.TickAsync(interval);
            Assert.Equal((secondCheck, secondCheck + interval), await megaplan.ReadRequestAsync());
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 5)]
    public async Task DisabledPollingDoesNotStartATimerOrReadMegaplan(bool syncEnabled, int intervalMinutes)
    {
        var clock = new PollingClock(Start);
        var megaplan = new PollingClient(_ => []);
        var options = Options.Create(new IntegrationOptions
        {
            Sync = new() { Enabled = syncEnabled, PollingIntervalMinutes = intervalMinutes }
        });
        using var services = new ServiceCollection().AddSingleton<IMegaplanClient>(megaplan).BuildServiceProvider();
        using var worker = new ContractorPollingWorker(new SyncRuntime(options, clock),
            services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ContractorPollingWorker>.Instance, clock);

        await worker.StartAsync(default);
        try
        {
            await worker.ExecuteTask!.WaitAsync(Timeout);
            Assert.False(clock.TimerStarted);
            Assert.False(megaplan.Requests.TryRead(out _));
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    private sealed class PollingClient(Func<int, IReadOnlyList<ContractorUpdate>> reply) : IMegaplanClient
    {
        private readonly Channel<(DateTimeOffset From, DateTimeOffset Until)> requests =
            Channel.CreateUnbounded<(DateTimeOffset, DateTimeOffset)>();
        private int attempts;

        public ChannelReader<(DateTimeOffset From, DateTimeOffset Until)> Requests => requests.Reader;

        public Task<(DateTimeOffset From, DateTimeOffset Until)> ReadRequestAsync() =>
            Requests.ReadAsync().AsTask().WaitAsync(Timeout);

        public Task<IReadOnlyList<ContractorUpdate>> GetContractorUpdatesAsync(
            DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
        {
            requests.Writer.TryWrite((from, until));
            return Task.FromResult(reply(++attempts));
        }

        public Task<string> GetAccountIdentityAsync(string field, CancellationToken ct) => throw new NotSupportedException();
        public Task<Contractor?> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MegaplanTag>> GetGlobalTagsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<object> DiagnoseAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PollingClock(DateTimeOffset start) : TimeProvider
    {
        private readonly TaskCompletionSource<PollingTimer> timerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long utcTicks = start.UtcTicks;

        public bool TimerStarted => timerStarted.Task.IsCompleted;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref utcTicks, elapsed.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PollingTimer(callback, state, dueTime, period);
            timerStarted.SetResult(timer);
            return timer;
        }

        public async Task WaitForTimerAsync(TimeSpan interval)
        {
            var timer = await timerStarted.Task.WaitAsync(Timeout);
            Assert.Equal(interval, timer.DueTime);
            Assert.Equal(interval, timer.Period);
        }

        public async Task TickAsync(TimeSpan elapsed)
        {
            var timer = await timerStarted.Task.WaitAsync(Timeout);
            Advance(elapsed);
            timer.Tick();
        }
    }

    private sealed class PollingTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public void Tick() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
