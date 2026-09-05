using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record CodexReceiptInput(Guid Id, string FileName, string ContentType, byte[] Content);
public sealed record CodexReceiptSource(Guid Id, Guid FileId, int SortOrder, int? PageNumber);

public sealed class CodexReceiptBridgeClient(
    IConfiguration configuration,
    IHttpClientFactory clients,
    ILogger<CodexReceiptBridgeClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Compatibility overload used by the existing single-file/debug path.
    public Task<CodexReceiptScanEnvelope?> TryScanAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        byte[] content,
        string contentType,
        string fileName,
        IReadOnlyList<string> categories,
        CancellationToken ct)
    {
        var fileId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        return TryScanAsync(
            userId,
            fullWorthSpaceId,
            [new CodexReceiptInput(fileId, fileName, contentType, content)],
            [new CodexReceiptSource(sourceId, fileId, 0, string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ? 1 : null)],
            categories,
            ct);
    }

    public async Task<CodexReceiptScanEnvelope?> TryScanAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        IReadOnlyList<CodexReceiptInput> files,
        IReadOnlyList<CodexReceiptSource> sources,
        IReadOnlyList<string> categories,
        CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("CodexTest:Enabled")) return null;
        if (files.Count == 0 || sources.Count == 0) return null;
        // Never silently omit one source. If a format is not supported by Codex, the complete set falls
        // back to local OCR/manual review so the user's receipt is not partially interpreted as complete.
        if (files.Any(file => string.Equals(file.ContentType, "image/heic", StringComparison.OrdinalIgnoreCase))) return null;

        var fileIds = files.Select(x => x.Id).ToHashSet();
        if (sources.Any(source => !fileIds.Contains(source.FileId))) return null;

        var key = configuration["CodexTest:BridgeKey"];
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = (configuration["CodexTest:BaseUrl"] ?? "http://fullworth-codex:8080").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttp)
            return null;

        var payload = JsonSerializer.Serialize(new
        {
            files = files.Select(file => new
            {
                id = file.Id,
                fileName = Path.GetFileName(file.FileName),
                contentType = file.ContentType,
                dataBase64 = Convert.ToBase64String(file.Content)
            }),
            sources = sources.OrderBy(source => source.SortOrder).Select(source => new
            {
                id = source.Id,
                fileId = source.FileId,
                sortOrder = source.SortOrder,
                pageNumber = source.PageNumber
            }),
            model = (string?)null,
            categories
        }, Json);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/scan"));
            message.Headers.Add("X-FullWorth-Internal-Key", key);
            message.Headers.Add("X-FullWorth-Codex-Scope", BridgeScope(userId, fullWorthSpaceId));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            var envelope = JsonSerializer.Deserialize<CodexReceiptScanEnvelope>(body, Json);
            if (envelope?.Success != true || envelope.Result is null)
            {
                logger.LogInformation("Codex receipt scan unavailable for queued job: {Error}", envelope?.Error ?? envelope?.ParseError ?? response.StatusCode.ToString());
                return null;
            }
            return envelope;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Codex receipt scan failed; queued receipt will use local OCR fallback");
            return null;
        }
    }

    private static string BridgeScope(Guid userId, Guid fullWorthSpaceId)
    {
        _ = fullWorthSpaceId; // authorization stays space-scoped; AI login itself is user-scoped.
        var input = Encoding.UTF8.GetBytes($"fullworth-ai:{userId:N}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}

public sealed class CodexReceiptScanEnvelope
{
    public bool Success { get; set; }
    public string? RequestId { get; set; }
    public string? Error { get; set; }
    public string? ParseError { get; set; }
    public CodexReceiptResult? Result { get; set; }
}

public sealed class CodexReceiptResult
{
    public CodexMerchant Merchant { get; set; } = new();
    public CodexReceiptMeta Receipt { get; set; } = new();
    public CodexReceiptTotals Totals { get; set; } = new();
    public CodexPayment Payment { get; set; } = new();
    public List<CodexReceiptItem> Items { get; set; } = [];
    public List<CodexReceiptDiscount> Discounts { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public decimal Confidence { get; set; }
}

public sealed class CodexMerchant
{
    public string? Name { get; set; }
}

public sealed class CodexReceiptMeta
{
    public string? Date { get; set; }
    public string? Time { get; set; }
    public string? Currency { get; set; }
    public string? ReceiptNumber { get; set; }
}

public sealed class CodexReceiptTotals
{
    public decimal? Subtotal { get; set; }
    /// <summary>Positive amount saved across all recognized discounts.</summary>
    public decimal? Discounts { get; set; }
    /// <summary>Positive deposit/Pfand added to the payable total.</summary>
    public decimal? Deposits { get; set; }
    public decimal? Tax { get; set; }
    public decimal? Rounding { get; set; }
    public decimal? Total { get; set; }
}

public sealed class CodexPayment
{
    public string? Method { get; set; }
}

public sealed class CodexReceiptItem
{
    public string? RawName { get; set; }
    public string? Name { get; set; }
    public string? Brand { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    /// <summary>Effective charged unit price after item discounts.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Reference/original unit price only when explicitly visible.</summary>
    public decimal? OriginalUnitPrice { get; set; }
    /// <summary>Effective merchandise line total after item discounts, excluding deposit.</summary>
    public decimal? TotalPrice { get; set; }
    /// <summary>Positive item-level amount saved.</summary>
    public decimal? DiscountAmount { get; set; }
    public string? DiscountLabel { get; set; }
    public decimal? Deposit { get; set; }
    public string? CategorySuggestion { get; set; }
    public decimal Confidence { get; set; }
    // Ordered logical source indexes (0-based) whose pixels/text support this item. The first source is
    // enough for simple receipts; overlap items may legitimately reference two adjacent sources.
    public List<int> SourceIndexes { get; set; } = [];
}

public sealed class CodexReceiptDiscount
{
    public string Type { get; set; } = "other";
    public string? Label { get; set; }
    /// <summary>Positive amount saved.</summary>
    public decimal Amount { get; set; }
    public decimal? Percentage { get; set; }
    public string? CouponCode { get; set; }
    public string? RawText { get; set; }
    /// <summary>Optional zero-based index into the returned Items array.</summary>
    public int? ItemIndex { get; set; }
    public decimal Confidence { get; set; }
    public List<int> SourceIndexes { get; set; } = [];
}
