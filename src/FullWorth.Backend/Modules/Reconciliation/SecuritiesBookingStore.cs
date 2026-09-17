using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FullWorth.Backend.Modules.Reconciliation;

/// <summary>
/// Eine Buchung, die der Eigentuemer ausdruecklich abgelehnt hat - sie taucht in
/// <see cref="SecuritiesBookingStore.ComputeSuggestionsAsync"/> nicht mehr auf. Dieselbe Idee wie
/// <c>DismissedContractCandidate</c> (Modules/Contracts/Review), nur je Buchung statt je Anbieter:
/// eine Buchung gehoert immer zu genau einem Wertpapier-Vorschlag, also reicht die Buchung selbst als
/// Schluessel.
/// </summary>
public sealed class DismissedSecuritiesBooking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid TransactionId { get; set; }
    public DateTimeOffset DismissedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Was ein Abgleichslauf vorschlaegt: die Treffer und ihre Wertpapiere, fuer die Anzeige.</summary>
public sealed record SecuritiesBookingSuggestions(
    SecuritiesBookingMatchBatch Batch,
    IReadOnlyDictionary<Guid, SecurityCandidate> Securities);

/// <summary>Was ein Uebernahme-Aufruf mit jeder angefragten Buchung getan hat.</summary>
public sealed record SecuritiesBookingApplyOutcome(
    IReadOnlyList<Guid> Applied,
    IReadOnlyList<Guid> AlreadyApplied,
    IReadOnlyList<Guid> Rejected);

