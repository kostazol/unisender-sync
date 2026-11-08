using System.Net;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class TagDiscoveryTests
{
    private static MegaplanClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var options = Options.Create(new IntegrationOptions
        {
            Megaplan = new() { BaseUrl = "https://crm.example.test" },
            Sync = new() { RateLimits = new() { MegaplanPerMinute = 600000 } }
        });
        return new(new HttpClient(new Handler(reply)), options, Options.Create(new Secrets { MegaplanApiToken = "fixture-token" }), new ApiRateLimiter(options));
    }
    private static HttpResponseMessage Page(int count, params object[] tags) => Scenario.Json(new { meta = new { status = 200, pagination = new { count } }, data = tags });

    [Fact] public async Task NameResolvesIdAllowlistAndMappingAndSurvivesCardTagRename()
    {
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест" };
        var client = Client(request =>
        {
            Assert.Equal("/api/v3/contractor/tags", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
            return Page(1, new { id = "42", name = "Тест", isGlobal = true });
        });
        await MegaplanTagResolver.ResolveAsync(options, client, default);
        Assert.Equal("42", options.EligibilityGlobalTagId); Assert.Equal(["42"], options.ManagedGlobalTagIds); Assert.Empty(options.TagMappings);
        using var card = JsonDocument.Parse("""{"id":"10","contentType":"ContractorHuman","contactInfo":[],"tags":[{"id":"42","name":"Новое имя","isGlobal":true}]}""");
        var contractor = MegaplanClient.Parse(card.RootElement, options, new());
        Assert.True(contractor.Eligible); Assert.Empty(contractor.Tags);
    }
    [Fact] public async Task PrivateNamesAreIgnored()
    {
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест" };
        var client = Client(_ => Page(2, new { id = "1", name = "Тест", isGlobal = false }, new { id = "2", name = "Тест", isGlobal = true }));
        await MegaplanTagResolver.ResolveAsync(options, client, default); Assert.Equal("2", options.EligibilityGlobalTagId);
    }
    [Theory] [InlineData(0)] [InlineData(2)]
    public async Task MissingOrAmbiguousGlobalNameFailsWithoutChangingSettings(int matches)
    {
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест" };
        var client = Client(_ => Page(matches, Enumerable.Range(1, matches).Select(x => (object)new { id = x.ToString(), name = "Тест", isGlobal = true }).ToArray()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MegaplanTagResolver.ResolveAsync(options, client, default));
        Assert.Empty(options.EligibilityGlobalTagId); Assert.Empty(options.ManagedGlobalTagIds); Assert.Empty(options.TagMappings);
    }
    [Fact] public async Task IdOnlyModeDoesNotQueryCatalog()
    {
        var options = new MegaplanOptions { EligibilityGlobalTagId = "42", ManagedGlobalTagIds = ["42"], SyncAllGlobalTags = false };
        await MegaplanTagResolver.ResolveAsync(options, Client(_ => throw new InvalidOperationException("Must not call API")), default);
        Assert.Equal("42", options.EligibilityGlobalTagId);
    }
    [Fact] public async Task ResolvingNamePreservesExplicitMappings()
    {
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест", ManagedGlobalTagIds = ["7", "42"], TagMappings = new() { ["7"] = "Other", ["42"] = "Custom name" } };
        await MegaplanTagResolver.ResolveAsync(options, Client(_ => Page(1, new { id = "42", name = "Тест", isGlobal = true })), default);
        Assert.Equal("Custom name", options.TagMappings["42"]); Assert.Equal("Other", options.TagMappings["7"]); Assert.Equal(2, options.ManagedGlobalTagIds.Length);
    }
    [Fact] public async Task CatalogReadsAllPagesWithInclusiveBoundary()
    {
        var calls = 0;
        var boundary = new { id = "100", name = "Private 100", isGlobal = false };
        var client = Client(request =>
        {
            calls++;
            using var query = JsonDocument.Parse(Uri.UnescapeDataString(request.RequestUri!.Query[1..]));
            Assert.Contains(query.RootElement.GetProperty("fields").EnumerateArray(), x => x.GetString() == "isGlobal");
            if (calls == 1)
            {
                Assert.False(query.RootElement.TryGetProperty("pageAfter", out _));
                return Page(101, Enumerable.Range(1, 100).Select(x => (object)new { id = x.ToString(), name = "Private " + x, isGlobal = false }).ToArray());
            }
            Assert.Equal("100", query.RootElement.GetProperty("pageAfter").GetProperty("id").GetString());
            Assert.Equal("Tag", query.RootElement.GetProperty("pageAfter").GetProperty("contentType").GetString());
            return Page(101, boundary, new { id = "101", name = "Тест", isGlobal = true });
        });
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест" };
        await MegaplanTagResolver.ResolveAsync(options, client, default); Assert.Equal("101", options.EligibilityGlobalTagId); Assert.Equal(2, calls);
    }
    [Fact] public async Task UnknownGlobalFlagIsNotGuessed()
    {
        var client = Client(_ => Page(1, new { id = "42", name = "Тест" }));
        Assert.Equal("megaplan_tag_global_scope_unknown", (await Assert.ThrowsAsync<SyncException>(() => client.GetGlobalTagsAsync(default))).Code);
    }
    [Fact] public async Task RepeatedPageCannotSilentlyTruncateCatalog()
    {
        var client = Client(_ => Page(2, new { id = "42", name = "Тест", isGlobal = true }));
        Assert.Equal("megaplan_tag_catalog_incomplete", (await Assert.ThrowsAsync<SyncException>(() => client.GetGlobalTagsAsync(default))).Code);
    }
    [Fact] public async Task AuthorizationFailureIsReportedWithoutResolvedIds()
    {
        var options = new MegaplanOptions { EligibilityGlobalTagName = "Тест" };
        var client = Client(_ => new(HttpStatusCode.Forbidden));
        Assert.Equal("megaplan_http_403", (await Assert.ThrowsAsync<SyncException>(() => MegaplanTagResolver.ResolveAsync(options, client, default))).Code);
        Assert.Empty(options.EligibilityGlobalTagId);
    }
    [Fact] public void NamedModeDoesNotRequireManualIds()
    {
        var options = new IntegrationOptions
        {
            Megaplan = new() { BaseUrl = "https://crm.example.test", ExpectedAccountId = "299", ExpectedIntegrationUuid = "fixture", EligibilityGlobalTagName = "Тест" },
            Unisender = new() { ListId = "42", ConsentPolicy = ConsentPolicy.VerifiedByAdministrator }, Sync = new() { Enabled = true }
        };
        Assert.Empty(options.Validate());
    }
}
