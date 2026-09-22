namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Die eine Abgleichsantwort eines Kaufs.
///
/// Es gab hier zwei Rechnungen fuer dieselbe Frage. Die Artikelwerkstatt und das Bestaetigen rechneten
/// ueber <see cref="PurchaseArticleCalculator.Reconcile"/>, die Kaufseite dagegen ueber eine eigene
/// SQL-Abfrage in einem Store - und die war stehengeblieben: sie las die Zahlung aus der alten
/// Einzelspalte <c>Purchases.TransactionId</c> statt aus <c>PurchasePaymentLinks</c>, und sie kannte
/// weder die Rabattzeilen noch Trinkgeld, Versand und Gebuehr. Ein Kauf konnte auf der einen Seite
/// "stimmt" und auf der anderen "Differenz" sein, ueber denselben Daten.
///
/// Deshalb liegt die Form jetzt hier, und beide Wege gehen hindurch. Wer eine Zahl ergaenzt, ergaenzt
/// sie fuer beide.
/// </summary>
public static class PurchaseReconciliationView
{
    /// <param name="legacyPaymentAmount">
    /// Der Betrag der alten Einzelverknuepfung <c>Purchases.TransactionId</c>, falls es noch keine
    /// Zeile in <c>PurchasePaymentLinks</c> gibt. Dieselbe Vertraeglichkeitsregel wendet
    /// <c>PurchaseLifecycleService.ConfirmAsync</c> beim Bestaetigen an - sie steht hier, damit die
    /// Anzeige nicht "nicht verknuepft" sagt, wo das Bestaetigen eine Zahlung sieht.
    /// </param>
    public static object Of(Purchase purchase, decimal? legacyPaymentAmount = null)
    {
        var payments = purchase.PaymentLinks.Count == 0 && legacyPaymentAmount is { } legacy
            ? [new PurchasePaymentLink
              {
                  FullWorthSpaceId = purchase.FullWorthSpaceId, PurchaseId = purchase.Id,
                  TransactionId = purchase.TransactionId ?? Guid.Empty, Amount = legacy,
                  Currency = purchase.Currency, LinkSource = "legacy", Confidence = purchase.MatchConfidence
              }]
            : (IReadOnlyCollection<PurchasePaymentLink>)purchase.PaymentLinks;
        var calculation = PurchaseArticleCalculator.Reconcile(
            purchase.TotalAmount,
            purchase.Items,
            purchase.Discounts,
            payments,
            purchase.Currency,
            purchase.SubtotalAmount,
            purchase.DiscountAmount,
            purchase.DepositAmount,
            purchase.RoundingAmount,
            purchase.TipAmount,
            purchase.ShippingAmount,
            purchase.FeeAmount);
        var accepted = purchase.AcceptedDifferences
            .ToDictionary(x => x.Kind, x => new { x.Amount, x.Reason, x.Note, x.AcceptedAt });
        return new
        {
            purchase.Id, purchase.Currency,
            calculation.PurchaseTotal,
            calculation.ItemTotal,
            calculation.MerchandiseTotal,
            calculation.ItemDiscountTotal,
            calculation.BasketDiscountTotal,
            totalDiscount = calculation.ItemDiscountTotal + calculation.BasketDiscountTotal,
            calculation.DepositTotal,
            calculation.AdditionalChargeTotal,
            calculation.RoundingAmount,
            calculation.ItemDifference,
            calculation.SubtotalAmount,
            calculation.FormulaTotal,
            calculation.FormulaDifference,
            calculation.LinkedPaymentTotal,
            calculation.PaymentDifference,
            calculation.ItemsReconciled,
            calculation.FormulaReconciled,
            calculation.PaymentsReconciled,
            calculation.FullyReconciled,
            calculation.Tolerance,
            hasForeignCurrencyPayments = payments.Any(
                x => !string.Equals(x.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase)),
            acceptedDifferences = accepted
        };
    }
}
