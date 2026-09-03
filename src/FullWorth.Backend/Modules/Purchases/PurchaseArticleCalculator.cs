namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseReconciliationCalculation(
    decimal PurchaseTotal,
    decimal ItemTotal,
    decimal ItemDifference,
    decimal LinkedPaymentTotal,
    decimal PaymentDifference,
    bool ItemsReconciled,
    bool PaymentsReconciled,
    bool FullyReconciled,
    decimal Tolerance)
{
    /// <summary>Effective merchandise/line charges before basket discounts, deposits and rounding.</summary>
    public decimal MerchandiseTotal { get; init; }
    /// <summary>Positive item-linked savings already reflected in MerchandiseTotal.</summary>
    public decimal ItemDiscountTotal { get; init; }
    /// <summary>Positive basket-level savings subtracted from MerchandiseTotal.</summary>
    public decimal BasketDiscountTotal { get; init; }
    public decimal DepositTotal { get; init; }
    public decimal AdditionalChargeTotal { get; init; }
    public decimal RoundingAmount { get; init; }
    public decimal? SubtotalAmount { get; init; }
    public decimal? FormulaTotal { get; init; }
    public decimal? FormulaDifference { get; init; }
    public bool FormulaReconciled { get; init; }
}

public sealed record ProductPriceComparison(
    decimal? PreviousPackPrice,
    decimal? CurrentPackPrice,
    decimal? PreviousBasePrice,
    decimal? CurrentBasePrice,
    decimal? PackPriceChangePercent,
    decimal? BasePriceChangePercent,
    decimal? PackageSizeChangePercent,
    bool PossibleShrinkflation);

