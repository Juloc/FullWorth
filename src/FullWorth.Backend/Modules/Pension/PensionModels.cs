namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// One occupational pension contract (bAV). See docs/PENSION.md for why this is its own aggregate
/// rather than a row in <c>RecurringContract</c> or <c>Assets</c>: implementation route, policy holder
/// vs insured person, guarantee quota and annuity factor, and the employer/employee split have no home
/// there, and folding them in would mix two different kinds of fact.
///
/// The two links out exist so nothing is duplicated:
/// <list type="bullet">
///   <item><see cref="AssetId"/> — the <c>Asset</c> that carries the current balance into net worth.
///     <c>Asset.IncludeInNetWorth</c> is the per-contract "count it or only show it" switch, so this
///     aggregate deliberately has no second flag of its own.</item>
///   <item><see cref="RecurringContractId"/> — the <c>RecurringContract</c> that carries the EMPLOYEE
///     payment into fixed costs. Null for a purely employer-financed contract.</item>
/// </list>
/// </summary>
public sealed class BavContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }

    /// <summary>The provider as written on the document (insurer, Pensionskasse, Unterstützungskasse …).</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Normalised provider name, the matching half of the identity pair. Derived, never displayed —
    /// see <see cref="PensionIdentity.ProviderKey"/>.
    /// </summary>
    public string ProviderKey { get; set; } = string.Empty;

    public string? TariffName { get; set; }

    /// <summary>
    /// The policy number, encrypted at rest with <c>FieldCipher</c>. A policy number is personal data
    /// and identifies a person to their provider, so it is never stored, logged or indexed in the clear.
    /// </summary>
    public string? PolicyNumberEncrypted { get; set; }

    /// <summary>
    /// Keyed blind index over the normalised policy number. This is what existing-contract detection
    /// matches on, so the same statement never creates a second contract even though the value itself
    /// is unreadable in the database.
    /// </summary>
    public string? PolicyNumberLookup { get; set; }

    /// <summary>Last four characters of the normalised policy number, so a list can say which contract it is.</summary>
    public string? PolicyNumberLast4 { get; set; }

    /// <summary>See <see cref="BavImplementationRoutes"/>. Never provider-specific.</summary>
    public string ImplementationRoute { get; set; } = BavImplementationRoutes.Other;

    /// <summary>See <see cref="BavContractStatuses"/>. <c>paid_up</c> is its own state, not a variant of terminated.</summary>
    public string Status { get; set; } = BavContractStatuses.Active;

    /// <summary>The employer as a name. An employer is not an entity in FullWorth yet (docs/PENSION.md).</summary>
    public string? EmployerName { get; set; }

    /// <summary>Versicherungsnehmer — usually the employer, which is exactly why it is not the insured person.</summary>
    public string? PolicyHolderName { get; set; }

    /// <summary>Versicherte Person.</summary>
    public string? InsuredPersonName { get; set; }

    public DateOnly? StartDate { get; set; }

    /// <summary>Planned start of the pension. Part of the fallback identity match.</summary>
    public DateOnly? RetirementDate { get; set; }

    public DateOnly? ContractEndDate { get; set; }

    /// <summary>The contract's own currency. Never overwritten by a base-currency conversion.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>Garantiequote in percent of the contributions, when the tariff states one.</summary>
    public decimal? GuaranteeQuotaPercent { get; set; }

    /// <summary>Garantierter Rentenfaktor: monthly annuity per 10 000 units of capital.</summary>
    public decimal? GuaranteedAnnuityFactor { get; set; }

    /// <summary>Whether the tariff lets the holder change the fund selection at all (docs/PENSION.md: managed portfolios).</summary>
    public bool FundSelectionChangeable { get; set; }

    public Guid? AssetId { get; set; }
    public Guid? RecurringContractId { get; set; }

    public string? Notes { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One dated state of a contract. A new annual statement adds a row; no field of an existing row is
/// ever rewritten. Identity is <c>(BavContractId, EffectiveDate, DocumentSha256)</c>, so re-reading the
/// same document produces nothing.
///
/// Guaranteed and projected figures are separate columns on purpose: only <see cref="Balance"/> is
/// today's money, and a projection carries the assumption that produced it. Nothing here may be
/// presented as guaranteed unless it came out of a guarantee column.
/// </summary>
public sealed class BavSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid BavContractId { get; set; }

    /// <summary>The date the values are valid for — the statement's date, not the day it was entered.</summary>
    public DateOnly EffectiveDate { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>Vertragsguthaben: the value that counts as wealth today. Fed by both contribution shares.</summary>
    public decimal? Balance { get; set; }

    /// <summary>The guaranteed part of the current balance, when the statement separates it.</summary>
    public decimal? GuaranteedBalance { get; set; }

    /// <summary>Rückkaufswert / Übertragungswert.</summary>
    public decimal? SurrenderValue { get; set; }

    /// <summary>Sicherungsvermögen at this date.</summary>
    public decimal? SecurityAssetsAmount { get; set; }

    /// <summary>Fondsvermögen at this date. Together with the line above this is the security/fund split.</summary>
    public decimal? FundAssetsAmount { get; set; }

    /// <summary>Garantierte Ablaufleistung — a guarantee, and only ever read from a document.</summary>
    public decimal? GuaranteedCapitalAtRetirement { get; set; }

    /// <summary>Garantierte Monatsrente.</summary>
    public decimal? GuaranteedMonthlyAnnuity { get; set; }

    /// <summary>Prognostizierte Ablaufleistung. NOT a guarantee and never today's money.</summary>
    public decimal? ProjectedCapitalAtRetirement { get; set; }

    /// <summary>Prognostizierte Monatsrente.</summary>
    public decimal? ProjectedMonthlyAnnuity { get; set; }

    /// <summary>The return assumption the projection rests on. Required whenever a projected figure is stored.</summary>
    public decimal? ProjectionReturnPercent { get; set; }

    /// <summary>See <see cref="BavProjectionBases"/>: where a projected figure came from.</summary>
    public string? ProjectionBasis { get; set; }

    /// <summary>See <see cref="BavValueSources"/>.</summary>
    public string Source { get; set; } = BavValueSources.Manual;

    public Guid? BavDocumentId { get; set; }

    /// <summary>The document hash, the third part of the snapshot identity. Null for a hand-entered snapshot.</summary>
    public string? DocumentSha256 { get; set; }

    public decimal? ExtractionConfidence { get; set; }

    /// <summary>The newest accepted snapshot of the contract — the one that feeds the asset value.</summary>
    public bool IsCurrent { get; set; }

    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One dated contribution arrangement. The split is explicit because the direction of the money
/// differs per share (docs/PENSION.md, "Money direction"): the employee share is deferred compensation
/// and belongs in fixed costs, the employer shares are a benefit and must never appear as a cost.
///
/// A tax or social-insurance effect may only be stored together with the source that stated it. A
/// computed one is stored as <c>simulation</c> and is labelled as such everywhere it is shown.
/// </summary>
public sealed class BavContribution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid BavContractId { get; set; }

    public DateOnly ValidFrom { get; set; }

    /// <summary>Set when the arrangement ends — this is how <c>beitragsfrei</c> is recorded.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>See <see cref="BavContributionEndReasons"/>. Only meaningful with <see cref="ValidUntil"/>.</summary>
    public string? EndReason { get; set; }

    /// <summary>See <see cref="BavContributionCycles"/>.</summary>
    public string Cycle { get; set; } = BavContributionCycles.Monthly;

    public string Currency { get; set; } = "EUR";

    /// <summary>Entgeltumwandlung: the employee's own deferred share.</summary>
    public decimal EmployeeAmount { get; set; }

    /// <summary>The employer's statutory subsidy on the deferred share.</summary>
    public decimal EmployerSubsidyAmount { get; set; }

    /// <summary>The purely employer-financed share.</summary>
    public decimal EmployerAmount { get; set; }

    /// <summary>
    /// The total as the document stated it, when it stated one. Kept separately from the sum of the
    /// three shares so a document that does not add up is visible instead of quietly corrected.
    /// </summary>
    public decimal? StatedTotalAmount { get; set; }

    /// <summary>See <see cref="BavValueSources"/>.</summary>
    public string Source { get; set; } = BavValueSources.Manual;

    /// <summary>Income tax saved by the deferral. Only with a <see cref="TaxEffectSource"/>.</summary>
    public decimal? TaxSavingAmount { get; set; }

    /// <summary>Social-insurance contributions saved. Only with a <see cref="TaxEffectSource"/>.</summary>
    public decimal? SocialSecuritySavingAmount { get; set; }

    /// <summary>The resulting net effort. Only with a <see cref="TaxEffectSource"/>.</summary>
    public decimal? NetEffortAmount { get; set; }

    /// <summary>
    /// See <see cref="BavTaxEffectSources"/>. Mandatory as soon as any of the three amounts above is
    /// set, enforced in the store and by a database check: FullWorth does not invent a tax effect.
    /// </summary>
    public string? TaxEffectSource { get; set; }

    /// <summary>What exactly stated it — a payslip period, a document title, or the simulation's assumptions.</summary>
    public string? TaxEffectSourceReference { get; set; }

    public Guid? BavDocumentId { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One fund/ETF position of the contract at a point in time: name, ISIN, weight, cost, class.</summary>
public sealed class BavInvestmentAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid BavContractId { get; set; }
    public Guid? BavSnapshotId { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public string FundName { get; set; } = string.Empty;

    /// <summary>ISIN as printed, validated as two letters plus ten alphanumerics.</summary>
    public string? Isin { get; set; }

    /// <summary>Share of the contract's investment, 0–100.</summary>
    public decimal? WeightPercent { get; set; }

    /// <summary>The position's value in the contract's own currency.</summary>
    public decimal? Amount { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>Ongoing charges / TER in percent per year.</summary>
    public decimal? OngoingChargesPercent { get; set; }

    /// <summary>True when the charge above is an estimate rather than a stated figure.</summary>
    public bool OngoingChargesEstimated { get; set; }

    /// <summary>See <see cref="BavAssetClasses"/>.</summary>
    public string AssetClass { get; set; } = BavAssetClasses.Other;

    public string Source { get; set; } = BavValueSources.Manual;
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One structured cost item. Costs are the main reason two otherwise identical contracts diverge, so
/// they are stored as kind + basis + timing rather than as one number, and an estimate says so.
///
/// <see cref="ContinuesWhenPaidUp"/> is what keeps "beitragsfrei" from being read as "cost-free": a
/// contract with no contributions still carries the costs that keep running on its capital.
/// </summary>
public sealed class BavCost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid BavContractId { get; set; }
    public Guid? BavSnapshotId { get; set; }

    public DateOnly EffectiveDate { get; set; }
    public DateOnly? AppliesUntilDate { get; set; }

    /// <summary>See <see cref="BavCostKinds"/>.</summary>
    public string Kind { get; set; } = BavCostKinds.Other;

    /// <summary>See <see cref="BavCostBases"/>: what the figure below is a figure of.</summary>
    public string Basis { get; set; } = BavCostBases.FixedAmount;

    /// <summary>Set for <see cref="BavCostBases.FixedAmount"/>.</summary>
    public decimal? Amount { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>Set for every percentage basis.</summary>
    public decimal? Percent { get; set; }

    /// <summary>See <see cref="BavCostTimings"/>: already charged, still running, or still to come.</summary>
    public string Timing { get; set; } = BavCostTimings.Ongoing;

    /// <summary>An estimated cost is never shown as a contract value.</summary>
    public bool IsEstimated { get; set; }

    /// <summary>How the estimate was made. Mandatory whenever <see cref="IsEstimated"/> is true.</summary>
    public string? EstimateBasis { get; set; }

    /// <summary>Whether the cost keeps running once the contract is beitragsfrei. Defaults to true.</summary>
    public bool ContinuesWhenPaidUp { get; set; } = true;

    public string Source { get; set; } = BavValueSources.Manual;
    public Guid? BavDocumentId { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One uploaded pension document. The bytes live outside the database (encrypted at rest); this row
/// carries the hash that makes an import idempotent, plus what the extraction found out about it.
/// The contract link is nullable because a document is stored before it is matched.
/// </summary>
public sealed class BavDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid? BavContractId { get; set; }

    /// <summary>Hex SHA-256 of the file. Unique per space: the same file is never imported twice.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>See <see cref="BavDocumentKinds"/>.</summary>
    public string Kind { get; set; } = BavDocumentKinds.Other;

    public string? OriginalFileName { get; set; }
    public string? MediaType { get; set; }
    public long ByteSize { get; set; }
    public int? PageCount { get; set; }

    /// <summary>Where the encrypted blob lives. Never a URL to anything outside the installation.</summary>
    public string? StoragePath { get; set; }

    /// <summary>Which scheme protects the stored blob, so a re-key can find what it has to re-wrap.</summary>
    public string? EncryptionScheme { get; set; }

    /// <summary>See <see cref="BavExtractionStatuses"/>.</summary>
    public string ExtractionStatus { get; set; } = BavExtractionStatuses.Pending;

    public decimal? ExtractionConfidence { get; set; }

    /// <summary>See <see cref="BavExtractionSources"/>: the deterministic parser, or the optional AI pass.</summary>
    public string? ExtractionSource { get; set; }

    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Durchführungsweg. Five routes exist in German law; nothing here names a provider.</summary>
public static class BavImplementationRoutes
{
    public const string DirectInsurance = "direct_insurance";      // Direktversicherung
    public const string PensionFund = "pension_fund";              // Pensionskasse
    public const string PensionScheme = "pension_scheme";          // Pensionsfonds
    public const string ProvidentFund = "provident_fund";          // Unterstützungskasse
    public const string DirectCommitment = "direct_commitment";    // Direktzusage
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { DirectInsurance, PensionFund, PensionScheme, ProvidentFund, DirectCommitment, Other };
}

/// <summary>
/// Contract states. <see cref="PaidUp"/> ("beitragsfrei") means no new contributions while the
/// contract, its balance and its costs all continue. It is not <see cref="Terminated"/>, and it does
/// not mean cost-free.
/// </summary>
public static class BavContractStatuses
{
    public const string Active = "active";
    public const string PaidUp = "paid_up";
    public const string InPayout = "in_payout";
    public const string Transferred = "transferred";
    public const string Terminated = "terminated";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Active, PaidUp, InPayout, Transferred, Terminated };

    /// <summary>A contract that still holds capital and still develops. Beitragsfrei is one of these.</summary>
    public static bool HoldsCapital(string? status) => status is Active or PaidUp or InPayout;
}

/// <summary>Where a stored value came from. Nothing is stored without one.</summary>
public static class BavValueSources
{
    public const string Manual = "manual";
    public const string Document = "document";
    public const string Payslip = "payslip";
    public const string Import = "import";
    public const string Provider = "provider";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Manual, Document, Payslip, Import, Provider };
}

/// <summary>
/// Where a projected figure came from. A <see cref="Simulation"/> value is FullWorth's own arithmetic
/// and is labelled as a simulation wherever it appears; the other two were printed on a document.
/// </summary>
public static class BavProjectionBases
{
    public const string DocumentGuaranteed = "document_guaranteed";
    public const string DocumentForecast = "document_forecast";
    public const string Simulation = "simulation";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { DocumentGuaranteed, DocumentForecast, Simulation };
}

/// <summary>
/// What may have stated a tax or social-insurance effect. There is no "computed" member that hides
/// itself: FullWorth's own arithmetic is <see cref="Simulation"/> and says so.
/// </summary>
public static class BavTaxEffectSources
{
    public const string Document = "document";
    public const string Payslip = "payslip";
    public const string Simulation = "simulation";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Document, Payslip, Simulation };

    /// <summary>True when the effect was read off a real document rather than computed.</summary>
    public static bool IsStated(string? source) => source is Document or Payslip;
}

/// <summary>How often a contribution is paid. A one-off is an Einmalbeitrag, not a cycle of zero.</summary>
public static class BavContributionCycles
{
    public const string Monthly = "monthly";
    public const string Quarterly = "quarterly";
    public const string SemiAnnual = "semiannual";
    public const string Yearly = "yearly";
    public const string OneOff = "one_off";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Monthly, Quarterly, SemiAnnual, Yearly, OneOff };

    /// <summary>How many payments of this cycle fall in a year. A one-off has none by definition.</summary>
    public static int PaymentsPerYear(string? cycle) => cycle switch
    {
        Monthly => 12,
        Quarterly => 4,
        SemiAnnual => 2,
        Yearly => 1,
        _ => 0
    };
}

/// <summary>Why a contribution arrangement ended. <c>paid_up</c> is the beitragsfrei case.</summary>
public static class BavContributionEndReasons
{
    public const string PaidUp = "paid_up";
    public const string EmployerChange = "employer_change";
    public const string AmountChange = "amount_change";
    public const string Payout = "payout";
    public const string Terminated = "terminated";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { PaidUp, EmployerChange, AmountChange, Payout, Terminated };
}

public static class BavCostKinds
{
    public const string Acquisition = "acquisition";                          // Abschluss- und Vertriebskosten
    public const string AdministrationOnContribution = "administration_on_contribution";
    public const string AdministrationOnCapital = "administration_on_capital";
    public const string AdministrationFixed = "administration_fixed";         // Stückkosten
    public const string Fund = "fund";                                        // Fondskosten / TER
    public const string Guarantee = "guarantee";                              // Garantiekosten
    public const string RiskPremium = "risk_premium";                         // Risikobeitrag
    public const string Payout = "payout";                                    // Kosten in der Rentenphase
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Acquisition, AdministrationOnContribution, AdministrationOnCapital, AdministrationFixed,
        Fund, Guarantee, RiskPremium, Payout, Other
    };
}

public static class BavCostBases
{
    public const string FixedAmount = "fixed_amount";
    public const string PercentOfContribution = "percent_of_contribution";
    public const string PercentOfCapital = "percent_of_capital";
    public const string PercentOfSum = "percent_of_sum";                      // % der Beitragssumme
    public const string PercentOfAnnuity = "percent_of_annuity";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { FixedAmount, PercentOfContribution, PercentOfCapital, PercentOfSum, PercentOfAnnuity };

    public static bool IsPercent(string? basis) => basis is not null && basis != FixedAmount && Allowed.Contains(basis);
}

public static class BavCostTimings
{
    public const string Incurred = "incurred";
    public const string Ongoing = "ongoing";
    public const string Future = "future";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Incurred, Ongoing, Future };
}

public static class BavAssetClasses
{
    public const string Equity = "equity";
    public const string Bond = "bond";
    public const string Mixed = "mixed";
    public const string MoneyMarket = "money_market";
    public const string RealEstate = "real_estate";
    public const string Commodity = "commodity";
    public const string GuaranteeAssets = "guarantee_assets";                 // Sicherungsvermögen
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Equity, Bond, Mixed, MoneyMarket, RealEstate, Commodity, GuaranteeAssets, Other };
}

public static class BavDocumentKinds
{
    public const string AnnualStatement = "annual_statement";                 // Standmitteilung
    public const string Certificate = "certificate";                          // Versicherungsschein
    public const string Offer = "offer";
    public const string Correspondence = "correspondence";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { AnnualStatement, Certificate, Offer, Correspondence, Other };
}

public static class BavExtractionStatuses
{
    public const string Pending = "pending";
    public const string Parsed = "parsed";
    public const string Reviewed = "reviewed";
    public const string Committed = "committed";
    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Pending, Parsed, Reviewed, Committed, Failed };
}

public static class BavExtractionSources
{
    public const string Deterministic = "deterministic";
    public const string Codex = "codex";
    public const string Manual = "manual";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { Deterministic, Codex, Manual };
}
