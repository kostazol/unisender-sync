using System.Net;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class UnisenderSetupTests
{
    private static UnisenderClient Client(UnisenderOptions settings, Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var options = Options.Create(new IntegrationOptions { Unisender = settings, Sync = new() { RateLimits = new() { GeneralPerMinute = 600000, SubscribePerMinute = 600000 } } });
        return new(new HttpClient(new Handler(reply)), options, Options.Create(new Secrets { UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(options));
    }
    [Fact] public async Task ExistingNamedListIsReusedWithoutCreation()
    {
        var settings = new UnisenderOptions { ListName = "Мегаплан" };
        var client = Client(settings, request =>
        {
            Assert.EndsWith("/getLists", request.RequestUri!.AbsolutePath);
            return Scenario.Json(new { result = new[] { new { id = 42, title = "Мегаплан" }, new { id = 99, title = "Other" } } });
        });
        await ConfigurationDiscovery.ResolveListAsync(settings, client, true, default);
        Assert.Equal("42", settings.ListId);
    }
    [Fact] public async Task MissingNamedListIsCreatedAndIdIsCachedInMemory()
    {
        var settings = new UnisenderOptions { ListName = "Мегаплан" }; var writes = 0;
        var client = Client(settings, request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/getLists", StringComparison.Ordinal)) return Scenario.Json(new { result = Array.Empty<object>() });
            Assert.EndsWith("/createList", request.RequestUri.AbsolutePath);
            var form = Scenario.ParseForm(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Мегаплан", form["title"]); Assert.Equal("fixture-key", form["api_key"]); writes++;
            return Scenario.Json(new { result = new { id = 42 } });
        });
        await ConfigurationDiscovery.ResolveListAsync(settings, client, true, default);
        Assert.Equal("42", settings.ListId); Assert.Equal("Мегаплан", settings.ListName); Assert.Equal(1, writes);
    }
    [Fact] public async Task DryRunNeverCreatesMissingList()
    {
        var settings = new UnisenderOptions { ListName = "Мегаплан" };
        var client = Client(settings, request =>
        { Assert.EndsWith("/getLists", request.RequestUri!.AbsolutePath); return Scenario.Json(new { result = Array.Empty<object>() }); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigurationDiscovery.ResolveListAsync(settings, client, false, default));
        Assert.Empty(settings.ListId);
    }
    [Fact] public async Task UncertainListCreationUsesReadbackInsteadOfDuplicateWrite()
    {
        var settings = new UnisenderOptions { ListName = "Мегаплан" }; var reads = 0; var writes = 0;
        var client = Client(settings, request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/getLists", StringComparison.Ordinal))
                return ++reads == 1 ? Scenario.Json(new { result = Array.Empty<object>() }) : Scenario.Json(new { result = new[] { new { id = 42, title = "Мегаплан" } } });
            writes++; return new(HttpStatusCode.InternalServerError);
        });
        await ConfigurationDiscovery.ResolveListAsync(settings, client, true, default);
        Assert.Equal("42", settings.ListId); Assert.Equal(1, writes); Assert.Equal(2, reads);
    }
    [Fact] public async Task ObservedObjectNotFoundResponseCanIdentifyMissingContact()
    {
        var settings = new UnisenderOptions();
        var client = Client(settings, _ => Scenario.Json(new { error = "Contact not found", code = "object_not_found" }));
        await client.VerifyMissingContactResponseAsync(default);
        var status = await client.GetContactAsync("fixture@example.com", default);
        Assert.False(status.Exists); Assert.True(status.MaySubscribe);
        Assert.Contains("unisender_object_not_found", settings.VerifiedMissingContactErrorCodes);
    }
    [Fact] public async Task EmailAndOrdinaryTagsAreSentWithoutNameOrEligibilityTag()
    {
        await using var s = new Scenario(); s.Options.Megaplan.SyncAllGlobalTags = true;
        s.Options.Unisender.FieldMappings.Clear(); s.Cards["10"] = Scenario.Card(tags: [1, 7]);
        await s.Apply(); var form = Assert.Single(s.Writes).Form;
        Assert.Equal("Tag 7", form["tags"]);
        Assert.Equal(["fields[email]"], form.Keys.Where(x => x.StartsWith("fields[", StringComparison.Ordinal)));
    }
    [Fact] public async Task RemovingLastOrdinaryTagClearsTagsButKeepsContactInList()
    {
        await using var s = new Scenario(); s.Options.Megaplan.SyncAllGlobalTags = true; s.Options.Unisender.FieldMappings.Clear();
        s.Cards["10"] = Scenario.Card(tags: [1, 7]); await s.Apply();
        s.Cards["10"] = Scenario.Card(tags: [1]); await s.Apply();
        Assert.Single(s.Writes, x => x.Method == "importContacts"); Assert.DoesNotContain(s.Writes, x => x.Method == "exclude");
    }
    [Fact] public async Task EmptyImportWithRowErrorCannotBeReportedAsSuccess()
    {
        var settings = new UnisenderOptions { ConfirmEmptyTagsClear = true };
        var client = Client(settings, _ => Scenario.Json(new { result = new { updated = 1, invalid = 1, log = new[] { new { code = "e_email_invalid" } } } }));
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => client.ClearTagsAsync("fixture@example.com", default))).Uncertain);
    }
}
