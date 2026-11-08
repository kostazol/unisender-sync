using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace MegaplanUnisender.Api;

public sealed class TerminalConsole(CliCommands commands, IHostApplicationLifetime lifetime, ILogger<TerminalConsole> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService()) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            await Console.Out.WriteLineAsync("Webhook запущен. CLI: help, status, events, configuration, retry <uuid>, stop.");
            while (!stoppingToken.IsCancellationRequested)
            {
                // A blocked terminal read must not delay Ctrl+C or host shutdown.
                var line = await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(stoppingToken);
                if (line is null) return; // Closed stdin leaves the webhook worker running.
                await commands.ExecuteAsync(line, Console.Out, running: true, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (IOException) { logger.LogWarning("Terminal input closed; webhook remains active"); }
    }
}
