using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.FinTs;

internal sealed record FinTsResponseCode(string Code, string Text, IReadOnlyList<string> Parameters)
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

    public void ThrowOnError()
    {
        var error = Codes.FirstOrDefault(x => x.IsError);
        if (error is null) return;
        var code = error.Code switch
        {
            "9942" or "9340" => "pin_wrong",
            "9930" or "9931" => "access_locked",
            _ => "bank_error"
        };
        throw new FinTsException(string.IsNullOrWhiteSpace(error.Text) ? $"FinTS bank error {error.Code}." : error.Text, code);
    }
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
                    foreach (var method in ParseTanMethods(segment))
                        if (!methods.Any(x => x.SecurityFunction == method.SecurityFunction)) methods.Add(method);
                    break;
                default:
                    if (segment.Type.StartsWith("HI", StringComparison.Ordinal) && segment.Type.EndsWith("S", StringComparison.Ordinal) && segment.Version > 0)
                        versions[segment.Type] = Math.Max(versions.GetValueOrDefault(segment.Type), segment.Version);
                    break;
            }
        }

        foreach (var code in response.Codes.Where(x => x.Code == "3920"))
            allowedSecurityFunctions.AddRange(code.Parameters.Where(x => x.All(char.IsDigit)));

        var security = current.SecurityFunction;
        if (allowedSecurityFunctions.Count > 0)
        {
            var allowed = allowedSecurityFunctions.Distinct().ToArray();
            var decoupled = methods.FirstOrDefault(x => x.IsDecoupled && allowed.Contains(x.SecurityFunction));
            security = decoupled?.SecurityFunction ?? methods.FirstOrDefault(x => allowed.Contains(x.SecurityFunction))?.SecurityFunction ?? allowed[0];
        }
        else if (security == "999" && methods.Count > 0)
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

    public static IReadOnlyList<FinTsHolding> Holdings(FinTsResponse response)
    {
        var result = new List<FinTsHolding>();
        foreach (var segment in response.FindAll("HIWPD"))
        {
            for (var i = 2; i < segment.Groups.Count; i++)
            {
                var holding = ParseHolding(segment.Groups[i]);
                if (holding is not null) result.Add(holding);
            }
        }
        return result;
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
                var text = Text(group, Math.Min(2, group.Values.Count - 1));
                var parameters = group.Values.Skip(3).OfType<FinTsValue.Text>().Select(x => x.Value).ToArray();
                result.Add(new FinTsResponseCode(code, text, parameters));
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
        if (string.IsNullOrWhiteSpace(iban) && string.IsNullOrWhiteSpace(accountNumber)) return null;
        return new FinTsAccount(iban, bic, accountNumber, sub, owner, product, currency, depot);
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
