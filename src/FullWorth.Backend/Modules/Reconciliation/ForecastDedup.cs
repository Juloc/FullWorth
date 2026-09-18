using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Modules.Reconciliation;

/// <summary>
/// Wann eine Prognose (ein erwarteter Vertrag, ein erwarteter Eingang) nicht neben einer echten
/// Buchung stehen darf, die sie schon erfuellt.
///
/// Stand vorher zweimal inline in <see cref="FinancialReconciliationReportService.CashflowAvailableAsync"/>
/// - einmal fuer Vertraege (gegen <c>ContractTransactionLinks</c> ueber die kanonischen Beitraege),
/// einmal fuer Einnahmen (Gegenpartei-Abgleich gegen vorgemerkte Einnahme-Buchungen). Die
/// Zukunfts-Timeline (#139) braucht dieselbe Regel ein drittes Mal - hierher extrahiert, statt sie
/// neu zu schreiben.
/// </summary>
public static class ForecastDedup
{
    /// <summary>Vertraege, deren naechste Faelligkeit im betrachteten Zeitraum schon durch eine echte
    /// (auch vorgemerkte) Buchung vertreten ist.</summary>
    public static HashSet<Guid> AlreadyLinkedContracts(CanonicalContributionLoad? contributions) =>
        contributions?.Items.SelectMany(item => item.ContractIds).ToHashSet() ?? [];

    /// <summary>Normalisierte Gegenparteien, fuer die schon eine vorgemerkte Einnahme-Buchung
    /// existiert - ein erwarteter Eingang mit demselben Namen ist dann keine neue Information mehr.</summary>
    public static HashSet<string> PendingIncomeParties(
        IEnumerable<(string? NormalizedCounterparty, string? Counterparty)> pendingIncome)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (normalizedCounterparty, counterparty) in pendingIncome)
        {
            var party = MerchantNormalization.Normalize(normalizedCounterparty ?? counterparty);
            if (party is not null) result.Add(party);
        }
        return result;
    }
}