/// <summary>
/// Datenzugriff hinter dem Buchungs-Abgleich: Buchungen des Verrechnungskontos und Wertpapiere des
/// Depots laden, sie ueber den reinen <see cref="SecuritiesBookingMatcher"/> abgleichen, und aus einem
/// bestaetigten Treffer einen "buy"-Handel machen.
///
/// Liegt in Modules/Reconciliation und nicht in Modules/Portfolio: das Feature braucht sowohl
/// Buchungen (Modules/Transactions) als auch Depot/Wertpapier/Kurs-Daten (Modules/Portfolio), und
/// Reconciliation ist genau der Ort fuer das, was zwei Module gemeinsam brauchen (siehe
/// FinancialReconciliationService daneben).
/// </summary>
public sealed class SecuritiesBookingStore(
    FullWorthDbContext db,
    AuditService audit,
    PortfolioValuationStore valuationStore,
    PortfolioValuationService valuationService,
    ILogger<SecuritiesBookingStore> logger)
{
    private const string ExternalKeyPrefix = "booking:";

    public async Task<SecuritiesBookingSuggestions?> ComputeSuggestionsAsync(
        Guid space, Guid portfolioId, CancellationToken ct)
    {
        var portfolio = await valuationStore.FindPortfolioAsync(space, portfolioId, ct);
        if (portfolio is null) return null;

        var connection = await RawSql.OpenAsync(db, ct);
        var dismissed = await DismissedTransactionIdsAsync(space, ct);
        var (batch, securities, _) = await MatchAsync(connection, portfolio, dismissed, ct);
        return new SecuritiesBookingSuggestions(batch, securities);
    }

    /// <summary>
    /// Uebernimmt ausgewaehlte Vorschlaege als "buy"-Handel. Serialisierbar, weil zwei gleichzeitige
    /// Aufrufe fuer dieselbe Buchung sonst beide "kein Handel mit dieser ExternalKey" saehen und beide
    /// einfuegen wuerden - der Trigger <c>TR_InvestmentTrades_ValidateLedger</c> schuetzt nur die
    /// Stueckzahl je Zeile, nicht die Eindeutigkeit der ExternalKey (dafuer gibt es keinen Unique-Index,
    /// siehe <c>InvestmentImportStore.TradeExistsAsync</c> fuer denselben Kompromiss beim Import).
    /// </summary>
    public async Task<SecuritiesBookingApplyOutcome?> ApplyAsync(
        Guid userId, Guid space, Guid portfolioId, IReadOnlyList<Guid> transactionIds, CancellationToken ct)
    {
        var portfolio = await valuationStore.FindPortfolioAsync(space, portfolioId, ct);
        if (portfolio is null) return null;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var connection = await RawSql.OpenAsync(db, ct);
            // Neu abgeglichen, nicht der Stand vom GET: zwischen Vorschlag und Uebernahme kann eine
            // neue Buchung dazugekommen oder ein Kurs nachgetragen worden sein, und zwei gleichzeitige
            // Uebernahmen sollen beide denselben, aktuellen Stand sehen.
            var (batch, _, descriptions) = await MatchAsync(connection, portfolio, new HashSet<Guid>(), ct);
            var matchByTransaction = batch.Matches.ToDictionary(match => match.TransactionId);

            var applied = new List<Guid>();
            var alreadyApplied = new List<Guid>();
            var rejected = new List<Guid>();
            var touchedSecurities = new HashSet<Guid>();
            var now = DateTimeOffset.UtcNow;

            foreach (var transactionId in transactionIds)
            {
                var externalKey = ExternalKeyPrefix + transactionId.ToString("N");
                if (await BookingTradeExistsAsync(connection, portfolioId, externalKey, ct))
                {
                    alreadyApplied.Add(transactionId);
                    continue;
                }

                // Eine Buchung ohne geschaetzte Stueckzahl kann nie eine gueltige Ledger-Zeile werden -
                // die Stueckzahl muss positiv sein (TR_InvestmentTrades_ValidateLedger), und irgendeine
                // Zahl zu erfinden waere keine Buchhaltung mehr. Sie bleibt ein Vorschlag fuer die
                // manuelle Erfassung.
                if (!matchByTransaction.TryGetValue(transactionId, out var match) || match.Quantity is null)
                {
                    rejected.Add(transactionId);
                    continue;
                }

                var tradeId = await InsertBookingTradeAsync(
                    connection, space, portfolioId, match, externalKey,
                    ShortNotes(descriptions.GetValueOrDefault(transactionId)), now, ct);
                audit.Record(space, userId, "investment.trade.created", "InvestmentTrade", tradeId);
                applied.Add(transactionId);
                touchedSecurities.Add(match.SecurityId);
            }

            foreach (var securityId in touchedSecurities)
            {
                var overexplained = await ApplyRestRuleAsync(connection, portfolioId, securityId, now, ct);
                if (overexplained)
                    logger.LogWarning(
                        "Depot {Portfolio}, Wertpapier {Security}: Buchungen erklaeren mehr, als die " +
                        "Bank heute meldet - vermutlich ein nicht erkannter Verkauf.",
                        portfolioId, securityId);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SecuritiesBookingApplyOutcome(applied, alreadyApplied, rejected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Eine Buchung dauerhaft aus den Vorschlaegen nehmen - mirrors DismissedContractCandidate.</summary>
    public async Task<bool> DismissAsync(
        Guid space, Guid portfolioId, IReadOnlyList<Guid> transactionIds, CancellationToken ct)
    {
        var portfolio = await valuationStore.FindPortfolioAsync(space, portfolioId, ct);
        if (portfolio is null) return false;

        foreach (var transactionId in transactionIds.Distinct())
        {
            var alreadyDismissed = await db.DismissedSecuritiesBookings.AsNoTracking()
                .AnyAsync(entry => entry.FullWorthSpaceId == space && entry.TransactionId == transactionId, ct);
            if (alreadyDismissed) continue;

            db.DismissedSecuritiesBookings.Add(new DismissedSecuritiesBooking
            {
                FullWorthSpaceId = space,
                TransactionId = transactionId
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Gleichzeitige Anfrage war schneller - dasselbe Ziel ist erreicht, kein Fehler.
                db.ChangeTracker.Clear();
            }
        }

        return true;
    }

    private async Task<(SecuritiesBookingMatchBatch Batch, Dictionary<Guid, SecurityCandidate> Securities, Dictionary<Guid, string?> Descriptions)>
        MatchAsync(DbConnection connection, PortfolioSettingsRow portfolio, IReadOnlySet<Guid> additionalExcluded, CancellationToken ct)
    {
        var applied = await AppliedTransactionIdsAsync(connection, portfolio.Id, ct);
        var excluded = additionalExcluded.Count == 0 ? applied : new HashSet<Guid>(applied.Concat(additionalExcluded));

        var (bookings, descriptions) = portfolio.AccountId is { } accountId
            ? await LoadBookingsAsync(accountId, excluded, ct)
            : ([], new Dictionary<Guid, string?>());

        var securities = await LoadSecuritiesAsync(connection, portfolio.FullWorthSpaceId, ct);
        var prices = await LoadPricesAsync(connection, securities.Keys, ct);
        var holdings = await LoadHoldingsAsync(portfolio, ct);

        var batch = SecuritiesBookingMatcher.Match(bookings, securities.Values.ToList(), prices, holdings);
        return (batch, securities, descriptions);
    }

    /// <summary>
    /// Alle Buchungen des Verrechnungskontos, ausser den bereits uebernommenen oder abgelehnten. Der
    /// Matcher selbst entscheidet, was davon ein Kauf sein kann (ignoriert/Umbuchung/Betrag) - hier wird
    /// nicht vorgefiltert, damit die Filterlogik nur an einer Stelle steht.
    /// </summary>
    private async Task<(List<BookingCandidate> Bookings, Dictionary<Guid, string?> Descriptions)> LoadBookingsAsync(
        Guid accountId, IReadOnlySet<Guid> excluded, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId)
            .Select(transaction => new
            {
                transaction.Id,
                transaction.BookingDate,
                transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                transaction.Description,
                transaction.IsIgnored,
                transaction.IsTransfer
            })
            .ToListAsync(ct);

        var bookings = new List<BookingCandidate>();
        var descriptions = new Dictionary<Guid, string?>();
        foreach (var row in rows)
        {
            if (excluded.Contains(row.Id)) continue;
            var date = row.BookingDate ?? row.ValueDate;
            if (date is null) continue; // ohne Datum kein Kurs zum Vergleich auffindbar

            bookings.Add(new BookingCandidate(
                row.Id, date.Value, row.Amount, row.Currency, row.Description, row.IsIgnored, row.IsTransfer));
            descriptions[row.Id] = row.Description;
        }
        return (bookings, descriptions);
    }

    private static async Task<Dictionary<Guid, SecurityCandidate>> LoadSecuritiesAsync(
        DbConnection connection, Guid space, CancellationToken ct)
    {
        await using var command = RawSql.Command(connection,
            "SELECT \"Id\",\"Isin\",\"Wkn\",\"Name\" FROM \"Securities\" WHERE \"FullWorthSpaceId\"=@space",
            ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new Dictionary<Guid, SecurityCandidate>();
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "Id");
            result[id] = new SecurityCandidate(
                id, RawSql.NullableString(reader, "Isin"), RawSql.NullableString(reader, "Wkn"),
                RawSql.String(reader, "Name"));
        }
        return result;
    }

    /// <summary>
    /// Die volle Kurshistorie je Wertpapier, nicht nur der juengste Kurs: der Matcher braucht zu JEDER
    /// Buchung den Kurs am oder vor IHREM Datum, und verschiedene Buchungen haben verschiedene Daten.
    /// <c>PortfolioValuationStore.LatestPricesAsync</c> liefert bewusst nur einen Stichtagskurs und
    /// passt darum hier nicht.
    /// </summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>>> LoadPricesAsync(
        DbConnection connection, IEnumerable<Guid> securityIds, CancellationToken ct)
    {
        var ids = securityIds.ToArray();
        var result = new Dictionary<Guid, IReadOnlyList<(DateOnly, decimal)>>();
        if (ids.Length == 0) return result;

        await using var command = RawSql.Command(connection,
            "SELECT \"SecurityId\",\"PriceDate\",\"Price\" FROM \"SecurityPrices\" WHERE \"SecurityId\"=ANY(@ids)",
            ("@ids", ids));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var bySecurity = new Dictionary<Guid, List<(DateOnly, decimal)>>();
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "SecurityId");
            if (!bySecurity.TryGetValue(id, out var list)) bySecurity[id] = list = [];
            list.Add((RawSql.NullableDate(reader, "PriceDate")!.Value, RawSql.Decimal(reader, "Price")));
        }
        foreach (var pair in bySecurity) result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>
    /// Bestand (Q) und Einstandspreis JE STUECK (P) - dieselbe Bewertung wie ueberall im Depot
    /// (<see cref="PortfolioValuationService.CalculateAsync"/>), nicht neu hergeleitet. CostBasis ist
    /// dort der GESAMTEINSTAND; der Matcher will den Kurs je Stueck, also wird durch die Stueckzahl
    /// geteilt.
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Quantity, decimal? CostPrice)>> LoadHoldingsAsync(
        PortfolioSettingsRow portfolio, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var calculation = await valuationService.CalculateAsync(portfolio, today, ct);
        return calculation.Positions.ToDictionary(
            position => position.SecurityId,
            position => (position.Quantity,
                position.CostBasis is { } cost && position.Quantity > 0m ? cost / position.Quantity : (decimal?)null));
    }

    /// <summary>Transaktionen, die bereits als Handel eingespielt sind - ihre ExternalKey traegt die Id.</summary>
    private static async Task<HashSet<Guid>> AppliedTransactionIdsAsync(
        DbConnection connection, Guid portfolioId, CancellationToken ct)
    {
        await using var command = RawSql.Command(connection,
            "SELECT \"ExternalKey\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"ExternalKey\" LIKE 'booking:%'",
            ("@portfolio", portfolioId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new HashSet<Guid>();
        while (await reader.ReadAsync(ct))
        {
            var key = RawSql.String(reader, "ExternalKey");
            if (Guid.TryParseExact(key.AsSpan(ExternalKeyPrefix.Length), "N", out var id)) result.Add(id);
        }
        return result;
    }

    private async Task<HashSet<Guid>> DismissedTransactionIdsAsync(Guid space, CancellationToken ct) =>
        (await db.DismissedSecuritiesBookings.AsNoTracking()
            .Where(entry => entry.FullWorthSpaceId == space)
            .Select(entry => entry.TransactionId)
            .ToListAsync(ct)).ToHashSet();

    /// <summary>Mirrors <c>InvestmentImportStore.TradeExistsAsync</c>: kein Unique-Index, die Pruefung IST die Schranke.</summary>
    private static async Task<bool> BookingTradeExistsAsync(
        DbConnection connection, Guid portfolioId, string externalKey, CancellationToken ct)
    {
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"ExternalKey\"=@key)",
            ("@portfolio", portfolioId), ("@key", externalKey));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<Guid> InsertBookingTradeAsync(
        DbConnection connection, Guid space, Guid portfolioId, SecuritiesBookingMatch match, string externalKey,
        string? notes, DateTimeOffset now, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var quantity = match.Quantity!.Value;
        var price = quantity > 0m ? match.Gross / quantity : (decimal?)null;

        await using var insert = RawSql.Command(connection, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price",
 "GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,'buy',@date,NULL,@quantity,@price,@gross,@amount,@currency,0,0,0,
        'bank_booking',@external,@notes,@now,@now)
""", ("@id", id), ("@space", space), ("@portfolio", portfolioId), ("@security", match.SecurityId),
            ("@date", match.Date), ("@quantity", quantity), ("@price", price), ("@gross", match.Gross),
            ("@amount", match.Gross), ("@currency", match.Currency), ("@external", externalKey),
            ("@notes", notes), ("@now", now));
        await insert.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>
    /// Nach dem Einfuegen neuer Kaeufe fuer ein Wertpapier: <see cref="SnapshotRestRule"/> gegen dessen
    /// FinTS-Snapshot-Zeile anwenden. Gibt zurueck, ob dabei eine Ueberdeckung (Rest &lt; 0) auffiel.
    /// </summary>
    private static async Task<bool> ApplyRestRuleAsync(
        DbConnection connection, Guid portfolioId, Guid securityId, DateTimeOffset now, CancellationToken ct)
    {
        Guid snapshotTradeId = Guid.Empty;
        decimal snapshotQuantity = 0m;
        await using (var find = RawSql.Command(connection, """
SELECT "Id","Quantity" FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio AND "SecurityId"=@security AND "Source"='fints_snapshot' AND "TradeType"='security_transfer_in'
LIMIT 1
""", ("@portfolio", portfolioId), ("@security", securityId)))
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false; // kein Snapshot fuer dieses Wertpapier - nichts abzugleichen
            snapshotTradeId = RawSql.Guid(reader, "Id");
            snapshotQuantity = RawSql.NullableDecimal(reader, "Quantity") ?? 0m;
        }

        decimal otherQuantity;
        await using (var sum = RawSql.Command(connection, """
SELECT COALESCE(SUM("Quantity"),0) FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio AND "SecurityId"=@security AND "Source"<>'fints_snapshot'
  AND "TradeType" IN ('buy','security_transfer_in')
""", ("@portfolio", portfolioId), ("@security", securityId)))
            otherQuantity = Convert.ToDecimal(await sum.ExecuteScalarAsync(ct));

        var result = SnapshotRestRule.Evaluate(snapshotQuantity, otherQuantity);
        if (result.Action == SnapshotRestAction.Delete)
        {
            await using var delete = RawSql.Command(connection,
                "DELETE FROM \"InvestmentTrades\" WHERE \"Id\"=@id", ("@id", snapshotTradeId));
            await delete.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var update = RawSql.Command(connection,
                "UPDATE \"InvestmentTrades\" SET \"Quantity\"=@quantity,\"UpdatedAt\"=@now WHERE \"Id\"=@id",
                ("@quantity", result.Remainder), ("@now", now), ("@id", snapshotTradeId));
            await update.ExecuteNonQueryAsync(ct);
        }
        return result.Overexplained;
    }

    private static string? ShortNotes(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var cleaned = description.Trim();
        return cleaned.Length > 240 ? cleaned[..240] : cleaned;
    }
}
