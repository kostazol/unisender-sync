using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MegaplanUnisender.Api;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MegaplanUnisender.IntegrationTests;

public sealed class TestApp(bool enabled = false, int capacity = 256, bool worker = true, bool discoverTagByName = false, bool automatic = false, bool dryRun = false, bool unisenderConfigured = true, int maxRetries = 6, int graylogPort = 0, string minimumLogLevel = "Information") : WebApplicationFactory<Program>
{
    public const string Secret = "fixture-webhook-secret-32-characters";
    public int WriteCalls;
    public int ThrottleNextRead;
    public int FailNextCrmRead;
    public bool FailWrite;
    public bool FailWriteAfterApplying;
    public bool ExistingContact = true;
    public string? BusinessErrorCode;
    public bool ContactLimitWarning;
    public int TagCatalogCalls;
    public bool MissingGlobalTag;
    public bool MissingAccountIdentity;
    public object[] Lists = [new { id = "42", title = "Мегаплан" }];
    public int AccountInfoCalls;
    public int ListCatalogCalls;
    public int MissingContactProbeCalls;
    public int ContactTagReads;
    public string[] ContactTags = [];
    public readonly Guid ExportTaskId = Guid.NewGuid();
    public SyncRuntime Runtime => Services.GetRequiredService<SyncRuntime>();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:WebhookSecret"] = Secret,
            ["Secrets:GraylogAddress"] = graylogPort == 0 ? "" : "127.0.0.1",
            ["Secrets:GraylogPort"] = graylogPort.ToString(),
            ["Secrets:GraylogApplicationName"] = "sync-tests",
            ["Secrets:GraylogMinimumLevel"] = minimumLogLevel,
            ["Secrets:MegaplanApiToken"] = "fixture-token", ["Secrets:UnisenderApiKey"] = "fixture-key",
            ["Settings:Megaplan:ExpectedAccountId"] = "299", ["Settings:Megaplan:ExpectedIntegrationUuid"] = "4897ba31-28ec-48fb-a581-85cf8844a7d1",
            ["Settings:Megaplan:AccountIdentityField"] = "uniqueAccountId", ["Settings:Megaplan:SyncAllGlobalTags"] = "false",
            ["Settings:Megaplan:BaseUrl"] = "https://crm.example.test", ["Settings:Megaplan:EligibilityGlobalTagId"] = "1",
            ["Settings:Megaplan:EligibilityGlobalTagName"] = discoverTagByName ? "Тест" : "",
            ["Settings:Megaplan:ManagedGlobalTagIds:0"] = "1", ["Settings:Megaplan:ManagedGlobalTagIds:1"] = "2",
            ["Settings:Megaplan:TagMappings:2"] = "mapped", ["Settings:Unisender:ListId"] = "42",
            ["Settings:Unisender:TagOwnershipMode"] = "Exclusive", ["Settings:Unisender:ConfirmOverwriteAllTags"] = "true",
            ["Settings:Unisender:ConsentPolicy"] = "VerifiedByAdministrator", ["Settings:Sync:DryRun"] = "false",
            ["Settings:Sync:Enabled"] = enabled.ToString(), ["Settings:Sync:QueueCapacity"] = capacity.ToString(),
            ["Settings:Sync:MaxRetries"] = maxRetries.ToString(),
            ["Settings:Sync:RetryBackoff"] = "00:00:00.010", ["Settings:Sync:RateLimits:GeneralPerMinute"] = "1200"
        }));
        builder.ConfigureServices(services =>
        {
            if (!unisenderConfigured) services.PostConfigure<Secrets>(s => s.UnisenderApiKey = "");
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(TerminalConsole)).ToArray()) services.Remove(descriptor);
            services.PostConfigure<IntegrationOptions>(o =>
            {
                if (discoverTagByName || automatic)
                {
                    o.Megaplan.SyncAllGlobalTags = true;
                    o.Megaplan.EligibilityGlobalTagId = "";
                    o.Megaplan.ManagedGlobalTagIds = [];
                    o.Megaplan.TagMappings = [];
                }
                if (automatic)
                {
                    o.Megaplan.ExpectedAccountId = "";
                    o.Megaplan.AccountIdentityField = "permanentHost";
                    o.Megaplan.EligibilityGlobalTagName = "Тест";
                    o.Megaplan.SyncAllGlobalTags = true;
                    o.Unisender.ListId = "";
                    o.Unisender.ListName = "";
                }
                o.Sync.DryRun = dryRun;
                if (dryRun) { o.Unisender.ConsentPolicy = ConsentPolicy.Disabled; o.Unisender.TagOwnershipMode = TagOwnershipMode.PreserveExternal; }
            });
            services.AddHttpClient<IMegaplanClient, MegaplanClient>().ConfigurePrimaryHttpMessageHandler(() => new MockApi(this));
            services.AddHttpClient<IUnisenderClient, UnisenderClient>().ConfigurePrimaryHttpMessageHandler(() => new MockApi(this));
            if (!worker)
                foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(WebhookWorker)).ToArray()) services.Remove(descriptor);
        });
    }
    public static object Event(string? uuid = null, string model = "ContractorHuman", string account = "299", string integration = "4897ba31-28ec-48fb-a581-85cf8844a7d1", string ev = "on_after_update", string? permanentHost = null) => new
    {
        uuid = uuid ?? Guid.NewGuid().ToString(), integrationUuid = integration, model, @event = ev,
        data = new { id = "1000251", contentType = model }, accountInfo = new { uniqueAccountId = account, permanentHost }
    };
    public async Task<WebhookEvent> WaitForEvent(Guid uuid)
    {
        for (var i = 0; i < 100; i++)
        {
            var entry = Runtime.Events().SingleOrDefault(x => x.Uuid == uuid);
            if (entry?.Status is not (null or "Pending" or "Processing")) return entry;
            await Task.Delay(100);
        }
        throw new TimeoutException("Webhook was not processed by the worker");
    }
    public async Task<(int ExitCode, string Output)> CommandAsync(string command, bool running = true)
    {
        using var output = new StringWriter();
        var exitCode = await Services.GetRequiredService<CliCommands>().ExecuteAsync(command, output, running);
        return (exitCode, output.ToString());
    }
    private sealed class MockApi(TestApp app) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            if (request.RequestUri!.Host == "usdata.s3.unisender.ru")
                return new(HttpStatusCode.OK) { Content = new StringContent("email,tags\r\nfixture@example.test,\"" +
                    string.Join(',', app.ContactTags).Replace("\"", "\"\"") + "\"\r\n", Encoding.UTF8, "text/csv") };
            if (request.RequestUri!.Host == "crm.example.test")
            {
                if (Interlocked.Exchange(ref app.FailNextCrmRead, 0) == 1) throw new HttpRequestException("fixture network failure");
                if (request.RequestUri.AbsolutePath == "/api/v3/accountInfo")
                {
                    Interlocked.Increment(ref app.AccountInfoCalls);
                    body = new { meta = new { status = 200 }, data = new { contentType = "AccountInfo", permanentHost = app.MissingAccountIdentity ? null : "permanent.example.test", ecometToken = "unrelated-private-token" } };
                }
                else if (request.RequestUri.AbsolutePath == "/api/v3/contractor/tags")
                {
                    Interlocked.Increment(ref app.TagCatalogCalls);
                    var tags = new List<object> { new { id = "2", name = "Дополнительно", isGlobal = true }, new { id = "99", name = "Тест", isGlobal = false } };
                    if (!app.MissingGlobalTag) tags.Add(new { id = "1", name = "Тест", isGlobal = true });
                    body = new { meta = new { status = 200, pagination = new { count = tags.Count } }, data = tags };
                }
                else body = new { meta = new { status = 200 }, data = new
            {
                id = "1000251", contentType = "ContractorHuman", name = "Fixture",
                contactInfo = new[] { new { type = "email", value = "fixture@example.test" } },
                tags = new[] { new { id = "1", name = "Тест", isGlobal = true }, new { id = "2", name = "Дополнительно", isGlobal = true } }
                } };
            }
            else
            {
                var method = request.RequestUri.AbsolutePath.Split('/').Last();
                if (method == "getLists")
                {
                    Interlocked.Increment(ref app.ListCatalogCalls); body = new { result = app.Lists };
                }
                else if (method == "getContact")
                {
                    var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
                    if (form["email"].StartsWith("megaplan-probe-", StringComparison.Ordinal))
                    { Interlocked.Increment(ref app.MissingContactProbeCalls); body = new { result = (object?)null }; }
                    else if (!app.ExistingContact) body = new { result = (object?)null };
                    else
                    {
                        if (Interlocked.Exchange(ref app.ThrottleNextRead, 0) == 1) return new(HttpStatusCode.TooManyRequests);
                        body = new { result = new { email = new { email = form["email"], status = "active" }, fields = new { }, lists = new[] { new { id = "42", status = "active" } } } };
                    }
                }
                else if (method == "exportContacts")
                {
                    Interlocked.Increment(ref app.ContactTagReads);
                    var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
                    Assert.Equal("fixture@example.test", form["email"]); Assert.False(form.ContainsKey("list_id"));
                    body = new { result = new { task_uuid = app.ExportTaskId, status = "new" } };
                }
                else if (method == "getTaskResult")
                    body = new { result = new { task_uuid = app.ExportTaskId, task_type = "export_contacts", status = "completed",
                        file_to_download = "https://usdata.s3.unisender.ru/fixture.csv" } };
                else if (method == "importContacts")
                {
                    Interlocked.Increment(ref app.WriteCalls);
                    if (app.FailWrite && !app.FailWriteAfterApplying) return new(HttpStatusCode.InternalServerError);
                    var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
                    var tags = form["data[0][1]"].Length == 0 ? [] : form["data[0][1]"].Split(',');
                    app.ContactTags = form["overwrite_tags"] == "1" ? tags : Canonical.Tags(app.ContactTags.Concat(tags));
                    if (app.FailWrite) return new(HttpStatusCode.InternalServerError);
                    body = new { result = new { updated = 1, invalid = 0, log = app.ContactLimitWarning
                        ? new[] { new { index = 0, code = "w_common_overlimit", message = "private-api-key fixture@example.test" } }
                        : [] } };
                }
                else if (method == "subscribe")
                {
                    Interlocked.Increment(ref app.WriteCalls);
                    if (app.BusinessErrorCode is { } code)
                        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                        { error = "Contact limit: private-api-key fixture@example.test", code }), Encoding.UTF8, "application/json") };
                    if (app.FailWrite) return new(HttpStatusCode.InternalServerError);
                    var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
                    if (form.TryGetValue("tags", out var tags)) app.ContactTags = tags.Split(',');
                    app.ExistingContact = true;
                    body = new { result = new { person_id = 12345 }, warnings = app.ContactLimitWarning
                        ? new[] { new { warning = "Addresses overlimit for current period" } } : [] };
                }
                else throw new InvalidOperationException("Unexpected mock endpoint");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
