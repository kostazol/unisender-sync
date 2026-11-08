using System.Net;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class ConfigurationDiscoveryTests
{
    [Theory]
    [InlineData("", "", "42", "Единственный", "" )]
    [InlineData("", "Второй", "43", "Первый", "Второй")]
    [InlineData("42", "", "42", "Первый", "Второй")]
    public void ListCanBeSelectedWithoutKnowingItsId(string id, string name, string expected, string first, string second)
    {
        var options = new UnisenderOptions { ListId = id, ListName = name };
        var lists = new List<object> { new { id = 42, title = first } };
        if (second.Length > 0) lists.Add(new { id = 43, title = second });
        ConfigurationDiscovery.ResolveList(options, JsonSerializer.SerializeToElement(lists));
        Assert.Equal(expected, options.ListId); Assert.False(string.IsNullOrWhiteSpace(options.ListName));
    }
    [Theory]
    [InlineData("[]", "", "")]
    [InlineData("[{\"id\":42,\"title\":\"A\"},{\"id\":43,\"title\":\"B\"}]", "", "")]
    [InlineData("[{\"id\":42,\"title\":\"A\"},{\"id\":43,\"title\":\"A\"}]", "", "A")]
    [InlineData("[{\"id\":42,\"title\":\"A\"}]", "43", "")]
    [InlineData("[{\"id\":42,\"title\":\"A\"}]", "42", "B")]
    public void MissingAmbiguousOrConflictingListCannotBeChosenArbitrarily(string json, string id, string name)
    {
        using var doc = JsonDocument.Parse(json);
        var options = new UnisenderOptions { ListId = id, ListName = name };
        Assert.Contains("ListName", Assert.Throws<InvalidOperationException>(() => ConfigurationDiscovery.ResolveList(options, doc.RootElement)).Message);
        Assert.Equal(id, options.ListId); Assert.Equal(name, options.ListName);
    }
    [Theory] [InlineData("{}")] [InlineData("[{\"id\":0,\"title\":\"A\"}]")]
    [InlineData("[{\"id\":42,\"title\":\"A\"},{\"id\":42,\"title\":\"B\"}]")]
    public void MalformedListCatalogIsRejected(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unisender_list_catalog_invalid", Assert.Throws<SyncException>(() => ConfigurationDiscovery.ResolveList(new(), doc.RootElement)).Code);
    }
    [Fact] public async Task AccountIdentityComesOnlyFromConfiguredAuthenticatedApi()
    {
        var options = Options.Create(new IntegrationOptions { Megaplan = new() { BaseUrl = "https://crm.example.test" } });
        var client = new MegaplanClient(new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/api/v3/accountInfo", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("fixture", request.Headers.Authorization.Parameter);
            return Scenario.Json(new { meta = new { status = 200 }, data = new { contentType = "AccountInfo", id = "299", permanentHost = "original.megaplan.ru", ecometToken = "do-not-retain" } });
        })), options, Options.Create(new Secrets { MegaplanApiToken = "fixture" }), new ApiRateLimiter(options));
        Assert.Equal("original.megaplan.ru", await client.GetAccountIdentityAsync("permanentHost", default));
    }
    [Fact] public async Task NewGlobalTagsAndTheirNamesNeedNoManualMapping()
    {
        await using var s = new Scenario(); s.Options.Megaplan.SyncAllGlobalTags = true;
        s.Options.Megaplan.TagMappings.Clear();
        s.Cards["10"] = Scenario.Card(tags: [1, 7]); await s.Apply();
        Assert.Equal("Tag 7", Assert.Single(s.Writes).Form["tags"]);
        s.Cards["10"] = Scenario.Card(tags: [1, 8]); await s.Apply();
        Assert.Equal("importContacts", s.Writes.Last().Method);
        Assert.Equal("Tag 8", s.Writes.Last().Form["data[0][1]"]);
    }
    [Fact] public void AutomaticTagsDoNotGuessGlobalScopeForUnknownIds()
    {
        using var doc = JsonDocument.Parse("""{"id":"10","contentType":"ContractorHuman","tags":[{"id":"7","name":"Unknown"}],"contactInfo":[]}""");
        Assert.Equal("megaplan_tag_global_scope_unknown", Assert.Throws<SyncException>(() => MegaplanClient.Parse(doc.RootElement, new(), new())).Code);
    }
    [Fact] public async Task MissingContactContractIsVerifiedByReadWithoutCreatingContact()
    {
        var settings = new IntegrationOptions(); var options = Options.Create(settings);
        var client = new UnisenderClient(new HttpClient(new Handler(request =>
        {
            Assert.Equal("/ru/api/getContact", request.RequestUri!.AbsolutePath);
            return Scenario.Json(new { result = (object?)null });
        })), options, Options.Create(new Secrets { UnisenderApiKey = "fixture" }), new ApiRateLimiter(options));
        await client.VerifyMissingContactResponseAsync(default);
        Assert.True(settings.Unisender.ConfirmNullResultMeansMissing);
    }
    [Fact] public async Task InvalidCredentialCannotBeInterpretedAsMissingContact()
    {
        var settings = new IntegrationOptions(); var options = Options.Create(settings);
        var client = new UnisenderClient(new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden))), options,
            Options.Create(new Secrets { UnisenderApiKey = "fixture" }), new ApiRateLimiter(options));
        await Assert.ThrowsAsync<SyncException>(() => client.VerifyMissingContactResponseAsync(default));
        Assert.False(settings.Unisender.ConfirmNullResultMeansMissing);
    }
    [Fact] public void DryRunAcceptsMinimalSettingsButWritesNeedExplicitPolicy()
    {
        var options = new IntegrationOptions { Megaplan = new() { BaseUrl = "https://crm.example.test", ExpectedIntegrationUuid = "fixture", EligibilityGlobalTagName = "Тест" }, Sync = new() { Enabled = true } };
        Assert.Empty(options.Validate());
        options.Sync.DryRun = false;
        Assert.Contains(options.Validate(), x => x.Contains("ConsentPolicy", StringComparison.Ordinal));
        Assert.Contains(options.Validate(), x => x.Contains("Exclusive", StringComparison.Ordinal));
    }
}
