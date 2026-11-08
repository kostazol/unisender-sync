using System.Net;
using System.Text;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

// CRM shapes follow the official demo schema. Missing-contact null is HYPOTHETICAL,
// explicitly enabled in this fixture; it is disabled by default in the application.
public sealed class Scenario : IAsyncDisposable
{
    public IntegrationOptions Options { get; } = new()
    {
        Megaplan = new() { BaseUrl = "https://crm.example.test", EligibilityGlobalTagId = "1", SyncAllGlobalTags = false,
            ManagedGlobalTagIds = ["1", "2", "3"], TagMappings = new() { ["2"] = "second", ["3"] = "third" } },
        Unisender = new() { ListId = "42", FieldMappings = new() { ["name"] = "Name" }, TagOwnershipMode = TagOwnershipMode.Exclusive, ConfirmOverwriteAllTags = true,
            ConsentPolicy = ConsentPolicy.VerifiedByAdministrator, ConfirmEmptyTagsClear = true, ConfirmNullResultMeansMissing = true },
        Sync = new() { Enabled = true, DryRun = false, RateLimits = new() { GeneralPerMinute = 600000, SubscribePerMinute = 600000, ExportPerMinute = 600000, MegaplanPerMinute = 600000 } }
    };
    public Secrets Secrets { get; } = new() { MegaplanApiToken = "fixture-token", UnisenderApiKey = "fixture-key" };
    public SyncRuntime Runtime { get; private set; }
    public Dictionary<string, object> Cards { get; } = [];
    public List<(string Method, Dictionary<string, string> Form)> Writes { get; } = [];
    public string? Status { get; set; }
    public string? ListStatus { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    public List<string> Gets { get; } = [];
    public List<string> UnisenderReads { get; } = [];
    public Dictionary<string, string[]> RemoteTags { get; } = [];
    public Dictionary<string, Dictionary<string, string>> RemoteFields { get; } = [];
    private readonly Dictionary<string, string> remoteStatus = [], remoteMembership = [];
    private readonly Dictionary<Guid, string> exports = [];
    public ManualClock Clock { get; } = new();
    public Scenario() => Runtime = new(Microsoft.Extensions.Options.Options.Create(Options), Clock);
    public void NewSession() => Runtime = new(Microsoft.Extensions.Options.Options.Create(Options), Clock);
    public static object Card(string id = "10", string email = "a@example.test", string name = "Alice", int[]? tags = null) => new
    {
        id, contentType = "ContractorHuman", name, canSeeFull = true,
        contactInfo = new[] { new { contentType = "ContactInfo", type = "email", value = email, isMain = true } },
        tags = (tags ?? [1, 2]).Select(x => new { contentType = "Tag", id = x.ToString(), name = "Tag " + x, isGlobal = true }).ToArray()
    };
    public IMegaplanClient Crm()
    {
        var o = Microsoft.Extensions.Options.Options.Create(Options);
        return new MegaplanClient(new HttpClient(new Handler(Reply)), o, Microsoft.Extensions.Options.Options.Create(Secrets), new ApiRateLimiter(o));
    }
    public IUnisenderClient Uni()
    {
        var o = Microsoft.Extensions.Options.Options.Create(Options);
        return new UnisenderClient(new HttpClient(new Handler(Reply)), o, Microsoft.Extensions.Options.Options.Create(Secrets), new ApiRateLimiter(o));
    }
    public WebhookProcessor Processor() => new(Runtime, Crm(), Uni(), Microsoft.Extensions.Options.Options.Create(Options), new TagStrategy(Options.Unisender));
    public Task<string> Apply(string id = "10", bool drop = false) => Processor().ProcessAsync(
        new WebhookEvent(Guid.NewGuid(), id, "ContractorHuman", drop ? "on_after_drop" : "on_after_update"), default);
    private HttpResponseMessage Reply(HttpRequestMessage req)
    {
        if (Override?.Invoke(req) is { } custom) return custom;
        var path = req.RequestUri!.AbsolutePath;
        if (req.RequestUri.Host == "usdata.s3.unisender.ru")
        {
            Assert.Equal(HttpMethod.Get, req.Method); Assert.Null(req.Headers.Authorization); Assert.Null(req.Content);
            Assert.DoesNotContain(Secrets.UnisenderApiKey, req.RequestUri.ToString());
            var email = exports[Guid.Parse(path.Trim('/'))];
            static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            return new(HttpStatusCode.OK) { Content = new StringContent("email,tags\r\n" + Quote(email) + "," +
                Quote(string.Join(',', RemoteTags.GetValueOrDefault(email, []))) + "\r\n", Encoding.UTF8, "text/csv") };
        }
        if (req.RequestUri.Host == "crm.example.test")
        {
            Gets.Add(path);
            if (path == "/api/v3/contractor") return Json(new { meta = new { status = 200, pagination = new { count = Cards.Count, limit = 100 } }, data = Cards.Values });
            var id = path.Split('/').Last();
            return Cards.TryGetValue(id, out var card) ? Json(new { meta = new { status = 200 }, data = card }) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var method = path.Split('/').Last();
        var form = ParseForm(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        Assert.DoesNotContain("api_key", req.RequestUri.Query);
        if (method == "getContact")
        {
            UnisenderReads.Add(method);
            var email = form["email"]; var status = Status ?? remoteStatus.GetValueOrDefault(email);
            var membership = ListStatus ?? remoteMembership.GetValueOrDefault(email);
            return status is null ? Json(new { result = (object?)null }) : Json(new { result = new
            { email = new { email, status }, fields = RemoteFields.GetValueOrDefault(email, []),
                lists = membership is null ? Array.Empty<object>() : [new { id = "42", status = membership }] } });
        }
        if (method == "exportContacts")
        {
            Assert.EndsWith("/async/exportContacts", path); Assert.Equal(5, form.Count);
            Assert.Equal("email", form["field_names[0]"]); Assert.Equal("tags", form["field_names[1]"]);
            var id = Guid.NewGuid(); exports[id] = form["email"]; UnisenderReads.Add(method);
            return Json(new { result = new { task_uuid = id, status = "new" } });
        }
        if (method == "getTaskResult")
            return Json(new { result = new { task_uuid = form["task_uuid"], task_type = "export_contacts", status = "completed",
                file_to_download = "https://usdata.s3.unisender.ru/" + form["task_uuid"] } });
        if (method == "getLists") return Json(new { result = new[] { new { id = "42", title = "Test list" } } });
        if (method == "getTags") return Json(new { result = Array.Empty<string>() });
        Writes.Add((method, form));
        if (method == "subscribe")
        {
            var email = form["fields[email]"]; remoteStatus[email] = "active"; remoteMembership[email] = "active";
            if (form.TryGetValue("tags", out var tags)) RemoteTags[email] = tags.Split(',');
            if (!RemoteFields.TryGetValue(email, out var fields)) RemoteFields[email] = fields = [];
            foreach (var (key, value) in form.Where(x => x.Key.StartsWith("fields[", StringComparison.Ordinal) && x.Key != "fields[email]"))
                fields[key[7..^1]] = value;
            return Json(new { result = new { person_id = 1234567890123L } });
        }
        if (method == "exclude") { remoteMembership[form["contact"]] = "excluded"; return Json(new { result = new { } }); }
        if (method == "importContacts")
        {
            var email = form["data[0][0]"]; var value = form["data[0][1]"]; var tags = value.Length == 0 ? [] : value.Split(',');
            RemoteTags[email] = form["overwrite_tags"] == "1" ? tags : Canonical.Tags(RemoteTags.GetValueOrDefault(email, []).Concat(tags));
            return Json(new { result = new { total = 1, updated = 1, inserted = 0, deleted = 0 } });
        }
        throw new InvalidOperationException("Unexpected fixture endpoint");
    }
    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static Dictionary<string, string> ParseForm(string body) => body.Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x.ElementAtOrDefault(1) ?? ""));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public TimeSpan AdvancePerRead { get; set; }
    public override DateTimeOffset GetUtcNow() { var result = now; now += AdvancePerRead; return result; }
    public void Advance(TimeSpan duration) => now += duration;
}
public sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
}
