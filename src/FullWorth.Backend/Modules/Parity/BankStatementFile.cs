using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>One booking read off a bank statement file.</summary>
internal sealed record StatementEntry(
    DateOnly BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? Counterparty,
    string? Description,
    string? ExternalKey);

/// <summary>A balance the statement itself states, with the date it is valid for.</summary>
internal sealed record StatementBalance(decimal Amount, string Currency, DateOnly AsOf);

/// <summary>
/// A parsed statement. <see cref="AccountIdentifier"/> is what the file says about the account it
/// belongs to — shown to the owner so they can see they are importing into the right account. It is
/// never used to create an account: a statement import always targets one that already exists.
/// </summary>
internal sealed record BankStatement(
    string AdapterKey,
    IReadOnlyList<StatementEntry> Entries,
    StatementBalance? ClosingBalance,
    string? AccountIdentifier);

/// <summary>
/// Readers for the two statement formats every European bank can export even when it offers no API:
/// SWIFT MT940 and ISO 20022 CAMT (.053 statements and .052 reports). They exist so an account whose
/// bank FullWorth cannot connect to — Ikano over FinTS, or any institution a private Enable Banking
/// application is not enabled for — can still be kept current and complete.
///
/// Both formats state a closing balance with the date it is valid for, which is the piece a CSV export
/// almost never carries: it turns "here are some bookings" into an account whose value is anchored.
///
/// Amounts go through <see cref="ImportNumber"/> like every other import. Both formats are documented
/// as never grouping their digits — MT940 writes the decimal comma, ISO 20022 the decimal point — so
/// <see cref="ImportNumber.ThreeDigitTail.Decimal"/> is the correct reading of a lone separator here,
/// unlike in a free-form CSV where "1.234" really is ambiguous.
/// </summary>
internal static class BankStatementFile
{
    internal const string Mt940Adapter = "mt940";
    internal const string CamtAdapter = "camt";

    /// <summary>The file extensions handled here, for the upload endpoint's allow-list.</summary>
    internal static readonly string[] Extensions = [".sta", ".mt940", ".940", ".txt", ".xml", ".camt"];

    /// <summary>
    /// Picks the reader from the content, not the extension: banks name MT940 files .txt, .sta, .940 and
    /// .mt940 interchangeably, and CAMT arrives as .xml or .camt.
    /// </summary>
    internal static BankStatement Read(byte[] bytes)
    {
        var text = Decode(bytes);
        var trimmed = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (trimmed.StartsWith('<')) return Camt.Read(trimmed);
        if (Mt940.LooksLikeMt940(trimmed)) return Mt940.Read(trimmed);
        throw new InvalidDataException(
            "The file is neither an MT940 statement nor a CAMT XML document. Use the CSV import for a spreadsheet export.");
    }

