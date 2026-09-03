using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FullWorth.Web.Modules.Purchases;

public static class ShareReceiptEndpoints
{
    public static IEndpointRouteBuilder MapShareReceiptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/share/receipt", async (HttpContext context, CancellationToken ct) =>
        {
            if (!SafeNavigation(context.Request)) return Results.BadRequest();
            if (!TryAuthUser(context, out var authUserId)) return Results.Unauthorized();
            if (!context.Request.HasFormContentType) return Results.BadRequest("multipart/form-data is required.");

            var form = await context.Request.ReadFormAsync(ct);
            var files = form.Files.GetFiles("receipt");
            if (files.Count == 0) files = form.Files.ToList();
            var stored = await SharedReceiptInbox.StoreAsync(authUserId, files, ct);
            if (stored.Error is not null) return Results.BadRequest(stored.Error);
            return Results.Redirect($"/share/receipt/{stored.Token}");
        }).RequireAuthorization();

        app.MapGet("/share/receipt/{token}", async (string token, HttpContext context, IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (!TryAuthUser(context, out var authUserId)) return Results.Unauthorized();
            var entry = await SharedReceiptInbox.ReadAsync(token, authUserId, ct);
            if (entry is null) return Results.NotFound();
            var spaces = await LoadSpacesAsync(factory.CreateClient("backend"), ct);
            if (spaces.Count == 0) return Results.Content(Page("FullWorth", "No FullWorth Space is available for this account.", null), "text/html; charset=utf-8");

            var options = string.Join("", spaces.Select(space =>
                $"<option value=\"{space.Id:D}\">{HtmlEncoder.Default.Encode(space.Name)} ({HtmlEncoder.Default.Encode(space.BaseCurrency)})</option>"));
            var fileSummary = entry.Files.Count == 1
                ? HtmlEncoder.Default.Encode(entry.Files[0].OriginalFileName)
                : $"{entry.Files.Count} files";
            var body = $"""
                <h1>Import shared receipt</h1>
                <p>{fileSummary}</p>
                <form method="post" action="/share/receipt/{HtmlEncoder.Default.Encode(token)}/import">
                  <label>FullWorth Space<select name="fullWorthSpaceId">{options}</select></label>
                  <button type="submit">Import and scan</button>
                </form>
                {(spaces.Count == 1 ? "<script>document.querySelector('form').requestSubmit();</script>" : string.Empty)}
                """;
            return Results.Content(Page("Import receipt", body, null, bodyIsHtml: true), "text/html; charset=utf-8");
        }).RequireAuthorization();

        app.MapPost("/share/receipt/{token}/import", async (string token, HttpContext context, IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (!SafeNavigation(context.Request)) return Results.BadRequest();
            if (!TryAuthUser(context, out var authUserId)) return Results.Unauthorized();
            var entry = await SharedReceiptInbox.ReadAsync(token, authUserId, ct);
            if (entry is null) return Results.NotFound();
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["fullWorthSpaceId"].ToString(), out var fullWorthSpaceId)) return Results.BadRequest("FullWorth Space is required.");

            var client = factory.CreateClient("backend");
            var spaces = await LoadSpacesAsync(client, ct);
            var space = spaces.SingleOrDefault(x => x.Id == fullWorthSpaceId);
            if (space is null) return Results.NotFound();

            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(space.BaseCurrency), "currency");
            var clientJobId = Guid.NewGuid();
            multipart.Add(new StringContent(clientJobId.ToString("D")), "clientJobId");
            var streams = new List<Stream>();
            try
            {
                foreach (var file in entry.Files.OrderBy(x => x.Index))
                {
                    var stream = File.OpenRead(file.AbsolutePath);
                    streams.Add(stream);
                    var content = new StreamContent(stream);
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                    multipart.Add(content, "receipt", file.OriginalFileName);
                    multipart.Add(new StringContent(Guid.NewGuid().ToString("D")), "sourceId");
                }

                using var create = await client.PostAsync($"api/purchases/receipt-scan/jobs?fullWorthSpaceId={fullWorthSpaceId:D}", multipart, ct);
                if (!create.IsSuccessStatusCode)
                    return Results.Content(Page("Import failed", "The receipt could not be imported. Open FullWorth and try again.", $"/share/receipt/{token}"), "text/html; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
                using var json = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
                var jobId = json.RootElement.TryGetProperty("id", out var idNode) && idNode.TryGetGuid(out var parsed) ? parsed : clientJobId;

                using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"api/purchases/receipt-scan/jobs/{jobId:D}/start?fullWorthSpaceId={fullWorthSpaceId:D}");
                using var start = await client.SendAsync(startRequest, ct);
                if (!start.IsSuccessStatusCode)
                    return Results.Redirect($"/purchases?sharedReceiptJob={jobId:D}");

                await SharedReceiptInbox.DeleteAsync(entry, ct);
                return Results.Redirect($"/purchases?sharedReceiptJob={jobId:D}");
            }
            finally
            {
                foreach (var stream in streams) await stream.DisposeAsync();
            }
        }).RequireAuthorization();

        return app;
    }

    private static bool SafeNavigation(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        return !string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAuthUser(HttpContext context, out Guid id) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out id);

    private static async Task<List<SpaceRow>> LoadSpacesAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("api/fullworth-spaces", ct);
        if (!response.IsSuccessStatusCode) return [];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        var rows = new List<SpaceRow>();
        foreach (var node in json.RootElement.EnumerateArray())
        {
            if (!node.TryGetProperty("id", out var idNode) || !idNode.TryGetGuid(out var id)) continue;
            var name = node.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            var currency = node.TryGetProperty("baseCurrency", out var currencyNode) ? currencyNode.GetString() : null;
            rows.Add(new SpaceRow(id, string.IsNullOrWhiteSpace(name) ? "FullWorth Space" : name!, NormalizeCurrency(currency)));
        }
        return rows;
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = string.IsNullOrWhiteSpace(value) ? "EUR" : value.Trim().ToUpperInvariant();
        return currency.Length == 3 && currency.All(x => x is >= 'A' and <= 'Z') ? currency : "EUR";
    }

    private static string Page(string title, string body, string? back, bool bodyIsHtml = false)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var content = bodyIsHtml ? body : $"<p>{HtmlEncoder.Default.Encode(body)}</p>";
        var backLink = string.IsNullOrWhiteSpace(back) ? "" : $"<p><a href=\"{HtmlEncoder.Default.Encode(back)}\">Back</a></p>";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{encodedTitle}} · FullWorth</title><style>body{font:16px system-ui,sans-serif;max-width:560px;margin:48px auto;padding:0 20px;color:#202124;background:#f5f6f7}main{background:#fff;border:1px solid #dfe1e5;border-radius:16px;padding:24px}label{display:grid;gap:8px;margin:18px 0}select,button{font:inherit;padding:11px 12px;border-radius:10px;border:1px solid #c7c9cc}button{cursor:pointer;background:#202124;color:#fff}</style></head>
            <body><main>{{content}}{{backLink}}</main></body></html>
            """;
    }

    private sealed record SpaceRow(Guid Id, string Name, string BaseCurrency);
}

internal static class SharedReceiptInbox
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "fullworth-share-receipts");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    public static async Task<StoreResult> StoreAsync(Guid authUserId, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        await CleanupExpiredAsync(ct);
        if (files.Count == 0) return new(null, "At least one receipt file is required.");
        if (files.Count > 24) return new(null, "Too many shared receipt files.");
        if (files.Any(x => x.Length <= 0 || x.Length > 20L * 1024 * 1024)) return new(null, "A shared receipt file is empty or too large.");
        if (files.Sum(x => x.Length) > 60L * 1024 * 1024) return new(null, "The shared receipt set is too large.");

        var token = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Root, token);
        Directory.CreateDirectory(directory);
        var metadata = new List<StoredFile>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".heic" and not ".pdf")
                    return await FailAsync(directory, "Unsupported receipt file type.");
                var header = new byte[16];
                int read;
                await using (var probe = file.OpenReadStream()) read = await probe.ReadAtLeastAsync(header, header.Length, false, ct);
                if (!MatchesSignature(header.AsSpan(0, read), ext)) return await FailAsync(directory, "Receipt file content does not match its type.");

                var path = Path.Combine(directory, $"{index:D2}{ext}");
                await using (var target = File.Create(path)) await file.CopyToAsync(target, ct);
                metadata.Add(new StoredFile(index, SafeFileName(file.FileName), ContentType(ext), file.Length, path));
            }

            var entry = new StoredEntry(token, authUserId, DateTimeOffset.UtcNow.Add(Lifetime), metadata);
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(entry), ct);
            return new(token, null);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public static async Task<StoredEntry?> ReadAsync(string token, Guid authUserId, CancellationToken ct)
    {
        await CleanupExpiredAsync(ct);
        if (!ValidToken(token)) return null;
        var directory = Path.Combine(Root, token);
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<StoredEntry>(await File.ReadAllTextAsync(metadataPath, ct));
            if (entry is null || entry.AuthUserId != authUserId || entry.ExpiresAt <= DateTimeOffset.UtcNow) return null;
            if (entry.Files.Any(file => !SafeWithin(directory, file.AbsolutePath) || !File.Exists(file.AbsolutePath))) return null;
            return entry;
        }
        catch (JsonException) { return null; }
    }

    public static Task DeleteAsync(StoredEntry entry, CancellationToken ct)
    {
        if (ValidToken(entry.Token)) TryDeleteDirectory(Path.Combine(Root, entry.Token));
        return Task.CompletedTask;
    }

    private static async Task CleanupExpiredAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            ct.ThrowIfCancellationRequested();
            var metadata = Path.Combine(directory, "metadata.json");
            try
            {
                if (!File.Exists(metadata)) { TryDeleteDirectory(directory); continue; }
                var entry = JsonSerializer.Deserialize<StoredEntry>(await File.ReadAllTextAsync(metadata, ct));
                if (entry is null || entry.ExpiresAt <= DateTimeOffset.UtcNow) TryDeleteDirectory(directory);
            }
            catch { TryDeleteDirectory(directory); }
        }
    }

    private static bool ValidToken(string? token) => token is { Length: 32 } && token.All(Uri.IsHexDigit);
    private static bool SafeWithin(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.Ordinal);
    }
    private static string SafeFileName(string? value)
    {
        var name = Path.GetFileName(value ?? "receipt");
        if (name.Length > 180) name = name[..180];
        return string.IsNullOrWhiteSpace(name) ? "receipt" : name;
    }
    private static string ContentType(string ext) => ext switch
    { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".heic" => "image/heic", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    private static bool MatchesSignature(ReadOnlySpan<byte> bytes, string ext) => ext switch
    {
        ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ".pdf" => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8),
        ".webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        ".heic" => bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8),
        _ => false
    };
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static async Task<StoreResult> FailAsync(string directory, string error) { TryDeleteDirectory(directory); await Task.CompletedTask; return new(null, error); }

    internal sealed record StoreResult(string? Token, string? Error);
    internal sealed record StoredEntry(string Token, Guid AuthUserId, DateTimeOffset ExpiresAt, IReadOnlyList<StoredFile> Files);
    internal sealed record StoredFile(int Index, string OriginalFileName, string ContentType, long SizeBytes, string AbsolutePath);
}