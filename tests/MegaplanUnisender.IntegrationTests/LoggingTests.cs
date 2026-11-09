using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MegaplanUnisender.Api;
using MegaplanUnisender.Core;
using Serilog;

namespace MegaplanUnisender.IntegrationTests;

public sealed class LoggingTests
{
    [Fact]
    public async Task WebhookDebugGelfCorrelatesReceiptWithQueueDuplicateAndRejectedRequest()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var app = new TestApp(enabled: true, worker: false, graylogPort: ((IPEndPoint)receiver.Client.LocalEndPoint!).Port,
            minimumLogLevel: "Debug");
        using var client = app.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uuid = Guid.NewGuid();
        var endpoint = "/webhooks/megaplan/" + TestApp.Secret;
        var payload = TestApp.Event(uuid.ToString());

        await AssertRequest(endpoint, HttpStatusCode.OK, "queued");
        await AssertRequest(endpoint, HttpStatusCode.OK, "duplicate");
        await AssertRequest(endpoint + "-invalid", HttpStatusCode.Unauthorized, "invalid_secret");
        Assert.Single(app.Runtime.Events());

        async Task AssertRequest(string path, HttpStatusCode status, string outcome)
        {
            using var response = await client.PostAsJsonAsync(path, payload, timeout.Token);
            Assert.Equal(status, response.StatusCode);
            var correlation = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
            using var receipt = await ReceiveMatching(receiver, IsThisRequest, timeout.Token);
            Assert.StartsWith("Megaplan webhook received", receipt.RootElement.GetProperty("short_message").GetString(), StringComparison.Ordinal);
            using var completed = await ReceiveMatching(receiver, IsThisRequest, timeout.Token);
            Assert.Equal(outcome, completed.RootElement.GetProperty("_WebhookOutcome").GetString());
            Assert.Equal((int)status, completed.RootElement.GetProperty("_HttpStatusCode").GetInt32());
            if (status == HttpStatusCode.OK)
            {
                Assert.Equal(uuid.ToString(), completed.RootElement.GetProperty("_EventUuid").GetString());
                Assert.Equal("1000251", completed.RootElement.GetProperty("_ContractorId").GetString());
            }
            foreach (var record in new[] { receipt.RootElement, completed.RootElement })
            {
                Assert.Equal(7, record.GetProperty("level").GetInt32());
                Assert.DoesNotContain(TestApp.Secret, record.ToString());
                Assert.DoesNotContain("accountInfo", record.ToString());
            }

            bool IsThisRequest(JsonElement root) => root.TryGetProperty("_CorrelationId", out var id) && id.GetString() == correlation;
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ContactLimitWarningProducesErrorGelfWithoutRepeatingAcceptedWrite(bool existing)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var app = new TestApp(enabled: true, graylogPort: ((IPEndPoint)receiver.Client.LocalEndPoint!).Port)
        { ExistingContact = existing, ContactLimitWarning = true };
        using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync("/webhooks/megaplan/" + TestApp.Secret, TestApp.Event(uuid.ToString()));
        var entry = await app.WaitForEvent(uuid);
        Assert.Equal("Synced", entry.Status); Assert.Equal(1, entry.Attempts); Assert.Equal(1, app.WriteCalls);
        using var alert = await ReceiveAlert(receiver, "unisender_contact_limit_exceeded"); var root = alert.RootElement;
        Assert.Equal(3, root.GetProperty("level").GetInt32());
        Assert.Equal(existing ? "importContacts" : "subscribe", root.GetProperty("_ApiMethod").GetString());
        Assert.Equal(uuid.ToString(), root.GetProperty("_EventUuid").GetString());
        Assert.Equal("1000251", root.GetProperty("_ContractorId").GetString());
        Assert.Equal("42", root.GetProperty("_ListId").GetString());
        Assert.DoesNotContain("private-api-key", root.ToString()); Assert.DoesNotContain("fixture@example.test", root.ToString());
        var next = Guid.NewGuid(); await client.PostAsJsonAsync("/webhooks/megaplan/" + TestApp.Secret, TestApp.Event(next.ToString()));
        Assert.Equal("Synced", (await app.WaitForEvent(next)).Status); Assert.Equal(1, app.WriteCalls);
    }
    [Theory] [InlineData("not_enough_money")] [InlineData("unspecified")]
    public async Task RejectedCreationProducesErrorGelfAndDoesNotRetryPermanentBusinessError(string code)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var app = new TestApp(enabled: true, graylogPort: ((IPEndPoint)receiver.Client.LocalEndPoint!).Port)
        { ExistingContact = false, BusinessErrorCode = code };
        using var client = app.CreateClient(); var uuid = Guid.NewGuid();
        await client.PostAsJsonAsync("/webhooks/megaplan/" + TestApp.Secret, TestApp.Event(uuid.ToString()));
        var entry = await app.WaitForEvent(uuid);
        Assert.Equal("Failed", entry.Status); Assert.Equal("unisender_" + code, entry.ErrorCode);
        Assert.Equal(1, entry.Attempts); Assert.Equal(1, app.WriteCalls);
        using var alert = await ReceiveAlert(receiver, "sync_failed"); var root = alert.RootElement;
        Assert.Equal(3, root.GetProperty("level").GetInt32());
        Assert.Equal("unisender_" + code, root.GetProperty("_ErrorCode").GetString());
        Assert.Equal("subscribe", root.GetProperty("_ApiMethod").GetString());
        Assert.Equal(uuid.ToString(), root.GetProperty("_EventUuid").GetString());
        Assert.DoesNotContain("private-api-key", root.ToString()); Assert.DoesNotContain("fixture@example.test", root.ToString());
    }
    private static async Task<JsonDocument> ReceiveAlert(UdpClient receiver, string alertCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await ReceiveMatching(receiver, root => root.TryGetProperty("_AlertCode", out var code) && code.GetString() == alertCode, timeout.Token);
    }

    private static async Task<JsonDocument> ReceiveMatching(UdpClient receiver, Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = (await receiver.ReceiveAsync(cancellationToken)).Buffer;
            using var bytes = new MemoryStream(packet);
            using Stream stream = packet is [0x1f, 0x8b, ..] ? new GZipStream(bytes, CompressionMode.Decompress) :
                packet[0] == 0x78 ? new ZLibStream(bytes, CompressionMode.Decompress) : bytes;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var document = JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken));
            if (predicate(document.RootElement)) return document;
            document.Dispose();
        }
    }
    [Fact] public async Task SerilogSendsGelfWithApplicationAndEventContextWithoutHttpSecrets()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var configuration = new LoggerConfiguration();
        ServiceLogging.Configure(configuration, new Secrets
        {
            GraylogAddress = "127.0.0.1", GraylogPort = port,
            GraylogApplicationName = "sync-tests", GraylogSource = "loopback-tests"
        });
        using var logger = configuration.CreateLogger();
        logger.Debug("Webhook diagnostic at Debug level");
        logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics").Information("request /webhooks/megaplan/private-hook-secret");
        logger.ForContext("SourceContext", "System.Net.Http.HttpClient.Unisender").Information("key=private-api-key");
        var uuid = Guid.NewGuid();
        logger.ForContext("EventUuid", uuid).ForContext("ContractorId", "10").Information("Webhook processed: {Status}", "Synced");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packet = (await receiver.ReceiveAsync(timeout.Token)).Buffer;
        using var bytes = new MemoryStream(packet);
        using Stream stream = packet is [0x1f, 0x8b, ..] ? new GZipStream(bytes, CompressionMode.Decompress) :
            packet[0] == 0x78 ? new ZLibStream(bytes, CompressionMode.Decompress) : bytes;
        using var reader = new StreamReader(stream, Encoding.UTF8); var json = await reader.ReadToEndAsync(timeout.Token);
        Assert.DoesNotContain("Webhook diagnostic at Debug level", json);
        Assert.DoesNotContain("private-hook-secret", json); Assert.DoesNotContain("private-api-key", json);
        using var message = JsonDocument.Parse(json); var root = message.RootElement;
        Assert.Equal("sync-tests", root.GetProperty("_application_name").GetString());
        Assert.Equal("loopback-tests", root.GetProperty("host").GetString());
        Assert.Equal(uuid.ToString(), root.GetProperty("_EventUuid").GetString());
        Assert.Equal("10", root.GetProperty("_ContractorId").GetString());
        Assert.False(receiver.Available > 0);
    }
    [Fact] public void UnconfiguredGraylogKeepsConsoleLoggingAvailable()
    {
        var configuration = new LoggerConfiguration(); ServiceLogging.Configure(configuration, new Secrets());
        using var logger = configuration.CreateLogger(); logger.Information("Local console logging is available");
    }
}
