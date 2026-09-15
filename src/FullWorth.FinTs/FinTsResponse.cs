using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.FinTs;

internal sealed record FinTsResponseCode(string Code, string? Reference, string Text, IReadOnlyList<string> Parameters)
{
    public bool IsError => Code.Length == 4 && Code[0] == '9';
    public bool TanRequired => Code is "0030" or "3955";
    public bool DecoupledPending => Code is "3955" or "3956";
    public bool ScaExempt => Code == "3076";
    public bool Touchdown => Code == "3040";
}

internal sealed class FinTsResponse
{
    public required IReadOnlyList<FinTsSegment> Segments { get; init; }
    public required IReadOnlyList<FinTsResponseCode> Codes { get; init; }

    public FinTsSegment? Find(string type) => Segments.FirstOrDefault(x => x.Type == type);
    public IEnumerable<FinTsSegment> FindAll(string type) => Segments.Where(x => x.Type == type);
    public bool NeedsTan => Codes.Any(x => x.TanRequired) && !Codes.Any(x => x.ScaExempt);
    public bool DecoupledPending => Codes.Any(x => x.DecoupledPending);
    public string? Touchdown => Codes.FirstOrDefault(x => x.Touchdown)?.Parameters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// Wirft, wenn die Bank einen Fehler gemeldet hat - und zwar mit ALLEN Codes, die sie geschickt hat
    /// (#130 §9).
    ///
    /// Die erste Meldung ist bei ING regelmaessig 9800 "Der Dialog wurde abgebrochen". Das ist die
    /// Sammelmeldung: sie sagt, dass der Dialog endete, nicht warum. Der Grund steht in den Codes
    /// dahinter - falsche Zugangsdaten, gesperrter Zugang, nicht zugelassene Produkt-ID. Genau die
    /// wurden bisher verworfen, und im Protokoll stand eine Meldung, mit der niemand etwas anfangen
    /// konnte.
    ///
    /// Eingeordnet wird deshalb ueber alle Fehlercodes, und die gesprechendste Einordnung gewinnt:
    /// 9800 allein bleibt "bank_error", 9800 zusammen mit 9942 ist "pin_wrong".
    /// </summary>
    public void ThrowOnError(IReadOnlyList<FinTsSegmentShape>? sentShape = null)
    {
        var errors = Codes.Where(x => x.IsError).ToList();
        if (errors.Count == 0) return;

        var classified = errors.Select(x => (Error: x, Kind: Classify(x.Code)))
            .Where(x => x.Kind != "bank_error")
            .ToList();
        // Der Code, der die Einordnung traegt - sonst der erste. Die Meldung kommt von demselben.
        var leading = classified.Count > 0 ? classified[0].Error : errors[0];
        var kind = classified.Count > 0 ? classified[0].Kind : "bank_error";

        var message = string.IsNullOrWhiteSpace(leading.Text) ? $"FinTS bank error {leading.Code}." : leading.Text;
        throw new FinTsException(message, kind,
            bankCode: leading.Code,
            segmentReference: string.IsNullOrWhiteSpace(leading.Reference) ? null : leading.Reference,
            bankMessage: string.IsNullOrWhiteSpace(leading.Text) ? null : leading.Text,
            bankCodes: [.. errors.Select(x => new FinTsBankCode(x.Code, string.IsNullOrWhiteSpace(x.Reference) ? null : x.Reference, x.Text))],
            sentShape: sentShape);
    }

    /// <summary>
    /// Die Einordnung - und nur fuer die Codes, deren Bedeutung belegt ist.
    ///
    /// Die uebrigen Faelle aus #130 §9 (Produkt-ID nicht zugelassen, Segmentversion nicht
    /// unterstuetzt, Dialog abgelaufen, Bank nicht erreichbar) fehlen hier mit Absicht: ich kenne die
    /// zugehoerigen DK-Codes nicht sicher, und eine falsche Einordnung ist schlimmer als keine - sie
    /// zeigt dem Benutzer eine Ursache, die nicht stimmt. Sichtbar sind sie trotzdem, weil seit #130 §9
    /// ALLE Codes mitgefuehrt und protokolliert werden. Wer eine echte Bankantwort mit einem dieser
    /// Codes hat, kann die Zeile hier belegt ergaenzen.
    /// </summary>
    private static string Classify(string code) => code switch
    {
        "9340" or "9942" => "pin_wrong",       // Zugangsdaten falsch
        "9930" or "9931" => "access_locked",   // Zugang gesperrt
        _ => "bank_error"
    };
}

