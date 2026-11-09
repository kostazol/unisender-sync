using System.Net;
using System.Text.Json;
using MegaplanUnisender.Core;
using MegaplanUnisender.Infrastructure;
using Microsoft.Extensions.Options;

namespace MegaplanUnisender.UnitTests;

public sealed class ContractorPollingApiTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 9, 13, 0, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = From.AddMinutes(1);

    [Fact]
    public async Task PollUsesTypedCursorAndKeepsOnlyRowsInTheRequestedWindow()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v3/contractor", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
            using var document = JsonDocument.Parse(Uri.UnescapeDataString(request.RequestUri.Query[1..]));
            var query = document.RootElement;
            Assert.True(query.GetProperty("onlyRequestedFields").GetBoolean());
            Assert.Contains(query.GetProperty("fields").EnumerateArray(), field => field.GetString() == "timeUpdated");
            var sort = Assert.Single(query.GetProperty("sortBy").EnumerateArray());
            Assert.Equal("SortField", sort.GetProperty("contentType").GetString());
            Assert.Equal("id", sort.GetProperty("fieldName").GetString());
            Assert.True(sort.GetProperty("desc").GetBoolean());
            AssertWindowFilter(query);

            if (++calls == 1)
            {
                Assert.False(query.TryGetProperty("pageAfter", out _));
                return Page(true,
                    Row("105", "ContractorHuman", "2026-11-09T13:01:15Z"),
                    Row("104", "ContractorCompany", "2026-11-09T13:00:15Z"),
                    Row("103", "ContractorCompany", "2026-11-09T13:00:14Z"));
            }

            var cursor = query.GetProperty("pageAfter");
            Assert.Equal("103", cursor.GetProperty("id").GetString());
            Assert.Equal("ContractorCompany", cursor.GetProperty("contentType").GetString());
            return Page(false, Row("102", "ContractorHuman", "2026-11-09T20:00:30+07:00"));
        }));

        var updates = await Client(http).GetContractorUpdatesAsync(From, Until, default);

        Assert.Equal(2, calls);
        Assert.Collection(updates,
            row => Assert.Equal(("104", "ContractorCompany"), (row.Id, row.Model)),
            row => Assert.Equal(("102", "ContractorHuman"), (row.Id, row.Model)));
    }

    [Fact]
    public async Task PollRejectsRepeatedCursorInsteadOfReturningPartialResults()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Assert.InRange(++calls, 1, 2);
            return Page(true, Row("104", "ContractorCompany", "2026-11-09T13:00:30Z"));
        }));

        var error = await Assert.ThrowsAsync<SyncException>(() => Client(http).GetContractorUpdatesAsync(From, Until, default));

        Assert.Equal("megaplan_updates_pagination_stalled", error.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PollReportsFailedContinuationInsteadOfReturningPartialResults()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => ++calls == 1
            ? Page(true, Row("104", "ContractorCompany", "2026-11-09T13:00:30Z"))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var error = await Assert.ThrowsAsync<SyncException>(() => Client(http).GetContractorUpdatesAsync(From, Until, default));

        Assert.Equal("megaplan_http_503", error.Code);
        Assert.Equal(2, calls);
    }

    private static void AssertWindowFilter(JsonElement query)
    {
        var filter = query.GetProperty("filter");
        Assert.Equal("CrmFilter", filter.GetProperty("contentType").GetString());
        var config = filter.GetProperty("config");
        Assert.Equal("FilterConfig", config.GetProperty("contentType").GetString());
        var group = config.GetProperty("termGroup");
        Assert.Equal("FilterTermGroup", group.GetProperty("contentType").GetString());
        Assert.Equal("and", group.GetProperty("join").GetString());
        var term = Assert.Single(group.GetProperty("terms").EnumerateArray());
        Assert.Equal("FilterTermDate", term.GetProperty("contentType").GetString());
        Assert.Equal("timeUpdated", term.GetProperty("field").GetString());
        Assert.Equal("equals", term.GetProperty("comparison").GetString());
        var interval = term.GetProperty("value");
        Assert.Equal("IntervalTime", interval.GetProperty("contentType").GetString());
        AssertDate(new DateTimeOffset(2026, 11, 9, 13, 0, 0, TimeSpan.Zero), interval.GetProperty("from"));
        AssertDate(new DateTimeOffset(2026, 11, 9, 13, 2, 0, TimeSpan.Zero), interval.GetProperty("to"));
    }

    private static void AssertDate(DateTimeOffset expected, JsonElement date)
    {
        Assert.Equal("DateTime", date.GetProperty("contentType").GetString());
        Assert.Equal(expected, date.GetProperty("value").GetDateTimeOffset());
    }

    private static MegaplanClient Client(HttpClient http)
    {
        var options = Options.Create(new IntegrationOptions
        {
            Megaplan = new() { BaseUrl = "https://crm.example.test" },
            Sync = new() { RateLimits = new() { MegaplanPerMinute = 1200 } }
        });
        return new(http, options, Options.Create(new Secrets { MegaplanApiToken = "fixture-token" }), new ApiRateLimiter(options));
    }

    private static HttpResponseMessage Page(bool hasMoreNext, params object[] rows) =>
        Scenario.Json(new { meta = new { status = 200, pagination = new { count = 4, limit = 100, hasMoreNext } }, data = rows });

    private static object Row(string id, string model, string updatedAt) =>
        new { id, contentType = model, timeUpdated = new { contentType = "DateTime", value = updatedAt } };
}
