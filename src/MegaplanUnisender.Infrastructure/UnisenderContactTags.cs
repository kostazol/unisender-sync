using System.Text;
using MegaplanUnisender.Core;
using Microsoft.VisualBasic.FileIO;

namespace MegaplanUnisender.Infrastructure;

public sealed partial class UnisenderClient
{
    public async Task<string[]> GetContactTagsAsync(string email, CancellationToken ct)
    {
        email = Canonical.Email(email) ?? throw new SyncException("unisender_export_email_invalid");
        // getContact does not expose tags. Export only this email, never a list or the address book.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await CallAsync("async/exportContacts", new()
            {
                ["email"] = email, ["field_names[0]"] = "email", ["field_names[1]"] = "tags"
            }, false, timeout.Token);
            if (!Guid.TryParse(result.Text("task_uuid"), out var taskId) || taskId == Guid.Empty) throw new SyncException("unisender_export_task_unverified");
            for (var poll = 0; ; poll++)
            {
                if (!Guid.TryParse(result.Text("task_uuid"), out var returnedId) || returnedId != taskId ||
                    (result.Text("task_type") is { } type && type is not ("exportContacts" or "export_contacts"))) throw new SyncException("unisender_export_task_unverified");
                var status = result.Text("status");
                if (status == "completed")
                    return ParseContactTags(await DownloadExportAsync(result.Text("file_to_download"), timeout.Token), email);
                if (status is not ("new" or "processing")) throw new SyncException("unisender_export_failed");
                if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                result = await CallAsync("async/getTaskResult", new() { ["task_uuid"] = taskId.ToString() }, false, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SyncException("unisender_export_timeout", transient: true); }
        catch (HttpRequestException) { throw new SyncException("unisender_export_download_network", transient: true); }
        catch (DecoderFallbackException) { throw new SyncException("unisender_export_invalid_csv"); }
    }

    private async Task<string> DownloadExportAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Fragment != "" ||
            !(uri.IdnHost.EndsWith(".s3.unisender.ru", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)))
            throw new SyncException("unisender_export_download_url_unverified");
        // The URL is signed by storage. No API key, authorization header or POST body is sent here.
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new SyncException("unisender_export_download_http_" + (int)response.StatusCode, transient: true);
        const int maxBytes = 65536;
        if (response.Content.Headers.ContentLength > maxBytes) throw new SyncException("unisender_export_too_large");
        using var buffer = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new SyncException("unisender_export_too_large");
            buffer.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private static string[] ParseContactTags(string csv, string email)
    {
        try
        {
            using var reader = new StringReader(csv.TrimStart('\uFEFF'));
            using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            var header = parser.ReadFields();
            if (header is null || header.Length != 2 || !header.Contains("email") || !header.Contains("tags"))
                throw new SyncException("unisender_export_columns_unverified");
            var row = parser.ReadFields();
            if (row is null || row.Length != header.Length || !parser.EndOfData ||
                !string.Equals(row[Array.IndexOf(header, "email")], email, StringComparison.OrdinalIgnoreCase))
                throw new SyncException("unisender_export_contact_mismatch", transient: true);
            var value = row[Array.IndexOf(header, "tags")];
            var tags = value.Length == 0 ? [] : value.Split(',');
            if (tags.Length > 10 || tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100))
                throw new SyncException("unisender_export_tags_unverified");
            return Canonical.Tags(tags);
        }
        catch (MalformedLineException) { throw new SyncException("unisender_export_invalid_csv"); }
    }
}
