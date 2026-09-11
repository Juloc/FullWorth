namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// A contract as the API returns it. The policy number is never returned in full — only the last four
/// characters and whether one is stored — because the API response is the one place a policy number
/// would leak into a browser cache, a log or a screenshot.
/// </summary>
public sealed record BavContractView(
    Guid Id,
    Guid FullWorthSpaceId,
    string ProviderName,
    string? TariffName,
    bool HasPolicyNumber,
    string? PolicyNumberLast4,
    string ImplementationRoute,
    string Status,
    // True for active, paid_up and in_payout: the contract still holds capital.
    bool HoldsCapital,
    // Beitragsfrei. Its own state: not cancelled, and not free of charge.
    bool IsPaidUp,
    string? EmployerName,
    string? PolicyHolderName,
    string? InsuredPersonName,
    DateOnly? StartDate,
    DateOnly? RetirementDate,
    DateOnly? ContractEndDate,
    string Currency,
    decimal? GuaranteeQuotaPercent,
    decimal? GuaranteedAnnuityFactor,
    bool FundSelectionChangeable,
    Guid? AssetId,
    Guid? RecurringContractId,
    // Whether the balance counts in net worth. Read from the linked asset, never duplicated here.
    bool? IncludeInNetWorth,
    string? Notes,
    BavSnapshotView? CurrentSnapshot,
    BavContributionView? CurrentContribution,
    int SnapshotCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BavContractDetailView(
    BavContractView Contract,
    IReadOnlyList<BavSnapshotView> Snapshots,
    IReadOnlyList<BavContributionView> Contributions,
    IReadOnlyList<BavCostView> Costs,
    IReadOnlyList<BavAllocationView> Allocations);

/// <summary>
/// A dated state. <c>balance</c> is the only figure here that is money today; the guaranteed and
/// projected figures are values at retirement and are deliberately separate fields so a caller cannot
/// render a projection where a balance belongs.
/// </summary>
public sealed record BavSnapshotView(
    Guid Id,
    Guid BavContractId,
    DateOnly EffectiveDate,
    string Currency,
    decimal? Balance,
    decimal? GuaranteedBalance,
    decimal? SurrenderValue,
    decimal? SecurityAssetsAmount,
    decimal? FundAssetsAmount,
    decimal? GuaranteedCapitalAtRetirement,
    decimal? GuaranteedMonthlyAnnuity,
    decimal? ProjectedCapitalAtRetirement,
    decimal? ProjectedMonthlyAnnuity,
    decimal? ProjectionReturnPercent,
    string? ProjectionBasis,
    // True when a projected figure here is FullWorth's own arithmetic rather than a document's.
    bool ProjectionIsSimulation,
    string Source,
    Guid? BavDocumentId,
    bool FromDocument,
    decimal? ExtractionConfidence,
    bool IsCurrent,
    string? Note,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt);

public sealed record BavContributionView(
    Guid Id,
    Guid BavContractId,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    string? EndReason,
    string Cycle,
    string Currency,
    decimal EmployeeAmount,
    decimal EmployerSubsidyAmount,
    decimal EmployerAmount,
    // Employee + both employer shares. What the contract receives.
    decimal PartsTotalAmount,
    // The two employer shares. A benefit — never a cost and never spendable income.
    decimal EmployerTotalAmount,
    decimal? StatedTotalAmount,
    // True when the document's total does not match the shares. Shown, never silently fixed.
    bool TotalMismatch,
    string Source,
    decimal? TaxSavingAmount,
    decimal? SocialSecuritySavingAmount,
    decimal? NetEffortAmount,
    string? TaxEffectSource,
    string? TaxEffectSourceReference,
    // True when the tax effect was read off a document or a payslip rather than computed.
    bool TaxEffectIsStated,
    // True when the tax effect is FullWorth's own arithmetic and must be labelled as such.
    bool TaxEffectIsSimulation,
    Guid? BavDocumentId,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed record BavCostView(
    Guid Id,
    Guid BavContractId,
    Guid? BavSnapshotId,
    DateOnly EffectiveDate,
    DateOnly? AppliesUntilDate,
    string Kind,
    string Basis,
    decimal? Amount,
    string Currency,
    decimal? Percent,
    string Timing,
    bool IsEstimated,
    string? EstimateBasis,
    bool ContinuesWhenPaidUp,
    string Source,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed record BavAllocationView(
    Guid Id,
    Guid BavContractId,
    Guid? BavSnapshotId,
    DateOnly EffectiveDate,
    string FundName,
    string? Isin,
    decimal? WeightPercent,
    decimal? Amount,
    string Currency,
    decimal? OngoingChargesPercent,
    bool OngoingChargesEstimated,
    string AssetClass,
    string Source,
    string? Note,
    DateTimeOffset CreatedAt,
    // Trailing and defaulted so the store can start reporting them without every caller changing at
    // once. A position that predates the column has no document, which is the honest state for one
    // that was typed in by hand.
    Guid? BavDocumentId = null,
    bool FromDocument = false);

/// <summary>
/// The Übersicht figures. Every total is per currency, and a currency that has no rate is reported as
/// missing rather than added in at 1:1 — a pension total is money and must not be quietly wrong.
/// The projected figures are separate from <see cref="TotalBalance"/> on purpose: a projection is not
/// wealth.
/// </summary>
public sealed record BavOverviewView(
    int ContractCount,
    int ActiveCount,
    int PaidUpCount,
    // Sum of the current balances, in the space's base currency.
    decimal TotalBalance,
    string Currency,
    // False when a contract's currency had no rate; its value is then NOT in the total.
    bool IsComplete,
    IReadOnlyList<string> MissingCurrencies,
    // Balances that could not be converted, listed in their own currency.
    IReadOnlyList<BavCurrencyAmount> UnconvertedBalances,
    // The employee share per month across all running arrangements. This is a real outflow.
    decimal MonthlyEmployeeContribution,
    // The employer share per month. A benefit — never counted as an expense.
    decimal MonthlyEmployerContribution,
    // Guaranteed monthly annuity, only where a document stated one.
    decimal GuaranteedMonthlyAnnuity,
    // Projected monthly annuity. Not guaranteed; always labelled.
    decimal ProjectedMonthlyAnnuity,
    // How many contracts have no snapshot at all, so nothing to show yet.
    int ContractsWithoutSnapshot,
    // How many contracts carry at least one cost marked as an estimate.
    int ContractsWithEstimatedCosts);

public sealed record BavCurrencyAmount(string Currency, decimal Amount);

/// <summary>Existing-contract detection: what an importer or the UI asks before creating anything.</summary>
public sealed record BavContractMatchRequest(
    string? PolicyNumber,
    string? ProviderName,
    string? TariffName,
    string? EmployerName,
    DateOnly? RetirementDate);

/// <summary>
/// The answer. <c>matchedOn</c> names which rule fired, so a review screen can say why it thinks this
/// is the same contract instead of silently merging two.
/// </summary>
public sealed record BavContractMatchResult(
    bool Matched,
    Guid? ContractId,
    string? MatchedOn,
    BavContractView? Contract);

public sealed record BavContractWrite(
    string ProviderName,
    string? TariffName = null,
    string? PolicyNumber = null,
    string? ImplementationRoute = null,
    string? Status = null,
    string? EmployerName = null,
    string? PolicyHolderName = null,
    string? InsuredPersonName = null,
    DateOnly? StartDate = null,
    DateOnly? RetirementDate = null,
    DateOnly? ContractEndDate = null,
    string? Currency = null,
    decimal? GuaranteeQuotaPercent = null,
    decimal? GuaranteedAnnuityFactor = null,
    bool FundSelectionChangeable = false,
    // Whether the balance counts in net worth. Applied to the linked asset, not stored twice.
    bool IncludeInNetWorth = true,
    string? Notes = null);

public sealed record BavSnapshotWrite(
    DateOnly EffectiveDate,
    string? Currency = null,
    decimal? Balance = null,
    decimal? GuaranteedBalance = null,
    decimal? SurrenderValue = null,
    decimal? SecurityAssetsAmount = null,
    decimal? FundAssetsAmount = null,
    decimal? GuaranteedCapitalAtRetirement = null,
    decimal? GuaranteedMonthlyAnnuity = null,
    decimal? ProjectedCapitalAtRetirement = null,
    decimal? ProjectedMonthlyAnnuity = null,
    decimal? ProjectionReturnPercent = null,
    string? ProjectionBasis = null,
    string? Source = null,
    Guid? BavDocumentId = null,
    string? DocumentSha256 = null,
    decimal? ExtractionConfidence = null,
    string? Note = null);

public sealed record BavContributionWrite(
    DateOnly ValidFrom,
    DateOnly? ValidUntil = null,
    string? EndReason = null,
    string? Cycle = null,
    string? Currency = null,
    decimal EmployeeAmount = 0m,
    decimal EmployerSubsidyAmount = 0m,
    decimal EmployerAmount = 0m,
    decimal? StatedTotalAmount = null,
    string? Source = null,
    decimal? TaxSavingAmount = null,
    decimal? SocialSecuritySavingAmount = null,
    decimal? NetEffortAmount = null,
    // Mandatory as soon as one of the three amounts above is set. See BavTaxEffectSources.
    string? TaxEffectSource = null,
    string? TaxEffectSourceReference = null,
    Guid? BavDocumentId = null,
    string? Note = null);

public sealed record BavCostWrite(
    DateOnly EffectiveDate,
    string Kind,
    string Basis,
    DateOnly? AppliesUntilDate = null,
    decimal? Amount = null,
    string? Currency = null,
    decimal? Percent = null,
    string? Timing = null,
    bool IsEstimated = false,
    // Mandatory when IsEstimated is true.
    string? EstimateBasis = null,
    bool ContinuesWhenPaidUp = true,
    string? Source = null,
    Guid? BavDocumentId = null,
    Guid? BavSnapshotId = null,
    string? Note = null);

public sealed record BavAllocationWrite(
    DateOnly EffectiveDate,
    string FundName,
    string? Isin = null,
    decimal? WeightPercent = null,
    decimal? Amount = null,
    string? Currency = null,
    decimal? OngoingChargesPercent = null,
    bool OngoingChargesEstimated = false,
    string? AssetClass = null,
    string? Source = null,
    Guid? BavSnapshotId = null,
    /// <summary>Which document this position was read from. See the entity for why it is here.</summary>
    Guid? BavDocumentId = null,
    string? Note = null);

public enum BavMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,
    // The write would have created a duplicate of something that already exists.
    Conflict
}

/// <summary>
/// A write outcome. <see cref="ExistingContractId"/> is set on a <see cref="BavMutationResult.Conflict"/>
/// from a contract create, so the caller knows which contract the statement belongs to and can add a
/// snapshot to it instead of a second contract.
/// </summary>
public sealed record BavContractOutcome(
    BavMutationResult Result,
    BavContractView? Contract = null,
    string? Error = null,
    Guid? ExistingContractId = null);

public sealed record BavSnapshotOutcome(
    BavMutationResult Result,
    BavSnapshotView? Snapshot = null,
    string? Error = null,
    Guid? ExistingSnapshotId = null);

public sealed record BavContributionOutcome(
    BavMutationResult Result,
    BavContributionView? Contribution = null,
    string? Error = null);

public sealed record BavCostOutcome(
    BavMutationResult Result,
    BavCostView? Cost = null,
    string? Error = null);

public sealed record BavAllocationOutcome(
    BavMutationResult Result,
    BavAllocationView? Allocation = null,
    string? Error = null);
