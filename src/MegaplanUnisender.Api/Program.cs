using MegaplanUnisender.Api;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

if (args is ["--help"] or ["-h"]) { Console.WriteLine(CliCommands.Help); return; }
if (args is ["--command", "generate-webhook-secret"] or ["--command=generate-webhook-secret"])
{
    Console.WriteLine(CliCommands.GenerateWebhookSecret());
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = "megaplan-unisender-sync");
builder.Host.UseSystemd();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true)
    .AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Logging.ClearProviders();
builder.Host.UseSerilog((_, services, logger) => ServiceLogging.Configure(logger,
    services.GetRequiredService<IOptions<Secrets>>().Value), preserveStaticLogger: true);
builder.Services.AddOptions<IntegrationOptions>().Bind(builder.Configuration.GetSection("Settings"))
    .ValidateOnStart();
builder.Services.AddOptions<Secrets>().Bind(builder.Configuration.GetSection("Secrets"))
    .ValidateOnStart();
builder.Services.AddSingleton<SyncRuntime>();
builder.Services.AddSingleton<ApiRateLimiter>();
builder.Services.AddHttpClient<IMegaplanClient, MegaplanClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IUnisenderClient, UnisenderClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<ITagStrategy>(sp => new TagStrategy(sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.Unisender));
builder.Services.AddScoped<WebhookProcessor>();
builder.Services.AddSingleton<CliCommands>();
builder.Services.AddHostedService<ConfigurationInitializer>();
builder.Services.AddHostedService<WebhookWorker>();
builder.Services.AddHostedService<ContractorPollingWorker>();
builder.Services.AddHostedService<TerminalConsole>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1048576);
var app = builder.Build();
var commands = app.Services.GetRequiredService<CliCommands>();
var command = builder.Configuration["command"] ?? "run";
if (command != "run")
{
    Environment.ExitCode = await commands.ExecuteAsync(command, Console.Out, running: false);
    await app.DisposeAsync();
    return;
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (SyncException failure)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { code = failure.Code, uncertain = failure.Uncertain });
        }
    }
    catch (Exception)
    {
        app.Logger.LogError("HTTP operation failed; correlation {CorrelationId}", context.TraceIdentifier);
        if (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 503; }
    }
});
app.MapPost("/webhooks/megaplan/{secret}", Webhook.HandleAsync);
await app.RunAsync();
public partial class Program;
