using MegaplanUnisender.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Infrastructure;

public sealed class WebhookWorker(SyncRuntime runtime, IServiceScopeFactory scopes, IOptions<IntegrationOptions> options, ILogger<WebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in runtime.Reader.ReadAllAsync(stoppingToken))
        {
            runtime.Begin(entry);
            using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["EventUuid"] = entry.Uuid, ["ContractorId"] = entry.ContractorId });
            await runtime.ProcessingLock.WaitAsync(stoppingToken);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    runtime.Attempt(entry);
                    using var scope = scopes.CreateScope();
                    try
                    {
                        var result = await scope.ServiceProvider.GetRequiredService<WebhookProcessor>().ProcessAsync(entry, stoppingToken);
                        runtime.Finish(entry, result); logger.LogInformation("Webhook processed: {Status}", result); break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        var error = e as SyncException ?? new SyncException(e is HttpRequestException ? "upstream_network" :
                            e is OperationCanceledException ? "upstream_timeout" : "processing_failed", e is HttpRequestException or OperationCanceledException);
                        // A retry starts ProcessAsync again: read both APIs before deciding whether any write is still needed.
                        if ((error.Transient || error.Uncertain) && attempt < options.Value.Sync.MaxRetries)
                        {
                            var delay = options.Value.Sync.RetryBackoff.TotalSeconds * Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() / 2);
                            await Task.Delay(TimeSpan.FromSeconds(Math.Max(delay, error.RetryAfter?.TotalSeconds ?? 0)), stoppingToken);
                            continue;
                        }
                        var status = error.Uncertain ? "Uncertain" : "Failed";
                        runtime.Finish(entry, status, error.Code);
                        logger.LogError("Синхронизация не выполнена. {AlertCode}; ошибка {ErrorCode}; метод {ApiMethod}; список {ListId}; статус {Status}; попыток {Attempts}",
                            "sync_failed", error.Code, error.Operation, options.Value.Unisender.ListId, status, entry.Attempts); break;
                    }
                }
            }
            finally { runtime.ProcessingLock.Release(); }
        }
    }
}
