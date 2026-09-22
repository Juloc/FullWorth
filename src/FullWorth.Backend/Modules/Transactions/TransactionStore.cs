using System.Linq.Expressions;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public sealed class TransactionStore(FullWorthDbContext db, TransferRuleStore transferRules)
{
    public async Task<object> SearchForUserAsync(Guid userId, Guid? fullWorthSpaceId, TransactionQuery request, CancellationToken ct)
    {
        var q = AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false);

        if (request.AccountId.HasValue)
            q = q.Where(x => x.AccountId == request.AccountId.Value);

        if (request.AccountGroupId.HasValue)
        {
            var accountGroupId = request.AccountGroupId.Value;
            q = q.Where(x => db.Accounts.Any(account =>
                account.Id == x.AccountId &&
                account.GroupId == accountGroupId &&
                (!fullWorthSpaceId.HasValue || account.FullWorthSpaceId == fullWorthSpaceId.Value)));
        }

        if (request.CategoryId.HasValue)
        {
            var categoryId = request.CategoryId.Value;
            IReadOnlyCollection<Guid> categoryIds = [categoryId];

            if (request.IncludeDescendants == true)
            {
                var categorySpaceId = fullWorthSpaceId;
                if (!categorySpaceId.HasValue)
                {
                    categorySpaceId = await db.Categories.AsNoTracking()
                        .Where(category =>
                            category.Id == categoryId &&
                            db.FullWorthSpaceMembers.Any(member =>
                                member.FullWorthSpaceId == category.FullWorthSpaceId &&
                                member.UserId == userId))
                        .Select(category => (Guid?)category.FullWorthSpaceId)
                        .SingleOrDefaultAsync(ct);
                }

                if (!categorySpaceId.HasValue)
                {
                    q = q.Where(_ => false);
                    categoryIds = Array.Empty<Guid>();
                }
                else
                {
                    var tree = await db.Categories.AsNoTracking()
                        .Where(category => category.FullWorthSpaceId == categorySpaceId.Value)
                        .Select(category => new { category.Id, category.ParentId })
                        .ToListAsync(ct);
                    var children = tree
                        .Where(category => category.ParentId.HasValue)
                        .GroupBy(category => category.ParentId!.Value)
                        .ToDictionary(group => group.Key, group => group.Select(category => category.Id).ToList());
                    var resolved = new HashSet<Guid>();
                    var pending = new Stack<Guid>();
                    pending.Push(categoryId);
                    while (pending.Count > 0)
                    {
                        var current = pending.Pop();
                        if (!resolved.Add(current)) continue;
                        if (children.TryGetValue(current, out var descendants))
                            foreach (var child in descendants) pending.Push(child);
                    }
                    categoryIds = resolved;
                }
            }

            if (categoryIds.Count > 0)
            {
                q = q.Where(x =>
                    (x.CategoryId.HasValue && categoryIds.Contains(x.CategoryId.Value)) ||
                    db.TransactionAllocations.Any(a =>
                        a.TransactionId == x.Id &&
                        a.CategoryId.HasValue &&
                        categoryIds.Contains(a.CategoryId.Value)) ||
                    db.Purchases.Any(p =>
                        (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                        (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                        p.Items.Any(i => i.CategoryId.HasValue && categoryIds.Contains(i.CategoryId.Value))));
            }
        }

        if (request.From.HasValue) q = q.Where(x => (x.BookingDate ?? x.ValueDate) >= request.From.Value);
        if (request.To.HasValue) q = q.Where(x => (x.BookingDate ?? x.ValueDate) <= request.To.Value);
        if (request.Direction == "income") q = q.Where(x => x.Amount > 0);
        if (request.Direction == "expense") q = q.Where(x => x.Amount < 0);

        if (request.IgnoredOnly == true) q = q.Where(x => x.IsIgnored);
        else if (request.IncludeIgnored != true) q = q.Where(x => !x.IsIgnored);

        if (request.TransfersOnly == true) q = q.Where(x => x.IsTransfer);
        if (request.RefundOnly == true) q = q.Where(x => x.RefundOfTransactionId != null);

        if (request.HasReceipt.HasValue)
        {
            var hasReceipt = request.HasReceipt.Value;
            q = q.Where(x =>
                (db.Purchases.Any(p =>
                    (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                    (p.Visibility != "private" || p.CreatedByUserId == userId))) == hasReceipt);
        }

        var normalizedStatus = request.Status?.Trim().ToLowerInvariant();
        if (normalizedStatus == "pending") q = q.Where(x => x.Status == "PDNG");
        if (normalizedStatus == "booked") q = q.Where(x => x.Status != "PDNG");

        if (request.MinAmount.HasValue)
        {
            var minimum = Math.Abs(request.MinAmount.Value);
            q = q.Where(x => Math.Abs(x.Amount) >= minimum);
        }
        if (request.MaxAmount.HasValue)
        {
            var maximum = Math.Abs(request.MaxAmount.Value);
            q = q.Where(x => Math.Abs(x.Amount) <= maximum);
        }

        if (request.CollectionIds is { Count: > 0 } requestedCollections)
        {
            // Eine Sammlung ist ein FinanceTag (#124) und gehoert einem Space. Es wird nie nach einer
            // Kennung gefiltert, die der Anfragende nicht sehen darf - sonst verriete schon die
            // Trefferzahl, welche Buchungen in einer fremden Sammlung liegen.
            var visible = await db.Set<FinanceTag>().AsNoTracking()
                .Where(tag =>
                    requestedCollections.Contains(tag.Id) &&
                    (!fullWorthSpaceId.HasValue || tag.FullWorthSpaceId == fullWorthSpaceId.Value) &&
                    db.FullWorthSpaceMembers.Any(member =>
                        member.FullWorthSpaceId == tag.FullWorthSpaceId && member.UserId == userId))
                .Select(tag => tag.Id)
                .ToArrayAsync(ct);

            // ODER: in mindestens einer der gewaehlten Sammlungen. Das DISTINCT ist der eigentliche
            // Punkt - eine Buchung, die in zweien der gewaehlten liegt, stuende sonst zweimal in der
            // Liste. Npgsql uebersetzt das anschliessende Contains zu einem "= ANY(@p)", also EINEM
            // Parameter und keiner IN-Liste, die mit der Sammlung waechst.
            var members = visible.Length == 0
                ? []
                : await db.Database.SqlQuery<Guid>(
                    $"""SELECT DISTINCT "TransactionId" FROM "TransactionTags" WHERE "TagId" = ANY({visible})""")
                    .ToArrayAsync(ct);

            q = members.Length == 0 ? q.Where(_ => false) : q.Where(x => members.Contains(x.Id));
        }

        if (request.MerchantId.HasValue)
        {
            if (!fullWorthSpaceId.HasValue)
            {
                q = q.Where(_ => false);
            }
            else
            {
                var merchantId = request.MerchantId.Value;
                var merchant = await db.Merchants.AsNoTracking()
                    .Where(item => item.Id == merchantId && item.FullWorthSpaceId == fullWorthSpaceId.Value)
                    .Select(item => new { item.NormalizedName })
                    .SingleOrDefaultAsync(ct);
                if (merchant is null)
                {
                    q = q.Where(_ => false);
                }
                else
                {
                    var keys = await db.MerchantAliases.AsNoTracking()
                        .Where(alias => alias.FullWorthSpaceId == fullWorthSpaceId.Value && alias.MerchantId == merchantId)
                        .Select(alias => alias.NormalizedAlias)
                        .ToListAsync(ct);
                    if (!string.IsNullOrWhiteSpace(merchant.NormalizedName)) keys.Add(merchant.NormalizedName);
                    keys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToList();
                    q = keys.Count == 0 ? q.Where(_ => false) : q.Where(MerchantIdentityPredicate(keys));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Merchant))
        {
            var merchantPattern = $"%{request.Merchant.Trim()}%";
            q = q.Where(x =>
                (x.NormalizedCounterparty != null && EF.Functions.ILike(x.NormalizedCounterparty, merchantPattern)) ||
                (x.Counterparty != null && EF.Functions.ILike(x.Counterparty, merchantPattern)));
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var pattern = $"%{request.Query.Trim()}%";

            // Die Treffer aus Kaeufen werden VORAB zu Buchungskennungen aufgeloest, statt als
            // Unterabfrage neben der Textsuche zu stehen (#161).
            //
            // Das ist der ganze Unterschied zwischen 160 ms und 0,08 ms, gemessen an 200 000
            // Buchungen: ein "ODER EXISTS(...)" neben dem Wortindex zwingt PostgreSQL, jede Zeile zu
            // pruefen - der GIN-Index wird wertlos. Zwei Bedingungen auf derselben Tabelle kann es
            // dagegen als BitmapOr ueber ZWEI Indizes zusammenlegen.
            //
            // Die Kauf-Abfrage selbst laeuft auf einer viel kleineren Tabelle und bleibt eine
            // Textsuche: Artikelnamen sind kurz, und ein eigener Wortindex dafuer waere ein zweites
            // Suchdokument, das mit dem ersten auseinanderlaufen kann.
            // Ein Kauf zeigt auf zwei Weisen auf eine Buchung: direkt, oder ueber eine
            // Zahlungsverknuepfung. Zwei einfache Abfragen statt einer verschachtelten - die liesse
            // sich nicht uebersetzen, und ein Ausdruck, den der Anbieter erst zur Laufzeit ablehnt,
            // ist eine Fehlermeldung statt einer Suche.
            var matchingPurchases = db.Purchases.AsNoTracking().Where(p =>
                (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                (EF.Functions.ILike(p.Merchant, pattern) || p.Items.Any(i => EF.Functions.ILike(i.Name, pattern))));

            var direct = await matchingPurchases
                .Where(p => p.TransactionId != null)
                .Select(p => p.TransactionId!.Value)
                .Distinct().Take(5000).ToArrayAsync(ct);
            var viaLinks = await matchingPurchases
                .SelectMany(p => p.PaymentLinks.Select(link => link.TransactionId))
                .Distinct().Take(5000).ToArrayAsync(ct);
            var purchaseMatches = direct.Concat(viaLinks).Distinct().ToArray();

            // Wortanfang statt irgendwo im Wort: "REW" findet "REWE". Der Doppelpunkt-Stern ist die
            // Praefix-Schreibweise von PostgreSQL; die Anfuehrungszeichen halten alles, was der
            // Benutzer tippt, als EIN Wort zusammen - sonst waere ein ":" in seiner Eingabe plötzlich
            // Syntax.
            var term = TransactionSearchTerm.ToPrefixQuery(request.Query);
            q = term is null
                ? q.Where(x => purchaseMatches.Contains(x.Id))
                : q.Where(x =>
                    x.SearchVector!.Matches(EF.Functions.ToTsQuery("simple", term)) ||
                    purchaseMatches.Contains(x.Id));
        }

        var descending = !string.Equals(request.Order, "asc", StringComparison.OrdinalIgnoreCase);
        q = request.Sort?.ToLowerInvariant() switch
        {
            "amount" => descending ? q.OrderByDescending(x => x.Amount) : q.OrderBy(x => x.Amount),
            "counterparty" => descending ? q.OrderByDescending(x => x.Counterparty) : q.OrderBy(x => x.Counterparty),
            // A pending entry is not booked yet, so on the date axis it belongs ahead of everything
            // that IS booked - including today. Ordering purely by booking date put a pending row
            // dated today underneath the "today" header among real bookings, and a pending row with
            // no booking date at all into an unlabelled group above it (PostgreSQL sorts NULLs first
            // on DESC). Falling back to the value date keeps a row whose booking date the bank has
            // not published yet in its real place instead of at the very top.
            // EINE Spalte statt vier (#161). Sie bildet dasselbe Tupel ab - vorgemerkt, Datum,
            // UpdatedAt, Id - und wird von der Datenbank aus denselben Spalten gerechnet. Der
            // Unterschied ist nicht kosmetisch: nur ueber eine Spalte wird aus der Cursor-Bedingung
            // ein Index-Bereich. Gemessen an 200 000 Buchungen auf Seite 2000: 80 ms mit OFFSET,
            // 24 ms als Cursor ueber vier Spalten, 0,3 ms so.
            //
            // Die Id am Ende ist kein Beiwerk: keines der ersten drei Felder ist eindeutig. Ein
            // Import legt dutzende Buchungen mit demselben Datum und demselben Zeitstempel an;
            // zwischen ihnen war die Reihenfolge bei JEDER Abfrage neu beliebig, und dann
            // ueberspringt ein Seitenwechsel Zeilen oder zeigt sie zweimal. Das galt schon fuer das
            // bisherige Skip/Take - der Cursor macht den Fehler sichtbar, statt ihn zu verursachen.
            _ => descending
                ? q.OrderByDescending(x => x.TimelineSortKey)
                : q.OrderBy(x => x.TimelineSortKey)
        };

        var limit = Math.Clamp(request.Limit ?? 200, 1, 5000);
        var timelineSort = request.Sort?.ToLowerInvariant() is null or "" or "date";
        var cursor = TransactionCursor.Normalize(request.After);

        // Der Cursor gilt fuer die Timeline-Sortierung. Nach Betrag oder Gegenpartei zu blaettern ist
        // eine Auswertung und keine Timeline - dort bleibt es beim Offset, statt ein zweites Tupel zu
        // pflegen, das niemand scrollt.
        if (cursor is not null && timelineSort)
        {
            // Ein Vergleich auf einer Spalte, spiegelbildlich fuer ASC und DESC. Genau das wird zum
            // Index-Bereich; eine ausgeschriebene Bedingung ueber vier Spalten wuerde es nicht.
            q = descending
                ? q.Where(x => string.Compare(x.TimelineSortKey, cursor) < 0)
                : q.Where(x => string.Compare(x.TimelineSortKey, cursor) > 0);
        }

        var offset = cursor is not null && timelineSort ? 0 : Math.Max(0, request.Offset ?? 0);

        // "Geht es weiter?" und "wie viele sind es insgesamt?" sind zwei Fragen, und nur die erste
        // braucht jede Seite. Beantwortet wird sie mit EINER Zeile mehr, als der Aufrufer sehen will.
        //
        // Die Gesamtzahl kostet dieselbe Arbeit wie die Seite selbst. Sie beim Nachladen jedes Mal
        // neu zu zaehlen ist die Haelfte der Arbeit fuer eine Zahl, die sich nicht geaendert hat -
        // deshalb nur auf der ersten Seite, und auf den folgenden gar nicht (#161).
        var total = cursor is null ? await q.CountAsync(ct) : (int?)null;

        var items = await q.Skip(offset).Take(limit + 1).Select(x => new TransactionListItem(
            x.Id,
            x.AccountId,
            db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(),
            x.BookingDate,
            x.ValueDate,
            x.Amount,
            x.Currency,
            x.Counterparty,
            x.NormalizedCounterparty,
            x.Description,
            x.MerchantCategoryCode,
            x.Status,
            x.CategoryId,
            db.Categories
                .Where(category => category.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == category.FullWorthSpaceId))
                .Select(category => category.Name)
                .FirstOrDefault(),
            db.Categories
                .Where(category => category.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == category.FullWorthSpaceId))
                .Select(category => category.Icon != null && category.Icon != "" ? category.Icon : category.Key)
                .FirstOrDefault(),
            x.UserNote,
            x.IsIgnored,
            x.IsTransfer,
            x.CategorizationSource,
            x.UpdatedAt,
            // Die Kauf-Zaehler stehen hier NICHT mehr (#161, Teil L). Sie kommen fuer die ganze Seite
            // in einer Abfrage nach - siehe PurchaseCountsAsync weiter unten. Gemessen an 200 000
            // Buchungen: 716 ms je Seite als Unterabfrage pro Zeile, 5,5 ms fuer die Seite am Stueck.
            0,
            0,
            null,
            null,
            db.Accounts.Any(a => a.Id == x.AccountId && a.BankConnectionId == null),
            x.ProviderTransactionId != null && x.ProviderTransactionId != "" &&
            db.Accounts.Any(a => a.Id == x.AccountId && a.BankConnectionId != null &&
                db.BankConnections.Any(c => c.Id == a.BankConnectionId && c.Provider != "fints")),
            x.TimelineSortKey)).ToListAsync(ct);

        // Die Kauf-Zaehler fuer diese Seite, in einer Abfrage (#161, Teil L). Die eine Zeile mehr, die
        // nur die Frage "geht es weiter?" beantwortet, bleibt dabei aussen vor - sie wird gleich
        // verworfen und soll keine Nacharbeit kosten.
        var pageIds = items.Take(limit).Select(item => item.Id).ToArray();
        if (pageIds.Length > 0)
        {
            var counts = await PurchaseCountsAsync(userId, pageIds, ct);
            for (var index = 0; index < items.Count; index++)
                if (counts.TryGetValue(items[index].Id, out var count))
                    items[index] = items[index] with
                    {
                        PurchaseCount = count.Purchases,
                        PurchaseItemCount = count.Items
                    };
        }

        if (fullWorthSpaceId.HasValue && items.Count > 0)
        {
            var merchants = await db.Merchants.AsNoTracking()
                .Where(merchant => merchant.FullWorthSpaceId == fullWorthSpaceId.Value)
                .Select(merchant => new { merchant.Id, merchant.Name, merchant.NormalizedName })
                .ToListAsync(ct);
            var aliases = await db.MerchantAliases.AsNoTracking()
                .Where(alias => alias.FullWorthSpaceId == fullWorthSpaceId.Value)
                .Select(alias => new { alias.MerchantId, alias.NormalizedAlias })
                .ToListAsync(ct);
            var merchantNames = merchants.ToDictionary(merchant => merchant.Id, merchant => merchant.Name);
            var matchers = new List<(string Key, Guid MerchantId)>();
            matchers.AddRange(merchants.Where(merchant => !string.IsNullOrWhiteSpace(merchant.NormalizedName))
                .Select(merchant => (merchant.NormalizedName, merchant.Id)));
            matchers.AddRange(aliases.Where(alias => !string.IsNullOrWhiteSpace(alias.NormalizedAlias))
                .Select(alias => (alias.NormalizedAlias, alias.MerchantId)));
            matchers = matchers.OrderByDescending(item => item.Key.Length).ToList();

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var normalized = item.NormalizedCounterparty ?? MerchantNormalization.Normalize(item.Counterparty);
                if (normalized is null) continue;
                var match = matchers.FirstOrDefault(candidate => normalized.Contains(candidate.Key, StringComparison.Ordinal));
                if (match.MerchantId == Guid.Empty || !merchantNames.TryGetValue(match.MerchantId, out var merchantName)) continue;
                items[index] = item with { MerchantId = match.MerchantId, MerchantDisplayName = merchantName };
            }
        }

        // Die eine Zeile mehr war nur die Frage "geht es weiter?" - sie gehoert nicht in die Antwort.
        var hasNext = items.Count > limit;
        if (hasNext) items.RemoveAt(items.Count - 1);

        // Der Cursor IST der Sortierschluessel der letzten gezeigten Zeile - nichts, was daraus
        // abgeleitet oder nachgerechnet wird. Eine zweite Rechnung waere eine zweite Meinung darueber,
        // wo die Seite endet.
        var nextCursor = hasNext && items.Count > 0 ? items[^1].TimelineSortKey : null;

        return new { total, offset, limit, items, nextCursor, hasNext };
    }

    public async Task<object?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.AccountId,
                Account = db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(),
                x.BookingDate,
                x.ValueDate,
                x.Amount,
                x.Currency,
                x.Counterparty,
                x.NormalizedCounterparty,
                x.Description,
                x.MerchantCategoryCode,
                x.EntryReference,
                x.Status,
                x.CategoryId,
                Category = db.Categories
                    .Where(c => c.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == c.FullWorthSpaceId))
                    .Select(c => c.Name)
                    .FirstOrDefault(),
                CategoryName = db.Categories
                    .Where(c => c.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == c.FullWorthSpaceId))
                    .Select(c => c.Name)
                    .FirstOrDefault(),
                CategoryIconKey = db.Categories
                    .Where(c => c.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == c.FullWorthSpaceId))
                    .Select(c => c.Icon != null && c.Icon != "" ? c.Icon : c.Key)
                    .FirstOrDefault(),
                x.UserNote,
                x.IsIgnored,
                x.IsTransfer,
                x.TransferPurpose,
                x.TransferGroupId,
                x.RefundOfTransactionId,
                x.RefundCategoryId,
                x.CategorizationSource,
                IsManual = x.ExternalKey.StartsWith("manual:"),
                x.FirstSeenAt,
                x.UpdatedAt
            }).SingleOrDefaultAsync(ct);
        if (tx is null) return null;

        // Only expose the transfer counterpart when the caller can actually see its account. Otherwise a
        // shared transfer would leak a transaction from a hidden/other-owner account.
        object? counterpart = tx.TransferGroupId is { } groupId
            ? await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
                .Where(x => x.TransferGroupId == groupId && x.Id != id)
                .Select(x => new { x.Id, x.AccountId, Account = db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(), x.Amount, x.Currency, x.BookingDate })
                .FirstOrDefaultAsync(ct)
            : null;

        var purchases = await db.Purchases.AsNoTracking()
            .Where(x =>
                x.FullWorthSpaceId == fullWorthSpaceId &&
                (x.TransactionId == id || x.PaymentLinks.Any(link => link.TransactionId == id)) &&
                (x.Visibility != "private" || x.CreatedByUserId == userId))
            .Include(x => x.Items)
            .OrderBy(x => x.PurchaseDate)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        var rawStatusHistory = await db.AuditEvents.AsNoTracking()
            .Where(x =>
                x.FullWorthSpaceId == fullWorthSpaceId &&
                x.EntityType == "Transaction" &&
                x.EntityId == id &&
                (x.Action == "transaction.pending_observed" || x.Action == "transaction.status_changed"))
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Take(20)
            .Select(x => new TransactionStatusAuditRow(x.Action, x.MetadataJson, x.OccurredAt))
            .ToListAsync(ct);
        var statusHistory = rawStatusHistory
            .Select(ParseStatusHistory)
            .Where(x => x is not null)
            .Cast<TransactionStatusHistoryItem>()
            .OrderBy(x => x.ObservedAt)
            .ToList();

        return new { transaction = tx, transferCounterpart = counterpart, purchases, statusHistory };
    }

    private static TransactionStatusHistoryItem? ParseStatusHistory(TransactionStatusAuditRow row)
    {
        if (string.Equals(row.Action, "transaction.pending_observed", StringComparison.Ordinal))
            return new TransactionStatusHistoryItem("PDNG", null, row.OccurredAt);

        if (!string.Equals(row.Action, "transaction.status_changed", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(row.MetadataJson))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<TransactionStatusAuditMetadata>(
                row.MetadataJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return metadata is null
                ? null
                : new TransactionStatusHistoryItem(metadata.ToStatus, metadata.FromStatus, row.OccurredAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<string?> GetOwnershipForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Join(db.Accounts.AsNoTracking(), transaction => transaction.AccountId, account => account.Id, (transaction, account) => new { transaction, account })
            .Where(x => x.account.FullWorthSpaceId == fullWorthSpaceId &&
                        db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Join(db.AccountOwners.AsNoTracking().Where(owner => owner.UserId == userId),
                x => x.account.Id,
                owner => owner.AccountId,
                (x, owner) => owner.OwnershipType)
            .SingleOrDefaultAsync(ct);

    public async Task<TransactionClassificationResult> ClassifyForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, TransactionClassification request, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransactionClassificationResult.NotFound;
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            return TransactionClassificationResult.InvalidCategory;

        if (!request.IsTransfer && entity.IsTransfer && entity.TransferGroupId is { } releasedGroup)
        {
            // Demoting the pair also clears the counterpart's transfer state; refuse (as a non-leaking 404)
            // when the caller can no longer write every mate, so a revoked counterpart is never mutated.
            if (!await CanWriteAllTransferMatesAsync(userId, fullWorthSpaceId, releasedGroup, entity.Id, ct))
                return TransactionClassificationResult.NotFound;
            await ReleaseTransferGroupAsync(releasedGroup, entity.Id, ct);
        }

        entity.CategoryId = request.CategoryId;
        entity.IsIgnored = request.IsIgnored;
        entity.IsTransfer = request.IsTransfer;
        entity.TransferPurpose = request.IsTransfer ? Normalize(request.TransferPurpose) : null;
        if (!request.IsTransfer) entity.TransferGroupId = null;
        entity.UserNote = Normalize(request.UserNote);
        entity.CategorizationSource = "manual";
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TransactionClassificationResult.Updated;
    }

    public async Task<TransferLinkResult> LinkTransferForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid firstId, Guid secondId, CancellationToken ct)
    {
        if (firstId == secondId) return TransferLinkResult.Invalid;
        var owned = AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true);
        var first = await owned.SingleOrDefaultAsync(x => x.Id == firstId, ct);
        var second = await owned.SingleOrDefaultAsync(x => x.Id == secondId, ct);
        if (first is null || second is null) return TransferLinkResult.NotFound;
        if (first.TransferGroupId is not null || second.TransferGroupId is not null) return TransferLinkResult.Invalid;
        if (first.AccountId == second.AccountId) return TransferLinkResult.Invalid;
        if (!string.Equals(first.Currency, second.Currency, StringComparison.OrdinalIgnoreCase)) return TransferLinkResult.Invalid;
        if (first.Amount == 0m || first.Amount != -second.Amount) return TransferLinkResult.Invalid;

        var groupId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        foreach (var entity in new[] { first, second })
        {
            entity.TransferGroupId = groupId;
            entity.IsTransfer = true;
            entity.UpdatedAt = now;
        }

        // #146: aus einer BESTAETIGTEN Verknuepfung lernen. Genau hier und nicht in der Erkennung -
        // sonst schriebe die Mechanik ihre eigenen Treffer als Regel fest, und ein einmaliger
        // Fehltreffer waere von da an eine Regel.
        await transferRules.LearnFromLinkAsync(userId, fullWorthSpaceId, first, second, ct);

        await db.SaveChangesAsync(ct);
        return TransferLinkResult.Linked;
    }

    /// <summary>
    /// "Umbuchung, aber das Gegenkonto wird hier nicht gefuehrt" (#146).
    ///
    /// Der zweite Fall des Issues: eine Ueberweisung auf das Sparkonto ausser Haus, eine Einzahlung
    /// ins Bargeld, ein Verrechnungskonto. Es gibt keine Gegenbuchung und soll auch keine erfundene
    /// geben - die Buchung wird als Umbuchung gefuehrt, faellt damit aus jeder Einnahmen- und
    /// Ausgabenauswertung heraus, und die Entscheidung wird als Regel behalten.
    /// </summary>
    public async Task<TransferLinkResult> MarkExternalTransferForOwnerAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, string? purpose, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransferLinkResult.NotFound;
        // Wer bereits eine Gegenbuchung hat, hat kein externes Ziel. Beides gleichzeitig zu behaupten
        // waere ein Widerspruch, den spaeter niemand aufloest.
        if (entity.TransferGroupId is not null) return TransferLinkResult.Invalid;

        entity.IsTransfer = true;
        entity.TransferPurpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim()[..Math.Min(purpose.Trim().Length, 80)];
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await transferRules.LearnExternalAsync(userId, fullWorthSpaceId, entity, ct);
        await db.SaveChangesAsync(ct);
        return TransferLinkResult.Linked;
    }

    public async Task<TransferUnlinkResult> UnlinkTransferForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransferUnlinkResult.NotFound;
        if (entity.TransferGroupId is not { } groupId) return TransferUnlinkResult.NotLinked;
        // Unlinking mutates the counterpart too; refuse (non-leaking 404) when a mate is no longer writable.
        if (!await CanWriteAllTransferMatesAsync(userId, fullWorthSpaceId, groupId, id, ct))
            return TransferUnlinkResult.NotFound;
        await ReleaseTransferGroupAsync(groupId, id, ct);
        entity.IsTransfer = false;
        entity.TransferGroupId = null;
        entity.TransferPurpose = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TransferUnlinkResult.Unlinked;
    }

    // True only when the caller owns (write access) every other transaction in the transfer group, so
    // releasing/demoting the group never silently mutates a transaction in a hidden/revoked account.
    private async Task<bool> CanWriteAllTransferMatesAsync(Guid userId, Guid fullWorthSpaceId, Guid groupId, Guid excludeId, CancellationToken ct)
    {
        var mateIds = await db.Transactions.AsNoTracking()
            .Where(x => x.TransferGroupId == groupId && x.Id != excludeId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (mateIds.Count == 0) return true;
        var writable = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true)
            .CountAsync(x => mateIds.Contains(x.Id), ct);
        return writable == mateIds.Count;
    }

    private async Task ReleaseTransferGroupAsync(Guid groupId, Guid excludeId, CancellationToken ct)
    {
        var mates = await db.Transactions.Where(x => x.TransferGroupId == groupId && x.Id != excludeId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var mate in mates)
        {
            mate.IsTransfer = false;
            mate.TransferGroupId = null;
            mate.TransferPurpose = null;
            mate.UpdatedAt = now;
        }
    }

    public async Task<object?> GetAllocationsForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Amount, x.Currency })
            .SingleOrDefaultAsync(ct);
        if (tx is null) return null;

        var raw = await db.TransactionAllocations.AsNoTracking()
            .Where(a => a.TransactionId == id)
            .Select(a => new
            {
                a.Id, a.CategoryId, a.Amount, a.Note, a.PurchaseItemId, a.CreatedAt,
                ArticleName = a.PurchaseItemId.HasValue
                    ? db.PurchaseItems.Where(item => item.Id == a.PurchaseItemId.Value).Select(item => item.Name).FirstOrDefault()
                    : null
            }).ToListAsync(ct);
        var lines = raw.OrderBy(a => a.CreatedAt).Select(a => new { a.Id, a.CategoryId, a.Amount, a.Note, a.PurchaseItemId, a.ArticleName }).ToList();
        var allocated = lines.Sum(l => l.Amount);
        return new { transactionId = tx.Id, amount = tx.Amount, currency = tx.Currency, allocated, remaining = tx.Amount - allocated, lines };
    }

    public async Task<AllocationResult> ReplaceAllocationsForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, IReadOnlyList<AllocationLine> lines, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (tx is null) return AllocationResult.NotFound;

        var purchaseItemIds = lines.Where(line => line.PurchaseItemId.HasValue).Select(line => line.PurchaseItemId!.Value).Distinct().ToList();
        var articleRows = await db.PurchaseItems.AsNoTracking()
            .Where(item =>
                purchaseItemIds.Contains(item.Id) &&
                item.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId) &&
                (item.Purchase.TransactionId == id || item.Purchase.PaymentLinks.Any(link => link.TransactionId == id)))
            .Select(item => new { item.Id, item.CategoryId, item.Name })
            .ToListAsync(ct);
        if (articleRows.Count != purchaseItemIds.Count) return AllocationResult.InvalidPurchaseItem;
        var articles = articleRows.ToDictionary(item => item.Id);

        foreach (var line in lines.Where(line => line.PurchaseItemId.HasValue))
        {
            var article = articles[line.PurchaseItemId!.Value];
            if (line.CategoryId != article.CategoryId) return AllocationResult.InvalidPurchaseItem;
        }

        var categoryIds = lines.Where(l => l.CategoryId.HasValue).Select(l => l.CategoryId!.Value)
            .Concat(articleRows.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value))
            .Distinct().ToList();
        if (categoryIds.Count > 0)
        {
            var valid = await db.Categories.AsNoTracking()
                .Where(c => c.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(c.Id))
                .Select(c => c.Id).ToListAsync(ct);
            if (valid.Count != categoryIds.Count) return AllocationResult.InvalidCategory;
        }

        // Signed detail lines are valid as long as their NET equals the real ledger transaction.
        // Example expense: -15 products + 2 coupon = -13 bank charge.
        if (lines.Count > 0 && Math.Abs(lines.Sum(l => l.Amount) - tx.Amount) > PurchaseArticleCalculator.Tolerance(tx.Currency))
            return AllocationResult.Unbalanced;

        var existing = await db.TransactionAllocations.Where(a => a.TransactionId == id).ToListAsync(ct);
        db.TransactionAllocations.RemoveRange(existing);
        foreach (var line in lines)
        {
            var article = line.PurchaseItemId.HasValue ? articles[line.PurchaseItemId.Value] : null;
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = id,
                CategoryId = article?.CategoryId ?? line.CategoryId,
                Amount = line.Amount,
                Note = Normalize(line.Note) ?? article?.Name,
                PurchaseItemId = line.PurchaseItemId
            });
        }
        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return AllocationResult.Updated;
    }

    public async Task<RefundLinkResult> LinkRefundForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid refundId, Guid? originalId, Guid? targetCategoryId, CancellationToken ct)
    {
        var refund = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == refundId, ct);
        if (refund is null) return RefundLinkResult.NotFound;

        if (originalId is null)
        {
            refund.RefundOfTransactionId = null;
            refund.RefundCategoryId = null;
            refund.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return RefundLinkResult.Updated;
        }
        if (refund.Amount <= 0m || originalId.Value == refundId) return RefundLinkResult.Invalid;

        var original = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true)
            .Select(x => new { x.Id, x.Amount, x.Currency, x.IsTransfer })
            .SingleOrDefaultAsync(x => x.Id == originalId.Value, ct);
        if (original is null) return RefundLinkResult.NotFound;
        if (original.Amount >= 0m || original.IsTransfer || !string.Equals(original.Currency, refund.Currency, StringComparison.OrdinalIgnoreCase))
            return RefundLinkResult.Invalid;

        if (targetCategoryId is { } target)
        {
            var known = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
                            .AnyAsync(x => x.Id == originalId.Value && x.CategoryId == target, ct)
                        || await db.TransactionAllocations.AsNoTracking().AnyAsync(a => a.TransactionId == originalId.Value && a.CategoryId == target, ct)
                        || await db.Purchases.AsNoTracking().AnyAsync(p =>
                            p.FullWorthSpaceId == fullWorthSpaceId &&
                            (p.TransactionId == originalId.Value || p.PaymentLinks.Any(link => link.TransactionId == originalId.Value)) &&
                            (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                            p.ReviewState == "confirmed" && p.Items.Any(i => i.CategoryId == target), ct);
            if (!known) return RefundLinkResult.Invalid;
        }

        refund.RefundOfTransactionId = originalId.Value;
        refund.RefundCategoryId = targetCategoryId;
        refund.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return RefundLinkResult.Updated;
    }

    public async Task<(TransactionCreateResult Result, Guid Id)> CreateManualForOwnerAsync(Guid userId, Guid fullWorthSpaceId, CreateTransactionRequest request, CancellationToken ct)
    {
        var magnitude = Math.Abs(request.Amount);
        if (magnitude == 0m) throw new ArgumentException("Amount must be greater than zero.");
        if (magnitude >= 1_000_000_000_000m) throw new ArgumentException("Amount must be less than 1,000,000,000,000.");
        var direction = (request.Direction ?? string.Empty).Trim().ToLowerInvariant();
        if (direction != "income" && direction != "expense") throw new ArgumentException("Direction must be 'income' or 'expense'.");
        var merchant = Normalize(request.Counterparty);
        if (merchant is null) throw new ArgumentException("A description is required.");

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.AccountId && x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId), ct);
        if (account is null) return (TransactionCreateResult.NotFound, Guid.Empty);
        var isOwner = await db.Set<AccountOwner>().AsNoTracking().AnyAsync(x => x.AccountId == request.AccountId && x.UserId == userId && x.OwnershipType == AccountOwnershipTypes.Owner, ct);
        if (!isOwner) return (TransactionCreateResult.Forbidden, Guid.Empty);
        // User corrections may be added to synced accounts as well. They use a manual:* key, so a
        // later provider sync cannot overwrite them. Import archive containers must be linked first.
        if (account.Provider == FullWorth.Backend.Modules.Import.FinanzguruAccountReconciliationService.ImportProvider)
            return (TransactionCreateResult.NotManual, Guid.Empty);
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(c => c.Id == request.CategoryId.Value && c.FullWorthSpaceId == fullWorthSpaceId, ct))
            return (TransactionCreateResult.InvalidCategory, Guid.Empty);

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? account.Currency : NormalizeCurrency(request.Currency);
        var bookingDate = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var entity = new FinanceTransaction
        {
            AccountId = account.Id,
            CategoryId = request.CategoryId,
            ExternalKey = "manual:" + Guid.NewGuid().ToString("N"),
            Status = "BOOK",
            BookingDate = bookingDate,
            ValueDate = bookingDate,
            Amount = direction == "expense" ? -magnitude : magnitude,
            Currency = currency,
            Counterparty = merchant,
            NormalizedCounterparty = merchant.ToUpperInvariant(),
            UserNote = Normalize(request.Note),
            CategorizationSource = request.CategoryId.HasValue ? "manual" : "none",
            RawJson = "{}"
        };
        db.Transactions.Add(entity);
        await db.SaveChangesAsync(ct);
        return (TransactionCreateResult.Created, entity.Id);
    }

    public async Task<TransactionDeleteResult> DeleteManualForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransactionDeleteResult.NotFound;
        if (!entity.ExternalKey.StartsWith("manual:", StringComparison.Ordinal)) return TransactionDeleteResult.NotManual;
        db.Transactions.Remove(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return TransactionDeleteResult.Referenced; }
        return TransactionDeleteResult.Deleted;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "EUR";
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter code.");
        return normalized;
    }

    private static Expression<Func<FinanceTransaction, bool>> MerchantIdentityPredicate(IReadOnlyCollection<string> keys)
    {
        var transaction = Expression.Parameter(typeof(FinanceTransaction), "transaction");
        var normalized = Expression.Property(transaction, nameof(FinanceTransaction.NormalizedCounterparty));
        var notNull = Expression.NotEqual(normalized, Expression.Constant(null, typeof(string)));
        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })
            ?? throw new InvalidOperationException("string.Contains(string) was not found.");

        Expression matches = Expression.Constant(false);
        foreach (var key in keys)
        {
            var contains = Expression.Call(normalized, containsMethod, Expression.Constant(key));
            matches = Expression.OrElse(matches, contains);
        }

        return Expression.Lambda<Func<FinanceTransaction, bool>>(Expression.AndAlso(notNull, matches), transaction);
    }

    /// <summary>
    /// Wie viele Kaeufe und Artikel an den Buchungen DIESER Seite haengen (#161, Teil L).
    ///
    /// Vorher stand das als zwei korrelierte Unterabfragen in der Projektion, also einmal je Zeile.
    /// Und beide hatten die Form <c>p.TransactionId = x.Id ODER es gibt eine Zahlungsverknuepfung</c> -
    /// ein ODER zwischen einer Spalte und einer Unterabfrage auf einer ANDEREN Tabelle. Damit ist der
    /// Index auf <c>Purchases.TransactionId</c> unbrauchbar, und PostgreSQL liest je Zeile die ganze
    /// Kauftabelle. Gemessen an 200 000 Buchungen und 4 000 Kaeufen: 716 ms fuer eine Seite.
    ///
    /// Getrennt gefragt kann jede Haelfte ihren eigenen Index benutzen, und gefragt wird einmal fuer
    /// die ganze Seite statt einmal je Zeile: 5,5 ms. Dieselbe Lehre wie bei der Volltextsuche - ein
    /// ODER ueber zwei Tabellen kostet den Index.
    /// </summary>
    private async Task<Dictionary<Guid, (int Purchases, int Items)>> PurchaseCountsAsync(
        Guid userId, Guid[] transactionIds, CancellationToken ct)
    {
        var direct = await db.Purchases.AsNoTracking()
            .Where(p => p.TransactionId != null && transactionIds.Contains(p.TransactionId.Value) &&
                        (p.Visibility != "private" || p.CreatedByUserId == userId))
            .Select(p => new { TransactionId = p.TransactionId!.Value, PurchaseId = p.Id, Items = p.Items.Count })
            .ToArrayAsync(ct);

        // Ein Join, kein korreliertes SelectMany: daraus wuerde LATERAL/APPLY, und das kann SQLite
        // nicht - die Einheitstests laufen darauf.
        var linked = await db.PurchasePaymentLinks.AsNoTracking()
            .Where(link => transactionIds.Contains(link.TransactionId))
            .Join(
                db.Purchases.AsNoTracking()
                    .Where(p => p.Visibility != "private" || p.CreatedByUserId == userId),
                link => link.PurchaseId,
                purchase => purchase.Id,
                (link, purchase) => new { link.TransactionId, PurchaseId = purchase.Id, Items = purchase.Items.Count })
            .ToArrayAsync(ct);

        // Ein Kauf kann direkt UND ueber eine Zahlungsverknuepfung an derselben Buchung haengen - er
        // zaehlt einmal. Das war die eigentliche Aufgabe des ODER, und sie wird hier erledigt.
        return direct.Concat(linked)
            .GroupBy(row => row.TransactionId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var distinct = group.DistinctBy(row => row.PurchaseId).ToArray();
                    return (distinct.Length, distinct.Sum(row => row.Items));
                });
    }

    private IQueryable<FinanceTransaction> AccessibleTransactions(Guid userId, Guid? fullWorthSpaceId, bool requireOwner)
    {
        var query = db.Transactions.AsQueryable();
        if (!requireOwner) query = query.AsNoTracking();
        return query.Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId &&
            (!fullWorthSpaceId.HasValue || account.FullWorthSpaceId == fullWorthSpaceId.Value) &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == account.FullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId && (!requireOwner || owner.OwnershipType == AccountOwnershipTypes.Owner))));
    }
}
