using System.Globalization;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Eine Zeile der hochgeladenen Datei, gelesen und geprueft.</summary>
public sealed record ImportCandidate(
    Guid Id,
    int RowNumber,
    DateOnly? TradeDate,
    DateOnly? SettlementDate,
    string? TradeType,
    string? SecurityName,
    string? Isin,
    string? Wkn,
    string? Ticker,
    string? AssetType,
    decimal? Quantity,
    decimal? Price,
    decimal? GrossAmount,
    decimal Amount,
    string Currency,
    decimal Fees,
    decimal Taxes,
    decimal WithholdingTax,
    string? ExternalKey,
    string Fingerprint,
    string Status,
    string? Error,
    string DuplicateStatus);

/// <summary>Ein bereits vorhandenes Wertpapier, so weit die Zuordnung es braucht.</summary>
public sealed record ImportSecurity(
    Guid Id, string Name, string? Isin, string? Wkn, string? Ticker, string Currency);

/// <summary>
/// Aus Dateizeilen werden Handel: lesen, pruefen, und jedem eine Kennung geben, an der er sich
/// wiedererkennt.
///
/// Drei Entscheidungen, die man den Zeilen nicht ansieht:
///
/// Der Fingerabdruck zaehlt mit, wie oft dieselbe Zeile in derselben Datei vorkommt. Zwei echte Kaeufe
/// am selben Tag ueber denselben Betrag sind zwei Kaeufe, kein Duplikat - ohne den Zaehler waere der
/// zweite beim Einspielen verschwunden.
///
/// Die Zuordnung zu einem vorhandenen Wertpapier ist absichtlich streng: ISIN entscheidet allein, WKN,
/// Ticker und Name nur, wenn genau ein Treffer bleibt. Zwei Treffer heissen "nicht zugeordnet" - die
/// Rueckfrage kostet einen Klick, die falsche Zuordnung eine Depothistorie.
///
/// Alle Betraege stehen ohne Vorzeichen. Die Richtung steckt in der Art des Handels, nicht im
/// Vorzeichen des Exports - Broker sind sich darin nicht einig.
/// </summary>
internal static class InvestmentImportCandidates
{
    public static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "cancellation", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };

    public static readonly HashSet<string> SecurityRequiredTypes = new(StringComparer.OrdinalIgnoreCase)
    { "buy", "sell", "cancellation", "security_transfer_in", "security_transfer_out", "split" };

    /// <summary>Alle Zeilen lesen; eine unlesbare Zeile wird zum Fehler-Kandidaten statt die Datei zu kippen.</summary>
    public static (List<ImportCandidate> Candidates, int ErrorCount) Build(
        IReadOnlyList<Dictionary<string, string>> rows, InvestmentImportColumnMapping mapping)
    {
        var candidates = new List<ImportCandidate>();
        var occurrenceBySemantic = new Dictionary<string, int>(StringComparer.Ordinal);
        var errorCount = 0;

        for (var index = 0; index < rows.Count; index++)
        {
            try
            {
                var candidate = Parse(index + 1, rows[index], mapping);
                var semantic = SemanticFingerprint(candidate);
                var occurrence = occurrenceBySemantic.GetValueOrDefault(semantic) + 1;
                occurrenceBySemantic[semantic] = occurrence;
                candidate = candidate with { Fingerprint = InvestmentImportFile.Sha256($"{semantic}|{occurrence}") };

                if (Validate(candidate) is { } error)
                {
                    errorCount++;
                    candidate = candidate with { Status = "error", Error = error };
                }
                candidates.Add(candidate);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                errorCount++;
                candidates.Add(new ImportCandidate(
                    Guid.NewGuid(), index + 1, null, null, null, null, null, null, null,
                    null, null, null, null, 0m, "EUR", 0m, 0m, 0m, null,
                    InvestmentImportFile.Sha256($"invalid|{index + 1}|{rows[index].Count}"),
                    "error", exception.Message, "new"));
            }
        }

        return (candidates, errorCount);
    }

    public static bool HasSecurityIdentity(ImportCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Isin) || !string.IsNullOrWhiteSpace(candidate.Wkn) ||
        !string.IsNullOrWhiteSpace(candidate.Ticker) || !string.IsNullOrWhiteSpace(candidate.SecurityName);

    /// <summary>Woran mehrere Zeilen als dasselbe Wertpapier erkannt werden - in dieser Rangfolge.</summary>
    public static string SecurityKey(ImportCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Isin)) return $"isin:{candidate.Isin}";
        if (!string.IsNullOrWhiteSpace(candidate.Wkn)) return $"wkn:{candidate.Wkn}";
        if (!string.IsNullOrWhiteSpace(candidate.Ticker)) return $"ticker:{candidate.Ticker}|{candidate.Currency}";
        return $"name:{InvestmentImportFile.Normalize(candidate.SecurityName!)}|{candidate.Currency}";
    }

    /// <summary>Genau ein Treffer zaehlt; bei zweien bleibt die Zeile offen.</summary>
    public static ImportSecurity? AutoMatch(ImportCandidate candidate, IReadOnlyList<ImportSecurity> securities)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Isin))
            return securities.SingleOrDefault(security =>
                string.Equals(security.Isin, candidate.Isin, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(candidate.Wkn))
        {
            var matches = securities.Where(security =>
                string.Equals(security.Wkn, candidate.Wkn, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }

        if (!string.IsNullOrWhiteSpace(candidate.Ticker))
        {
            var matches = securities.Where(security =>
                string.Equals(security.Ticker, candidate.Ticker, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(security.Currency, candidate.Currency, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }

        if (!string.IsNullOrWhiteSpace(candidate.SecurityName))
        {
            var name = InvestmentImportFile.Normalize(candidate.SecurityName);
            var matches = securities.Where(security =>
                InvestmentImportFile.Normalize(security.Name) == name &&
                string.Equals(security.Currency, candidate.Currency, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }

        return null;
    }

    /// <summary>
    /// Der Schluessel, unter dem ein eingespielter Handel wiedererkannt wird. Eine eigene Kennung des
    /// Brokers gewinnt; sonst der Fingerabdruck der Zeile. Dasselbe Depot zweimal einzuspielen legt
    /// darum nichts doppelt an.
    /// </summary>
    public static string StableExternalKey(ImportCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.ExternalKey)
            ? $"investment-import:external:{InvestmentImportFile.Sha256(candidate.ExternalKey.Trim())}"
            : $"investment-import:fingerprint:{candidate.Fingerprint}";

    /// <summary>Innerhalb eines Tages: erst kaufen, dann splitten, dann verkaufen.</summary>
    public static int TradeOrderPriority(string? type) => type switch
    {
        "buy" or "security_transfer_in" => 0,
        "split" => 1,
        "sell" or "security_transfer_out" or "cancellation" => 2,
        _ => 3
    };

    private static ImportCandidate Parse(
        int rowNumber, IReadOnlyDictionary<string, string> row, InvestmentImportColumnMapping mapping)
    {
        string? Cell(string? column) => string.IsNullOrWhiteSpace(column) ? null : row.GetValueOrDefault(column!);

        var quantity = InvestmentImportFile.AbsNullable(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.Quantity)));
        var price = InvestmentImportFile.AbsNullable(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.Price)));
        var gross = InvestmentImportFile.AbsNullable(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.GrossAmount)));

        // Fehlt der Nettobetrag, wird er aus Brutto oder aus Kurs mal Stueckzahl gebildet - nicht auf
        // null gesetzt: eine Buchung ohne Betrag waere spaeter nicht als Luecke zu erkennen.
        var amount = Math.Abs(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.Amount)) ?? gross ??
                              (price.HasValue && quantity.HasValue ? price.Value * quantity.Value : 0m));

        return new ImportCandidate(
            Guid.NewGuid(),
            rowNumber,
            InvestmentImportFile.ParseDate(Cell(mapping.TradeDate)),
            InvestmentImportFile.ParseOptionalDate(Cell(mapping.SettlementDate)),
            NormalizeTradeType(Cell(mapping.TradeType)),
            InvestmentImportFile.Clean(Cell(mapping.SecurityName)),
            InvestmentImportFile.Clean(Cell(mapping.Isin))?.ToUpperInvariant(),
            InvestmentImportFile.Clean(Cell(mapping.Wkn))?.ToUpperInvariant(),
            InvestmentImportFile.Clean(Cell(mapping.Ticker))?.ToUpperInvariant(),
            NormalizeAssetType(Cell(mapping.AssetClass), mapping.SourceProvider),
            quantity, price, gross, amount,
            InvestmentImportFile.ParseCurrency(Cell(mapping.Currency)),
            Math.Abs(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.Fees)) ?? 0m),
            Math.Abs(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.Taxes)) ?? 0m),
            Math.Abs(InvestmentImportFile.ParseOptionalAmount(Cell(mapping.WithholdingTax)) ?? 0m),
            InvestmentImportFile.Clean(Cell(mapping.ExternalKey)),
            "", "ready", null, "new");
    }

    private static string? Validate(ImportCandidate candidate)
    {
        if (!candidate.TradeDate.HasValue) return "Trade date is required.";
        if (string.IsNullOrWhiteSpace(candidate.TradeType) || !AllowedTypes.Contains(candidate.TradeType))
            return "Unsupported investment transaction type.";
        if (candidate.Currency.Length != 3 || !candidate.Currency.All(char.IsLetter))
            return "Currency must contain three letters.";
        if (candidate.Isin is { Length: > 0 } && candidate.Isin.Length != 12)
            return "ISIN must contain 12 characters.";

        if (SecurityRequiredTypes.Contains(candidate.TradeType))
        {
            if (!HasSecurityIdentity(candidate)) return "This transaction type requires a security identifier or name.";
            if (candidate.Quantity is null or <= 0) return "This transaction type requires a positive quantity or split ratio.";
        }
        if (candidate.TradeType is "buy" or "sell"
            && candidate.Price is null or <= 0 && candidate.GrossAmount is null or <= 0)
            return "Buy/sell requires a positive price or gross amount.";

        return null;
    }

    /// <summary>Alles, was eine Zeile fachlich ausmacht - ohne Zeilennummer und ohne eigene Kennung.</summary>
    private static string SemanticFingerprint(ImportCandidate candidate) => InvestmentImportFile.Sha256(string.Join('|',
        candidate.TradeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        candidate.SettlementDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        candidate.TradeType ?? "",
        candidate.Isin ?? "",
        candidate.Wkn ?? "",
        candidate.Ticker ?? "",
        candidate.AssetType ?? "",
        InvestmentImportFile.Normalize(candidate.SecurityName ?? ""),
        candidate.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.Price?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.GrossAmount?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.Amount.ToString(CultureInfo.InvariantCulture),
        candidate.Currency,
        candidate.Fees.ToString(CultureInfo.InvariantCulture),
        candidate.Taxes.ToString(CultureInfo.InvariantCulture),
        candidate.WithholdingTax.ToString(CultureInfo.InvariantCulture)));

    private static string NormalizeTradeType(string? value) => InvestmentImportFile.Normalize(value ?? "") switch
    {
        "buy" or "kauf" or "kaufen" => "buy",
        "sell" or "verkauf" or "verkaufen" => "sell",
        "dividend" or "dividende" or "ausschuettung" or "ausschüttung" => "dividend",
        "interest" or "zins" or "zinsen" or "interestpayment" => "interest",
        "fee" or "fees" or "gebuehr" or "gebühr" or "gebuehren" or "gebühren" => "fee",
        "tax" or "taxes" or "steuer" or "steuern" or "taxoptimization" or "secaccount" => "tax",
        "deposit" or "einzahlung" or "customerinbound" or "customerinpayment" or "transferinbound" or "transferinstantinbound" => "deposit",
        "withdrawal" or "auszahlung" or "customeroutboundrequest" or "transferoutbound" or "transferinstantoutbound" => "withdrawal",
        "cardtransaction" => "other",
        "securitytransferin" or "transferin" or "eingang" => "security_transfer_in",
        "securitytransferout" or "transferout" or "ausgang" => "security_transfer_out",
        "redemption" => "sell",
        "buycancelled" or "buycanceled" or "kaufstorno" or "stornokauf" => "cancellation",
        "split" or "aktiensplit" => "split",
        "other" or "sonstiges" or "compensation" => "other",
        _ => value?.Trim().ToLowerInvariant() ?? ""
    };

    private static string? NormalizeAssetType(string? value, string? sourceProvider)
    {
        var provider = InvestmentImportFile.Normalize(sourceProvider ?? "");
        return InvestmentImportFile.Normalize(value ?? "") switch
        {
            "" => null,
            "stock" or "aktie" or "equity" => "stock",
            "etf" => "etf",
            // Trade Republic nennt seine ETFs "Fonds"; ueberall sonst ist ein Fonds ein Fonds.
            "fund" or "fonds" when provider == "traderepublic" => "etf",
            "fund" or "fonds" or "mutualfund" => "fund",
            "derivative" or "derivatives" or "derivat" or "derivate" or "warrant" or "certificate" => "derivative",
            "bond" or "anleihe" => "bond",
            "crypto" or "cryptocurrency" or "krypto" => "crypto",
            "commodity" or "rohstoff" or "metal" or "metall" => "commodity",
            "cash" or "geld" => "cash",
            _ => "other"
        };
    }
}
