using System.Net;
using System.Text;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class ContactTagsTests
{
    private const string TaskId = "89b86cb7-7d83-4d10-b977-835685050cfe";
    private static UnisenderClient Client(string csv, Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null)
    {
        var options = Options.Create(new IntegrationOptions { Sync = new() { RateLimits = new()
        { GeneralPerMinute = 600000, ExportPerMinute = 600000 } } });
        return new(new HttpClient(new Handler(request =>
        {
            if (intercept?.Invoke(request) is { } response) return response;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("usdata.s3.unisender.ru", request.RequestUri!.Host);
                Assert.Null(request.Headers.Authorization); Assert.Null(request.Content);
                Assert.DoesNotContain("fixture-key", request.RequestUri.ToString());
                return new(HttpStatusCode.OK) { Content = new StringContent(csv, Encoding.UTF8, "text/csv") };
            }
            var form = Scenario.ParseForm(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("fixture-key", form["api_key"]); Assert.DoesNotContain("api_key", request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("/async/exportContacts", StringComparison.Ordinal))
            {
                Assert.Equal("a@example.test", form["email"]); Assert.Equal(5, form.Count);
                Assert.Equal("email", form["field_names[0]"]); Assert.Equal("tags", form["field_names[1]"]);
                return Scenario.Json(new { result = new { task_uuid = TaskId, status = "new" } });
            }
            Assert.EndsWith("/async/getTaskResult", request.RequestUri.AbsolutePath);
            Assert.Equal(TaskId, form["task_uuid"]);
            return Scenario.Json(new { result = new { task_uuid = TaskId, task_type = "exportContacts", status = "completed",
                file_to_download = "https://usdata.s3.unisender.ru/one.csv?signature=fixture" } });
        })), options, Options.Create(new Secrets { UnisenderApiKey = "fixture-key" }), new ApiRateLimiter(options));
    }
    [Theory]
    [InlineData("email,tags\r\na@example.test,\"third,second,second\"\r\n", "second|third")]
    [InlineData("\uFEFFtags,email\n\"Приветствие\",a@example.test\n", "Приветствие")]
    [InlineData("email,tags\r\na@example.test,\r\n", "")]
    [InlineData("email,tags\r\na@example.test,\"A \"\"quote\"\"\"\r\n", "A \"quote\"")]
    public async Task ReadsOnlyRequestedContactAndParsesCsvWithoutAssumingColumnOrder(string csv, string expected)
    {
        var tags = await Client(csv).GetContactTagsAsync("a@example.test", default);
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), tags);
    }
    [Theory]
    [InlineData("email,tags\r\n", "unisender_export_contact_mismatch")]
    [InlineData("email,tags\r\nb@example.test,second\r\n", "unisender_export_contact_mismatch")]
    [InlineData("email,tags\r\na@example.test,second\r\na@example.test,third\r\n", "unisender_export_contact_mismatch")]
    [InlineData("email,tags,Name\r\na@example.test,second,Alice\r\n", "unisender_export_columns_unverified")]
    [InlineData("email,email\r\na@example.test,a@example.test\r\n", "unisender_export_columns_unverified")]
    [InlineData("email,tags\r\na@example.test,\"second\r\n", "unisender_export_invalid_csv")]
    [InlineData("email,tags\r\na@example.test,\"second,,third\"\r\n", "unisender_export_tags_unverified")]
    public async Task IncompleteOrMismatchedExportCannotBecomeAnEmptyTagSet(string csv, string code)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<SyncException>(() => Client(csv).GetContactTagsAsync("a@example.test", default))).Code);
    }
    [Theory]
    [InlineData("http://usdata.s3.unisender.ru/file.csv")]
    [InlineData("https://localhost/file.csv")]
    [InlineData("https://usdata.s3.unisender.ru.attacker.test/file.csv")]
    [InlineData("https://user:secret@usdata.s3.unisender.ru/file.csv")]
    public async Task UntrustedStorageUrlIsRejectedBeforeDownload(string url)
    {
        var client = Client("", request => request.RequestUri!.AbsolutePath.EndsWith("/getTaskResult", StringComparison.Ordinal)
            ? Scenario.Json(new { result = new { task_uuid = TaskId, status = "completed", file_to_download = url } }) : null);
        Assert.Equal("unisender_export_download_url_unverified", (await Assert.ThrowsAsync<SyncException>(() => client.GetContactTagsAsync("a@example.test", default))).Code);
    }
    [Fact] public async Task WrongTaskResponseCannotBeUsedAsCurrentContact()
    {
        var client = Client("", request => request.RequestUri!.AbsolutePath.EndsWith("/getTaskResult", StringComparison.Ordinal)
            ? Scenario.Json(new { result = new { task_uuid = Guid.NewGuid(), status = "completed" } }) : null);
        Assert.Equal("unisender_export_task_unverified", (await Assert.ThrowsAsync<SyncException>(() => client.GetContactTagsAsync("a@example.test", default))).Code);
    }
    [Theory] [InlineData("exportContacts")] [InlineData("export_contacts")]
    public async Task DocumentedAndObservedExportTaskTypesAreAccepted(string type)
    {
        var client = Client("email,tags\r\na@example.test,second\r\n", request => request.RequestUri!.AbsolutePath.EndsWith("/getTaskResult", StringComparison.Ordinal)
            ? Scenario.Json(new { result = new { task_uuid = TaskId, task_type = type, status = "completed", file_to_download = "https://usdata.s3.unisender.ru/one.csv" } }) : null);
        Assert.Equal(["second"], await client.GetContactTagsAsync("a@example.test", default));
    }
    [Fact] public async Task OversizedExportIsRejected()
    {
        var client = Client(new string('x', 65537));
        Assert.Equal("unisender_export_too_large", (await Assert.ThrowsAsync<SyncException>(() => client.GetContactTagsAsync("a@example.test", default))).Code);
    }
    [Fact] public async Task ExportHasItsOwnRateBudget()
    {
        var options = Options.Create(new IntegrationOptions { Sync = new() { RateLimits = new() { GeneralPerMinute = 600000 } } });
        var limiter = new ApiRateLimiter(options); await limiter.WaitAsync("async/exportContacts", default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync("async/exportContacts", cancel.Token));
    }
    [Theory] [InlineData("")] [InlineData(" ")] [InlineData("not-an-email")]
    public async Task MissingEmailCannotStartAnUnfilteredExport(string email)
    {
        var client = Client("", _ => throw new InvalidOperationException("No request may be sent"));
        Assert.Equal("unisender_export_email_invalid", (await Assert.ThrowsAsync<SyncException>(() => client.GetContactTagsAsync(email, default))).Code);
    }
}
