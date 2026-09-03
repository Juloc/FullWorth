using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

public static class InvestmentPdfOcrImportParityEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;
    private const int MaxPages = 12;
    private const int MaxOcrChars = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapInvestmentPdfOcrImportParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/investment-import/pdf/ocr-detect", Detect)
            .WithTags("Investments", "Import");
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "investments.manage", ct))
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

        IReadOnlyList<string> pages;
        try
        {
            pages = await BrokerPdfOcr.ExtractPagesAsync(bytes, MaxPages, MaxOcrChars, ct);
        }
        catch (FileNotFoundException)
        {
            return Results.Problem("PDF OCR is unavailable on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        if (pages.Count == 0 || pages.All(string.IsNullOrWhiteSpace))
            return Results.BadRequest(new { error = "OCR could not recognize readable text in this PDF." });

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var accepted = new List<BrokerPdfParseResult>();
        var warnings = new List<string>();

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var text = pages[pageIndex];
            if (string.IsNullOrWhiteSpace(text)) continue;
            var parsed = BrokerPdfTradeParser.Parse(text, $"broker-pdf:{sha}:page:{pageIndex + 1}");
            if (parsed.Trade is null) continue;
            if (parsed.Confidence < 0.65m)
            {
                warnings.Add($"Page {pageIndex + 1}: recognition confidence was too low and was skipped.");
                continue;
            }
            accepted.Add(parsed);
            warnings.AddRange(parsed.Warnings.Select(warning => $"Page {pageIndex + 1}: {warning}"));
        }

        if (accepted.Count == 0)
        {
            var combined = string.Join("\n\n", pages.Where(page => !string.IsNullOrWhiteSpace(page)));
            var parsed = BrokerPdfTradeParser.Parse(combined, $"broker-pdf:{sha}:ocr");
            if (parsed.Trade is not null && parsed.Confidence >= 0.65m)
            {
                accepted.Add(parsed);
                warnings.AddRange(parsed.Warnings);
            }
        }

        if (accepted.Count == 0)
            return Results.BadRequest(new { error = "OCR text was found, but no investment transaction could be recognized safely." });

        var distinct = accepted
            .Where(result => result.Trade is not null)
            .GroupBy(result => Signature(result.Trade!), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(result => result.Confidence).First())
            .ToArray();

        var normalizedRows = distinct.Select(result => ToRow(result.Trade!)).ToArray();
        var brokers = distinct.Select(result => result.Broker).Where(broker => broker != "unknown").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var confidence = distinct.Length == 0 ? 0m : distinct.Average(result => result.Confidence);

        return Results.Ok(new
        {
            fileName = Path.GetFileName(file.FileName),
            broker = brokers.Length == 1 ? brokers[0] : brokers.Length > 1 ? "multiple" : "unknown",
            confidence,
            extraction = "ocr",
            pagesProcessed = pages.Count,
            warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            rowCount = normalizedRows.Length,
            normalizedRows,
            suggestedMapping = Mapping()
        });
    }

    private static Dictionary<string, string?> ToRow(BrokerPdfTrade row) => new(StringComparer.OrdinalIgnoreCase)
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
    };

    private static object Mapping() => new
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
    };

    private static string Signature(BrokerPdfTrade trade) => string.Join('|',
        trade.TradeDate,
        trade.TradeType,
        trade.Isin ?? trade.Wkn ?? trade.SecurityName ?? string.Empty,
        trade.Quantity ?? string.Empty,
        trade.Amount,
        trade.Currency);
}

internal static class BrokerPdfOcr
{
    public static async Task<IReadOnlyList<string>> ExtractPagesAsync(byte[] content, int maxPages, int maxChars, CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"fullworth-pdf-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var input = Path.Combine(tempDirectory, "input.pdf");
        var prefix = Path.Combine(tempDirectory, "page");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            await RunAsync("pdftoppm", ["-f", "1", "-l", maxPages.ToString(), "-jpeg", "-r", "220", input, prefix], TimeSpan.FromSeconds(45), ct);

            var images = Directory.EnumerateFiles(tempDirectory, "page-*.jpg")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(maxPages)
                .ToArray();
            if (images.Length == 0)
                throw new InvalidDataException("PDF rasterization produced no pages.");

            var result = new List<string>(images.Length);
            var totalChars = 0;
            foreach (var image in images)
            {
                var text = await RunCaptureAsync("tesseract", [image, "stdout", "-l", "deu+eng", "--psm", "6"], TimeSpan.FromSeconds(35), ct);
                if (text.Length + totalChars > maxChars)
                    text = text[..Math.Max(0, maxChars - totalChars)];
                result.Add(text);
                totalChars += text.Length;
                if (totalChars >= maxChars) break;
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeoutValue, CancellationToken ct)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: false);
        Start(process, fileName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutValue);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidDataException($"{fileName} timed out.");
        }
        if (process.ExitCode != 0)
        {
            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? $"{fileName} failed." : $"{fileName} failed: {error}");
        }
    }

    private static async Task<string> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeoutValue, CancellationToken ct)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: true);
        Start(process, fileName);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutValue);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidDataException($"{fileName} timed out.");
        }
        if (process.ExitCode != 0)
        {
            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? $"{fileName} failed." : $"{fileName} failed: {error}");
        }
        return await stdout;
    }

    private static Process CreateProcess(string fileName, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo };
    }

    private static void Start(Process process, string fileName)
    {
        try
        {
            if (!process.Start()) throw new FileNotFoundException($"{fileName} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new FileNotFoundException($"{fileName} is not installed.", exception);
        }
    }
}
