using System.Text.Json;
using System.Security.Cryptography;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Api;

public sealed class CliCommands(IServiceProvider services, IOptions<IntegrationOptions> options, IOptions<Secrets> secrets)
{
    public const string Help = """
        Запуск: MegaplanUnisender.Api.exe
        Отдельная команда: MegaplanUnisender.Api.exe --command "<команда>"
          run                 Webhook + команды в этом терминале (по умолчанию)
          check               Проверить подключения и автоматически определить ID, без записи и HTTP-сервера
          account             Идентичность аккаунта Megaplan
          tags                Глобальные метки Megaplan
          lists               Списки Unisender
          unisender           Списки и справочник тегов Unisender
          card <id>           Структура карточки Megaplan
          contact <email>     Статус контакта Unisender
          configuration       Настройки и определённые ID, без секретов
          generate-webhook-secret  Сгенерировать WebhookSecret: 64 hex-символа, без сохранения
          live-test <email> <add|replace|clear|exclude> --confirm-automation-disabled
          help                Эта справка
        В терминале работающего run также доступны:
          status              Состояние обработчика
          events [id]         Последние события, необязательный ID клиента
          retry <uuid>        Повтор с новой проверкой фактического состояния API
          sync <id>           Прочитать клиента Megaplan и обработать его через очередь
          stop                Остановить утилиту (также Ctrl+C)
        """;

    public static string GenerateWebhookSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task ValidateAndDiscoverAsync(CancellationToken ct)
    {
        var o = options.Value;
        var errors = o.Validate().Concat(secrets.Value.Validate(o)).ToArray();
        if (errors.Length > 0) throw new OptionsValidationException(nameof(IntegrationOptions), typeof(IntegrationOptions), errors);
        using var scope = services.CreateScope();
        await ConfigurationDiscovery.ResolveAsync(o, scope.ServiceProvider.GetRequiredService<IMegaplanClient>(),
            scope.ServiceProvider.GetRequiredService<IUnisenderClient>(), ct, secrets.Value.HasUnisenderApiKey);
    }

