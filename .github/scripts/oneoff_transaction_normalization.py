from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}\n--- needle ---\n{old}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


def replace_optional(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old in text:
        p.write_text(text.replace(old, new, 1), encoding="utf-8")


bank = "src/FullWorth.Banking/Services/BankSyncService.cs"

# Provider account labels: do not surface machine enum values such as PAYPAL_PREMIER_ACCOUNT.
replace_once(
    bank,
    '''        var product = GetString(json, "product");
        var display = GetString(json, "details") ?? product;
        return new(
            hash,
            uid,
            display ?? connection.InstitutionName,''',
    '''        var product = GetString(json, "product");
        var rawDisplay = GetString(json, "details") ?? product;
        var display = NormalizeAccountDisplayName(connection.InstitutionName, rawDisplay, product);
        return new(
            hash,
            uid,
            display,''')
replace_once(bank, '            HasDetails: display is not null,', '            HasDetails: rawDisplay is not null,')
replace_once(
    bank,
    '            DisplayName = GetString(details, "details") ?? GetString(details, "product") ?? account.DisplayName,',
    '''            DisplayName = NormalizeAccountDisplayName(
                account.DisplayName,
                GetString(details, "details") ?? GetString(details, "product"),
                GetString(details, "product")),''')

# Effective date: provider booking date first, then actual transaction date, then value date.
# Keep the original provider dates for the fingerprint so normalization cannot create duplicates.
replace_once(
    bank,
    '''        var booking = ParseDate(json, "booking_date");
        var value = ParseDate(json, "value_date");
        var counterparty = GetCounterparty(json);
        var description = GetDescription(json);''',
    '''        var providerBooking = ParseDate(json, "booking_date");
        var transactionDate = ParseDate(json, "transaction_date");
        var providerValue = ParseDate(json, "value_date");
        var booking = providerBooking ?? transactionDate ?? providerValue;
        var value = providerValue;
        var counterparty = GetCounterparty(json);
        var rawDescription = GetProviderDescription(json);
        var description = NormalizeDescription(rawDescription, counterparty);''')
replace_once(
    bank,
    '            : $"fp:{Fingerprint(account.IdentificationHash, status, booking, value, amount, currency, counterparty, description)}";',
    '''            : $"fp:{Fingerprint(account.IdentificationHash, status, providerBooking, providerValue, amount, currency, counterparty, rawDescription)}";''')

# Raw provider text remains available in RawJson/provider details. Description becomes a concise purpose.
replace_once(
    bank,
    '''    private static string? GetDescription(JsonElement json)
    {
        if (json.TryGetProperty("remittance_information", out var lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join(" | ", lines.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return GetString(json, "note") ?? GetNestedString(json, "bank_transaction_code", "description");
    }
''',
    r'''    private static string? GetProviderDescription(JsonElement json)
    {
        if (json.TryGetProperty("remittance_information", out var lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join(" | ", lines.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return GetString(json, "note") ?? GetNestedString(json, "bank_transaction_code", "description");
    }

    private static string? NormalizeDescription(string? raw, string? counterparty)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sepaPurpose = TryExtractSepaPurpose(raw);
        if (!string.IsNullOrWhiteSpace(sepaPurpose)) return LimitPurpose(sepaPurpose);

        var candidates = new List<string>();
        foreach (var piece in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = NormalizePurposePart(piece);
            if (string.IsNullOrWhiteSpace(text) || IsTechnicalPurpose(text)) continue;
            if (!string.IsNullOrWhiteSpace(counterparty) &&
                string.Equals(text, NormalizePurposePart(counterparty), StringComparison.OrdinalIgnoreCase))
                continue;
            if (candidates.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
            candidates.Add(text);
            if (candidates.Count == 2) break;
        }

        return candidates.Count == 0 ? null : LimitPurpose(string.Join(" · ", candidates));
    }

    private static string? TryExtractSepaPurpose(string raw)
    {
        var match = Regex.Match(
            raw,
            @"(?:^|\s)SVWZ\+(.*?)(?=\s+(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM)\+|\s*\|\s*|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(100));
        return match.Success ? NormalizePurposePart(match.Groups[1].Value) : null;
    }

    private static string? NormalizePurposePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsTechnicalPurpose(string value)
    {
        var text = value.Trim();
        if (Regex.IsMatch(
                text,
                @"^(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM|ENDTOENDID|MANDATE(?:ID)?|TRANSACTION(?:\s+ID)?|TXID|REFERENCE|REF)\s*[:+=]",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100)))
            return true;
        if (Guid.TryParse(text, out _)) return true;
        if (Regex.IsMatch(text, @"^\d{14,}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true;
        if (Regex.IsMatch(text, @"^[0-9A-F]{20,}$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) return true;
        return false;
    }

    private static string LimitPurpose(string value)
    {
        var normalized = NormalizePurposePart(value) ?? string.Empty;
        const int max = 180;
        return normalized.Length <= max ? normalized : normalized[..(max - 1)].TrimEnd() + "…";
    }

    private static string NormalizeAccountDisplayName(string fallback, string? candidate, string? product)
    {
        var display = NormalizePurposePart(candidate);
        var normalizedProduct = NormalizePurposePart(product);
        if (!string.IsNullOrWhiteSpace(display) && !LooksMachineGeneratedDisplayName(display)) return display;
        if (!string.IsNullOrWhiteSpace(normalizedProduct) && !LooksMachineGeneratedDisplayName(normalizedProduct))
            return normalizedProduct;
        return fallback;
    }

    private static bool LooksMachineGeneratedDisplayName(string value) =>
        Regex.IsMatch(
            value,
            @"^[A-Za-z0-9]+(?:_[A-Za-z0-9]+)+$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
''')

# Existing provider-generated account names may refresh; user-friendly/custom names remain sticky.
ingestion = "src/FullWorth.Backend/Modules/Ingestion/IngestionModule.cs"
replace_once(
    ingestion,
    '''            var mayRefreshDisplayName = isNew ||
                string.IsNullOrWhiteSpace(entity.DisplayName) ||
                string.Equals(entity.DisplayName, entity.InstitutionName, StringComparison.OrdinalIgnoreCase);''',
    '''            var mayRefreshDisplayName = isNew ||
                string.IsNullOrWhiteSpace(entity.DisplayName) ||
                string.Equals(entity.DisplayName, entity.InstitutionName, StringComparison.OrdinalIgnoreCase) ||
                LooksProviderGeneratedDisplayName(entity.DisplayName);''')
replace_once(
    ingestion,
    '''    private static IReadOnlyList<string> AccountHashes(FinanceAccount account)
    {''',
    '''    private static bool LooksProviderGeneratedDisplayName(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('_')) return false;
        return text.All(character => char.IsUpper(character) || char.IsDigit(character) || character == '_');
    }

    private static IReadOnlyList<string> AccountHashes(FinanceAccount account)
    {''')

# Legacy rows with only a value date must still participate in date filters.
backend_tx = "src/FullWorth.Backend/Modules/Transactions/TransactionsModule.cs"
replace_once(
    backend_tx,
    '        if (request.From.HasValue) q = q.Where(x => x.BookingDate >= request.From.Value);\n        if (request.To.HasValue) q = q.Where(x => x.BookingDate <= request.To.Value);',
    '        if (request.From.HasValue) q = q.Where(x => (x.BookingDate ?? x.ValueDate) >= request.From.Value);\n        if (request.To.HasValue) q = q.Where(x => (x.BookingDate ?? x.ValueDate) <= request.To.Value);')

web = "src/FullWorth.Web/wwwroot/features/transactions.js"

# Frontend fallback protects already stored legacy descriptions until they are re-synced.
replace_once(
    web,
    "function deLabel(de, en) { return document.documentElement.lang?.startsWith('en') ? en : de; }\n",
    r'''function deLabel(de, en) { return document.documentElement.lang?.startsWith('en') ? en : de; }
function transactionDate(item) { return item?.bookingDate || item?.valueDate || null; }
function transactionListPurpose(item, merchantName, categoryName) {
  const raw = String(item?.description || '').trim();
  if (!raw) return '';
  const sepa = raw.match(/(?:^|\s)SVWZ\+(.*?)(?=\s+(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM)\+|\s*\|\s*|$)/i);
  const pieces = sepa?.[1] ? [sepa[1]] : raw.split(/\s*\|\s*|\r?\n/);
  const technical = /^(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM|ENDTOENDID|MANDATE(?:ID)?|TRANSACTION(?:\s+ID)?|TXID|REFERENCE|REF)\s*[:+=]/i;
  const ignored = [merchantName, categoryName, item?.account].map(x => String(x || '').trim().toLowerCase()).filter(Boolean);
  const result = [];
  for (const piece of pieces) {
    const text = String(piece || '').replace(/\s+/g, ' ').trim();
    if (!text || technical.test(text)) continue;
    if (/^\d{14,}$/.test(text) || /^[0-9a-f]{20,}$/i.test(text) || /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(text)) continue;
    if (ignored.includes(text.toLowerCase())) continue;
    if (result.some(x => x.toLowerCase() === text.toLowerCase())) continue;
    result.push(text);
    if (result.length === 2) break;
  }
  const joined = result.join(' · ');
  return joined.length <= 160 ? joined : joined.slice(0, 159).trimEnd() + '…';
}
function displayAccountName(value) {
  const text = String(value || '').trim();
  if (!/^[A-Z0-9]+(?:_[A-Z0-9]+)+$/.test(text)) return text;
  const special = { PAYPAL: 'PayPal', IBAN: 'IBAN', SEPA: 'SEPA', EUR: 'EUR', USD: 'USD', GBP: 'GBP', IDR: 'IDR', VISA: 'Visa' };
  return text.split('_').map(part => special[part] || (part.charAt(0) + part.slice(1).toLowerCase())).join(' ');
}
''')

# List grouping/date display use one consistent date fallback.
replace_once(web, "String(x.bookingDate || '').slice(0, 10)", "String(transactionDate(x) || '').slice(0, 10)")
replace_once(web, "ctx.date(x.bookingDate || x.valueDate)", "ctx.date(transactionDate(x))")

# Compact row: desktop gets only a sanitized purpose; mobile gets the category instead of raw provider text.
replace_once(
    web,
    "    const cat = x.categoryName || x.category || ctx.get('common.uncategorized');\n    const tr = document.createElement('tr');",
    "    const cat = x.categoryName || x.category || ctx.get('common.uncategorized');\n    const purpose = transactionListPurpose(x, name, cat);\n    const tr = document.createElement('tr');")
replace_once(
    web,
    '''<span class="tx-cp-main"><strong>${ctx.esc(name)}</strong>${markers(x)}<span class="row-sub">${ctx.esc(x.description || cat)}</span></span>''',
    '''<span class="tx-cp-main"><strong>${ctx.esc(name)}</strong>${markers(x)}${purpose ? `<span class="row-sub tx-list-purpose">${ctx.esc(purpose)}</span>` : ''}<span class="row-sub tx-mobile-category">${ctx.esc(cat)}</span></span>''')

# Stale machine labels are at least humanized immediately; the next sync replaces them at the data layer.
replace_once(web, "  const name = x.account || '';", "  const name = displayAccountName(x.account || '');")

# Nice-to-have fallbacks outside the main list are intentionally optional so source formatting changes
# cannot block the core fix.
replace_optional(web, "String(t.bookingDate || '').slice(0, 10)", "String(transactionDate(t) || '').slice(0, 10)")
replace_optional(web, "String(item.bookingDate || '').slice(0, 10)", "String(transactionDate(item) || '').slice(0, 10)")
replace_optional(web, "ctx.date(t.bookingDate)", "ctx.date(transactionDate(t))")
replace_optional(web, "ctx.esc(t.account || '')", "ctx.esc(displayAccountName(t.account || ''))")
replace_optional(
    web,
    "label = a?.displayName || a?.institutionName || '';",
    "label = displayAccountName(a?.displayName || a?.institutionName || '');")

css = Path("src/FullWorth.Web/wwwroot/app.css")
css_text = css.read_text(encoding="utf-8")
marker = "/* Transaction list metadata normalization */"
if marker not in css_text:
    css.write_text(
        css_text.rstrip() + "\n\n" + marker + "\n"
        "#transactions-body .tx-mobile-category { display: none; }\n"
        "@media (max-width: 760px) {\n"
        "  #transactions-body .tx-list-purpose { display: none; }\n"
        "  #transactions-body .tx-mobile-category { display: block; }\n"
        "}\n",
        encoding="utf-8")

print("transaction normalization patch applied")