    /// <summary>True when the extension could be a statement file at all.</summary>
    internal static bool CouldBeStatement(string extension) =>
        Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    // MT940 is latin-1 in practice and CAMT declares its own encoding; UTF-8 with a permissive fallback
    // covers both without mangling a German umlaut into a replacement character.
    private static string Decode(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(false, true);
        try { return utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    internal static decimal ParseAmount(string? text) =>
        ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Decimal);

    internal static string NormalizeCurrency(string? currency)
    {
        var trimmed = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetterUpper))
            throw new InvalidDataException($"'{currency}' is not a three-letter currency code.");
        return trimmed;
    }

    /// <summary>SWIFT MT940 "Customer Statement Message".</summary>
    internal static class Mt940
    {
        // :61: value date, optional entry date, debit/credit mark, optional funds code, amount, type id,
        // then the references. The mark carries the sign: D is money out, and a leading R reverses it.
        private static readonly Regex Line = new(
            @"^(?<value>\d{6})(?<entry>\d{4})?(?<mark>[RE]?[CD])(?<funds>[A-Z])?(?<amount>[0-9][0-9,]{0,14})(?<type>[A-Z][A-Z0-9]{3})(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // :60F:/:62F:/:64: - mark, date, currency, amount.
        private static readonly Regex Balance = new(
            @"^(?<mark>[CD])(?<date>\d{6})(?<currency>[A-Z]{3})(?<amount>[0-9][0-9,]{0,14})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool LooksLikeMt940(string text) =>
            text.Contains(":61:", StringComparison.Ordinal) ||
            text.Contains(":20:", StringComparison.Ordinal) ||
            text.Contains(":60F:", StringComparison.Ordinal);

        internal static BankStatement Read(string text)
        {
            var entries = new List<StatementEntry>();
            StatementBalance? closing = null;
            string? account = null;
            string? statementCurrency = null;

            // A tag's value continues on the following lines until the next ":tag:", which is how :86:
            // carries a multi-line remittance text.
            foreach (var (tag, value) in Tags(text))
            {
                switch (tag)
                {
                    case "25":
                        account ??= value.Trim();
                        break;
                    case "60F" or "60M":
                        if (Balance.Match(value.Trim()) is { Success: true } opening)
                            statementCurrency ??= NormalizeCurrency(opening.Groups["currency"].Value);
                        break;
                    case "62F" or "62M":
                        if (Balance.Match(value.Trim()) is { Success: true } match)
                        {
                            var amount = ParseAmount(match.Groups["amount"].Value);
                            // A "D" closing balance is an overdrawn account, i.e. a negative balance.
                            if (match.Groups["mark"].Value == "D") amount = -amount;
                            var currency = NormalizeCurrency(match.Groups["currency"].Value);
                            statementCurrency ??= currency;
                            closing = new StatementBalance(amount, currency, ParseDate(match.Groups["date"].Value));
                        }
                        break;
                    case "61":
                        var parsed = ParseLine(value, statementCurrency);
                        if (parsed is not null) entries.Add(parsed);
                        break;
                    case "86":
                        // Information belongs to the statement line above it.
                        if (entries.Count > 0) entries[^1] = WithInformation(entries[^1], value);
                        break;
                }
            }

            if (entries.Count == 0 && closing is null)
                throw new InvalidDataException("The MT940 file contains no statement lines and no balance.");

            return new BankStatement(Mt940Adapter, entries, closing, account);
        }

        private static IEnumerable<(string Tag, string Value)> Tags(string text)
        {
            string? tag = null;
            var value = new StringBuilder();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                // A "-" on its own closes the message block.
                if (line.Trim() == "-") continue;
                if (line.StartsWith(':') && line.IndexOf(':', 1) is var close && close > 1)
                {
                    if (tag is not null) yield return (tag, value.ToString());
                    tag = line[1..close];
                    value.Clear();
                    value.Append(line[(close + 1)..]);
                    continue;
                }
                if (tag is null) continue;
                value.Append('\n').Append(line);
            }
            if (tag is not null) yield return (tag, value.ToString());
        }

        private static StatementEntry? ParseLine(string value, string? statementCurrency)
        {
            var head = value.Split('\n')[0].Trim();
            var match = Line.Match(head);
            if (!match.Success) return null;

            var amount = ParseAmount(match.Groups["amount"].Value);
            var mark = match.Groups["mark"].Value;
            // D is money leaving the account; a leading R marks a reversal, which flips it back.
            var negative = mark.EndsWith('D');
            if (mark.StartsWith('R')) negative = !negative;
            if (negative) amount = -amount;

            var valueDate = ParseDate(match.Groups["value"].Value);
            var bookingDate = match.Groups["entry"].Success
                ? ParseEntryDate(match.Groups["entry"].Value, valueDate)
                : valueDate;

            // customer reference // bank reference. NONREF means the bank supplied none, so it must not
            // become an identity - every row would share it.
            var rest = match.Groups["rest"].Value;
            var separator = rest.IndexOf("//", StringComparison.Ordinal);
            var customerReference = (separator < 0 ? rest : rest[..separator]).Trim();
            var bankReference = separator < 0 ? string.Empty : rest[(separator + 2)..].Trim();
            var reference = !string.IsNullOrWhiteSpace(bankReference)
                ? bankReference
                : customerReference.Equals("NONREF", StringComparison.OrdinalIgnoreCase) ? null : customerReference;

            // The funds code is the currency's third letter, so it identifies the currency only together
            // with the statement's own. Without a statement currency there is nothing to complete.
            var currency = statementCurrency
                ?? throw new InvalidDataException("The MT940 file states no currency (no :60F:/:62F: balance).");

            return new StatementEntry(bookingDate, valueDate, amount, currency, null, null, reference);
        }

        // :86: is either free text or the German structured form ?00posting text?20..?29 remittance
        // ?32/?33 counterparty name. Both are read; nothing is invented when a field is absent.
        private static StatementEntry WithInformation(StatementEntry entry, string value)
        {
            var text = value.Replace("\n", string.Empty).Trim();
            if (!text.StartsWith('?'))
                return entry with { Description = Combine(entry.Description, text) };

            var description = new StringBuilder();
            var name = new StringBuilder();
            foreach (var field in text.Split('?', StringSplitOptions.RemoveEmptyEntries))
            {
                if (field.Length < 2 || !char.IsAsciiDigit(field[0]) || !char.IsAsciiDigit(field[1])) continue;
                var key = field[..2];
                var body = field[2..].Trim();
                if (body.Length == 0) continue;
                if (key is "32" or "33") name.Append(body);
                else if (key is "00" or "20" or "21" or "22" or "23" or "24" or "25" or "26" or "27" or "28" or "29")
                    description.Append(description.Length > 0 ? " " : string.Empty).Append(body);
            }

            return entry with
            {
                Description = Combine(entry.Description, description.ToString()),
                Counterparty = name.Length > 0 ? name.ToString() : entry.Counterparty
            };
        }

        private static string? Combine(string? existing, string addition) =>
            string.IsNullOrWhiteSpace(addition)
                ? existing
                : string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

        // YYMMDD. MT940 has no century; a statement is never from the 1900s.
        private static DateOnly ParseDate(string text)
        {
            if (!DateOnly.TryParseExact(text, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidDataException($"'{text}' is not an MT940 date.");
            return date;
        }

        // The entry date carries only MMDD. It is within days of the value date, so a December entry on a
        // January value date belongs to the previous year - not to this one.
        private static DateOnly ParseEntryDate(string text, DateOnly valueDate)
        {
            var month = int.Parse(text[..2], CultureInfo.InvariantCulture);
            var day = int.Parse(text[2..], CultureInfo.InvariantCulture);
            if (month is < 1 or > 12) throw new InvalidDataException($"'{text}' is not an MT940 entry date.");
            var year = valueDate.Year;
            if (month - valueDate.Month > 6) year--;
            else if (valueDate.Month - month > 6) year++;
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
                throw new InvalidDataException($"'{text}' is not an MT940 entry date.");
            return new DateOnly(year, month, day);
        }
    }

    /// <summary>ISO 20022 CAMT.053 statements and CAMT.052 account reports.</summary>
    internal static class Camt
    {
        internal static BankStatement Read(string text)
        {
            XDocument document;
            try { document = XDocument.Parse(text, LoadOptions.None); }
            catch (System.Xml.XmlException exception) { throw new InvalidDataException($"Invalid XML: {exception.Message}"); }

            // Matched by local name so every CAMT version and namespace works without a schema.
            var statements = Descendants(document.Root, "Stmt").Concat(Descendants(document.Root, "Rpt")).ToList();
            if (statements.Count == 0)
                throw new InvalidDataException("The XML contains no CAMT statement (Stmt) or report (Rpt) element.");

            var entries = new List<StatementEntry>();
            StatementBalance? closing = null;
            string? account = null;

            foreach (var statement in statements)
            {
                var accountElement = Element(statement, "Acct");
                account ??= Value(Element(Element(accountElement, "Id"), "IBAN"))
                            ?? Value(Element(Element(Element(accountElement, "Id"), "Othr"), "Id"));
                var accountCurrency = Value(Element(accountElement, "Ccy"));

                closing = ClosingBalance(statement, accountCurrency) ?? closing;

                foreach (var entry in Descendants(statement, "Ntry"))
                {
                    var amountElement = Element(entry, "Amt");
                    var raw = Value(amountElement);
                    if (raw is null) continue;
                    var amount = ParseAmount(raw);
                    if (Value(Element(entry, "CdtDbtInd")) is "DBIT") amount = -amount;

                    var currency = NormalizeCurrency(
                        Attribute(amountElement, "Ccy")
                        ?? accountCurrency
                        ?? throw new InvalidDataException("A CAMT entry states no currency."));

                    var booking = Date(Element(entry, "BookgDt"));
                    var valueDate = Date(Element(entry, "ValDt"));
                    if (booking is null && valueDate is null) continue;

                    var details = Descendants(entry, "TxDtls").FirstOrDefault();
                    var description = Descendants(entry, "Ustrd").Select(x => x.Value.Trim())
                        .Where(x => x.Length > 0).ToList();
                    var remittance = description.Count > 0
                        ? string.Join(" ", description)
                        : Value(Element(entry, "AddtlNtryInf"));

                    // The counterparty is whichever party is not the account holder, which the entry's
                    // own direction already tells us.
                    var parties = Element(details, "RltdPties");
                    var counterparty = amount < 0
                        ? Value(Element(Element(parties, "Cdtr"), "Nm")) ?? Value(Element(Element(Element(parties, "Cdtr"), "Pty"), "Nm"))
                        : Value(Element(Element(parties, "Dbtr"), "Nm")) ?? Value(Element(Element(Element(parties, "Dbtr"), "Pty"), "Nm"));

                    entries.Add(new StatementEntry(
                        booking ?? valueDate!.Value,
                        valueDate,
                        amount,
                        currency,
                        counterparty,
                        remittance,
                        Value(Element(entry, "NtryRef"))
                            ?? Value(Element(entry, "AcctSvcrRef"))
                            ?? Value(Element(Element(details, "Refs"), "AcctSvcrRef"))
                            ?? Value(Element(Element(details, "Refs"), "EndToEndId"))));
                }
            }

            if (entries.Count == 0 && closing is null)
                throw new InvalidDataException("The CAMT document contains no entries and no closing balance.");

            return new BankStatement(CamtAdapter, entries, closing, account);
        }

        // CLBD is the booked closing balance and the only one that anchors an account. CLAV (available)
        // includes what is not settled, PRCD is the previous statement's close, OPBD the opening one.
        private static StatementBalance? ClosingBalance(XElement statement, string? accountCurrency)
        {
            foreach (var balance in Descendants(statement, "Bal"))
            {
                var code = Value(Element(Element(Element(balance, "Tp"), "CdOrPrtry"), "Cd"));
                if (code is not "CLBD") continue;
                var amountElement = Element(balance, "Amt");
                var raw = Value(amountElement);
                var asOf = Date(Element(balance, "Dt"));
                if (raw is null || asOf is null) continue;
                var amount = ParseAmount(raw);
                if (Value(Element(balance, "CdtDbtInd")) is "DBIT") amount = -amount;
                return new StatementBalance(
                    amount,
                    NormalizeCurrency(Attribute(amountElement, "Ccy") ?? accountCurrency),
                    asOf.Value);
            }
            return null;
        }

        private static DateOnly? Date(XElement? element)
        {
            if (element is null) return null;
            var text = Value(Element(element, "Dt")) ?? Value(Element(element, "DtTm")) ?? element.Value.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateOnly.TryParseExact(text[..Math.Min(10, text.Length)], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            return null;
        }

        private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
            root is null ? [] : root.Descendants().Where(x => x.Name.LocalName == localName);

        private static XElement? Element(XElement? parent, string localName) =>
            parent?.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

        private static string? Value(XElement? element)
        {
            var text = element?.Value.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string? Attribute(XElement? element, string name) =>
            element?.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
    }
}