public sealed class WebhookTests
{
    [Fact] public async Task AutomaticStartupResolvesAccountTagsAndListBeforeProcessingWebhook()
    {
        using var app = new TestApp(enabled: true, automatic: true); using var client = app.CreateClient();
        var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationOptions>>().Value;
        Assert.Equal("permanent.example.test", settings.Megaplan.ExpectedAccountId); Assert.Equal("42", settings.Unisender.ListId);
        Assert.Equal("Мегаплан", settings.Unisender.ListName); Assert.Equal(2, settings.Megaplan.ManagedGlobalTagIds.Length);
        Assert.Equal(1, app.AccountInfoCalls); Assert.Equal(1, app.TagCatalogCalls); Assert.Equal(1, app.ListCatalogCalls); Assert.Equal(1, app.MissingContactProbeCalls);
        var uuid = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString(), permanentHost: "permanent.example.test"))).StatusCode);
        Assert.Equal("Synced", (await app.WaitForEvent(uuid)).Status); Assert.Equal(1, app.WriteCalls);
        var configuration = await app.CommandAsync("configuration");
        Assert.Equal(0, configuration.ExitCode);
        Assert.Contains("permanent.example.test", configuration.Output); Assert.DoesNotContain("unrelated-private-token", configuration.Output);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Url, TestApp.Event(permanentHost: "foreign.example.test"))).StatusCode);
    }
    [Fact] public async Task AutomaticDryRunNeedsNoConsentOrTagOverwriteSettings()
    {
        using var app = new TestApp(enabled: true, automatic: true, dryRun: true); using var client = app.CreateClient();
        var uuid = Guid.NewGuid(); await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString(), permanentHost: "permanent.example.test"));
        Assert.Equal("DryRun", (await app.WaitForEvent(uuid)).Status); Assert.Equal(0, app.WriteCalls); Assert.Equal(0, app.MissingContactProbeCalls);
    }
    [Fact] public async Task MegaplanOnlyDryRunAcceptsWebhookBeforeUnisenderIsConfigured()
    {
        using var app = new TestApp(enabled: true, automatic: true, dryRun: true, unisenderConfigured: false); using var client = app.CreateClient();
        var uuid = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString(), permanentHost: "permanent.example.test"))).StatusCode);
        Assert.Equal("DryRun", (await app.WaitForEvent(uuid)).Status);
        Assert.Equal(1, app.AccountInfoCalls); Assert.Equal(1, app.TagCatalogCalls); Assert.Equal(0, app.ListCatalogCalls); Assert.Equal(0, app.MissingContactProbeCalls); Assert.Equal(0, app.WriteCalls);
        var configuration = (await app.CommandAsync("configuration")).Output;
        Assert.Contains("\"configured\": false", configuration);
    }
    [Fact] public void AutomaticStartupRejectsAmbiguousListBeforeAnyWrite()
    {
        using var app = new TestApp(enabled: true, automatic: true) { Lists = [new { id = "42", title = "Первый" }, new { id = "43", title = "Второй" }] };
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("ListName", error.Message); Assert.Equal(0, app.WriteCalls);
    }
    [Fact] public void AutomaticStartupDoesNotLearnIdentityFromFirstWebhook()
    {
        using var app = new TestApp(enabled: true, automatic: true) { MissingAccountIdentity = true };
        Assert.Throws<InvalidOperationException>(() => app.CreateClient()); Assert.Equal(0, app.WriteCalls); Assert.Equal(0, app.TagCatalogCalls);
    }
    [Fact] public void DisabledAutomaticSetupDoesNotCallExternalApis()
    {
        using var app = new TestApp(automatic: true); using var client = app.CreateClient();
        Assert.Equal(0, app.AccountInfoCalls); Assert.Equal(0, app.TagCatalogCalls); Assert.Equal(0, app.ListCatalogCalls); Assert.Equal(0, app.MissingContactProbeCalls);
    }
    [Fact] public async Task InstallationLifecycleWithoutAccountInfoDoesNotRequireManualAccountSetup()
    {
        using var app = new TestApp(enabled: true, automatic: true, dryRun: true); using var client = app.CreateClient();
        var uuid = Guid.NewGuid();
        var lifecycle = new { uuid = uuid.ToString(), integrationUuid = "4897ba31-28ec-48fb-a581-85cf8844a7d1", model = "Integration", @event = "on_after_create", data = new { apiToken = "do-not-adopt-token" } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, lifecycle)).StatusCode);
        Assert.Equal("SkippedLifecycle", (await app.WaitForEvent(uuid)).Status); Assert.Equal(0, app.WriteCalls);
        var contractor = new { uuid = Guid.NewGuid().ToString(), lifecycle.integrationUuid, model = "ContractorHuman", @event = "on_after_update", data = new { id = "1000251" } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Url, contractor)).StatusCode);
    }
    [Fact] public async Task StartupResolvesGlobalTagNameAndWebhookUsesItsId()
    {
        using var app = new TestApp(enabled: true, discoverTagByName: true); using var client = app.CreateClient();
        Assert.Equal(1, app.TagCatalogCalls);
        var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationOptions>>().Value;
        Assert.Equal("1", settings.Megaplan.EligibilityGlobalTagId); Assert.Empty(settings.Megaplan.TagMappings);
        var uuid = Guid.NewGuid(); await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString()));
        Assert.Equal("Synced", (await app.WaitForEvent(uuid)).Status); Assert.Equal(1, app.WriteCalls); Assert.Equal(1, app.TagCatalogCalls);
    }
    [Fact] public void StartupFailsWhenOnlyPrivateTagMatches()
    {
        using var app = new TestApp(enabled: true, discoverTagByName: true) { MissingGlobalTag = true };
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("Глобальная метка не найдена", error.Message); Assert.Equal(0, app.WriteCalls);
    }
    [Fact] public void DisabledSyncDoesNotFetchTagCatalogAtStartup()
    {
        using var app = new TestApp(discoverTagByName: true); using var client = app.CreateClient(); Assert.Equal(0, app.TagCatalogCalls);
    }
    [Fact] public async Task CrmNetworkFailureIsRetriedInMemory()
    {
        using var app = new TestApp(enabled: true) { FailNextCrmRead = 1 }; using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString())); var entry = await app.WaitForEvent(uuid);
        Assert.Equal("Synced", entry.Status); Assert.Equal(2, entry.Attempts); Assert.Equal(1, app.WriteCalls);
    }
    private static string Url => "/webhooks/megaplan/" + TestApp.Secret;
    [Fact] public async Task FullWebhookFlowRunsAgainstMockHttpWithoutDatabase()
    {
        using var app = new TestApp(enabled: true); using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString()))).StatusCode);
        Assert.Equal("Synced", (await app.WaitForEvent(uuid)).Status); Assert.Equal(1, app.WriteCalls);
    }
    [Fact] public async Task DuplicateUuidDoesNotRepeatExternalWork()
    {
        using var app = new TestApp(enabled: true); using var client = app.CreateClient(); var uuid = Guid.NewGuid(); var ev = TestApp.Event(uuid.ToString());
        await client.PostAsJsonAsync(Url, ev); await app.WaitForEvent(uuid);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, ev)).StatusCode); Assert.Equal(1, app.WriteCalls); Assert.Single(app.Runtime.Events());
    }
    [Fact] public async Task TransientFailureIsRetriedInMemory()
    {
        using var app = new TestApp(enabled: true) { ThrottleNextRead = 1 }; using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString())); var entry = await app.WaitForEvent(uuid);
        Assert.Equal("Synced", entry.Status); Assert.Equal(2, entry.Attempts); Assert.Equal(1, app.WriteCalls);
    }
    [Fact] public async Task UncertainWriteRetryRechecksActualApiState()
    {
        using var app = new TestApp(enabled: true, maxRetries: 0) { FailWrite = true }; using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString())); Assert.Equal("Uncertain", (await app.WaitForEvent(uuid)).Status);
        app.FailWrite = false;
        Assert.Equal(0, (await app.CommandAsync("retry " + uuid)).ExitCode);
        Assert.Equal("Synced", (await app.WaitForEvent(uuid)).Status); Assert.Equal(2, app.WriteCalls);
    }
    [Fact] public async Task LostWriteResponseRecoversAutomaticallyWithoutDuplicateWrite()
    {
        using var app = new TestApp(enabled: true) { FailWrite = true, FailWriteAfterApplying = true };
        using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString()));
        var entry = await app.WaitForEvent(uuid);
        Assert.Equal("Synced", entry.Status); Assert.Equal(2, entry.Attempts);
        Assert.Equal(1, app.WriteCalls); Assert.Equal(2, app.ContactTagReads);
    }
    [Fact] public async Task FullQueueReturns503WithoutRememberingRejectedEvent()
    {
        using var app = new TestApp(enabled: true, capacity: 1, worker: false); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(Url, TestApp.Event())).StatusCode); Assert.Single(app.Runtime.Events());
    }
    [Theory] [InlineData("wrong", "299", "4897ba31-28ec-48fb-a581-85cf8844a7d1")]
    [InlineData(TestApp.Secret, "other", "4897ba31-28ec-48fb-a581-85cf8844a7d1")] [InlineData(TestApp.Secret, "299", "untrusted")]
    public async Task RejectsUntrustedIdentity(string secret, string account, string integration)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/webhooks/megaplan/" + secret, TestApp.Event(account: account, integration: integration))).StatusCode);
        Assert.Empty(app.Runtime.Events());
    }
    [Theory] [InlineData("Contractor")] [InlineData("ContractorCompany")] [InlineData("ContractorHuman")]
    public async Task AcceptsConfiguredModelVariants(string model)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event(model: model))).StatusCode);
        Assert.Equal("Disabled", Assert.Single(app.Runtime.Events()).Status);
    }
    [Theory] [InlineData("Integration", "SkippedLifecycle")] [InlineData("Unknown", "SkippedUnsupported")]
    public async Task UnsupportedEventsAreSkipped(string model, string status)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url, TestApp.Event(model: model))).StatusCode);
        Assert.Equal(status, Assert.Single(app.Runtime.Events()).Status);
    }
    [Fact] public async Task NewProcessHasNoPreviousEventHistory()
    {
        using (var app = new TestApp()) { using var client = app.CreateClient(); await client.PostAsJsonAsync(Url, TestApp.Event()); Assert.Single(app.Runtime.Events()); }
        using var restarted = new TestApp(); using var nextClient = restarted.CreateClient(); Assert.Empty(restarted.Runtime.Events());
    }
    [Fact] public async Task OversizedAndMalformedBodiesAreRejected()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(Url, new StringContent(new string('a', 70000), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Url, new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Theory]
    [InlineData("/admin/status")] [InlineData("/admin/events")] [InlineData("/admin/configuration")]
    [InlineData("/admin/diagnostics/live-test")] [InlineData("/admin/diagnostics/unisender")]
    [InlineData("/health/live")] [InlineData("/health/ready")] [InlineData("/")]
    public async Task NoManagementOrHealthEndpointIsExposed(string path)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(0, app.WriteCalls);
    }
    [Fact] public void WebhookIsTheOnlyRegisteredHttpRoute()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var routes = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToArray();
        var route = Assert.Single(routes);
        Assert.Equal("/webhooks/megaplan/{secret}", route.RoutePattern.RawText);
        Assert.Equal(["POST"], route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods);
    }
    [Fact] public async Task TerminalCommandsReadLiveStateAndStandaloneStatusDoesNotPretendToSeeAnotherProcess()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var uuid = Guid.NewGuid(); await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString()));
        var status = await app.CommandAsync("status"); Assert.Equal(0, status.ExitCode); Assert.Contains("pending", status.Output);
        var events = await app.CommandAsync("events 1000251"); Assert.Equal(0, events.ExitCode); Assert.Contains(uuid.ToString(), events.Output);
        var standalone = await app.CommandAsync("status", running: false); Assert.Equal(1, standalone.ExitCode); Assert.Contains("терминале уже работающего", standalone.Output);
    }
    [Fact] public async Task CliReadOnlyCheckDoesNotEnableWritesOrChangeConfiguredRunMode()
    {
        using var app = new TestApp(automatic: true); using var client = app.CreateClient();
        var check = await app.CommandAsync("check", running: false);
        Assert.Equal(0, check.ExitCode); Assert.Contains("checked", check.Output); Assert.Equal(0, app.WriteCalls);
        var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationOptions>>().Value;
        Assert.False(settings.Sync.Enabled); Assert.False(settings.Sync.DryRun); Assert.Equal("42", settings.Unisender.ListId);
        Assert.Equal(1, app.AccountInfoCalls); Assert.Equal(0, app.MissingContactProbeCalls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WebhookSecretGeneratorWorksWithoutApisAndDoesNotRotateConfiguredSecret(bool running)
    {
        using var app = new TestApp();
        var generated = await app.CommandAsync("generate-webhook-secret", running);
        Assert.Equal(0, generated.ExitCode); Assert.Matches("\\A[0-9A-F]{64}\\z", generated.Output.Trim());
        Assert.Equal(TestApp.Secret, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Secrets>>().Value.WebhookSecret);
        Assert.Equal(0, app.AccountInfoCalls); Assert.Equal(0, app.TagCatalogCalls); Assert.Equal(0, app.ListCatalogCalls);
        Assert.Equal(0, app.MissingContactProbeCalls); Assert.Equal(0, app.WriteCalls); Assert.Empty(app.Runtime.Events());
    }
    [Fact] public async Task CliDoesNotExposeTokensAndLiveTestRemainsExplicitlyRestricted()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var configuration = await app.CommandAsync("configuration");
        Assert.DoesNotContain("fixture-token", configuration.Output); Assert.DoesNotContain("fixture-key", configuration.Output); Assert.DoesNotContain(TestApp.Secret, configuration.Output);
        var result = await app.CommandAsync("live-test fixture@example.test add --confirm-automation-disabled");
        Assert.Equal(1, result.ExitCode); Assert.Equal(0, app.WriteCalls);
        Assert.Equal(1, (await app.CommandAsync("unknown-command")).ExitCode);
    }
    [Fact] public async Task CliSyncUsesSameWorkerAndKeepsResolvedListIdAcrossEvents()
    {
        using var app = new TestApp(enabled: true, automatic: true); using var client = app.CreateClient();
        Assert.Equal(0, (await app.CommandAsync("sync 1000251")).ExitCode);
        for (var i = 0; i < 100 && app.Runtime.Events().SingleOrDefault()?.Status is not "Synced"; i++) await Task.Delay(20);
        Assert.Equal("Synced", Assert.Single(app.Runtime.Events()).Status);
        Assert.Equal(1, app.ListCatalogCalls); Assert.Equal(1, app.WriteCalls);
        var uuid = Guid.NewGuid(); await client.PostAsJsonAsync(Url, TestApp.Event(uuid.ToString(), permanentHost: "permanent.example.test"));
        Assert.Equal("Synced", (await app.WaitForEvent(uuid)).Status);
        Assert.Equal(1, app.ListCatalogCalls); Assert.Equal(1, app.WriteCalls);
        Assert.Equal(2, app.ContactTagReads);
    }
}
