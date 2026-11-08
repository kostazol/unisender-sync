using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MegaplanUnisender.Core;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MegaplanUnisender.Infrastructure;

public static class JsonFields
{
    public static string? Text(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
    public static bool? Bool(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
// Smooth spacing is deliberately more conservative than a burst-per-minute window.
public sealed class ApiRateLimiter(IOptions<IntegrationOptions> options)
{
    private readonly SemaphoreSlim megaplanMutex = new(1, 1), unisenderMutex = new(1, 1);
    private readonly Dictionary<string, DateTime> megaplanNext = [], unisenderNext = [];
    public async Task WaitAsync(string method, CancellationToken ct)
    {
        var o = options.Value.Sync.RateLimits;
        var (mutex, next) = method == "megaplan" ? (megaplanMutex, megaplanNext) : (unisenderMutex, unisenderNext);
        await mutex.WaitAsync(ct);
        try
        {
            var keys = method == "megaplan" ? new[] { ("megaplan", o.MegaplanPerMinute) } :
                method == "subscribe" ? [("general", o.GeneralPerMinute), ("subscribe", o.SubscribePerMinute)] :
                method == "async/exportContacts" ? [("general", o.GeneralPerMinute), ("export", o.ExportPerMinute)] :
                new[] { ("general", o.GeneralPerMinute) };
            var at = keys.Select(x => next.GetValueOrDefault(x.Item1, DateTime.MinValue)).Append(DateTime.UtcNow).Max();
            if (at > DateTime.UtcNow) await Task.Delay(at - DateTime.UtcNow, ct);
            foreach (var (key, limit) in keys) next[key] = DateTime.UtcNow.AddSeconds(60d / limit);
        }
        finally { mutex.Release(); }
    }
}
public sealed class MegaplanClient(HttpClient http, IOptions<IntegrationOptions> options, IOptions<Secrets> secrets, ApiRateLimiter limiter) : IMegaplanClient
{
    private readonly MegaplanOptions o = options.Value.Megaplan;
    public async Task<string> GetAccountIdentityAsync(string field, CancellationToken ct)
    {
        if (field is not ("permanentHost" or "uniqueAccountId" or "id")) throw new SyncException("megaplan_account_identity_field_invalid");
        var root = await RequestAsync("api/v3/accountInfo", null, ct) ?? throw new SyncException("megaplan_account_info_unavailable");
        var data = root.GetProperty("data");
        if (data.Text("contentType") != "AccountInfo" || string.IsNullOrWhiteSpace(data.Text(field)))
            throw new InvalidOperationException("Megaplan не вернул идентификатор аккаунта. Используйте AccountIdentityField=permanentHost или задайте проверенный ExpectedAccountId вручную.");
        // AccountInfo can contain unrelated tokens; retain only the identity used to validate webhooks.
        return data.Text(field)!;
    }
    public async Task<IReadOnlyList<MegaplanTag>> GetGlobalTagsAsync(CancellationToken ct)
    {
        var allTags = new Dictionary<string, MegaplanTag>(StringComparer.Ordinal);
        string? cursorId = null;
        int? expectedCount = null;
        for (var page = 0; page < 100; page++)
        {
            var query = new Dictionary<string, object> { ["limit"] = 100, ["fields"] = new[] { "name", "isGlobal" } };
            if (cursorId is not null) query["pageAfter"] = new { id = cursorId, contentType = "Tag" };
            var root = await RequestAsync("api/v3/contractor/tags", query, ct) ?? throw new SyncException("megaplan_tag_catalog_unavailable");
            var data = root.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array) throw new SyncException("megaplan_tag_catalog_invalid");
            if (!root.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("pagination", out var pagination) ||
                !int.TryParse(pagination.Text("count"), out var count) || count < 0)
                throw new SyncException("megaplan_tag_catalog_pagination_missing");
            if (expectedCount is not null && expectedCount != count) throw new SyncException("megaplan_tag_catalog_changed", true);
            expectedCount = count;
            var before = allTags.Count;
            string? lastId = null;
            foreach (var item in data.EnumerateArray())
            {
                var tag = new MegaplanTag(item.Text("id") ?? throw new SyncException("megaplan_tag_id_unknown"),
                    item.Text("name") ?? throw new SyncException("megaplan_tag_name_unknown"),
                    item.Bool("isGlobal") ?? throw new SyncException("megaplan_tag_global_scope_unknown"));
                if (!allTags.TryAdd(tag.Id, tag) && (tag.Id != cursorId || allTags[tag.Id] != tag))
                    throw new SyncException("megaplan_tag_catalog_changed", true);
                lastId = tag.Id;
            }
            if (allTags.Count == count) return allTags.Values.Where(x => x.IsGlobal).ToArray();
            if (allTags.Count > count || allTags.Count == before || lastId is null)
                throw new SyncException("megaplan_tag_catalog_incomplete");
            cursorId = lastId;
        }
        throw new SyncException("megaplan_tag_catalog_page_limit");
    }
    public async Task<Contractor?> GetAsync(string id, CancellationToken ct)
    {
        var root = await RequestAsync("api/v3/contractor/" + Uri.EscapeDataString(id), null, ct);
        if (root is null) return null;
        var data = root.Value.GetProperty("data");
        if (data.Bool("isDropped") == true) return null;
        if (!data.TryGetProperty("tags", out _))
        {
            root = await RequestAsync("api/v3/contractor/" + Uri.EscapeDataString(id), new { fields = new[] { "tags", "contactInfo" } }, ct);
            if (root is null) throw new SyncException("megaplan_card_disappeared", true);
            data = root.Value.GetProperty("data");
        }
        var c = Parse(data, o, options.Value.Unisender);
        if (c.Id != id) throw new SyncException("megaplan_id_mismatch");
        return c;
    }
    public async Task<object> DiagnoseAsync(string id, CancellationToken ct)
    {
        var root = await RequestAsync("api/v3/contractor/" + Uri.EscapeDataString(id), new { fields = new[] { "tags", "contactInfo" } }, ct)
            ?? throw new SyncException("megaplan_test_card_missing");
        var data = root.GetProperty("data");
        return new
        {
            Model = data.Text("contentType"), Properties = data.EnumerateObject().Select(x => new { x.Name, Type = x.Value.ValueKind.ToString() }),
            Tags = data.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array ?
                tags.EnumerateArray().Select(x => new { HasId = x.Text("id") is not null, Global = x.Bool("isGlobal"), Properties = x.EnumerateObject().Select(p => p.Name).ToArray() }).ToArray() : null
        };
    }
    private async Task<JsonElement?> RequestAsync(string path, object? query, CancellationToken ct)
    {
        await limiter.WaitAsync("megaplan", ct);
        var uri = new Uri(new Uri(o.BaseUrl.TrimEnd('/') + "/"), path + (query is null ? "" : "?" + Uri.EscapeDataString(JsonSerializer.Serialize(query))));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Value.MegaplanApiToken);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new SyncException("megaplan_http_" + (int)response.StatusCode, (int)response.StatusCode is 429 or >= 500);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException) { throw new SyncException("megaplan_invalid_json"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SyncException("megaplan_invalid_envelope");
            if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                throw new SyncException("megaplan_business_error");
            if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("status", out var status) && status.ToString() is not ("200" or "ok"))
                throw new SyncException("megaplan_meta_error");
            if (!root.TryGetProperty("data", out _)) throw new SyncException("megaplan_missing_data");
            return root.Clone();
        }
    }
    public static Contractor Parse(JsonElement d, MegaplanOptions o, UnisenderOptions u)
    {
        var id = d.Text("id") ?? throw new SyncException("megaplan_missing_id");
        var model = d.Text("contentType") ?? throw new SyncException("megaplan_missing_model");
        if (model is not ("ContractorHuman" or "ContractorCompany")) throw new SyncException("megaplan_unsupported_card_model");
        if (d.Bool("canSeeFull") == false) throw new SyncException("megaplan_partial_card");
        if (!d.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) throw new SyncException("megaplan_tags_unknown");
        if (tags.EnumerateArray().Any(x => x.Text("id") is null)) throw new SyncException("megaplan_tag_id_unknown");
        if (int.TryParse(d.Text("tagsCount"), out var tc) && tc != tags.GetArrayLength()) throw new SyncException("megaplan_partial_tags");
        if (o.SyncAllGlobalTags && tags.EnumerateArray().Any(t => t.Bool("isGlobal") is null && !o.ManagedGlobalTagIds.Contains(t.Text("id"))))
            throw new SyncException("megaplan_tag_global_scope_unknown");
        var global = tags.EnumerateArray().Where(t => t.Bool("isGlobal") != false &&
            (t.Bool("isGlobal") == true || o.ManagedGlobalTagIds.Contains(t.Text("id"))))
            .Select(t => t.Text("id") ?? throw new SyncException("megaplan_tag_id_unknown")).ToHashSet(StringComparer.Ordinal);
        if (!d.TryGetProperty("contactInfo", out var info) || info.ValueKind != JsonValueKind.Array) throw new SyncException("megaplan_contact_info_unknown");
        if (int.TryParse(d.Text("contactInfoCount"), out var cc) && cc != info.GetArrayLength()) throw new SyncException("megaplan_partial_contact_info");
        // contactInfo is a union of ContactInfo and postal Address. Address.type is an AddressType object.
        var contacts = info.EnumerateArray().Where(x => x.Text("contentType") != "Address").ToArray();
        if (contacts.Any(x => x.Text("type") is null)) throw new SyncException("megaplan_partial_contact_info");
        var emails = contacts.Where(x => x.Text("type") == "email").ToArray();
        if (emails.Any(x => Canonical.Email(x.Text("value")) is null)) throw new SyncException("megaplan_invalid_email");
        var main = emails.Where(x => x.Bool("isMain") == true).Select(x => Canonical.Email(x.Text("value"))!).Distinct().ToArray();
        if (main.Length > 1) throw new SyncException("megaplan_ambiguous_primary_email");
        var email = main.FirstOrDefault() ?? emails.Select(x => Canonical.Email(x.Text("value"))!).Order(StringComparer.Ordinal).FirstOrDefault();
        var fields = new Dictionary<string, string>();
        foreach (var (source, target) in u.FieldMappings)
        {
            var value = source switch
            {
                "name" => d.Text("name") ?? string.Join(" ", new[] { d.Text("lastName"), d.Text("firstName"), d.Text("middleName") }.Where(x => !string.IsNullOrEmpty(x))),
                "phone" => contacts.Where(x => x.Text("type") == "phone").OrderByDescending(x => x.Bool("isMain") == true).Select(x => x.Text("value")).FirstOrDefault() ?? "",
                _ => d.Text(source[6..]) ?? throw new SyncException("megaplan_mapped_field_unknown")
            };
            fields[target] = value;
        }
        var desired = global.Where(id => id != o.EligibilityGlobalTagId && (o.SyncAllGlobalTags || o.TagMappings.ContainsKey(id))).Select(id =>
        {
            var mapping = o.TagMappings.GetValueOrDefault(id, "@crm-name");
            return mapping == "@crm-name" ? tags.EnumerateArray().First(t => t.Text("id") == id).Text("name")
                ?? throw new SyncException("megaplan_tag_name_unknown") : mapping;
        }).ToArray();
        if (desired.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains(',') || x.Length > 100)) throw new SyncException("unrepresentable_tag_name");
        return new Contractor(id, model, email, global.Contains(o.EligibilityGlobalTagId), fields, Canonical.Tags(desired));
    }
}
public sealed partial class UnisenderClient(HttpClient http, IOptions<IntegrationOptions> options, IOptions<Secrets> secrets, ApiRateLimiter limiter,
    ILogger<UnisenderClient>? logger = null) : IUnisenderClient
{
    private readonly UnisenderOptions o = options.Value.Unisender;
    private readonly ILogger<UnisenderClient> log = logger ?? NullLogger<UnisenderClient>.Instance;
    public async Task<string> CreateListAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new SyncException("unisender_list_name_missing");
        var result = await CallAsync("createList", new() { ["title"] = name.Trim() }, true, ct);
        var id = result.Text("id");
        if (!long.TryParse(id, out var number) || number <= 0) throw new SyncException("unisender_create_list_invalid_result", uncertain: true);
        return id!;
    }
    public async Task VerifyMissingContactResponseAsync(CancellationToken ct)
    {
        if (o.ConfirmNullResultMeansMissing || o.VerifiedMissingContactErrorCodes.Length > 0) return;
        // A unique address under the reserved example.com domain; this is a read, never a subscription.
        var email = "megaplan-probe-" + Guid.NewGuid().ToString("N") + "@example.com";
        JsonElement result;
        try { result = await CallAsync("getContact", new() { ["email"] = email, ["include_lists"] = "1", ["include_fields"] = "0" }, false, ct); }
        catch (SyncException error) when (error.Code == "unisender_object_not_found")
        {
            o.VerifiedMissingContactErrorCodes = o.VerifiedMissingContactErrorCodes.Append(error.Code).Distinct(StringComparer.Ordinal).ToArray();
            return;
        }
        if (result.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Unisender вернул неизвестный ответ на проверку отсутствующего контакта. Запись не включена; требуется проверить совместимость getContact, а не подбирать ID в настройках.");
        o.ConfirmNullResultMeansMissing = true;
    }
    public async Task<ContactStatus> GetContactAsync(string email, CancellationToken ct)
    {
        JsonElement r;
        try { r = await CallAsync("getContact", new() { ["email"] = email, ["include_lists"] = "1", ["include_fields"] = o.FieldMappings.Count > 0 ? "1" : "0" }, false, ct); }
        catch (SyncException failure) when (o.VerifiedMissingContactErrorCodes.Contains(failure.Code)) { return new(false, true, false, "missing_verified"); }
        if (r.ValueKind == JsonValueKind.Null && o.ConfirmNullResultMeansMissing) return new(false, true, false, "missing_verified");
        if (r.ValueKind == JsonValueKind.Null) throw new SyncException("unisender_null_result_unverified");
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("email", out var e) || e.ValueKind != JsonValueKind.Object ||
            !string.Equals(e.Text("email"), email, StringComparison.OrdinalIgnoreCase)) throw new SyncException("unisender_contact_shape_unverified");
        var status = e.Text("status");
        if (status is not ("active" or "new")) return new(true, false, false, "email_status_not_subscribable");
        var fields = new Dictionary<string, string>();
        if (o.FieldMappings.Count > 0)
        {
            if (!r.TryGetProperty("fields", out var values) || values.ValueKind != JsonValueKind.Object)
                throw new SyncException("unisender_fields_unverified");
            foreach (var name in o.FieldMappings.Values.Distinct(StringComparer.Ordinal))
            {
                if (values.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                    throw new SyncException("unisender_fields_unverified");
                fields[name] = values.Text(name) ?? "";
            }
        }
        if (!r.TryGetProperty("lists", out var lists) || lists.ValueKind != JsonValueKind.Array) throw new SyncException("unisender_lists_unknown");
        var memberships = lists.EnumerateArray().ToArray();
        if (memberships.Any(x => !long.TryParse(x.Text("id"), out var id) || id <= 0))
            throw new SyncException("unisender_list_membership_unverified");
        var list = memberships.Where(x => x.Text("id") == o.ListId).ToArray();
        if (list.Length > 1) throw new SyncException("unisender_duplicate_list_status");
        if (list.Length == 0) return new(true, true, false, "allowed") { Fields = fields };
        if (!list[0].TryGetProperty("status", out var membershipStatus) || membershipStatus.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(membershipStatus.GetString())) throw new SyncException("unisender_list_membership_unverified");
        var listStatus = membershipStatus.GetString();
        if (listStatus is not ("active" or "new" or "excluded")) return new(true, false, false, "list_status_not_subscribable");
        return new(true, true, listStatus is "active" or "new", "allowed") { Fields = fields };
    }
    public async Task SubscribeAsync(string email, IReadOnlyDictionary<string, string> fields, string[]? tags, CancellationToken ct)
    {
        if (tags?.Length > 10) throw new SyncException("tag_limit_exceeded");
        var p = new Dictionary<string, string> { ["list_ids"] = o.ListId, ["fields[email]"] = email, ["overwrite"] = "2", ["double_optin"] = o.DoubleOptInMode.ToString() };
        foreach (var (key, value) in fields) p["fields[" + key + "]"] = value;
        if (tags is not null) p["tags"] = string.Join(',', tags);
        var r = await CallAsync("subscribe", p, true, ct);
        if (!long.TryParse(r.Text("person_id"), out var person) || person <= 0) throw new SyncException("unisender_subscribe_invalid_result", uncertain: true);
    }
    public async Task ExcludeAsync(string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.ListId)) throw new SyncException("unisender_target_list_missing");
        var result = await CallAsync("exclude", new() { ["list_ids"] = o.ListId, ["contact_type"] = "email", ["contact"] = email }, true, ct);
        if (result.ValueKind != JsonValueKind.Object) throw new SyncException("unisender_exclude_invalid_result", uncertain: true);
    }
    public Task ClearTagsAsync(string email, CancellationToken ct) => UpdateTagsAsync(email, [], replace: true, ct);
    public async Task UpdateTagsAsync(string email, string[] tags, bool replace, CancellationToken ct)
    {
        if (tags.Length > 10) throw new SyncException("tag_limit_exceeded");
        if (tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains(',') || x.Length > 100)) throw new SyncException("unrepresentable_tag_name");
        if (replace && tags.Length == 0 && !o.ConfirmEmptyTagsClear) throw new SyncException("empty_tag_clear_requires_live_verification");
        var r = await CallAsync("importContacts", new() { ["field_names[0]"] = "email", ["field_names[1]"] = "tags", ["data[0][0]"] = email,
            ["data[0][1]"] = string.Join(',', tags), ["overwrite_tags"] = replace ? "1" : "0", ["overwrite_lists"] = "0" }, true, ct);
        if (r.Text("updated") != "1" || (r.Text("invalid") is { } invalid && invalid != "0") ||
            (r.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array && log.EnumerateArray().Any(x => x.Text("code")?.StartsWith("e_", StringComparison.Ordinal) == true)) ||
            (r.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0))
            throw new SyncException("unisender_import_partial_result", uncertain: true);
    }
    public Task<JsonElement> ReadAsync(string method, CancellationToken ct) => method is "getLists" or "getTags" ?
        CallAsync(method, [], false, ct) : throw new SyncException("unisender_diagnostic_method_not_allowed");
    private async Task<JsonElement> CallAsync(string method, Dictionary<string, string> p, bool write, CancellationToken ct)
    {
        SyncException Failure(string code, bool transient = false, bool uncertain = false, TimeSpan? retryAfter = null) =>
            new(code, transient, uncertain, retryAfter, operation: method);
        await limiter.WaitAsync(method, ct);
        p["api_key"] = secrets.Value.UnisenderApiKey; p["format"] = "json";
        try
        {
            using var content = new FormUrlEncodedContent(p);
            using var response = await http.PostAsync(new Uri(new Uri(o.ApiBaseUrl.TrimEnd('/') + "/"), method), content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                throw Failure("unisender_http_" + code, code == 429 || (!write && (code >= 500 || code == 408)), write && (code >= 500 || code == 408),
                    response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Failure("unisender_invalid_envelope", uncertain: write);
            if (root.TryGetProperty("error", out _))
            {
                var code = root.Text("code") ?? root.Text("error_code") ?? "unknown";
                // Never retain vendor error text: it may contain email addresses or credentials.
                code = new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(64).ToArray());
                throw Failure("unisender_" + code, code is "retry_later" or "api_call_limit_exceeded_for_api_key" or "api_call_limit_exceeded_for_ip");
            }
            if (!root.TryGetProperty("result", out var result)) throw Failure("unisender_missing_result", uncertain: write);
            var rowLimit = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("log", out var entries) &&
                entries.ValueKind == JsonValueKind.Array && entries.EnumerateArray().Any(x => x.Text("code") == "w_common_overlimit");
            var responseLimit = root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array &&
                warnings.EnumerateArray().Any(x => string.Equals(x.Text("warning")?.Trim(), "Addresses overlimit for current period", StringComparison.OrdinalIgnoreCase));
            if (rowLimit || responseLimit)
                log.LogError("Unisender сообщил о превышении лимита контактов текущего тарифа. Проверьте тариф. {AlertCode}; метод {ApiMethod}; список {ListId}; источник {WarningSource}",
                    "unisender_contact_limit_exceeded", method, o.ListId, rowLimit ? "w_common_overlimit" : "response_warning");
            // A successful response with a warning is still applied; do not turn it into a retryable failure.
            return result.Clone();
        }
        catch (HttpRequestException) { throw Failure("unisender_network", transient: !write, uncertain: write); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("unisender_timeout", transient: !write, uncertain: write); }
        catch (JsonException) { throw Failure("unisender_invalid_json", uncertain: write); }
    }
}
