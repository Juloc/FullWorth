namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Ein Hinweis aus dem Abgleich - je nach Schwere ein Fehler, eine Warnung oder eine Auskunft.</summary>
public sealed record ImportReconciliationWarning(string Code, string Severity, string Message);

/// <summary>Ein Bestand, wie ihn das Nachrechnen der Handel ergibt.</summary>
public sealed record ImportReconciliationPosition(Guid SecurityId, string Name, string? Isin, decimal Quantity);

/// <summary>Ein geschaetzter Barbestand je Waehrung.</summary>
public sealed record ImportReconciliationCash(string Currency, decimal Amount);

/// <summary>
/// Das Ergebnis des Abgleichs. <c>Exists=false</c> heisst: das Depot gibt es nicht (mehr) - dann
/// stehen hier nur der Fehlerhinweis und sonst nichts.
/// </summary>
public sealed record ImportReconciliationView(
    Guid PortfolioId, bool Exists, string? PortfolioName, string? PortfolioCurrency, int TradeCount,
    IReadOnlyList<ImportReconciliationPosition> Positions, IReadOnlyList<ImportReconciliationCash> CashBalances,
    IReadOnlyList<ImportReconciliationWarning> Warnings, bool Healthy);

/// <summary>
/// Rechnet die Handel eines Depots noch einmal durch und sagt, ob das Ergebnis Sinn ergibt.
///
/// Der wichtigste Wert ist <c>MinimumQuantity</c>: nicht der Bestand am Ende zaehlt, sondern der
/// niedrigste Bestand unterwegs. Ein Depot kann am Ende sauber aussehen und trotzdem zwischendurch
/// mehr verkauft haben, als es besass - meist, weil ein Kauf im Export fehlt oder ein Split falsch
/// einsortiert wurde. Genau das findet der Abgleich, und nur das ist ein Fehler.
///
/// Negativer Barbestand ist dagegen nur eine Warnung: eine Depotdatei enthaelt haeufig keine
/// Einzahlungen, und dann ist das Minus richtig gerechnet und trotzdem harmlos.
/// </summary>
public static class InvestmentImportReconciliation
{
    public static ImportReconciliationView Build(
        Guid portfolioId, string portfolioName, string portfolioCurrency,
        IReadOnlyList<ImportLedgerTrade> trades)
    {
        var positions = new Dictionary<Guid, PositionAccumulator>();
        var cash = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<ImportReconciliationWarning>();
        var otherEvents = 0;

        foreach (var trade in trades)
        {
            if (trade.TradeType == "other") otherEvents++;

            var impact = CashImpact(trade);
            if (!cash.TryAdd(trade.Currency, impact)) cash[trade.Currency] += impact;

            if (!trade.SecurityId.HasValue) continue;
            if (!positions.TryGetValue(trade.SecurityId.Value, out var position))
                positions[trade.SecurityId.Value] = position = new PositionAccumulator(
                    trade.SecurityId.Value,
                    trade.SecurityName ?? trade.SecurityId.Value.ToString("D"),
                    trade.Isin);

            switch (trade.TradeType)
            {
                case "buy":
                case "security_transfer_in":
                    position.Quantity += trade.Quantity;
                    break;
                case "sell":
                case "security_transfer_out":
                case "cancellation":
                    position.Quantity -= trade.Quantity;
                    break;
                case "split" when trade.Quantity > 0:
                    position.Quantity *= trade.Quantity;
                    break;
            }
            position.MinimumQuantity = Math.Min(position.MinimumQuantity, position.Quantity);
        }

        foreach (var position in positions.Values.Where(item => item.MinimumQuantity < -0.0000000001m))
            warnings.Add(new ImportReconciliationWarning("negative_position", "error",
                $"{position.Name} became negative during ledger replay ({position.MinimumQuantity:0.##########})."));

        foreach (var balance in cash.Where(item => item.Value < -0.01m))
            warnings.Add(new ImportReconciliationWarning("negative_cash", "warning",
                $"Estimated cash balance is negative: {balance.Value:0.00} {balance.Key}."));

        if (cash.Count > 1)
            warnings.Add(new ImportReconciliationWarning("mixed_currencies", "warning",
                "The portfolio contains multiple transaction currencies; cash is shown separately per currency."));

        if (otherEvents > 0)
            warnings.Add(new ImportReconciliationWarning("unclassified_events", "info",
                $"{otherEvents} event(s) are classified as other and are excluded from estimated cash movement."));

        return new ImportReconciliationView(
            portfolioId, true, portfolioName, portfolioCurrency, trades.Count,
            positions.Values
                .Where(position => Math.Abs(position.Quantity) > 0.0000000001m)
                .OrderBy(position => position.Name)
                .Select(position => new ImportReconciliationPosition(
                    position.SecurityId, position.Name, position.Isin, position.Quantity))
                .ToArray(),
            cash.OrderBy(item => item.Key)
                .Select(item => new ImportReconciliationCash(item.Key, item.Value)).ToArray(),
            warnings,
            warnings.All(warning => warning.Severity != "error"));
    }

    public static ImportReconciliationView Missing(Guid portfolioId) => new(
        portfolioId, false, null, null, 0, [], [],
        [new ImportReconciliationWarning("portfolio_missing", "error", "Portfolio does not exist.")],
        false);

    /// <summary>
    /// Was ein Handel am Barbestand aendert. Gebuehren und Steuern stehen in eigenen Spalten und
    /// gehen darum zusaetzlich ab; bei einer reinen Gebuehren- oder Steuerbuchung stehen sie
    /// stattdessen im Betrag, weshalb dort der Betrag Vorrang hat.
    /// </summary>
    private static decimal CashImpact(ImportLedgerTrade trade) => trade.TradeType switch
    {
        "deposit" => trade.Amount,
        "withdrawal" => -trade.Amount,
        "buy" => -(trade.Amount + trade.Fees + trade.Taxes + trade.WithholdingTax),
        "sell" => trade.Amount - trade.Fees - trade.Taxes - trade.WithholdingTax,
        "cancellation" => trade.Amount - trade.Fees - trade.Taxes - trade.WithholdingTax,
        "dividend" or "interest" => trade.Amount - trade.Fees - trade.Taxes - trade.WithholdingTax,
        "fee" => -(trade.Amount > 0 ? trade.Amount : trade.Fees),
        "tax" => -(trade.Amount > 0 ? trade.Amount : trade.Taxes + trade.WithholdingTax),
        _ => 0m
    };

    private sealed class PositionAccumulator(Guid securityId, string name, string? isin)
    {
        public Guid SecurityId { get; } = securityId;
        public string Name { get; } = name;
        public string? Isin { get; } = isin;
        public decimal Quantity { get; set; }
        public decimal MinimumQuantity { get; set; }
    }
}
