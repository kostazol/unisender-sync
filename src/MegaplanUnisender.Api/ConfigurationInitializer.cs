using MegaplanUnisender.Core;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Api;

// Runs after the service lifetime connects to SCM/systemd, before the worker and HTTP listener.
public sealed class ConfigurationInitializer(CliCommands commands, IOptions<IntegrationOptions> options,
    IOptions<Secrets> secrets, ILogger<ConfigurationInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Logging configured: Serilog; Graylog {GraylogEnabled}; application {ApplicationName}",
            !string.IsNullOrWhiteSpace(secrets.Value.GraylogAddress), secrets.Value.GraylogApplicationName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await commands.ValidateAndDiscoverAsync(timeout.Token);
        var o = options.Value;
        if (!o.Sync.Enabled) return;
        logger.LogInformation("Configuration resolved: Megaplan account field {AccountField}; eligibility tag {TagId}; Unisender list {ListId}; dry run {DryRun}",
            o.Megaplan.AccountIdentityField, o.Megaplan.EligibilityGlobalTagId, o.Unisender.ListId, o.Sync.DryRun);
        if (o.Sync.DryRun && !secrets.Value.HasUnisenderApiKey)
            logger.LogInformation("Megaplan-only dry-run: Unisender is not configured; webhook events will only read Megaplan");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
