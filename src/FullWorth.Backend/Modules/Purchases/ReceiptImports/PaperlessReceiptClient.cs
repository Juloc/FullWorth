using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public sealed class PaperlessReceiptClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ReceiptImportOptions> options)
{
    private readonly ReceiptImportOptions settings = options.Value;

    public async Task<(bool Success, string? ServerVersion, string? Error)> TestAsync(
        string baseUrl,
        string token,
        CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, token, "api/documents/?page_size=1");
            using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (false, null, $"Paperless returned HTTP {(int)response.StatusCode}.");
            var version = response.Headers.TryGetValues("X-Version", out var values) ? values.FirstOrDefault() : null;
            return (true, version, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, null, SafeError(ex));
        }
    }

    public async Task<PaperlessPreviewResult> PreviewAsync(
        string baseUrl,
        string token,
        PaperlessPreviewRequest filter,
        CancellationToken ct)
    {
        var requestedLimit = Math.Clamp(filter.Limit ?? settings.MaxBatchItems, 1, settings.MaxBatchItems);
        var pageSize = Math.Clamp(settings.PaperlessPageSize, 1, 1000);
        var result = new List<PaperlessDocumentSummary>();
        var next = BuildDocumentsUrl(filter, pageSize);
        var total = 0;

        while (!string.IsNullOrWhiteSpace(next) && result.Count < requestedLimit)
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, token, next);
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Paperless returned HTTP {(int)response.StatusCode} while listing documents.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (root.TryGetProperty("count", out var countNode) && countNode.TryGetInt32(out var parsedCount)) total = parsedCount;
            if (!root.TryGetProperty("results", out var resultsNode) || resultsNode.ValueKind != JsonValueKind.Array) break;

            foreach (var document in resultsNode.EnumerateArray())
            {
                if (result.Count >= requestedLimit) break;
                result.Add(ParseDocument(document));
            }

            next = ReadNextRelative(root, baseUrl);
        }

        return new PaperlessPreviewResult(total, result, total > result.Count);
    }

    public async Task<PaperlessFilterOptionsView> GetFilterOptionsAsync(
        string baseUrl,
        string token,
        CancellationToken ct)
    {
        var tags = await ReadNamedOptionsAsync(baseUrl, token, "api/tags/?page_size=1000&ordering=name", ct);
        var documentTypes = await ReadNamedOptionsAsync(baseUrl, token, "api/document_types/?page_size=1000&ordering=name", ct);
        var correspondents = await ReadNamedOptionsAsync(baseUrl, token, "api/correspondents/?page_size=1000&ordering=name", ct);
        var storagePaths = await ReadNamedOptionsAsync(baseUrl, token, "api/storage_paths/?page_size=1000&ordering=name", ct);
        var customFields = await ReadNamedOptionsAsync(baseUrl, token, "api/custom_fields/?page_size=1000&ordering=name", ct);

        return new PaperlessFilterOptionsView(tags, documentTypes, correspondents, storagePaths, customFields);
    }

    public async Task<PaperlessDocumentDownload> DownloadAsync(
        string baseUrl,
        string token,
        int documentId,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, token, $"api/documents/{documentId}/download/");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Paperless returned HTTP {(int)response.StatusCode} while downloading document {documentId}.");

        var length = response.Content.Headers.ContentLength;
        var maxBytes = 25L * 1024 * 1024;
        if (length is > 0 && length > maxBytes)
            throw new InvalidOperationException($"Paperless document {documentId} exceeds the import size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream(length is > 0 and <= int.MaxValue ? (int)length.Value : 0);
        var scratch = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(scratch, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                await buffer.DisposeAsync();
                throw new InvalidOperationException($"Paperless document {documentId} exceeds the import size limit.");
            }
            await buffer.WriteAsync(scratch.AsMemory(0, read), ct);
        }
        buffer.Position = 0;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"paperless-{documentId}{ExtensionFor(contentType)}";
        return new PaperlessDocumentDownload(documentId, fileName, contentType, buffer);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("PaperlessReceipts");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.PaperlessTimeoutSeconds, 5, 300));
        return await client.SendAsync(request, completion, ct);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string token, string relativeOrAbsolute)
    {
        var baseUri = NormalizeBaseUri(baseUrl);
        var target = Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute)
            ? RebaseToConfiguredServer(baseUri, absolute)
            : new Uri(baseUri, relativeOrAbsolute.TrimStart('/'));
        var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
        request.Headers.Accept.ParseAdd("application/json; version=10");
        return request;
    }

    public static Uri NormalizeBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new UriFormatException("Paperless base URL must be an absolute http/https URL.");
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    private static Uri RebaseToConfiguredServer(Uri baseUri, Uri next)
    {
        // Paperless commonly emits pagination links using its internal PAPERLESS_URL
        // (for example http://paperless:8000) even when FullWorth connects through
        // a reverse proxy. Never follow that advertised origin. Reuse only the
        // path/query and keep requests pinned to the configured server.
        var path = next.AbsolutePath.TrimStart('/');
        var configuredPrefix = baseUri.AbsolutePath.Trim('/');

        if (!string.IsNullOrWhiteSpace(configuredPrefix))
        {
            var prefix = configuredPrefix + "/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                path = path[prefix.Length..];
        }

        var relative = path + next.Query;
        return new Uri(baseUri, relative);
    }

    private static string BuildDocumentsUrl(PaperlessPreviewRequest filter, int pageSize)
    {
        var query = new Dictionary<string, string?>
        {
            ["page_size"] = pageSize.ToString(),
            ["ordering"] = "-created"
        };
        if (!string.IsNullOrWhiteSpace(filter.Query)) query["query"] = filter.Query.Trim();
        if (filter.DocumentTypeId.HasValue) query["document_type__id"] = filter.DocumentTypeId.Value.ToString();
        if (filter.CorrespondentId.HasValue) query["correspondent__id"] = filter.CorrespondentId.Value.ToString();
        if (filter.CreatedFrom.HasValue) query["created__date__gte"] = filter.CreatedFrom.Value.ToString("yyyy-MM-dd");
        if (filter.CreatedTo.HasValue) query["created__date__lte"] = filter.CreatedTo.Value.ToString("yyyy-MM-dd");
        if (filter.TagIds is { Count: > 0 }) query["tags__id__all"] = string.Join(',', filter.TagIds.Distinct());
        return QueryHelpers.AddQueryString("api/documents/", query!);
    }

    private static string? ReadNextRelative(JsonElement root, string baseUrl)
    {
        if (!root.TryGetProperty("next", out var nextNode) || nextNode.ValueKind == JsonValueKind.Null) return null;
        var next = nextNode.GetString();
        if (string.IsNullOrWhiteSpace(next)) return null;
        var baseUri = NormalizeBaseUri(baseUrl);
        if (!Uri.TryCreate(next, UriKind.Absolute, out var absolute)) return next;
        return RebaseToConfiguredServer(baseUri, absolute).ToString();
    }

    private async Task<IReadOnlyList<PaperlessFilterOption>> ReadNamedOptionsAsync(
        string baseUrl,
        string token,
        string firstPage,
        CancellationToken ct)
    {
        const int maxItems = 5000;
        var result = new List<PaperlessFilterOption>();
        var next = firstPage;

        while (!string.IsNullOrWhiteSpace(next) && result.Count < maxItems)
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, token, next);
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Paperless returned HTTP {(int)response.StatusCode} while loading filter options.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (!root.TryGetProperty("results", out var resultsNode) || resultsNode.ValueKind != JsonValueKind.Array) break;

            foreach (var item in resultsNode.EnumerateArray())
            {
                if (result.Count >= maxItems) break;
                if (!item.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id)) continue;
                var name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new PaperlessFilterOption(id, name.Trim()));
            }

            next = ReadNextRelative(root, baseUrl);
        }

        return result
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static PaperlessDocumentSummary ParseDocument(JsonElement document)
    {
        var id = document.GetProperty("id").GetInt32();
        var title = document.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? $"Document {id}" : $"Document {id}";
        DateOnly? created = null;
        if (document.TryGetProperty("created", out var createdNode) && DateOnly.TryParse(createdNode.GetString(), out var parsed)) created = parsed;
        int? documentType = ReadNullableInt(document, "document_type");
        int? correspondent = ReadNullableInt(document, "correspondent");
        var tags = new List<int>();
        if (document.TryGetProperty("tags", out var tagsNode) && tagsNode.ValueKind == JsonValueKind.Array)
            foreach (var tag in tagsNode.EnumerateArray()) if (tag.TryGetInt32(out var value)) tags.Add(value);
        var originalFileName = document.TryGetProperty("original_file_name", out var fileNode) ? fileNode.GetString() : null;
        return new PaperlessDocumentSummary(id, title, created, documentType, correspondent, tags, originalFileName);
    }

    private static int? ReadNullableInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var node) || node.ValueKind == JsonValueKind.Null) return null;
        return node.TryGetInt32(out var value) ? value : null;
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        _ => ".bin"
    };

    private static string SafeError(Exception ex) => ex switch
    {
        TaskCanceledException => "Paperless request timed out.",
        UriFormatException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => "Paperless could not be reached."
    };
}

public sealed record PaperlessDocumentDownload(
    int DocumentId,
    string FileName,
    string ContentType,
    MemoryStream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