public static class PurchaseArticleCalculator
{
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    { "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };

    private static readonly HashSet<string> ThreeDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    { "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" };

    public static int CurrencyDecimals(string? currency) =>
        currency is not null && ZeroDecimalCurrencies.Contains(currency) ? 0 :
        currency is not null && ThreeDecimalCurrencies.Contains(currency) ? 3 : 2;

    public static decimal RoundMoney(decimal value, string? currency) =>
        Math.Round(value, CurrencyDecimals(currency), MidpointRounding.AwayFromZero);

    public static decimal Tolerance(string? currency)
    {
        var decimals = CurrencyDecimals(currency);
        return decimals switch { 0 => .5m, 3 => .0005m, _ => .005m };
    }

    /// <summary>
    /// Legacy helper kept for callers that still provide a pre-discount unit price. The returned value is
    /// merchandise only: deposit/Pfand is intentionally NOT part of PurchaseItem.TotalPrice anymore.
    /// </summary>
    public static decimal SuggestedItemTotal(decimal quantity, decimal? unitPrice, decimal? discount, decimal? deposit, string? currency)
    {
        var gross = unitPrice.HasValue ? quantity * unitPrice.Value : 0m;
        return RoundMoney(gross - (discount ?? 0m), currency);
    }

    public static decimal? BaseUnitPrice(
        decimal? unitPrice,
        decimal quantity,
        string? quantityUnit,
        decimal? packageCount,
        decimal? packageQuantity,
        string? packageUnit,
        string? currency)
    {
        if (!unitPrice.HasValue) return null;

        // Weighted goods: unit price is already the base price when the quantity unit is kg/l/m.
        var qUnit = NormalizeUnit(quantityUnit);
        if (qUnit is "kg" or "l" or "m") return RoundMoney(unitPrice.Value, currency);
        if (qUnit == "g") return RoundMoney(unitPrice.Value * 1000m, currency);
        if (qUnit == "ml") return RoundMoney(unitPrice.Value * 1000m, currency);

        if (!packageQuantity.HasValue || packageQuantity.Value <= 0) return null;
        var count = packageCount is > 0 ? packageCount.Value : 1m;
        var baseSize = ConvertPackageToBase(packageQuantity.Value * count, packageUnit);
        if (baseSize is null || baseSize <= 0) return null;

        // unitPrice is price for one purchased pack; packageCount describes packs in that sales unit.
        return RoundMoney(unitPrice.Value / baseSize.Value, currency);
    }

    public static decimal? ConvertPackageToBase(decimal value, string? unit) => NormalizeUnit(unit) switch
    {
        "kg" => value,
        "g" => value / 1000m,
        "l" => value,
        "ml" => value / 1000m,
        "m" => value,
        "cm" => value / 100m,
        "piece" => value,
        "load" => value,
        _ => null
    };

    public static string? ComparableBaseUnit(string? unit) => NormalizeUnit(unit) switch
    {
        "kg" or "g" => "kg",
        "l" or "ml" => "l",
        "m" or "cm" => "m",
        "piece" => "piece",
        "load" => "load",
        _ => null
    };

    /// <summary>Backwards-compatible reconciliation for callers without canonical discount metadata.</summary>
    public static PurchaseReconciliationCalculation Reconcile(
        decimal purchaseTotal,
        IEnumerable<PurchaseItem> items,
        IEnumerable<PurchasePaymentLink> paymentLinks,
        string currency) =>
        Reconcile(
            purchaseTotal,
            items,
            [],
            paymentLinks,
            currency,
            subtotalAmount: null,
            declaredDiscountAmount: null,
            declaredDepositAmount: null,
            roundingAmount: 0m,
            tipAmount: null,
            shippingAmount: null,
            feeAmount: null);

    /// <summary>
    /// Canonical receipt reconciliation. Item TotalPrice is effective charged merchandise and therefore
    /// already contains item-level reductions. Only basket-level discounts are subtracted again. Deposit
    /// is separate, tax is informational, and rounding is an explicit signed adjustment.
    /// </summary>
    public static PurchaseReconciliationCalculation Reconcile(
        decimal purchaseTotal,
        IEnumerable<PurchaseItem> items,
        IEnumerable<PurchaseDiscount> discounts,
        IEnumerable<PurchasePaymentLink> paymentLinks,
        string currency,
        decimal? subtotalAmount,
        decimal? declaredDiscountAmount,
        decimal? declaredDepositAmount,
        decimal roundingAmount,
        decimal? tipAmount = null,
        decimal? shippingAmount = null,
        decimal? feeAmount = null)
    {
        var itemRows = items.ToList();
        var discountRows = discounts.Where(x => x.Amount >= 0m).ToList();
        var hasCanonicalDiscounts = discountRows.Count > 0;

        decimal merchandise = 0m;
        decimal legacyBasketDiscount = 0m;
        decimal itemDeposit = 0m;
        var hasTipLine = false;
        var hasShippingLine = false;
        var hasFeeLine = false;

        foreach (var item in itemRows)
        {
            var type = (item.LineType ?? "product").Trim().ToLowerInvariant();
            if (type is "discount" or "coupon")
            {
                // Temporary pre-canonical scans represented basket discounts as negative item rows.
                // Ignore those once canonical rows exist; otherwise preserve their old meaning.
                if (!hasCanonicalDiscounts) legacyBasketDiscount += Math.Abs(item.TotalPrice);
                continue;
            }
            if (type is "deposit" or "pfand")
            {
                itemDeposit += Math.Abs(item.TotalPrice);
                continue;
            }
            if (type == "tip") hasTipLine = true;
            if (type == "shipping") hasShippingLine = true;
            if (type == "fee") hasFeeLine = true;
            merchandise += item.TotalPrice;
            itemDeposit += Math.Max(0m, item.DepositAmount ?? 0m);
        }

        merchandise = RoundMoney(merchandise, currency);
        var itemDiscount = RoundMoney(discountRows.Where(x => x.PurchaseItemId.HasValue).Sum(x => x.Amount), currency);
        var basketDiscount = RoundMoney(discountRows.Where(x => !x.PurchaseItemId.HasValue).Sum(x => x.Amount) + legacyBasketDiscount, currency);
        var deposit = RoundMoney(declaredDepositAmount ?? itemDeposit, currency);
        var additionalCharges = RoundMoney(
            (hasTipLine ? 0m : Math.Max(0m, tipAmount ?? 0m)) +
            (hasShippingLine ? 0m : Math.Max(0m, shippingAmount ?? 0m)) +
            (hasFeeLine ? 0m : Math.Max(0m, feeAmount ?? 0m)), currency);
        var rounding = RoundMoney(roundingAmount, currency);

        var expectedFromItems = RoundMoney(merchandise - basketDiscount + deposit + additionalCharges + rounding, currency);
        var paymentTotal = RoundMoney(paymentLinks
            .Where(link => string.Equals(link.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Sum(link => Math.Abs(link.Amount)), currency);
        var normalizedPurchase = RoundMoney(Math.Abs(purchaseTotal), currency);
        var itemDifference = RoundMoney(normalizedPurchase - expectedFromItems, currency);
        var paymentDifference = RoundMoney(normalizedPurchase - paymentTotal, currency);
        var tolerance = Tolerance(currency);
        var itemsOk = Math.Abs(itemDifference) <= tolerance;
        var paymentsOk = paymentLinks.Any() ? Math.Abs(paymentDifference) <= tolerance : true;

        decimal? formulaTotal = null;
        decimal? formulaDifference = null;
        var formulaOk = true;
        if (subtotalAmount.HasValue)
        {
            var totalDiscount = declaredDiscountAmount ?? discountRows.Sum(x => x.Amount);
            // Receipt summary math uses the same additional-charge model as item reconciliation:
            // subtotal - savings + deposit + shipping/tip/fees + signed rounding = payable total.
            formulaTotal = RoundMoney(
                subtotalAmount.Value - Math.Max(0m, totalDiscount) + deposit + additionalCharges + rounding,
                currency);
            formulaDifference = RoundMoney(normalizedPurchase - formulaTotal.Value, currency);
            formulaOk = Math.Abs(formulaDifference.Value) <= tolerance;
        }

        return new(normalizedPurchase, expectedFromItems, itemDifference, paymentTotal, paymentDifference,
            itemsOk, paymentsOk, itemsOk && paymentsOk && formulaOk, tolerance)
        {
            MerchandiseTotal = merchandise,
            ItemDiscountTotal = itemDiscount,
            BasketDiscountTotal = basketDiscount,
            DepositTotal = deposit,
            AdditionalChargeTotal = additionalCharges,
            RoundingAmount = rounding,
            SubtotalAmount = subtotalAmount,
            FormulaTotal = formulaTotal,
            FormulaDifference = formulaDifference,
            FormulaReconciled = formulaOk
        };
    }

    public static ProductPriceComparison Compare(
        decimal? previousPackPrice,
        decimal? currentPackPrice,
        decimal? previousBasePrice,
        decimal? currentBasePrice,
        decimal? previousPackageBaseSize,
        decimal? currentPackageBaseSize)
    {
        static decimal? Change(decimal? oldValue, decimal? newValue) => oldValue is > 0m && newValue.HasValue
            ? Math.Round((newValue.Value - oldValue.Value) / oldValue.Value * 100m, 2, MidpointRounding.AwayFromZero)
            : null;

        var packChange = Change(previousPackPrice, currentPackPrice);
        var baseChange = Change(previousBasePrice, currentBasePrice);
        var sizeChange = Change(previousPackageBaseSize, currentPackageBaseSize);
        var shrink = previousPackageBaseSize is > 0m && currentPackageBaseSize is > 0m &&
                     currentPackageBaseSize.Value < previousPackageBaseSize.Value &&
                     baseChange is > 0m;
        return new(previousPackPrice, currentPackPrice, previousBasePrice, currentBasePrice, packChange, baseChange, sizeChange, shrink);
    }

    public static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return "piece";
        return unit.Trim().ToLowerInvariant() switch
        {
            "stück" or "st" or "pcs" or "pc" or "each" => "piece",
            "liter" or "litre" => "l",
            "milliliter" or "millilitre" => "ml",
            "gram" => "g",
            "kilogram" or "kilogramm" => "kg",
            "meter" or "metre" => "m",
            "centimeter" or "centimetre" => "cm",
            "wl" or "washload" or "washloads" => "load",
            var normalized => normalized
        };
    }
}