internal static class FinTsResponseParser
{
    private static readonly Regex IbanRegex = new("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BicRegex = new("^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsinRegex = new("^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FinTsResponse Parse(byte[] bytes)
    {
        var outer = FinTsWire.Parse(bytes).ToList();
        var expanded = new List<FinTsSegment>();
        foreach (var segment in outer)
        {
            expanded.Add(segment);
            if (segment.Type == "HNVSD" && segment.GetBinary(1, 0) is { Length: > 0 } inner)
                expanded.AddRange(FinTsWire.Parse(inner));
        }
        return new FinTsResponse { Segments = expanded, Codes = ParseCodes(expanded) };
    }

    public static string DialogId(FinTsResponse response)
        => response.Find("HNHBK")?.GetText(3, 0) is { Length: > 0 } value ? value : "0";

    public static FinTsBankParameters MergeParameters(FinTsBankParameters current, FinTsResponse response)
    {
        var bpd = current.BpdVersion;
        var upd = current.UpdVersion;
        var systemId = current.SystemId;
        var versions = new Dictionary<string, int>(current.SegmentVersions, StringComparer.OrdinalIgnoreCase);
        var tanRequired = new Dictionary<string, bool>(current.TanRequired, StringComparer.OrdinalIgnoreCase);
        var methods = current.TanMethods.ToList();
        var accounts = current.Accounts.ToList();
        var allowedSecurityFunctions = new List<string>();

        foreach (var segment in response.Segments)
        {
            switch (segment.Type)
            {
                case "HIBPA":
                    if (int.TryParse(segment.GetText(1, 0), out var b)) bpd = b;
                    break;
                case "HIUPA":
                    if (int.TryParse(segment.GetText(1, 0), out var u)) upd = u;
                    break;
                case "HISYN":
                    var sid = segment.GetText(1, 0);
                    if (!string.IsNullOrWhiteSpace(sid)) systemId = sid;
                    break;
                case "HIUPD":
                    var account = ParseAccount(segment);
                    if (account is not null && !accounts.Any(x => SameAccount(x, account))) accounts.Add(account);
                    break;
                case "HIPINS":
                    foreach (var pair in ParsePinTanRules(segment)) tanRequired[pair.Key] = pair.Value;
                    break;
                case "HITANS":
                    // Eine Bank kuendigt dasselbe Verfahren in MEHREREN Segmentversionen an - ING
                    // schickt HITANS:4 und HITANS:6 nebeneinander. Vorher gewann das zuerst gesehene,
                    // und das ist regelmaessig das aelteste: fuer Sicherheitsfunktion 942 blieb
                    // Version 4 stehen, die Version 6 daneben wurde weggeworfen. Gebraucht wird die
                    // hoechste, denn nach ihr wird HKTAN gebaut (#130 §8).
                    foreach (var method in ParseTanMethods(segment))
                    {
                        var existing = methods.FindIndex(x => x.SecurityFunction == method.SecurityFunction);
                        if (existing < 0) methods.Add(method);
                        else if (methods[existing].SegmentVersion < method.SegmentVersion) methods[existing] = method;
                    }
                    break;
                default:
                    if (segment.Type.StartsWith("HI", StringComparison.Ordinal) && segment.Type.EndsWith("S", StringComparison.Ordinal) && segment.Version > 0)
                        versions[segment.Type] = Math.Max(versions.GetValueOrDefault(segment.Type), segment.Version);
                    break;
            }
        }

        foreach (var responseCode in response.Codes.Where(x => x.Code == "3920"))
            allowedSecurityFunctions.AddRange(responseCode.Parameters.Where(x => x.All(char.IsDigit)));

        var security = current.SecurityFunction;
        if (allowedSecurityFunctions.Count > 0)
        {
            var allowed = allowedSecurityFunctions.Distinct().ToArray();
            var decoupled = methods.FirstOrDefault(x => x.IsDecoupled && allowed.Contains(x.SecurityFunction));
            security = decoupled?.SecurityFunction ?? methods.FirstOrDefault(x => allowed.Contains(x.SecurityFunction))?.SecurityFunction ?? allowed[0];
        }
        else if (security == FinTsMessages.OneStepSecurityFunction && methods.Count > 0)
        {
            security = methods.FirstOrDefault(x => x.IsDecoupled)?.SecurityFunction ?? methods[0].SecurityFunction;
        }

        return new FinTsBankParameters(bpd, upd, systemId, security, current.TanMedium, versions, tanRequired, methods, accounts);
    }

    public static FinTsTanChallenge? Challenge(FinTsResponse response, FinTsBankParameters parameters)
    {
        var hitan = response.Find("HITAN");
        if (hitan is null) return null;
        var task = hitan.Version >= 6 ? NonEmpty(hitan.GetText(2, 0), hitan.GetText(3, 0)) : hitan.GetText(2, 0);
        var challenge = hitan.Version >= 6 ? NonEmpty(hitan.GetText(4, 0), hitan.GetText(3, 0)) : hitan.GetText(3, 0);
        if (string.IsNullOrWhiteSpace(task) && string.IsNullOrWhiteSpace(challenge)) return null;
        var hhd = hitan.Version >= 6 ? hitan.GetBinary(5, 0) : null;
        var decoupled = response.Codes.Any(x => x.Code is "3955" or "3956") || parameters.TanMethods.Any(x => x.SecurityFunction == parameters.SecurityFunction && x.IsDecoupled);
        return new FinTsTanChallenge(task, challenge, decoupled, hhd);
    }

    public static FinTsBalance? Balance(FinTsResponse response)
    {
        var segment = response.Find("HISAL");
        if (segment is null) return null;
        var balance = segment.Groups.Count > 4 ? segment.Groups[4] : null;
        if (balance is null || balance.Values.Count < 2) return null;
        var dc = Text(balance, 0);
        if (!TryDecimal(Text(balance, 1), out var amount)) return null;
        if (dc == "D") amount = -amount;
        var currency = NonEmpty(Text(balance, 2), segment.GetText(3, 0), "EUR");
        var date = ParseDate(Text(balance, 3)) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        decimal? credit = segment.Groups.Count > 6 && TryDecimal(Text(segment.Groups[6], 0), out var c) ? c : null;
        decimal? available = segment.Groups.Count > 7 && TryDecimal(Text(segment.Groups[7], 0), out var a) ? a : null;
        return new FinTsBalance(amount, currency, date, available, credit);
    }

    public static IReadOnlyList<FinTsTransaction> Transactions(FinTsResponse response)
    {
        var items = new List<FinTsTransaction>();
        foreach (var segment in response.FindAll("HIKAZ"))
        {
            if (segment.GetBinary(1, 0) is { Length: > 0 } booked) items.AddRange(ParseMt940(booked, false));
            if (segment.GetBinary(2, 0) is { Length: > 0 } pending) items.AddRange(ParseMt940(pending, true));
        }
        return items;
    }

    /// <summary>
    /// Der Depotbestand aus HIWPD.
    ///
    /// HIWPD traegt die Aufstellung als MT535 in einem Binaerfeld - das ist das Format, und es wird
    /// jetzt gelesen (#130 §5). Vorher wurde ausschliesslich geraten: die erste Zahl des Segments galt
    /// als Stueckzahl, die zweite als Kurs, die dritte als Wert. Das stimmt, solange die Bank genau
    /// diese drei in genau dieser Reihenfolge schickt, und sonst nie - auffallen wuerde es erst am
    /// Vermoegen.
    ///
    /// Die alte Auslegung bleibt als Rueckfall fuer Antworten OHNE MT535 stehen. Sie raet weiterhin,
    /// aber sie ist dann das Einzige, was da ist; und sie kommt nicht mehr zum Zug, sobald eine
    /// richtige Aufstellung vorliegt.
    /// </summary>
    public static IReadOnlyList<FinTsHolding> Holdings(FinTsResponse response)
    {
        var result = new List<FinTsHolding>();
        foreach (var segment in response.FindAll("HIWPD"))
        {
            var statement = Mt535Text(segment);
            if (statement is not null)
            {
                result.AddRange(Mt535Parser.Parse(statement));
                continue;
            }

            for (var i = 2; i < segment.Groups.Count; i++)
            {
                var holding = ParseHolding(segment.Groups[i]);
                if (holding is not null) result.Add(holding);
            }
        }
        return result;
    }

    /// <summary>Die MT535-Aufstellung des Segments - als Binaerfeld oder, seltener, als Text.</summary>
    private static string? Mt535Text(FinTsSegment segment)
    {
        foreach (var group in segment.Groups)
        {
            foreach (var value in group.Values)
            {
                var text = value switch
                {
                    FinTsValue.Binary binary => System.Text.Encoding.Latin1.GetString(binary.Value),
                    FinTsValue.Text plain => plain.Value,
                    _ => null
                };
                if (Mt535Parser.LooksLikeStatement(text)) return text;
            }
        }
        return null;
    }

    private static IReadOnlyList<FinTsResponseCode> ParseCodes(IReadOnlyList<FinTsSegment> segments)
    {
        var result = new List<FinTsResponseCode>();
        foreach (var segment in segments.Where(x => x.Type is "HIRMG" or "HIRMS"))
        {
            for (var i = 1; i < segment.Groups.Count; i++)
            {
                var group = segment.Groups[i];
                var code = Text(group, 0);
                if (code.Length != 4 || !code.All(char.IsDigit)) continue;
                var reference = Text(group, 1);
                var text = Text(group, Math.Min(2, group.Values.Count - 1));
                var parameters = group.Values.Skip(3).OfType<FinTsValue.Text>().Select(x => x.Value).ToArray();
                result.Add(new FinTsResponseCode(code, reference, text, parameters));
            }
        }
        return result;
    }

    private static FinTsAccount? ParseAccount(FinTsSegment segment)
    {
        if (segment.Groups.Count < 2) return null;
        var accountGroup = segment.Groups[1];
        var all = segment.Groups.SelectMany(x => x.Values).OfType<FinTsValue.Text>().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var iban = all.FirstOrDefault(x => IbanRegex.IsMatch(x)) ?? string.Empty;
        var bic = all.FirstOrDefault(x => BicRegex.IsMatch(x)) ?? string.Empty;
        var accountNumber = Text(accountGroup, 0);
        var sub = Text(accountGroup, 1);
        var currency = all.FirstOrDefault(IsCurrency) ?? "EUR";
        var owner = FirstUseful(segment, 6, 7);
        var product = FirstUseful(segment, 8, 9);
        var depot = (product ?? string.Empty).Contains("Depot", StringComparison.OrdinalIgnoreCase);
        // Stelle 4 der klassischen Kontoverbindung ist der Kreditinstitutscode. Nennt die Bank ihn,
        // wird er uebernommen; sonst steht er in der IBAN (#130 §3).
        var bankCode = Text(accountGroup, 3);
        if (string.IsNullOrWhiteSpace(iban) && string.IsNullOrWhiteSpace(accountNumber)) return null;
        return new FinTsAccount(iban, bic, accountNumber, sub, owner, product, currency, depot,
            string.IsNullOrWhiteSpace(bankCode) ? null : bankCode);
    }

    private static Dictionary<string, bool> ParsePinTanRules(FinTsSegment segment)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in segment.Groups.Skip(3))
        {
            var values = group.Values.OfType<FinTsValue.Text>().Select(x => x.Value).ToArray();
            for (var i = 0; i + 1 < values.Length; i++)
            {
                if (values[i].StartsWith("HK", StringComparison.Ordinal) && values[i].Length is >= 5 and <= 8)
                    result[values[i]] = values[i + 1] == "J";
            }
        }
        return result;
    }

