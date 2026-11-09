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
    public static async Task<IResult> HandleAsync(string secret, HttpRequest request, SyncRuntime runtime,
        IOptions<IntegrationOptions> options, IOptions<Secrets> secrets, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(Webhook).FullName!);
        var outcome = "request_failed";
        WebhookEvent? entry = null;
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var context = request.HttpContext;
            logger.LogDebug("Megaplan webhook received; correlation {CorrelationId}; content length {ContentLength}",
                context.TraceIdentifier, request.ContentLength);
            context.Response.OnCompleted(() =>
            {
                logger.LogDebug("Megaplan webhook completed; correlation {CorrelationId}; HTTP {HttpStatusCode}; outcome {WebhookOutcome}; event {EventUuid}; contractor {ContractorId}",
                    context.TraceIdentifier, context.Response.StatusCode, ct.IsCancellationRequested ? "request_aborted" : outcome,
                    entry?.Uuid, entry?.ContractorId);
                return Task.CompletedTask;
            });
        }

        IResult Complete(IResult result, string reason)
        {
            outcome = reason;
            return result;
        }

        var o = options.Value;
        if (secrets.Value.WebhookSecret.Length < 32 || !Security.EqualSecret(secret, secrets.Value.WebhookSecret))
            return Complete(Results.Unauthorized(), "invalid_secret");
        if (!request.HasJsonContentType()) return Complete(Results.StatusCode(415), "unsupported_content_type");
        if (request.ContentLength > o.Webhook.MaxBodySize) return Complete(Results.StatusCode(413), "body_too_large");
        using var bytes = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > o.Webhook.MaxBodySize) return Complete(Results.StatusCode(413), "body_too_large");
            bytes.Write(buffer, 0, read);
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return Complete(Results.BadRequest(new { code = "invalid_json" }), "invalid_json"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Complete(Results.BadRequest(), "invalid_json_root");
            if (string.IsNullOrEmpty(o.Megaplan.ExpectedIntegrationUuid) || root.Text("integrationUuid") != o.Megaplan.ExpectedIntegrationUuid)
                return Complete(Results.Unauthorized(), "integration_mismatch");
            var model = root.Text("model") ?? ""; var ev = root.Text("event") ?? "";
            // Official Integration lifecycle events can omit accountInfo. They never enter the sync worker.
            var hasAccount = root.TryGetProperty("accountInfo", out var account);
            if ((hasAccount || model != "Integration") && (!hasAccount || string.IsNullOrEmpty(o.Megaplan.ExpectedAccountId) ||
                account.Text(o.Megaplan.AccountIdentityField) != o.Megaplan.ExpectedAccountId))
                return Complete(Results.Unauthorized(), "account_mismatch");
            if (!Guid.TryParse(root.Text("uuid"), out var uuid))
                return Complete(Results.BadRequest(new { code = "invalid_uuid" }), "invalid_uuid");
            var data = root.TryGetProperty("data", out var d) ? d : default;
            var id = data.Text("id");
            var known = o.Megaplan.ContractorModels.Contains(model) && ev is "on_after_create" or "on_after_update" or "on_after_drop";
            var validId = !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(char.IsAsciiDigit);
            var (status, reason) = model == "Integration" ? ("SkippedLifecycle", "ignored_lifecycle") :
                !known ? ("SkippedUnsupported", "unsupported_event") : !validId ? ("SkippedUnsupported", "invalid_contractor_id") :
                !o.Sync.Enabled ? ("Disabled", "sync_disabled") : ("Pending", "queued");
            entry = new WebhookEvent(uuid, validId ? id : null, SafeLabel(model), SafeLabel(ev)) { Status = status };
            if (!runtime.TryAccept(entry, out var duplicate)) return Complete(Results.StatusCode(503), "queue_full");
            return Complete(Results.Ok(), duplicate ? "duplicate" : reason);
        }
    }
    private static string SafeLabel(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(80).ToArray());
}
