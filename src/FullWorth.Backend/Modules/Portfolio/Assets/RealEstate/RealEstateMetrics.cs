using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public static class RealEstateMetrics
{
    public static async Task<RealEstateMutationOutcome<RealEstateMetricsView>> CalculateAsync(
        FullWorthDbContext db,
        CurrencyConverter fx,
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        Func<Guid, CancellationToken, Task<RealEstateDetailView?>> readDetail,
        Func<Guid, CancellationToken, Task<List<RealEstateAcquisitionCostView>>> readCosts,
        Func<Guid, Guid, CancellationToken, Task<List<AssetDebtLinkView>>> readDebtLinks,
        CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking()
            .Where(item => item.Id == assetId && item.FullWorthSpaceId == fullWorthSpaceId && item.Kind == AssetKinds.RealEstate &&
                           db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(item => new { item.Id, item.CurrentValue, item.Currency })
            .SingleOrDefaultAsync(ct);
        if (asset is null) return new(RealEstateMutationResult.NotFound);

        var detail = await readDetail(assetId, ct);
        var costs = await readCosts(assetId, ct);
        var links = await readDebtLinks(fullWorthSpaceId, assetId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var earliest = costs.Select(item => item.Date).Where(date => date.HasValue).Select(date => date!.Value)
            .Append(detail?.PurchaseDate ?? today)
            .DefaultIfEmpty(today)
            .Min();
        if (earliest > today) earliest = today;
        var snapshot = await fx.PrepareAsync(asset.Currency, earliest, today, ct);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        decimal allocatedDebt = 0m;
        var debtComplete = true;
        foreach (var link in links)
        {
            var converted = snapshot.ToBaseOn(link.CurrentBalance, link.Currency, today);
            if (!converted.HasValue)
            {
                debtComplete = false;
                missing.Add(FxSnapshot.Normalize(link.Currency));
                continue;
            }
            allocatedDebt += converted.Value * (link.AllocationPercent / 100m);
        }

        var acquisitionBasis = CalculateAcquisitionBasis(asset.Currency, detail, costs, snapshot, today, missing);
        decimal? equity = debtComplete ? asset.CurrentValue - allocatedDebt : null;
        decimal? ltv = debtComplete && asset.CurrentValue > 0m ? allocatedDebt / asset.CurrentValue : null;
        decimal? valueGain = acquisitionBasis.HasValue ? asset.CurrentValue - acquisitionBasis.Value : null;

        return new(RealEstateMutationResult.Success, new RealEstateMetricsView(
            asset.Id,
            asset.CurrentValue,
            asset.Currency,
            allocatedDebt,
            equity,
            ltv,
            acquisitionBasis,
            valueGain,
            detail?.OwnershipSharePercent ?? 100m,
            missing.Count == 0,
            missing.Order(StringComparer.Ordinal).Select(value => value.ToUpperInvariant()).ToArray()));
    }

    private static decimal? CalculateAcquisitionBasis(
        string targetCurrency,
        RealEstateDetailView? detail,
        IReadOnlyCollection<RealEstateAcquisitionCostView> costs,
        FxSnapshot snapshot,
        DateOnly today,
        ISet<string> missing)
    {
        var detailedPropertyPrice = costs.Any(item => item.Type == "property_price");
        decimal total = 0m;
        var hasBasis = false;
        var complete = true;

        if (!detailedPropertyPrice && detail?.PurchasePrice is { } purchasePrice)
        {
            hasBasis = true;
            var currency = detail.PurchaseCurrency ?? targetCurrency;
            var converted = Convert(snapshot, purchasePrice, currency, detail.PurchaseDate ?? today, missing);
            if (converted.HasValue) total += converted.Value;
            else complete = false;
        }

        foreach (var cost in costs)
        {
            hasBasis |= cost.Type == "property_price";
            var converted = Convert(snapshot, cost.Amount, cost.Currency, cost.Date ?? detail?.PurchaseDate ?? today, missing);
            if (converted.HasValue) total += converted.Value;
            else complete = false;
        }

        // The legacy summary field is only used when no detailed rows exist, otherwise the detailed
        // rows are the canonical acquisition-cost breakdown and summing both would double count fees.
        if (costs.Count == 0 && detail?.PurchasePrice is not null && detail.AcquisitionCosts is { } summaryCosts)
        {
            var currency = detail.PurchaseCurrency ?? targetCurrency;
            var converted = Convert(snapshot, summaryCosts, currency, detail.PurchaseDate ?? today, missing);
            if (converted.HasValue) total += converted.Value;
            else complete = false;
        }

        return hasBasis && complete ? total : null;
    }

    private static decimal? Convert(
        FxSnapshot snapshot,
        decimal amount,
        string currency,
        DateOnly date,
        ISet<string> missing)
    {
        var converted = snapshot.ToBaseOn(amount, currency, date);
        if (!converted.HasValue) missing.Add(FxSnapshot.Normalize(currency));
        return converted;
    }
}
