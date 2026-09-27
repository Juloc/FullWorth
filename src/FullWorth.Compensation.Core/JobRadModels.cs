namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// One leasing contract as the user actually has it - not the marketing figure (UVP), the figure on their
/// own leasing rate sheet. <see cref="ListPriceGross"/> is still needed: it is the base the geldwerter
/// Vorteil and the Übernahmepreis percentage are computed FROM, per §8 Abs. 2 EStG and the 2019 BMF-Schreiben
/// on Dienstfahrräder - a discount the leasing company gets does not shrink it.
/// </summary>
public sealed record JobRadLeasingInput(
    decimal ListPriceGross,
    decimal MonthlyLeasingRateGross,
    int TermMonths = 36,
    // Gehaltsumwandlung (true): the rate leaves the GROSS salary, so it also lowers what social insurance and
    // wage tax see - that is the entire tax advantage, and also the entire pension effect below. Gehaltsextra
    // (false, on top of the salary already owed): §3 Nr. 37 EStG makes private use fully tax- and
    // SV-free, so nothing here ever touches the salary calculation at all.
    bool SalaryConversion = true,
    // An Arbeitgeberzuschuss reduces what is actually converted from gross - it is the employer's own money,
    // not the employee's, so it must not shrink net income for a second time on top of that.
    decimal EmployerSubsidyMonthly = 0m,
    // Versicherung/Service, when the employee pays for it separately from the leasing rate itself (some
    // providers bill it apart). Modelled as ordinary net cash cost - EStG does not treat it as part of the
    // bike's geldwerter Vorteil.
    decimal EmployeeInsuranceServiceMonthly = 0m,
    // Übernahmepreis am Ende - als Prozent der UVP (z. B. 20 für 20 %) oder, wenn gesetzt, als fester Betrag.
    decimal TakeoverPricePercentOfList = 0m,
    decimal? TakeoverPriceFixed = null,
    // Ob der Leasinggeber die Versteuerung des Preisvorteils beim Übernahmepreis pauschal übernimmt (§ 37b
    // EStG) - der ueberwiegende Praxisfall bei den großen Anbietern, aber keine gesetzliche Pflicht.
    bool ProviderCoversTakeoverTax = true)
{
    public decimal TakeoverPrice => TakeoverPriceFixed ?? Math.Round(ListPriceGross * TakeoverPricePercentOfList / 100m, 2);

    /// <summary>
    /// Der geldwerte Vorteil nach der 0,25-%-Regel (§ 8 Abs. 2 Satz 1 i. V. m. dem BMF-Schreiben vom 9.1.2020):
    /// die UVP wird geviertelt, auf volle 100 € abgerundet, und davon 1 % angesetzt - das rundet in zwei
    /// Schritten, nicht in einem, und 0,25 % der UVP direkt ausgerechnet trifft die Rundungsstufe deshalb
    /// nicht immer exakt (bei genau 2.500 € UVP z. B. 6 € und nicht 6,25 €).
    /// </summary>
    public decimal MonthlyTaxableBenefit =>
        SalaryConversion ? Math.Floor(ListPriceGross / 4m / 100m) * 100m * 0.01m : 0m;
}

/// <summary>Die reale Alternative: was das Rad tatsächlich beim Händler kostet, nicht die UVP.</summary>
public sealed record JobRadCashPurchaseInput(
    decimal ActualPurchasePrice,
    decimal AnnualInsuranceCost = 0m,
    decimal AnnualServiceCost = 0m,
    // Finanzierungskosten insgesamt, falls nicht bar bezahlt wird (Zinsen über die Laufzeit) - 0, wenn bar.
    decimal FinancingCostTotal = 0m);

public sealed record JobRadComparisonRequest(
    CompensationProfileInput Salary,
    JobRadLeasingInput Leasing,
    JobRadCashPurchaseInput CashAlternative);

/// <summary>
/// Die Rentenwirkung einer Gehaltsumwandlung: entgangene Entgeltpunkte über die Leasingdauer und ihr Wert im
/// heutigen aktuellen Rentenwert - bewusst nicht mit einer angenommenen künftigen Rentenanpassung
/// hochgerechnet, das wäre Scheingenauigkeit über eine Zahl, die niemand kennt.
/// </summary>
public sealed record JobRadPensionImpact(
    decimal LostEntgeltpunkteOverTerm,
    decimal MonthlyPensionReductionAtCurrentValue,
    // true, wenn das beitragspflichtige Entgelt schon ohne die Umwandlung an oder über der
    // Rentenversicherungs-Beitragsbemessungsgrenze lag: dann kappt die Umwandlung nichts, was nicht ohnehin
    // gekappt war, und es entsteht keine Rentenwirkung.
    bool AboveContributionCeiling,
    decimal CurrentPensionValueMonthly,
    decimal AverageEarningsAnnual);

/// <summary>
/// Eine der "weiteren Folgen" aus der niedrigeren SV-Bemessungsbasis (Krankengeld, ALG I, Elterngeld) -
/// bewusst als Delta zum Status quo, nicht als vollständige Leistungsberechnung: alle drei Leistungen haben
/// eigene Bemessungszeiträume, Deckel und Ausnahmen, die eine Gehaltsumwandlung von wenigen Euro im Monat
/// nicht neu berechnen kann, ohne Genauigkeit vorzutäuschen, die nicht da ist. <see cref="IsEstimate"/> ist
/// deshalb bei allen dreien true, und <see cref="Basis"/> nennt die verwendete Ersatzquote im Klartext.
/// </summary>
public sealed record JobRadBenefitEstimate(string Label, decimal MonthlyDeltaEstimate, bool IsEstimate, string Basis);

/// <summary>Issue #179, Abschnitt 5 - drei Zahlen, damit sichtbar wird, ob ein Zuschuss die eigene Ersparnis
/// nur zurückgibt: was die Umwandlung an Arbeitgeber-SV spart, wie viel davon als Zuschuss an den
/// Arbeitnehmer weitergereicht wird, und was danach beim Arbeitgeber tatsächlich übrig bleibt.</summary>
public sealed record JobRadEmployerImpact(
    decimal MonthlySavingsFromLowerContributions,
    decimal MonthlySubsidyPassedToEmployee,
    decimal MonthlyNetBenefit);

public sealed record JobRadResult(
    decimal MonthlyTaxableBenefit,
    // Netto pro Monat: was ein normaler Monat mit der Umwandlung WENIGER an Netto bringt als ohne - negativ,
    // weil es fast immer ein Abzug ist; 0 im (theoretischen) Fall eines Zuschusses, der die Rate voll deckt.
    decimal NetMonthlyImpact,
    decimal TotalLeaseCostUntilOwnership,
    decimal ComparableCashPurchaseCost,
    // Positiv: Leasing ist günstiger als der Barkauf. Negativ: der Barkauf wäre günstiger gewesen.
    decimal DifferenceLeasingVsCash,
    JobRadPensionImpact Pension,
    IReadOnlyList<JobRadBenefitEstimate> SocialBenefitEstimates,
    JobRadEmployerImpact Employer,
    CompensationCalculationResult WithoutJobRad,
    CompensationCalculationResult WithJobRad,
    CompensationAssumptions Assumptions);
