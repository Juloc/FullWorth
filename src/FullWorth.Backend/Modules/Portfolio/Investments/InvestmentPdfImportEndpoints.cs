using FullWorth.Backend.Validation;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record BrokerPdfTrade(
    string TradeDate,
    string TradeType,
    string? SettlementDate,
    string? SecurityName,
    string? Isin,
    string? Wkn,
    string? Ticker,
    string? Quantity,
    string? Price,
    string? GrossAmount,
    string Amount,
    string Currency,
    string Fees,
    string Taxes,
    string WithholdingTax,
    string ExternalKey);

public sealed record BrokerPdfParseResult(
    string Broker,
    decimal Confidence,
    BrokerPdfTrade? Trade,
    IReadOnlyList<string> Warnings);

public static class InvestmentPdfImportEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;
    private const int MaxExtractedTextChars = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapInvestmentPdfImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/investment-import/pdf/detect", Detect)
            .WithTags("Investments", "Import");
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        SpaceAccess access,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await access.HasCapabilityAsync(userId, fullWorthSpaceId, "investments.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Expected multipart/form-data." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "No PDF uploaded." });
        if (file.Length > MaxUploadBytes)
            return Results.BadRequest(new { error = "Maximum PDF size is 25 MB." });
        if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Only PDF files are supported here." });

        byte[] bytes;
        await using (var memory = new MemoryStream(checked((int)file.Length)))
        {
            await file.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return Results.BadRequest(new { error = "The uploaded file is not a valid PDF." });

        string text;
        try
        {
            text = await PdfTextExtractor.ExtractAsync(bytes, MaxExtractedTextChars, ct);
        }
        catch (FileNotFoundException)
        {
            return Results.Problem("PDF extraction is unavailable on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { error = "The PDF contains no readable text. Scanned/image-only broker documents are not imported automatically yet." });

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var parsed = BrokerPdfTradeParser.Parse(text, $"broker-pdf:{sha}");
        if (parsed.Trade is null)
            return Results.BadRequest(new
            {
                error = "No investment transaction could be recognized safely in this PDF.",
                broker = parsed.Broker,
                warnings = parsed.Warnings
            });

        var row = parsed.Trade;
        var normalizedRows = new[]
        {
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TradeDate"] = row.TradeDate,
                ["TradeType"] = row.TradeType,
                ["SettlementDate"] = row.SettlementDate,
                ["SecurityName"] = row.SecurityName,
                ["ISIN"] = row.Isin,
                ["WKN"] = row.Wkn,
                ["Ticker"] = row.Ticker,
                ["Quantity"] = row.Quantity,
                ["Price"] = row.Price,
                ["GrossAmount"] = row.GrossAmount,
                ["Amount"] = row.Amount,
                ["Currency"] = row.Currency,
                ["Fees"] = row.Fees,
                ["Taxes"] = row.Taxes,
                ["WithholdingTax"] = row.WithholdingTax,
                ["ExternalKey"] = row.ExternalKey
            }
        };

        return Results.Ok(new
        {
            fileName = Path.GetFileName(file.FileName),
            broker = parsed.Broker,
            confidence = parsed.Confidence,
            warnings = parsed.Warnings,
            rowCount = normalizedRows.Length,
            normalizedRows,
            suggestedMapping = new
            {
                tradeDate = "TradeDate",
                tradeType = "TradeType",
                settlementDate = "SettlementDate",
                securityName = "SecurityName",
                isin = "ISIN",
                wkn = "WKN",
                ticker = "Ticker",
                quantity = "Quantity",
                price = "Price",
                grossAmount = "GrossAmount",
                amount = "Amount",
                currency = "Currency",
                fees = "Fees",
                taxes = "Taxes",
                withholdingTax = "WithholdingTax",
                externalKey = "ExternalKey"
            }
        });
    }
}
