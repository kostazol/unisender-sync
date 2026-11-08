using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.Api;

public static class Security
{
    public static bool EqualSecret(string input, string expected) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(input)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
public static class Webhook
{
    public static async Task<IResult> HandleAsync(string secret, HttpRequest request, SyncRuntime runtime, IOptions<IntegrationOptions> options, IOptions<Secrets> secrets, CancellationToken ct)
    {
        var o = options.Value;
        if (secrets.Value.WebhookSecret.Length < 32 || !Security.EqualSecret(secret, secrets.Value.WebhookSecret)) return Results.Unauthorized();
        if (!request.HasJsonContentType()) return Results.StatusCode(415);
        if (request.ContentLength > o.Webhook.MaxBodySize) return Results.StatusCode(413);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > o.Webhook.MaxBodySize) return Results.StatusCode(413);
            bytes.Write(buffer, 0, read);
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            if (string.IsNullOrEmpty(o.Megaplan.ExpectedIntegrationUuid) || root.Text("integrationUuid") != o.Megaplan.ExpectedIntegrationUuid) return Results.Unauthorized();
            var model = root.Text("model") ?? ""; var ev = root.Text("event") ?? "";
            // Official Integration lifecycle events can omit accountInfo. They never enter the sync worker.
            var hasAccount = root.TryGetProperty("accountInfo", out var account);
            if ((hasAccount || model != "Integration") && (!hasAccount || string.IsNullOrEmpty(o.Megaplan.ExpectedAccountId) ||
                account.Text(o.Megaplan.AccountIdentityField) != o.Megaplan.ExpectedAccountId)) return Results.Unauthorized();
            if (!Guid.TryParse(root.Text("uuid"), out var uuid)) return Results.BadRequest(new { code = "invalid_uuid" });
            var data = root.TryGetProperty("data", out var d) ? d : default;
            var id = data.Text("id");
            var known = o.Megaplan.ContractorModels.Contains(model) && ev is "on_after_create" or "on_after_update" or "on_after_drop";
            var validId = !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(char.IsAsciiDigit);
            var entry = new WebhookEvent(uuid, validId ? id : null, SafeLabel(model), SafeLabel(ev))
            {
                Status = model == "Integration" ? "SkippedLifecycle" : !known || !validId ? "SkippedUnsupported" :
                    !o.Sync.Enabled ? "Disabled" : "Pending"
            };
            if (!runtime.TryAccept(entry)) return Results.StatusCode(503);
            return Results.Ok();
        }
    }
    private static string SafeLabel(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(80).ToArray());
}
