using System.Net;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class SyncTests
{
    [Fact] public async Task CurrentApiCardRemainsAuthoritativeWhenItsHintExpiresMidOperation()
    {
        await using var s = new Scenario(); s.Options.Sync.RecentContractorCacheTtl = TimeSpan.FromSeconds(1);
        s.Clock.AdvancePerRead = TimeSpan.FromSeconds(2); s.Cards["10"] = Scenario.Card();
        await s.Apply(); Assert.Equal("subscribe", Assert.Single(s.Writes).Method);
    }
    [Fact] public async Task ExpiredHintDoesNotPreventSynchronizationFromCurrentApis()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Clock.Advance(TimeSpan.FromMinutes(6)); s.RemoteTags["a@example.test"] = ["third"];
        await s.Apply(); Assert.Equal("second", s.Writes.Last().Form["data[0][1]"]);
    }
    [Fact] public async Task ExpiredHintCannotActAsPermanentEmailHistory()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Clock.Advance(TimeSpan.FromMinutes(6)); s.Cards.Clear();
        Assert.Equal("SkippedUnknownDeletion", await s.Apply(drop: true)); Assert.Single(s.Writes);
    }
    [Fact] public async Task OldestHintIsEvictedAtCapacity()
    {
        await using var s = new Scenario(); s.Options.Sync.RecentContractorCacheCapacity = 2;
        s.Cards["10"] = Scenario.Card(); await s.Apply(); s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Cards["20"] = Scenario.Card(id: "20", email: "b@example.test"); await s.Apply("20"); s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Cards["30"] = Scenario.Card(id: "30", email: "c@example.test"); await s.Apply("30");
        s.Cards.Remove("10"); Assert.Equal("SkippedUnknownDeletion", await s.Apply(drop: true)); Assert.Equal(3, s.Writes.Count);
    }
    [Fact] public async Task OtherObservedOwnersTagsAreRereadFromMegaplanRatherThanCached()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); s.Cards["20"] = Scenario.Card(id: "20", tags: [1, 3]);
        await s.Apply(); await s.Apply("20"); s.Cards["20"] = Scenario.Card(id: "20", tags: [1]);
        var before = s.Gets.Count(x => x == "/api/v3/contractor/20"); await s.Apply();
        Assert.Equal(before + 1, s.Gets.Count(x => x == "/api/v3/contractor/20"));
        Assert.Equal(["second"], s.RemoteTags["a@example.test"]);
    }
    [Fact] public async Task RestartComparesRemoteTagsWithoutRepeatingSubscription()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.NewSession(); await s.Apply();
        Assert.Single(s.Writes); Assert.Single(s.UnisenderReads, x => x == "exportContacts");
    }
    [Fact] public async Task EmptyRemoteTagsAfterRestartDoNotTriggerClearOrSubscription()
    {
        await using var s = new Scenario { Status = "active", ListStatus = "active" };
        s.Options.Unisender.FieldMappings.Clear(); s.Cards["10"] = Scenario.Card(tags: [1]);
        await s.Apply(); s.NewSession(); await s.Apply();
        Assert.Empty(s.Writes); Assert.Equal(2, s.UnisenderReads.Count(x => x == "exportContacts"));
    }
    [Fact] public async Task FailedTagReadIsNeverInterpretedAsEmptyEvenAfterSuccessfulSync()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Override = r => r.RequestUri!.AbsolutePath.EndsWith("/async/exportContacts", StringComparison.Ordinal)
            ? new(HttpStatusCode.InternalServerError) : null;
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Transient);
        Assert.Single(s.Writes);
    }
    [Fact] public async Task RemovedRemoteMembershipIsRestoredWithoutReassigningMatchingTags()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        await s.Uni().ExcludeAsync("a@example.test", default); await s.Apply();
        Assert.Equal("subscribe", s.Writes.Last().Method); Assert.False(s.Writes.Last().Form.ContainsKey("tags"));
        Assert.Equal(3, s.Writes.Count);
    }
    [Fact] public async Task ManualSubscriptionIsDetectedWithoutMembershipSnapshot()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(tags: [2]);
        await s.Apply(); Assert.Empty(s.Writes);
        await s.Uni().SubscribeAsync("a@example.test", new Dictionary<string, string>(), ["test"], default);
        await s.Apply();
        Assert.Equal("exclude", s.Writes.Last().Method);
        Assert.Equal(2, s.Writes.Count);
        await s.Apply(); Assert.Equal(2, s.Writes.Count);
    }
    [Fact] public async Task UncertainWriteDoesNotBlockNextWebhook()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card();
        s.Override = r => r.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal) ? new(HttpStatusCode.InternalServerError) : null;
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Uncertain);
        s.Override = null; await s.Apply(); Assert.Single(s.Writes);
    }
    [Fact] public async Task RepeatedIneligibleCardChecksActualMembership()
    {
        await using var s = new Scenario(); var reads = 0;
        s.Override = request => { if (request.RequestUri!.AbsolutePath.EndsWith("getContact", StringComparison.Ordinal)) reads++; return null; };
        s.Cards["10"] = Scenario.Card(tags: [2]); await s.Apply(); await s.Apply();
        Assert.Equal(2, reads); Assert.Empty(s.Writes);
    }
    [Fact] public async Task ExternalTagChangesAreDetectedWithoutInvalidation()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.RemoteTags["a@example.test"] = ["third"];
        await s.Apply(); Assert.Equal(2, s.Writes.Count); Assert.Equal("second", s.Writes.Last().Form["data[0][1]"]);
    }
    [Fact] public async Task AppliedWriteWithLostResponseIsConfirmedByApiWithoutAnotherWrite()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card();
        s.Override = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal)) return null;
            s.Status = "active"; s.ListStatus = "active"; s.RemoteTags["a@example.test"] = ["second"];
            s.RemoteFields["a@example.test"] = new() { ["Name"] = "Alice" };
            return new(HttpStatusCode.InternalServerError);
        };
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Uncertain);
        s.Override = null; await s.Apply(); Assert.Empty(s.Writes);
    }
    [Fact] public async Task GateRemovalAfterRestartExcludesKnownRemoteMembership()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply(); s.NewSession();
        s.Cards["10"] = Scenario.Card(tags: [2]); await s.Apply();
        Assert.Equal("exclude", s.Writes.Last().Method); Assert.Equal("42", s.Writes.Last().Form["list_ids"]);
    }
    [Fact] public async Task UnknownIneligibleCardDoesNotExcludeUnsubscribedContact()
    {
        await using var s = new Scenario { Status = "active", ListStatus = "unsubscribed" };
        s.Cards["10"] = Scenario.Card(tags: [2]); await s.Apply(); Assert.Empty(s.Writes);
    }
    [Fact] public async Task LastOrdinaryTagRemovalDoesNotResubscribeAlreadyActiveUnchangedContact()
    {
        await using var s = new Scenario(); s.Options.Unisender.FieldMappings.Clear();
        s.Cards["10"] = Scenario.Card(); await s.Apply(); s.Cards["10"] = Scenario.Card(tags: [1]); await s.Apply();
        Assert.Equal(2, s.Writes.Count); Assert.Equal("importContacts", s.Writes.Last().Method);
        await s.Apply(); Assert.Equal(2, s.Writes.Count);
    }
    [Fact] public async Task AnotherOwnersRemovalCannotExcludeAddressNeededByFailedTransfer()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); s.Cards["20"] = Scenario.Card(id: "20");
        await s.Apply(); await s.Apply("20"); s.Cards["10"] = Scenario.Card(email: "b@example.test");
        s.Override = request => request.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal)
            ? Scenario.Json(new { error = "denied", code = "invalid_arg" }) : null;
        await Assert.ThrowsAsync<SyncException>(() => s.Apply()); s.Override = null;
        s.Cards.Remove("20"); await s.Apply("20", drop: true);
        Assert.DoesNotContain(s.Writes, x => x.Method == "exclude");
        await s.Apply(); Assert.Contains(s.Writes, x => x.Method == "exclude" && x.Form["contact"] == "a@example.test");
    }
    [Fact] public async Task IneligibleNewContractorDoesNotWrite()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(tags: [2]);
        await s.Apply(); Assert.Empty(s.Writes);
    }
    [Fact] public async Task EligibilityCreatesContactWithMappedTagsAndScopedList()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        var w = Assert.Single(s.Writes); Assert.Equal("subscribe", w.Method); Assert.Equal("second", w.Form["tags"]);
        Assert.Equal("42", w.Form["list_ids"]); Assert.Equal("2", w.Form["overwrite"]);
    }
    [Fact] public async Task NameOnlyUpdateOmitsTags()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Cards["10"] = Scenario.Card(name: "Updated"); await s.Apply();
        Assert.Equal(2, s.Writes.Count); Assert.False(s.Writes[1].Form.ContainsKey("tags")); Assert.Equal("Updated", s.Writes[1].Form["fields[Name]"]);
    }
    [Theory] [InlineData(true, "second,third")] [InlineData(false, "third")]
    public async Task TagChangesSendOnlyMissingTagsOrReplaceWhenRemoving(bool add, string expected)
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Cards["10"] = Scenario.Card(tags: add ? [1, 2, 3] : [1, 3]); await s.Apply();
        var update = s.Writes.Last(); Assert.Equal("importContacts", update.Method);
        Assert.Equal("third", update.Form["data[0][1]"]);
        Assert.Equal(add ? "0" : "1", update.Form["overwrite_tags"]); Assert.Equal("0", update.Form["overwrite_lists"]);
        Assert.Equal(expected.Split(','), Canonical.Tags(s.RemoteTags["a@example.test"]));
    }
    [Fact] public async Task RemovingAndAddingTagsKeepsUnchangedTagsInReplacementSet()
    {
        await using var s = new Scenario(); s.Options.Megaplan.SyncAllGlobalTags = true; s.Options.Megaplan.TagMappings.Clear();
        s.Cards["10"] = Scenario.Card(tags: [1, 7, 8]); await s.Apply();
        s.Cards["10"] = Scenario.Card(tags: [1, 8, 9]); await s.Apply();
        var write = s.Writes.Last(); Assert.Equal("importContacts", write.Method);
        Assert.Equal("1", write.Form["overwrite_tags"]); Assert.Equal("Tag 8,Tag 9", write.Form["data[0][1]"]);
        Assert.Equal(["Tag 8", "Tag 9"], Canonical.Tags(s.RemoteTags["a@example.test"]));
    }
    [Fact] public async Task LastTagRemovalUsesVerifiedEmptyImportWithoutReplacingLists()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Cards["10"] = Scenario.Card(tags: [1]); await s.Apply();
        var clear = Assert.Single(s.Writes, x => x.Method == "importContacts");
        Assert.Equal("", clear.Form["data[0][1]"]); Assert.Equal("1", clear.Form["overwrite_tags"]); Assert.Equal("0", clear.Form["overwrite_lists"]);
        Assert.False(s.Writes.Last().Form.ContainsKey("tags"));
    }
    [Fact] public async Task UnverifiedEmptyTagBehaviorBlocksRemoval()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply(); s.Options.Unisender.ConfirmEmptyTagsClear = false;
        s.Cards["10"] = Scenario.Card(tags: [1]);
        Assert.Equal("empty_tag_clear_requires_live_verification", (await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Code); Assert.Single(s.Writes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RemovingEligibilityOrDeletingExcludesOnlyManagedList(bool delete)
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        if (delete) s.Cards.Remove("10"); else s.Cards["10"] = Scenario.Card(tags: [2]);
        await s.Apply(drop: delete); var w = s.Writes.Last(); Assert.Equal("exclude", w.Method); Assert.Equal("42", w.Form["list_ids"]);
        Assert.DoesNotContain(s.Writes, x => x.Method == "unsubscribe");
    }
    [Fact] public async Task RepeatedLogicalStateDoesNotTriggerAgain()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(tags: [1, 3, 2]); await s.Apply();
        s.Cards["10"] = Scenario.Card(tags: [2, 1, 3]); await s.Apply(); Assert.Single(s.Writes);
    }
    [Theory] [InlineData("unsubscribed", null)] [InlineData("blocked", null)] [InlineData("active", "unsubscribed")] [InlineData("invited", null)]
    public async Task SubscriberRefusalNeverResubscribed(string status, string? listStatus)
    {
        await using var s = new Scenario { Status = status, ListStatus = listStatus }; s.Cards["10"] = Scenario.Card();
        await Assert.ThrowsAsync<SyncException>(() => s.Apply()); Assert.Empty(s.Writes);
    }
    [Fact] public async Task ElevenTagsAreRejectedWithoutTruncation()
    {
        await using var s = new Scenario(); s.Options.Megaplan.TagMappings = Enumerable.Range(1, 12).ToDictionary(x => x.ToString(), x => "T" + x);
        s.Cards["10"] = Scenario.Card(tags: Enumerable.Range(1, 12).ToArray());
        Assert.Equal("tag_limit_exceeded", (await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Code); Assert.Empty(s.Writes);
    }
    [Fact] public async Task ObservedSharedEmailUnionsTagsAndKeepsSubscription()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); s.Cards["20"] = Scenario.Card(id: "20", tags: [1, 3]);
        await s.Apply(); await s.Apply("20"); Assert.Equal(["second", "third"], Canonical.Tags(s.RemoteTags["a@example.test"]));
        s.Cards.Remove("10"); await s.Apply(drop: true); Assert.DoesNotContain(s.Writes, x => x.Method == "exclude"); Assert.Equal("third", s.Writes.Last().Form["data[0][1]"]);
    }
    [Fact] public async Task EmailChangeAppliesNewAddressBeforeExcludingOld()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.Cards["10"] = Scenario.Card(email: "b@example.test"); await s.Apply();
        Assert.Equal("b@example.test", s.Writes[1].Form["fields[email]"]); Assert.Equal("exclude", s.Writes[2].Method); Assert.Equal("a@example.test", s.Writes[2].Form["contact"]);
    }
    [Fact] public async Task FailedDestinationKeepsOldSubscriptionAndCanRetry()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply(); s.Cards["10"] = Scenario.Card(email: "b@example.test");
        s.Override = r => r.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal) ? Scenario.Json(new { error = "denied", code = "invalid_arg" }) : null;
        await Assert.ThrowsAsync<SyncException>(() => s.Apply()); Assert.DoesNotContain(s.Writes, x => x.Method == "exclude");
        s.Override = null; await s.Apply(); Assert.Contains(s.Writes, x => x.Method == "exclude");
    }
    [Fact] public async Task RefusalIsNotErasedByRemovingEligibility()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply();
        s.ListStatus = "unsubscribed"; s.Cards["10"] = Scenario.Card(tags: [2]); await s.Apply(); Assert.Single(s.Writes);
        s.Cards["10"] = Scenario.Card(); await Assert.ThrowsAsync<SyncException>(() => s.Apply()); Assert.Single(s.Writes);
    }
    [Fact] public async Task DryRunReadsCrmWithoutExternalWrites()
    {
        await using var s = new Scenario(); s.Options.Sync.DryRun = true; s.Cards["10"] = Scenario.Card();
        Assert.Equal("DryRun", await s.Apply()); Assert.Empty(s.Writes); Assert.Single(s.Gets);
    }
    [Fact] public async Task PreserveExternalBlocksTagWritesButAllowsFieldsOnly()
    {
        await using var s = new Scenario(); s.Options.Unisender.TagOwnershipMode = TagOwnershipMode.PreserveExternal; s.Cards["10"] = Scenario.Card();
        Assert.Equal("preserve_external_ownership_not_supported", (await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Code); Assert.Empty(s.Writes);
        s.Options.Unisender.TagOwnershipMode = TagOwnershipMode.Exclusive; await s.Apply();
        s.Options.Unisender.TagOwnershipMode = TagOwnershipMode.PreserveExternal; s.Cards["10"] = Scenario.Card(name: "Changed"); await s.Apply();
        Assert.False(s.Writes.Last().Form.ContainsKey("tags"));
    }
    [Fact] public async Task NetworkFailureIsRetriedAgainstActualApiState()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card();
        s.Override = r => r.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal) ? throw new HttpRequestException("private@example.test") : null;
        Assert.True((await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Uncertain);
        s.Override = null; await s.Apply(); Assert.Single(s.Writes);
    }
    [Fact] public async Task RestartDoesNotRestoreOldEmailMapping()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply(); s.NewSession(); s.Cards.Clear();
        Assert.Equal("SkippedUnknownDeletion", await s.Apply(drop: true)); Assert.Single(s.Writes);
    }
    [Fact] public async Task MissingTagsAreNotInterpretedAsEmpty()
    {
        await using var s = new Scenario(); s.Cards["10"] = new { id = "10", contentType = "ContractorHuman", contactInfo = Array.Empty<object>() };
        Assert.Equal("megaplan_tags_unknown", (await Assert.ThrowsAsync<SyncException>(() => s.Apply())).Code); Assert.Empty(s.Writes); Assert.Equal(2, s.Gets.Count);
    }
    [Fact] public async Task DelayedDeleteReadsActualCard()
    {
        await using var s = new Scenario(); s.Cards["10"] = Scenario.Card(); await s.Apply(drop: true); Assert.Equal("subscribe", Assert.Single(s.Writes).Method);
    }
    [Fact] public void QueueIsBoundedAndDuplicatesDoNotTakeAnotherSlot()
    {
        var options = Options.Create(new IntegrationOptions { Sync = new() { QueueCapacity = 1 } }); var runtime = new SyncRuntime(options);
        var first = new WebhookEvent(Guid.NewGuid(), "10", "ContractorHuman", "on_after_update");
        Assert.True(runtime.TryAccept(first)); Assert.True(runtime.TryAccept(first));
        var second = new WebhookEvent(Guid.NewGuid(), "20", "ContractorHuman", "on_after_update"); Assert.False(runtime.TryAccept(second));
        Assert.True(runtime.Reader.TryRead(out var received)); Assert.Same(first, received); runtime.Begin(first); runtime.Finish(first, "Synced");
        Assert.True(runtime.TryAccept(second));
    }
    [Fact] public void CrmNameMappingFollowsRenameAndPrivateTagsDoNotGrantEligibility()
    {
        using var doc = JsonDocument.Parse("""{"id":"10","contentType":"ContractorHuman","contactInfo":[],"tags":[{"id":"1","isGlobal":false},{"id":"2","isGlobal":true,"name":"Renamed"}]}""");
        var o = new MegaplanOptions { EligibilityGlobalTagId = "1", ManagedGlobalTagIds = ["1", "2"], TagMappings = new() { ["2"] = "@crm-name" } };
        var c = MegaplanClient.Parse(doc.RootElement, o, new()); Assert.False(c.Eligible); Assert.Equal(["Renamed"], c.Tags);
    }
}
