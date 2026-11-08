using System.Net.Mail;
using System.Text.Json;

namespace MegaplanUnisender.Core;

public sealed record Contractor(string Id, string Model, string? Email, bool Eligible, Dictionary<string, string> Fields, string[] Tags);
public sealed record ContactStatus(bool Exists, bool MaySubscribe, bool InList, string Reason)
{
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}
public sealed record MegaplanTag(string Id, string Name, bool IsGlobal);
public interface IMegaplanClient
{
    Task<string> GetAccountIdentityAsync(string field, CancellationToken ct);
    Task<Contractor?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<MegaplanTag>> GetGlobalTagsAsync(CancellationToken ct);
    Task<object> DiagnoseAsync(string id, CancellationToken ct);
}
public interface IUnisenderClient
{
    Task<string> CreateListAsync(string name, CancellationToken ct);
    Task VerifyMissingContactResponseAsync(CancellationToken ct);
    Task<ContactStatus> GetContactAsync(string email, CancellationToken ct);
    Task<string[]> GetContactTagsAsync(string email, CancellationToken ct);
    Task SubscribeAsync(string email, IReadOnlyDictionary<string, string> fields, string[]? tags, CancellationToken ct);
    Task ExcludeAsync(string email, CancellationToken ct);
    Task ClearTagsAsync(string email, CancellationToken ct);
    Task UpdateTagsAsync(string email, string[] tags, bool replace, CancellationToken ct);
    Task<JsonElement> ReadAsync(string method, CancellationToken ct);
}
public sealed class SyncException(string code, bool transient = false, bool uncertain = false, TimeSpan? retryAfter = null, string? operation = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
    public bool Uncertain { get; } = uncertain;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? Operation { get; } = operation;
}
public static class Canonical
{
    public static string[] Tags(IEnumerable<string> tags) => tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static string? Email(string? value)
    {
        if (value is null || !MailAddress.TryCreate(value.Trim(), out var m) || !string.Equals(m.Address, value.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return m.Address.ToLowerInvariant();
    }
}
public interface ITagStrategy { string[] Prepare(string[] desired, string[] previous); }
public sealed class TagStrategy(UnisenderOptions options) : ITagStrategy
{
    public string[] Prepare(string[] desired, string[] previous)
    {
        desired = Canonical.Tags(desired);
        if (desired.Length > 10) throw new SyncException("tag_limit_exceeded");
        if (options.TagOwnershipMode == TagOwnershipMode.PreserveExternal)
            throw new SyncException("preserve_external_ownership_not_supported");
        if (!options.ConfirmOverwriteAllTags) throw new SyncException("exclusive_ownership_not_confirmed");
        if (desired.Length == 0 && previous.Length > 0 && !options.ConfirmEmptyTagsClear)
            throw new SyncException("empty_tag_clear_requires_live_verification");
        return desired;
    }
    public static string[] Merge(IEnumerable<string> current, IEnumerable<string> previous, IEnumerable<string> desired) =>
        Canonical.Tags(current.Except(previous, StringComparer.Ordinal).Concat(desired));
}