    public async Task<int> ExecuteAsync(string line, TextWriter output, bool running, CancellationToken ct = default)
    {
        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length == 0) return 0;
        if (args is ["generate-webhook-secret"])
        {
            await output.WriteLineAsync(GenerateWebhookSecret());
            return 0;
        }
        try
        {
            var command = args[0];
            var o = options.Value;
            if (command is "status" or "events" or "retry" or "sync" or "stop" && !running)
                throw new InvalidOperationException("Введите эту команду в терминале уже работающего run: состояние хранится только в его памяти.");
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            switch (command)
            {
                case "help" when args.Length == 1: await output.WriteLineAsync(Help); break;
                case "status" when args.Length == 1: await PrintAsync(output, sp.GetRequiredService<SyncRuntime>().Status()); break;
                case "events" when args.Length <= 2:
                    await PrintAsync(output, sp.GetRequiredService<SyncRuntime>().Events(args.ElementAtOrDefault(1))); break;
                case "sync" when args.Length == 2 && args[1].Length <= 40 && args[1].All(char.IsAsciiDigit):
                    if (!o.Sync.Enabled) throw new InvalidOperationException("Синхронизация отключена: Settings.Sync.Enabled=false.");
                    var entry = new WebhookEvent(Guid.NewGuid(), args[1], "Contractor", "on_after_update");
                    var accepted = sp.GetRequiredService<SyncRuntime>().TryAccept(entry);
                    await PrintAsync(output, new { entry.Uuid, status = accepted ? "Queued" : "QueueFull" }); return accepted ? 0 : 1;
                case "configuration" when args.Length == 1: await PrintAsync(output, Configuration()); break;
                case "retry" when args.Length == 2 && Guid.TryParse(args[1], out var uuid):
                    var status = sp.GetRequiredService<SyncRuntime>().Retry(uuid);
                    await PrintAsync(output, new { status }); return status == "Queued" ? 0 : 1;
                case "stop" when args.Length == 1:
                    services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); break;
                case "check" when args.Length == 1 && !running:
                    var enabled = o.Sync.Enabled; var dryRun = o.Sync.DryRun;
                    try { o.Sync.Enabled = true; o.Sync.DryRun = true; await ValidateAndDiscoverAsync(ct); }
                    finally { o.Sync.Enabled = enabled; o.Sync.DryRun = dryRun; }
                    await PrintAsync(output, new { status = secrets.Value.HasUnisenderApiKey ? "checked" : "checked_megaplan_only", configuration = Configuration() }); break;
                case "account" when args.Length == 1:
                    ValidateMegaplan();
                    await PrintAsync(output, new { field = o.Megaplan.AccountIdentityField,
                        identity = await sp.GetRequiredService<IMegaplanClient>().GetAccountIdentityAsync(o.Megaplan.AccountIdentityField, ct) }); break;
                case "tags" when args.Length == 1:
                    ValidateMegaplan(); await PrintAsync(output, await sp.GetRequiredService<IMegaplanClient>().GetGlobalTagsAsync(ct)); break;
                case "card" when args.Length == 2 && args[1].Length <= 40 && args[1].All(char.IsAsciiDigit):
                    ValidateMegaplan(); await PrintAsync(output, await sp.GetRequiredService<IMegaplanClient>().DiagnoseAsync(args[1], ct)); break;
                case "lists" when args.Length == 1:
                    ValidateUnisender(); await PrintAsync(output, await sp.GetRequiredService<IUnisenderClient>().ReadAsync("getLists", ct)); break;
                case "unisender" when args.Length == 1:
                    ValidateUnisender();
                    var client = sp.GetRequiredService<IUnisenderClient>();
                    await PrintAsync(output, new { lists = await client.ReadAsync("getLists", ct), tags = await client.ReadAsync("getTags", ct) }); break;
                case "contact" when args.Length == 2 && Canonical.Email(args[1]) is { } email:
                    ValidateUnisender();
                    var uni = sp.GetRequiredService<IUnisenderClient>();
                    ConfigurationDiscovery.ResolveList(o.Unisender, await uni.ReadAsync("getLists", ct));
                    var contact = await uni.GetContactAsync(email, ct);
                    await PrintAsync(output, new { status = contact, tags = contact.Exists ? await uni.GetContactTagsAsync(email, ct) : [] }); break;
                case "live-test" when args.Length == 4 && args[3] == "--confirm-automation-disabled":
                    if (!running) await ValidateAndDiscoverAsync(ct);
                    await LiveTestAsync(args[1], args[2], sp, ct);
                    await PrintAsync(output, new { status = "executed_verify_tags_in_unisender_ui" }); break;
                default: throw new InvalidOperationException("Неизвестная команда или неверные аргументы. Введите help.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 1; }
        catch (OptionsValidationException error) { await output.WriteLineAsync(string.Join(Environment.NewLine, error.Failures)); }
        catch (SyncException error) { await PrintAsync(output, new { code = error.Code, uncertain = error.Uncertain }); }
        catch (InvalidOperationException error) { await output.WriteLineAsync(error.Message); }
        catch (HttpRequestException) { await PrintAsync(output, new { code = "upstream_network" }); }
        catch (OperationCanceledException) { await PrintAsync(output, new { code = "upstream_timeout" }); }
        catch (Exception) { await PrintAsync(output, new { code = "cli_failed" }); }
        return 1;
    }

    private object Configuration()
    {
        var o = options.Value;
        return new { o.Sync.Enabled, o.Sync.DryRun, o.Sync.PollingIntervalMinutes,
            Megaplan = new { o.Megaplan.AccountIdentityField, o.Megaplan.ExpectedAccountId, o.Megaplan.EligibilityGlobalTagId,
                o.Megaplan.EligibilityGlobalTagName, o.Megaplan.SyncAllGlobalTags, KnownGlobalTags = o.Megaplan.ManagedGlobalTagIds.Length },
            Unisender = new { Configured = secrets.Value.HasUnisenderApiKey, o.Unisender.ListId, o.Unisender.ListName },
            Cache = new { o.Sync.RecentContractorCacheCapacity, o.Sync.RecentContractorCacheTtl },
            Logging = new { Provider = "Serilog", GraylogConfigured = !string.IsNullOrWhiteSpace(secrets.Value.GraylogAddress),
                secrets.Value.GraylogApplicationName } };
    }
    private void ValidateMegaplan()
    {
        if (!SafeHttps(options.Value.Megaplan.BaseUrl, true) || options.Value.Megaplan.BaseUrl.Contains("CHANGE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(secrets.Value.MegaplanApiToken))
            throw new InvalidOperationException("Заполните Settings:Megaplan:BaseUrl и Secrets:MegaplanApiToken.");
    }
    private void ValidateUnisender()
    {
        if (!SafeHttps(options.Value.Unisender.ApiBaseUrl, false) || string.IsNullOrWhiteSpace(secrets.Value.UnisenderApiKey))
            throw new InvalidOperationException("Проверьте Settings:Unisender:ApiBaseUrl и Secrets:UnisenderApiKey.");
    }
    private static bool SafeHttps(string url, bool origin) => Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.UserInfo == "" && u.Query == "" && u.Fragment == "" && (!origin || u.AbsolutePath == "/");
    private static Task PrintAsync(TextWriter output, object value) => output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    private async Task LiveTestAsync(string address, string scenario, IServiceProvider sp, CancellationToken ct)
    {
        var o = options.Value; var email = Canonical.Email(address);
        if (!o.Diagnostics.EnableLiveTests || email is null || email != Canonical.Email(o.Diagnostics.TestEmail) ||
            !o.Sync.Enabled || o.Sync.DryRun || o.Unisender.TagOwnershipMode != TagOwnershipMode.Exclusive ||
            !o.Unisender.ConfirmOverwriteAllTags || o.Unisender.ConsentPolicy != ConsentPolicy.VerifiedByAdministrator || o.Unisender.DoubleOptInMode != 3 ||
            scenario is not ("add" or "replace" or "clear" or "exclude"))
            throw new InvalidOperationException("Live-test разрешён только для Diagnostics.TestEmail при EnableLiveTests=true и настроенных политиках записи.");
        var runtime = sp.GetRequiredService<SyncRuntime>();
        await runtime.ProcessingLock.WaitAsync(ct);
        try
        {
            var client = sp.GetRequiredService<IUnisenderClient>();
            var status = await client.GetContactAsync(email, ct);
            if (!status.MaySubscribe) throw new SyncException(status.Reason);
            switch (scenario)
            {
                case "add": await client.SubscribeAsync(email, new Dictionary<string, string>(), ["integration-test-a", "integration-test-b"], ct); break;
                case "replace": await client.SubscribeAsync(email, new Dictionary<string, string>(), ["integration-test-b"], ct); break;
                case "clear": await client.ClearTagsAsync(email, ct); break;
                case "exclude": await client.ExcludeAsync(email, ct); break;
            }
        }
        finally { runtime.ProcessingLock.Release(); }
    }
}