    private static IEnumerable<FinTsTanMethod> ParseTanMethods(FinTsSegment segment)
    {
        for (var i = 4; i < segment.Groups.Count; i++)
        {
            var g = segment.Groups[i];
            var security = Text(g, 0);
            if (string.IsNullOrWhiteSpace(security)) continue;
            var process = Text(g, 1);
            var name = segment.Version >= 6 ? Text(g, 3) : Text(g, 2);
            var needsMedium = segment.Version >= 6 && g.Values.Count > 13 && Text(g, 13) is "1" or "2";
            var isDecoupled = segment.Version >= 7 && g.Values.Count > 21 && Text(g, 21) == "J";
            var maxPolls = ParseInt(g, 22, -1);
            var waitFirst = ParseInt(g, 23, 0);
            var waitNext = ParseInt(g, 24, 0);
            yield return new FinTsTanMethod(security, string.IsNullOrWhiteSpace(name) ? $"TAN-{security}" : name, process, needsMedium, isDecoupled, maxPolls, waitFirst, waitNext, segment.Version);
        }
    }

    private static IReadOnlyList<FinTsTransaction> ParseMt940(byte[] data, bool pending)
    {
        var text = Encoding.Latin1.GetString(data).Replace("\r\n", "\n");
        var result = new List<FinTsTransaction>();
        string currency = "EUR";
        var opening = Regex.Match(text, @":60[FM]:[CD][0-9]{6}([A-Z]{3})");
        if (opening.Success) currency = opening.Groups[1].Value;
        var matches = Regex.Matches(text, @":61:(?<date>[0-9]{6})(?<value>[0-9]{4})?(?<dc>[CD])(?<funds>[A-Z])?(?<amount>[0-9,]+)(?<rest>[^\n]*)(?:\n:86:(?<desc>.*?))?(?=\n:61:|\n:62[FM]:|\z)", RegexOptions.Singleline);
        foreach (Match match in matches)
        {
            var booking = ParseShortDate(match.Groups["date"].Value);
            var valueDate = match.Groups["value"].Success && booking.HasValue ? ParseMonthDay(booking.Value, match.Groups["value"].Value) : booking;
            if (!TryDecimal(match.Groups["amount"].Value, out var amount)) continue;
            if (match.Groups["dc"].Value == "D") amount = -amount;
            var desc = match.Groups["desc"].Value.Replace("\n", " ").Trim();
            var counterparty = ExtractMt940Field(desc, "32") ?? ExtractMt940Field(desc, "33");
            var keyMaterial = $"{booking:yyyyMMdd}|{valueDate:yyyyMMdd}|{amount}|{currency}|{match.Groups["rest"].Value}|{desc}|{pending}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
            result.Add(new FinTsTransaction(key, booking, valueDate, amount, currency, counterparty, desc, match.Value.Trim(), pending));
        }
        return result;
    }

