using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

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

public static class InvestmentPdfImportParityEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;
    private const int MaxExtractedTextChars = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapInvestmentPdfImportParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/investment-import/pdf/detect", Detect)
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

public static partial class BrokerPdfTradeParser
{
    private static readonly Regex IsinRegex = new(@"\b[A-Z]{2}[A-Z0-9]{10}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DateRegex = new(@"(?<!\d)(?<date>\d{1,2}\.\d{1,2}\.\d{4})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyRegex = new(@"\b(EUR|USD|GBP|CHF|SEK|NOK|DKK|PLN|CZK|HUF|JPY|CAD|AUD)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string MoneyNumber = @"[+-]?(?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d{1,8})?|[+-]?\d+\.\d{1,8}";

    public static BrokerPdfParseResult Parse(string text, string externalKey = "broker-pdf:test")
    {
        if (string.IsNullOrWhiteSpace(text))
            return new("unknown", 0m, null, ["PDF text is empty."]);

        var normalized = NormalizeText(text);
        var broker = DetectBroker(normalized);
        var warnings = new List<string>();
        var type = DetectTradeType(normalized);
        if (type is null)
        {
            warnings.Add("Transaction type could not be recognized.");
            return new(broker, 0.2m, null, warnings);
        }

        var tradeDate = FindDate(normalized,
            "Ausführungstag", "Ausfuehrungstag", "Ausführungsdatum", "Ausfuehrungsdatum",
            "Handelstag", "Schlusstag", "Geschäftstag", "Geschaeftstag", "Datum");
        if (tradeDate is null)
        {
            var fallback = DateRegex.Match(normalized);
            if (fallback.Success && TryDate(fallback.Groups["date"].Value, out var parsedDate))
            {
                tradeDate = parsedDate;
                warnings.Add("Trade date was inferred from the first date in the document.");
            }
        }
        if (tradeDate is null)
        {
            warnings.Add("Trade date is missing.");
            return new(broker, 0.3m, null, warnings);
        }

        var settlement = FindDate(normalized, "Valuta", "Wertstellung", "Settlement");
        var isin = IsinRegex.Match(normalized.ToUpperInvariant()) is { Success: true } isinMatch ? isinMatch.Value : null;
        var wkn = FindToken(normalized, @"\bWKN\b\s*[:\-]?\s*(?<value>[A-Z0-9]{6})\b");
        var securityName = FindSecurityName(normalized, isin);
        var quantity = FindNumber(normalized, "Stückzahl", "Stueckzahl", "Stück", "Stueck", "Anzahl", "Nominale");
        var price = FindMoneyValue(normalized, out var priceCurrency, "Ausführungskurs", "Ausfuehrungskurs", "Kurs", "Preis");
        var gross = FindMoneyValue(normalized, out var grossCurrency, "Kurswert", "Bruttobetrag", "Brutto");
        var amount = FindMoneyValue(normalized, out var amountCurrency,
            "Ausmachender Betrag", "Endbetrag", "Abrechnungsbetrag", "Gesamtbetrag", "Gesamtsumme",
            "Zu Ihren Lasten", "Zu Ihren Gunsten", "Gutschrift", "Belastung");

        if (amount is null)
        {
            amount = gross ?? (price.HasValue && quantity.HasValue ? Math.Abs(price.Value * quantity.Value) : null);
            if (amount.HasValue) warnings.Add("Net amount was derived because no labelled final amount was found.");
        }
        if (amount is null || amount <= 0m)
        {
            warnings.Add("Transaction amount is missing.");
            return new(broker, 0.45m, null, warnings);
        }

        if (type is "buy" or "sell")
        {
            if (quantity is null or <= 0m) warnings.Add("Quantity was not recognized; review will reject the row until corrected.");
            if (string.IsNullOrWhiteSpace(isin) && string.IsNullOrWhiteSpace(wkn) && string.IsNullOrWhiteSpace(securityName))
                warnings.Add("Security identity was not recognized; review will require a security mapping.");
        }

        var fees = SumLabeledMoney(normalized,
            "Provision", "Orderentgelt", "Transaktionsentgelt", "Transaktionsgebühr", "Transaktionsgebuehr",
            "Fremde Spesen", "Börsenplatzentgelt", "Boersenplatzentgelt", "Handelsplatzgebühr", "Handelsplatzgebuehr");
        var withholding = SumLabeledMoney(normalized, "Quellensteuer", "ausländische Quellensteuer", "auslaendische Quellensteuer");
        var taxes = SumLabeledMoney(normalized,
            "Kapitalertragsteuer", "Solidaritätszuschlag", "Solidaritaetszuschlag", "Kirchensteuer");

        var currency = (amountCurrency ?? grossCurrency ?? priceCurrency ?? FindCurrency(normalized) ?? "EUR").ToUpperInvariant();
        var confidence = 0.55m;
        if (broker != "unknown") confidence += 0.08m;
        if (isin is not null || wkn is not null) confidence += 0.12m;
        if (quantity.HasValue || type is not ("buy" or "sell")) confidence += 0.08m;
        if (price.HasValue || type is not ("buy" or "sell")) confidence += 0.06m;
        if (amountCurrency is not null) confidence += 0.05m;
        confidence = Math.Min(0.95m, confidence);

        var trade = new BrokerPdfTrade(
            tradeDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            type,
            settlement?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            securityName,
            isin,
            wkn,
            null,
            Format(quantity),
            Format(price),
            Format(gross),
            Format(amount)!,
            currency,
            Format(fees) ?? "0",
            Format(taxes) ?? "0",
            Format(withholding) ?? "0",
            externalKey);

        if (Regex.Matches(normalized, @"Wertpapierabrechnung|Kaufabrechnung|Verkaufsabrechnung", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count > 1)
            warnings.Add("The PDF appears to contain multiple confirmations. Only the first safely recognizable transaction is imported.");

        return new(broker, confidence, trade, warnings);
    }

    private static string NormalizeText(string text) => text.Replace('\u00A0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');

    private static string DetectBroker(string text)
    {
        if (Contains(text, "TRADE REPUBLIC")) return "trade-republic";
        if (Contains(text, "SCALABLE CAPITAL")) return "scalable-capital";
        if (Contains(text, "ING-DIBA") || Regex.IsMatch(text, @"\bING\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "ing";
        if (Contains(text, "DEUTSCHE KREDITBANK") || Regex.IsMatch(text, @"\bDKB\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "dkb";
        if (Contains(text, "COMDIRECT")) return "comdirect";
        if (Contains(text, "FLATEX")) return "flatex";
        return "unknown";
    }

    private static string? DetectTradeType(string text)
    {
        var head = text.Length > 5000 ? text[..5000] : text;
        if (Regex.IsMatch(head, @"(?:Wertpapierabrechnung|Abrechnung|Kaufabrechnung)[^\n]{0,30}\bKauf\b|\bSparplanausf(?:ü|ue)hrung\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "buy";
        if (Regex.IsMatch(head, @"(?:Wertpapierabrechnung|Abrechnung|Verkaufsabrechnung)[^\n]{0,30}\bVerkauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "sell";
        if (Regex.IsMatch(head, @"Dividend(?:e|engutschrift)|Ertragsgutschrift|Ausschüttung|Ausschuettung", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "dividend";
        if (Regex.IsMatch(head, @"Zinsgutschrift|\bZinsen\b|\bZins\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "interest";
        if (Regex.IsMatch(head, @"\bVerkauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "sell";
        if (Regex.IsMatch(head, @"\bKauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "buy";
        return null;
    }

    private static DateOnly? FindDate(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,60}}?(?<date>\d{{1,2}}\.\d{{1,2}}\.\d{{4}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryDate(match.Groups["date"].Value, out var date)) return date;
        }
        return null;
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, ["d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static decimal? FindNumber(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,40}}?(?<value>{MoneyNumber})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryDecimal(match.Groups["value"].Value, out var value)) return Math.Abs(value);
        }
        return null;
    }

    private static decimal? FindMoneyValue(string text, out string? currency, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,80}}?(?<value>{MoneyNumber})\s*(?<currency>EUR|USD|GBP|CHF|SEK|NOK|DKK|PLN|CZK|HUF|JPY|CAD|AUD)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryDecimal(match.Groups["value"].Value, out var value))
            {
                currency = match.Groups["currency"].Success ? match.Groups["currency"].Value.ToUpperInvariant() : null;
                return Math.Abs(value);
            }
        }
        currency = null;
        return null;
    }

    private static decimal SumLabeledMoney(string text, params string[] labels)
    {
        decimal total = 0m;
        foreach (var label in labels)
        {
            foreach (Match match in Regex.Matches(text, $@"{Regex.Escape(label)}[^\r\n]{{0,80}}?(?<value>{MoneyNumber})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                if (TryDecimal(match.Groups["value"].Value, out var value)) total += Math.Abs(value);
        }
        return Math.Round(total, 8, MidpointRounding.AwayFromZero);
    }

    private static string? FindToken(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim().ToUpperInvariant() : null;
    }

    private static string? FindSecurityName(string text, string? isin)
    {
        foreach (var label in new[] { "Wertpapierbezeichnung", "Bezeichnung", "Wertpapier" })
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}\s*[:\-]?\s*(?<value>[^\r\n]{{3,120}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var value = CleanSecurityName(match.Groups["value"].Value, isin);
                if (value is not null) return value;
            }
        }

        if (isin is null) return null;
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var index = Array.FindIndex(lines, line => line.Contains(isin, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            var candidate = CleanSecurityName(lines[index - 1], isin);
            if (candidate is not null && candidate.Length >= 3 && !DateRegex.IsMatch(candidate)) return candidate;
        }
        return null;
    }

    private static string? CleanSecurityName(string value, string? isin)
    {
        var result = Regex.Replace(value, @"\s+", " ").Trim(' ', ':', '-', '|');
        if (isin is not null) result = result.Replace(isin, "", StringComparison.OrdinalIgnoreCase).Trim(' ', ':', '-', '|');
        result = Regex.Replace(result, @"\bWKN\b.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return result.Length is >= 3 and <= 120 ? result : null;
    }

    private static bool TryDecimal(string value, out decimal result)
    {
        var clean = value.Trim().Replace(" ", "");
        if (clean.Contains(',')) clean = clean.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    private static string? FindCurrency(string text)
    {
        var match = CurrencyRegex.Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? Format(decimal? value) => value.HasValue ? value.Value.ToString("0.########", CultureInfo.InvariantCulture) : null;
    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

internal static class PdfTextExtractor
{
    public static async Task<string> ExtractAsync(byte[] content, int maxChars, CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"fullworth-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var input = Path.Combine(tempDirectory, "input.pdf");
        var output = Path.Combine(tempDirectory, "output.txt");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pdftotext",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-layout");
            process.StartInfo.ArgumentList.Add("-nopgbrk");
            process.StartInfo.ArgumentList.Add("-enc");
            process.StartInfo.ArgumentList.Add("UTF-8");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add(output);

            try
            {
                if (!process.Start()) throw new FileNotFoundException("pdftotext could not be started.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException("pdftotext is not installed.", exception);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidDataException("PDF text extraction timed out.");
            }
            if (process.ExitCode != 0)
            {
                var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "PDF text extraction failed." : $"PDF text extraction failed: {error}");
            }
            if (!File.Exists(output)) throw new InvalidDataException("PDF text extraction produced no output.");
            var info = new FileInfo(output);
            if (info.Length > maxChars * 4L) throw new InvalidDataException("Extracted PDF text is too large.");
            var text = await File.ReadAllTextAsync(output, Encoding.UTF8, ct);
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }
}
