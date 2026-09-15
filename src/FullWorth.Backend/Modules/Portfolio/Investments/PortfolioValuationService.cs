using FullWorth.Backend.Modules.Fx;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Eine Position im Depot, mit dem Kurs, aus dem ihr Wert stammt.</summary>
public sealed record PortfolioPositionView(
    Guid SecurityId, string Name, string AssetType, decimal Quantity, decimal? CostBasis, decimal? Price,
    string? PriceCurrency, DateOnly? PriceDate, string PriceState, decimal? MarketValue, decimal? UnrealizedResult,
    bool CostBasisIncomplete);

/// <summary>Was ein Depot zu einem Stichtag wert ist und wie es dorthin kam.</summary>
public sealed record PortfolioCalculation(
    decimal SecurityValue, decimal Cash, decimal TotalValue, decimal RealizedResult, decimal Dividends,
    bool Incomplete, IReadOnlyList<PortfolioPositionView> Positions);

/// <summary>
/// Der Wert eines Depots zu einem Stichtag: Bestand, Einstand, Barbestand, realisiertes Ergebnis und
/// Ausschuettungen - aus den Handeln nachgerechnet, nicht irgendwo gespeichert.
///
/// Drei Regeln stecken darin, die man den Zahlen spaeter nicht mehr ansieht:
///
/// <c>Incomplete</c> ist keine Nebensache. Fehlt ein Umrechnungskurs oder ein Boersenkurs, wird der
/// Beitrag mit 0 gerechnet UND das Ergebnis als unvollstaendig gekennzeichnet. Ein fehlender Kurs
/// darf nie stillschweigend als 1:1 oder als 0 durchgehen.
///
/// Ein Einbuchen von aussen (<c>security_transfer_in</c>) bringt Stuecke ohne Einstandspreis mit.
/// Die Position merkt sich das (<c>CostBasisIncomplete</c>) und meldet danach weder Einstand noch
/// unrealisiertes Ergebnis, statt eine Zahl zu erfinden.
///
/// Die Reihenfolge der Handel ist die des Stores (Datum, Anlagezeit, Id). Wer hier selbst sortiert,
/// bekommt bei Splits andere Stueckzahlen heraus.
/// </summary>
public sealed class PortfolioValuationService(PortfolioValuationStore store, CurrencyConverter fx)
{
    public async Task<PortfolioCalculation> CalculateAsync(
        PortfolioSettingsRow portfolio, DateOnly day, CancellationToken ct)
    {
        var trades = await store.ListTradesAsync(portfolio.Id, day, null, ct);
        var securityIds = trades.Where(trade => trade.SecurityId.HasValue)
            .Select(trade => trade.SecurityId!.Value).Distinct().ToArray();
        var securities = await store.SecuritiesAsync(portfolio.FullWorthSpaceId, securityIds, ct);
        var prices = await store.LatestPricesAsync(securityIds, day, ct);
        var snapshot = await fx.PrepareAsync(portfolio.Currency,
            trades.Count == 0 ? day : trades.Min(trade => trade.TradeDate), day, ct);

        var states = new Dictionary<Guid, PositionState>();
        decimal cash = 0, dividends = 0, realized = 0;
        var incomplete = false;

        foreach (var trade in trades)
        {
            decimal Convert(decimal amount)
            {
                var converted = snapshot.ToBaseOn(amount, trade.Currency, trade.TradeDate);
                if (!converted.HasValue) { incomplete = true; return 0; }
                return converted.Value;
            }

            var gross = trade.GrossAmount
                ?? (trade.Price.HasValue && trade.Quantity.HasValue ? trade.Price.Value * trade.Quantity.Value : trade.Amount);

            switch (trade.TradeType)
            {
                case "deposit": cash += Convert(trade.Amount); break;
                case "withdrawal": cash -= Convert(trade.Amount); break;
                case "interest": cash += Convert(trade.Amount - trade.Taxes - trade.WithholdingTax); break;
                case "fee": cash -= Convert(trade.Amount + trade.Fees); break;
                case "tax": cash -= Convert(trade.Amount + trade.Taxes + trade.WithholdingTax); break;
            }

            if (!trade.SecurityId.HasValue) continue;
            if (!states.TryGetValue(trade.SecurityId.Value, out var state))
                states[trade.SecurityId.Value] = state = new PositionState();

            switch (trade.TradeType)
            {
                case "buy":
                {
                    var cost = Convert(gross + trade.Fees + trade.Taxes + trade.WithholdingTax);
                    state.Quantity += trade.Quantity ?? 0;
                    state.CostBasis += cost;
                    cash -= cost;
                    break;
                }
                case "sell":
                {
                    var quantity = trade.Quantity ?? 0;
                    var averageCost = state.Quantity > 0 ? state.CostBasis / state.Quantity : 0;
                    var proceeds = Convert(gross - trade.Fees - trade.Taxes - trade.WithholdingTax);
                    realized += proceeds - averageCost * quantity;
                    state.CostBasis -= averageCost * quantity;
                    state.Quantity -= quantity;
                    cash += proceeds;
                    break;
                }
                case "security_transfer_in":
                    state.Quantity += trade.Quantity ?? 0;
                    state.CostBasisIncomplete = true;
                    break;
                case "security_transfer_out":
                {
                    var quantity = trade.Quantity ?? 0;
                    var averageCost = state.Quantity > 0 ? state.CostBasis / state.Quantity : 0;
                    state.CostBasis -= averageCost * quantity;
                    state.Quantity -= quantity;
                    break;
                }
                case "split":
                    if (trade.Quantity is > 0) state.Quantity *= trade.Quantity.Value;
                    break;
                case "dividend":
                {
                    var net = Convert(trade.Amount - trade.Taxes - trade.WithholdingTax);
                    dividends += net;
                    cash += net;
                    break;
                }
            }
        }

        var positions = new List<PortfolioPositionView>();
        decimal securityValue = 0;
        foreach (var pair in states.Where(pair => pair.Value.Quantity > 0.0000000001m))
        {
            var security = securities.GetValueOrDefault(pair.Key);
            var state = pair.Value;
            prices.TryGetValue(pair.Key, out var price);

            decimal? market = null;
            var priceState = "missing";
            if (price is not null)
            {
                var converted = snapshot.ToBaseOn(price.Price * state.Quantity, price.Currency, price.Date);
                if (converted.HasValue)
                {
                    market = converted.Value;
                    securityValue += converted.Value;
                    var age = day.DayNumber - price.Date.DayNumber;
                    priceState = age <= 3 ? "current" : age <= 7 ? "recent" : "stale";
                }
                else incomplete = true;
            }
            else incomplete = true;

            positions.Add(new PortfolioPositionView(
                pair.Key, security?.Name ?? "Unknown", security?.AssetType ?? "other", state.Quantity,
                state.CostBasisIncomplete ? null : state.CostBasis, price?.Price, price?.Currency, price?.Date,
                priceState, market,
                market.HasValue && !state.CostBasisIncomplete ? market.Value - state.CostBasis : null,
                state.CostBasisIncomplete));
        }

        return new PortfolioCalculation(
            securityValue, cash, securityValue + cash, realized, dividends, incomplete, positions);
    }

    /// <summary>
    /// Wie viele Stuecke eines Wertpapiers an einem Tag im Depot lagen. Nur Stueckzahlen, keine
    /// Betraege - das ist die Frage vor einem Verkauf.
    /// </summary>
    public async Task<decimal> OwnedQuantityAtAsync(
        Guid portfolioId, Guid securityId, DateOnly date, CancellationToken ct)
    {
        decimal quantity = 0;
        foreach (var trade in await store.ListTradesAsync(portfolioId, date, securityId, ct))
            switch (trade.TradeType)
            {
                case "buy": case "security_transfer_in": quantity += trade.Quantity ?? 0; break;
                case "sell": case "security_transfer_out": quantity -= trade.Quantity ?? 0; break;
                case "split" when trade.Quantity is > 0: quantity *= trade.Quantity.Value; break;
            }
        return quantity;
    }

    private sealed class PositionState
    {
        public decimal Quantity { get; set; }
        public decimal CostBasis { get; set; }
        public bool CostBasisIncomplete { get; set; }
    }
}
