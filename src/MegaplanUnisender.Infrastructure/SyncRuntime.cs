using System.Threading.Channels;
using MegaplanUnisender.Core;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Infrastructure;

public sealed record WebhookEvent(Guid Uuid, string? ContractorId, string Model, string Event)
{
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
    public int Attempts { get; set; }
}

// Everything in this class lasts only for the lifetime of the process.
public sealed class SyncRuntime(IOptions<IntegrationOptions> options, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object historyLock = new();
    private readonly Dictionary<Guid, WebhookEvent> events = [];
    private readonly Channel<WebhookEvent> queue = Channel.CreateBounded<WebhookEvent>(new BoundedChannelOptions(options.Value.Sync.QueueCapacity)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int pending;
    private DateTime? lastSuccessAt;
    public SemaphoreSlim ProcessingLock { get; } = new(1, 1);
    public ChannelReader<WebhookEvent> Reader => queue.Reader;
    private readonly Dictionary<string, RecentContractor> contractors = [];
    internal Dictionary<string, HashSet<string>> PendingEmails { get; } = [];
    private readonly Dictionary<string, HashSet<string>> ownersByEmail = new(StringComparer.Ordinal);

    internal void Observe(string id, Contractor? card)
    {
        PruneRecentContractors();
        if (contractors.TryGetValue(id, out var old) && old is { Eligible: true, Email: not null } && ownersByEmail.TryGetValue(old.Email, out var oldOwners))
        {
            oldOwners.Remove(id);
            if (oldOwners.Count == 0) ownersByEmail.Remove(old.Email);
        }
        if (old is null && contractors.Count >= options.Value.Sync.RecentContractorCacheCapacity)
            Forget(contractors.MinBy(x => x.Value.ExpiresAt).Key);
        // Only short-lived identity hints: never retain a card's tags, fields or subscriber status.
        contractors[id] = new(card?.Email, card?.Eligible == true, old?.ExpiresAt ?? time.GetUtcNow() + options.Value.Sync.RecentContractorCacheTtl);
        if (card is { Eligible: true, Email: not null })
        {
            if (!ownersByEmail.TryGetValue(card.Email, out var owners)) ownersByEmail[card.Email] = owners = [];
            owners.Add(id);
        }
    }
    internal string? PreviousEmail(string id)
    { PruneRecentContractors(); return contractors.GetValueOrDefault(id)?.Email; }
    internal string[] OwnerIds(string email)
    { PruneRecentContractors(); return ownersByEmail.TryGetValue(email, out var ids) ? ids.Order(StringComparer.Ordinal).ToArray() : []; }
    private void PruneRecentContractors()
    {
        foreach (var id in contractors.Where(x => x.Value.ExpiresAt <= time.GetUtcNow()).Select(x => x.Key).ToArray()) Forget(id);
    }
    private void Forget(string id)
    {
        if (contractors.Remove(id, out var old) && old.Email is { } email && ownersByEmail.TryGetValue(email, out var ids))
        { ids.Remove(id); if (ids.Count == 0) ownersByEmail.Remove(email); }
        PendingEmails.Remove(id);
    }
    private sealed record RecentContractor(string? Email, bool Eligible, DateTimeOffset ExpiresAt);

    public bool TryAccept(WebhookEvent entry)
    {
        lock (historyLock)
        {
            if (events.ContainsKey(entry.Uuid)) return true;
            if (entry.Status == "Pending")
            {
                if (!queue.Writer.TryWrite(entry)) return false;
                pending++;
            }
            events.Add(entry.Uuid, entry);
            if (events.Count > options.Value.Sync.QueueCapacity + 1000)
            {
                var oldest = events.Values.Where(x => x.Status is not ("Pending" or "Processing")).MinBy(x => x.ReceivedAt);
                if (oldest is not null) events.Remove(oldest.Uuid);
            }
            return true;
        }
    }
    public string Retry(Guid uuid)
    {
        lock (historyLock)
        {
            if (!events.TryGetValue(uuid, out var entry)) return "NotFound";
            if (entry.Status is not ("Failed" or "Uncertain")) return "NotRetryable";
            if (!queue.Writer.TryWrite(entry)) return "QueueFull";
            entry.Status = "Pending"; entry.ErrorCode = null; entry.Attempts = 0; pending++;
            return "Queued";
        }
    }
    public void Begin(WebhookEvent entry)
    { lock (historyLock) { pending--; entry.Status = "Processing"; } }
    public void Attempt(WebhookEvent entry)
    { lock (historyLock) entry.Attempts++; }
    public void Finish(WebhookEvent entry, string status, string? error = null)
    {
        lock (historyLock)
        {
            entry.Status = status; entry.ErrorCode = error;
            if (status == "Synced") lastSuccessAt = DateTime.UtcNow;
        }
    }
    public WebhookEvent[] Events(string? contractorId = null, DateTime? since = null)
    {
        lock (historyLock) return events.Values.Where(x => (contractorId is null || x.ContractorId == contractorId) && (since is null || x.ReceivedAt >= since))
            .OrderByDescending(x => x.ReceivedAt).Take(100).Select(x => x with { }).ToArray();
    }
    public object Status()
    {
        lock (historyLock) return new
        {
            options.Value.Sync.Enabled, options.Value.Sync.DryRun, Pending = pending,
            Processing = events.Values.Count(x => x.Status == "Processing"), LastSuccessAt = lastSuccessAt,
            Errors = events.Values.Where(x => x.Status is "Failed" or "Uncertain").OrderByDescending(x => x.ReceivedAt).Take(20).Select(x => x with { }).ToArray()
        };
    }
}
