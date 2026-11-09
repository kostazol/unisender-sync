using MegaplanUnisender.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Infrastructure;

public sealed class ContractorPollingWorker(SyncRuntime runtime, IServiceScopeFactory scopes, IOptions<IntegrationOptions> options,
    ILogger<ContractorPollingWorker> logger, TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    // Hosted services are constructed before configuration discovery starts. Megaplan reports seconds.
    private DateTimeOffset from = ToSecond((clock ?? TimeProvider.System).GetUtcNow());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sync = options.Value.Sync;
        if (!sync.Enabled || sync.PollingIntervalMinutes == 0) return;

        logger.LogInformation("Megaplan polling enabled; interval {IntervalMinutes} minutes; from {From}", sync.PollingIntervalMinutes, from);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(sync.PollingIntervalMinutes), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var until = ToSecond(time.GetUtcNow());
            if (until <= from) continue;

            try
            {
                using var scope = scopes.CreateScope();
                var megaplan = scope.ServiceProvider.GetRequiredService<IMegaplanClient>();
                var updates = await megaplan.GetContractorUpdatesAsync(from, until, stoppingToken);
                foreach (var update in updates)
                {
                    await runtime.EnqueueAsync(new WebhookEvent(Guid.NewGuid(), update.Id, update.Model, "on_after_update"), stoppingToken);
                }

                runtime.CompletePoll(until);
                logger.LogDebug("Megaplan polling completed; from {From}; until {Until}; queued {Queued}", from, until, updates.Count);
                from = until;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                var code = error is SyncException failure ? failure.Code : error.GetType().Name;
                logger.LogWarning("Megaplan polling failed; from {From}; until {Until}; error {ErrorCode}", from, until, code);
            }
        }
    }

    private static DateTimeOffset ToSecond(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
}