    private static FinTsHolding? ParseHolding(FinTsGroup group)
    {
        var values = group.Values.OfType<FinTsValue.Text>().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (values.Length < 3) return null;
        string? isin = values.FirstOrDefault(x => IsinRegex.IsMatch(x));
        string? wkn = values.FirstOrDefault(x => x.Length == 6 && x.All(char.IsLetterOrDigit) && x != isin);
        var decimals = values.Select((x, i) => (x, i)).Where(x => TryDecimal(x.x, out _)).ToArray();
        if (decimals.Length == 0) return null;
        TryDecimal(decimals[0].x, out var quantity);
        decimal? price = decimals.Length > 1 && TryDecimal(decimals[1].x, out var p) ? p : null;
        decimal? market = decimals.Length > 2 && TryDecimal(decimals[2].x, out var m) ? m : null;
        var currencies = values.Where(IsCurrency).ToArray();
        var date = values.Select(ParseDate).FirstOrDefault(x => x.HasValue);
        var name = values.FirstOrDefault(x => x.Length > 3 && !IsCurrency(x) && !IbanRegex.IsMatch(x) && !BicRegex.IsMatch(x) && !IsinRegex.IsMatch(x) && !TryDecimal(x, out _) && ParseDate(x) is null) ?? isin ?? wkn ?? "Wertpapier";
        var exchange = values.FirstOrDefault(x => x.Length is >= 2 and <= 10 && x.All(char.IsLetter) && !IsCurrency(x) && !string.Equals(x, name, StringComparison.Ordinal));
        return new FinTsHolding(isin, wkn, name, quantity, price, currencies.ElementAtOrDefault(0), date, market, currencies.ElementAtOrDefault(1) ?? currencies.ElementAtOrDefault(0), exchange);
    }

