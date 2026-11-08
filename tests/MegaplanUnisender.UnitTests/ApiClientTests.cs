using System.Net;
using System.Text;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace MegaplanUnisender.UnitTests;

public sealed class ApiClientTests
{
    [Theory]
    [InlineData("[{\"id\":42}]")]
    [InlineData("[{\"id\":42,\"status\":null}]")]
    [InlineData("[{\"id\":42,\"status\":{}}]")]
    [InlineData("[{\"id\":42,\"status\":false}]")]
    [InlineData("[{\"id\":42,\"status\":\"\"}]")]
    [InlineData("[{\"status\":\"unsubscribed\"}]")]
    [InlineData("[{\"id\":0,\"status\":\"unsubscribed\"}]")]
    public async Task MalformedMembershipCannotAuthorizeSubscription(string json)
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card();
        s.Options.Unisender.FieldMappings.Clear();
        using var lists = System.Text.Json.JsonDocument.Parse(json);
        s.Override = request => request.RequestUri!.AbsolutePath.EndsWith("getContact", StringComparison.Ordinal)
            ? Scenario.Json(new { result = new { email = new { email = "a@example.test", status = "active" }, lists = lists.RootElement } }) : null;
        Assert.Equal("unisender_list_membership_unverified", (await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Code);
        Assert.Empty(s.Writes);
    }
    [Fact] public async Task MegaplanThrottleDoesNotBlockIndependentUnisenderBudget()
    {
        var limiter = new ApiRateLimiter(Options.Create(new IntegrationOptions()));
        await limiter.WaitAsync("megaplan", default);
        using var cancel = new CancellationTokenSource();
        var delayed = limiter.WaitAsync("megaplan", cancel.Token);
        var independent = limiter.WaitAsync("getContact", default);
        Assert.Same(independent, await Task.WhenAny(independent, Task.Delay(500)));
        await independent;
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayed);
    }
    [Fact] public void SecretValuesAreBoundSeparatelyFromSettings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Settings:Megaplan:BaseUrl"] = "https://crm.example.test",
            ["Secrets:MegaplanApiToken"] = "private-fixture-token"
        }).Build();
        var settings = config.GetSection("Settings").Get<IntegrationOptions>()!;
        var secrets = config.GetSection("Secrets").Get<Secrets>()!;
        Assert.Equal("private-fixture-token", secrets.MegaplanApiToken);
        Assert.DoesNotContain("private-fixture-token", System.Text.Json.JsonSerializer.Serialize(settings));
    }
    [Theory] [InlineData("retry_later")] [InlineData("api_call_limit_exceeded_for_api_key")] [InlineData("api_call_limit_exceeded_for_ip")]
    public async Task DocumentedBusinessThrottleIsRetried(string code)
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => Scenario.Json(new { error = "temporary", code }))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        var e = await Assert.ThrowsAsync<SyncException>(() => client.ReadAsync("getLists", default)); Assert.True(e.Transient); Assert.False(e.Uncertain);
    }
    [Theory] [InlineData(429, true, false)] [InlineData(500, false, true)] [InlineData(403, false, false)] [InlineData(408, false, true)] [InlineData(402, false, false)]
    public async Task WritesClassifyHttpFailures(int status, bool transient, bool uncertain)
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => new((HttpStatusCode)status))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        var e = await Assert.ThrowsAsync<SyncException>(() => client.SubscribeAsync("a@example.test", new Dictionary<string, string>(), ["tag"], default));
        Assert.Equal(transient, e.Transient); Assert.Equal(uncertain, e.Uncertain);
        Assert.Equal("subscribe", e.Operation);
    }
    [Fact] public async Task UnisenderBusinessErrorsAreRedacted()
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => Scenario.Json(new { error = "secret@example.test API-KEY", code = "invalid_api_key" }))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        var e = await Assert.ThrowsAsync<SyncException>(() => client.ReadAsync("getLists", default)); Assert.Equal("unisender_invalid_api_key", e.Code); Assert.DoesNotContain("secret", e.Message);
    }
    [Fact] public async Task MegaplanBusinessErrorsAreNotSuccessfulCards()
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new MegaplanClient(new HttpClient(new Handler(_ => Scenario.Json(new { meta = new { status = 422 }, data = new { } }))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        Assert.Equal("megaplan_meta_error", (await Assert.ThrowsAsync<SyncException>(() => client.GetAsync("1", default))).Code);
    }
    [Fact] public async Task MegaplanAuthorizationAndRequestEncodingFollowSdk()
    {
        var o = Options.Create(new IntegrationOptions { Megaplan = new() { BaseUrl = "https://crm.example.test" } });
        var client = new MegaplanClient(new HttpClient(new Handler(r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme); Assert.Equal("test-token", r.Headers.Authorization.Parameter);
            Assert.Equal("/api/v3/contractor/1", r.RequestUri!.AbsolutePath); return new(HttpStatusCode.NotFound);
        })), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        Assert.Null(await client.GetAsync("1", default));
    }
    [Fact] public async Task UnknownMissingContactResponseIsBlockedByDefault()
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => Scenario.Json(new { result = (object?)null }))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        Assert.Equal("unisender_null_result_unverified", (await Assert.ThrowsAsync<SyncException>(() => client.GetContactAsync("a@example.test", default))).Code);
    }
    [Fact] public async Task InvalidWriteJsonIsUncertain()
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not-json", Encoding.UTF8) })), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => client.SubscribeAsync("a@example.test", new Dictionary<string, string>(), ["tag"], default))).Uncertain);
    }
    [Fact] public async Task ExcludeRefusesUnscopedRequest()
    {
        var o = Options.Create(new IntegrationOptions());
        var client = new UnisenderClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send"))), o, Options.Create(new Secrets { MegaplanApiToken = "test-token", UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(o));
        Assert.Equal("unisender_target_list_missing", (await Assert.ThrowsAsync<SyncException>(() => client.ExcludeAsync("a@example.test", default))).Code);
    }
    [Fact] public void DefaultConfigurationCannotWriteAndExclusiveRequiresConfirmation()
    {
        var o = new IntegrationOptions(); Assert.False(o.Sync.Enabled); Assert.True(o.Sync.DryRun); Assert.Empty(o.Validate());
        o.Unisender.TagOwnershipMode = TagOwnershipMode.Exclusive; Assert.Contains(o.Validate(), x => x.Contains("ConfirmOverwriteAllTags", StringComparison.Ordinal));
    }
    [Theory] [InlineData("ContractorHuman")] [InlineData("ContractorCompany")]
    public void PostalAddressDoesNotMakeCompleteContactInfoLookPartial(string model)
    {
        var data = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = "10", contentType = model, name = "Fixture", tags = Array.Empty<object>(), contactInfoCount = 3,
            contactInfo = new object[]
            {
                new { contentType = "ContactInfo", type = "email", value = "test@example.com", isMain = true },
                new { contentType = "ContactInfo", type = "phone", value = "+70000000000" },
                new { contentType = "Address", type = new { contentType = "AddressType", id = "1", name = "Office" }, value = "Postal address" }
            }
        });
        var contractor = MegaplanClient.Parse(data, new(), new() { FieldMappings = new() { ["name"] = "Name", ["phone"] = "Phone" } });
        Assert.Equal("test@example.com", contractor.Email); Assert.Equal("+70000000000", contractor.Fields["Phone"]);
    }
    [Fact] public void PostalAddressAloneIsNotAnEmail()
    {
        using var data = System.Text.Json.JsonDocument.Parse("""{"id":"10","contentType":"ContractorCompany","tags":[],"contactInfoCount":1,"contactInfo":[{"contentType":"Address","type":{"contentType":"AddressType","id":"1"},"value":"test@example.com"}]}""");
        Assert.Null(MegaplanClient.Parse(data.RootElement, new(), new()).Email);
    }
    [Fact] public void UnknownContactTypeStillBlocksProcessing()
    {
        using var data = System.Text.Json.JsonDocument.Parse("""{"id":"10","contentType":"ContractorCompany","tags":[],"contactInfo":[{"contentType":"ContactInfo","type":{"id":"1"},"value":"test@example.com"}]}""");
        Assert.Equal("megaplan_partial_contact_info", Assert.Throws<SyncException>(() => MegaplanClient.Parse(data.RootElement, new(), new())).Code);
    }
    [Fact] public void PostalAddressDoesNotHideIncompleteCollection()
    {
        using var data = System.Text.Json.JsonDocument.Parse("""{"id":"10","contentType":"ContractorCompany","tags":[],"contactInfoCount":2,"contactInfo":[{"contentType":"Address","type":{"id":"1"},"value":"Postal address"}]}""");
        Assert.Equal("megaplan_partial_contact_info", Assert.Throws<SyncException>(() => MegaplanClient.Parse(data.RootElement, new(), new())).Code);
    }
    [Fact] public void UnisenderKeyIsRequiredForWritesButNotForMegaplanOnlyDryRun()
    {
        var secrets = new Secrets { MegaplanApiToken = "fixture", WebhookSecret = new string('s', 32) };
        var settings = new IntegrationOptions { Sync = new() { Enabled = true, DryRun = true } };
        Assert.Empty(secrets.Validate(settings));
        settings.Sync.DryRun = false;
        Assert.Contains(secrets.Validate(settings), x => x.Contains("UnisenderApiKey", StringComparison.Ordinal));
    }
}
