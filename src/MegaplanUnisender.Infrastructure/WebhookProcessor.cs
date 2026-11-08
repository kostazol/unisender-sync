using MegaplanUnisender.Core;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Infrastructure;

public sealed class WebhookProcessor(SyncRuntime runtime, IMegaplanClient megaplan, IUnisenderClient unisender,
    IOptions<IntegrationOptions> options, ITagStrategy tagStrategy)
{
    public async Task<string> ProcessAsync(WebhookEvent entry, CancellationToken ct)
    {
        if (!options.Value.Sync.Enabled) return "Disabled";
        var id = entry.ContractorId ?? throw new SyncException("contractor_id_missing");
        var card = await megaplan.GetAsync(id, ct);
        if (card is null && entry.Event != "on_after_drop") throw new SyncException("megaplan_missing_card_without_delete_evidence", true);
        if (options.Value.Sync.DryRun) return "DryRun";

        var previousEmail = runtime.PreviousEmail(id);
        if (!runtime.PendingEmails.TryGetValue(id, out var affected)) runtime.PendingEmails[id] = affected = [];
        if (previousEmail is { } oldEmail) affected.Add(oldEmail);
        if (card?.Email is { } newEmail) affected.Add(newEmail);
        runtime.Observe(id, card);
        // Apply the destination first. Failed transfers retain previous emails in memory.
        if (card?.Email is { } destination)
        { await SyncEmailAsync(destination, card, ct, id); affected.Remove(destination); }
        foreach (var email in affected.ToArray())
        {
            if (card is { Eligible: true, Email: not null } && runtime.OwnerIds(email).Length == 0)
            {
                var status = await unisender.GetContactAsync(card.Email, ct);
                if (!status.MaySubscribe || !status.InList) throw new SyncException("email_transfer_destination_not_subscribed", true);
            }
            await SyncEmailAsync(email, card, ct, id); affected.Remove(email);
        }
        runtime.PendingEmails.Remove(id);
        return card is null && previousEmail is null ? "SkippedUnknownDeletion" : "Synced";
    }
    private async Task SyncEmailAsync(string email, Contractor? current, CancellationToken ct, string releasingContractorId)
    {
        var owners = new List<Contractor>();
        var candidates = runtime.OwnerIds(email).ToHashSet(StringComparer.Ordinal);
        // The fresh card is authoritative even if its short-lived hint expired during this operation.
        if (current?.Email == email) candidates.Add(current.Id);
        foreach (var id in candidates.Order(StringComparer.Ordinal))
        {
            var owner = current?.Id == id ? current : await megaplan.GetAsync(id, ct);
            if (current?.Id != id) runtime.Observe(id, owner);
            if (owner is { Eligible: true } && owner.Email == email) owners.Add(owner);
        }
        if (owners.Count == 0)
        {
            // Another failed email transfer still needs the old subscription until its destination succeeds.
            if (runtime.PendingEmails.Any(x => x.Key != releasingContractorId && x.Value.Contains(email))) return;
            var contact = await unisender.GetContactAsync(email, ct);
            // Do not erase a subscriber's refusal by excluding the unsubscribed list entry.
            if (contact.Exists && contact.MaySubscribe && contact.InList)
            {
                await WriteAsync(email, () => unisender.ExcludeAsync(email, ct));
            }
            return;
        }
        var tags = Canonical.Tags(owners.SelectMany(x => x.Tags));
        var fields = owners[0].Fields;
        if (tags.Length > 10) throw new SyncException("tag_limit_exceeded");
        var status = await unisender.GetContactAsync(email, ct);
        if (!status.MaySubscribe) throw new SyncException(status.Reason);
        var currentTags = status.Exists ? await unisender.GetContactTagsAsync(email, ct) : [];
        var tagsChanged = !currentTags.SequenceEqual(tags);
        var changedFields = fields.Where(x => status.Fields.GetValueOrDefault(x.Key, "") != x.Value).ToDictionary();
        if (status.InList && !tagsChanged && changedFields.Count == 0) return;
        string[]? outgoingTags = tagsChanged ? tagStrategy.Prepare(tags, currentTags) : null;
        var o = options.Value.Unisender;
        if (o.ConsentPolicy == ConsentPolicy.Disabled || (o.DoubleOptInMode == 3 && o.ConsentPolicy != ConsentPolicy.VerifiedByAdministrator) ||
            (o.DoubleOptInMode is 0 or 4 && !o.AllowConfirmationEmails)) throw new SyncException("consent_policy_denies_subscription");
        if (status.InList && changedFields.Count == 0 && tagsChanged)
        {
            var replace = currentTags.Except(tags, StringComparer.Ordinal).Any();
            var delta = replace ? outgoingTags! : tags.Except(currentTags, StringComparer.Ordinal).ToArray();
            await WriteAsync(email, () => unisender.UpdateTagsAsync(email, delta, replace, ct));
            return;
        }
        if (tagsChanged && outgoingTags!.Length == 0 && status.Exists)
        {
            await WriteAsync(email, () => unisender.ClearTagsAsync(email, ct));
            outgoingTags = null;
        }
        await WriteAsync(email, () => unisender.SubscribeAsync(email, changedFields, outgoingTags, ct));
    }
    private static async Task WriteAsync(string email, Func<Task> write)
    {
        try { await write(); }
        catch (Exception e) when (e is not SyncException) { throw new SyncException("remote_write_outcome_unknown", uncertain: true); }
    }
}