    private static string? ExtractMt940Field(string text, string id)
    {
        var match = Regex.Match(text, $@"\?{Regex.Escape(id)}(?<v>.*?)(?=\?[0-9]{{2}}|$)");
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static bool SameAccount(FinTsAccount a, FinTsAccount b)
        => !string.IsNullOrWhiteSpace(a.Iban) && string.Equals(a.Iban, b.Iban, StringComparison.OrdinalIgnoreCase)
           || string.Equals(a.AccountNumber, b.AccountNumber, StringComparison.OrdinalIgnoreCase) && string.Equals(a.SubAccount, b.SubAccount, StringComparison.OrdinalIgnoreCase);

    private static string Text(FinTsGroup group, int index)
        => index >= 0 && group.Values.Count > index && group.Values[index] is FinTsValue.Text t ? t.Value : string.Empty;
    private static int ParseInt(FinTsGroup group, int index, int fallback)
        => int.TryParse(Text(group, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static string? FirstUseful(FinTsSegment segment, params int[] groups)
        => groups.Where(i => i < segment.Groups.Count).Select(i => Text(segment.Groups[i], 0)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static string NonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static bool IsCurrency(string value) => value.Length == 3 && value.All(char.IsUpper);
    private static bool TryDecimal(string? value, out decimal result)
        => decimal.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static DateOnly? ParseShortDate(string value)
        => DateOnly.TryParseExact(value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static DateOnly ParseMonthDay(DateOnly booking, string mmdd)
    {
        if (mmdd.Length != 4 || !int.TryParse(mmdd[..2], out var month) || !int.TryParse(mmdd[2..], out var day) || month is < 1 or > 12 || day < 1) return booking;
        var year = booking.Year;
        var candidate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
        if (candidate.DayNumber - booking.DayNumber > 180) candidate = candidate.AddYears(-1);
        if (booking.DayNumber - candidate.DayNumber > 180) candidate = candidate.AddYears(1);
        return candidate;
    }
}