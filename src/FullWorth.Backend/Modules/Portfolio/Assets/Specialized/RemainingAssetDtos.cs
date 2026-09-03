namespace FullWorth.Backend.Modules.Portfolio;

public sealed record CollectibleDetailWrite(
    string Category,
    string? Maker,
    string? Model,
    string? SerialNumber,
    string? Condition,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    decimal? InsuredValue,
    decimal? AppraisedValue,
    DateOnly? AppraisedAt,
    string? ProvenanceNotes);

public sealed record CollectibleDetailView(
    Guid AssetId,
    string Category,
    string? Maker,
    string? Model,
    string? SerialNumber,
    string? Condition,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    decimal? InsuredValue,
    decimal? AppraisedValue,
    DateOnly? AppraisedAt,
    string? ProvenanceNotes,
    DateTimeOffset UpdatedAt);

public sealed record ReceivableDetailWrite(
    string CounterpartyDisplayLabel,
    decimal OriginalPrincipal,
    decimal OutstandingPrincipal,
    string Currency,
    decimal? InterestRate,
    DateOnly? StartDate,
    DateOnly? DueDate,
    string? PaymentCycle,
    decimal? ExpectedPayment,
    string Status = "active",
    string? Notes = null);

public sealed record ReceivableDetailView(
    Guid AssetId,
    string CounterpartyDisplayLabel,
    decimal OriginalPrincipal,
    decimal OutstandingPrincipal,
    string Currency,
    decimal? InterestRate,
    DateOnly? StartDate,
    DateOnly? DueDate,
    string? PaymentCycle,
    decimal? ExpectedPayment,
    string Status,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record ReceivablePaymentWrite(
    Guid? TransactionId,
    DateOnly Date,
    decimal PrincipalAmount,
    decimal InterestAmount,
    string Currency,
    string? Notes);

public sealed record ReceivablePaymentView(
    Guid Id,
    Guid AssetId,
    Guid? TransactionId,
    DateOnly Date,
    decimal PrincipalAmount,
    decimal InterestAmount,
    string Currency,
    string? Notes,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt);

public sealed record ReceivableWriteDownRequest(
    decimal RecoverableAmount,
    bool Confirmed);

public sealed record ReceivableMutationView(
    ReceivableDetailView Detail,
    decimal AcceptedAssetValue,
    string AssetCurrency,
    DateOnly? ValuedAt);

public sealed record BusinessInterestDetailWrite(
    string CompanyDisplayName,
    string? LegalForm,
    decimal? OwnershipPercent,
    DateOnly? AcquisitionDate,
    decimal? InvestedCapital,
    string? InvestedCurrency,
    string? ValuationMethod,
    DateOnly? LastDistributionDate,
    string? Notes);

public sealed record BusinessInterestDetailView(
    Guid AssetId,
    string CompanyDisplayName,
    string? LegalForm,
    decimal? OwnershipPercent,
    DateOnly? AcquisitionDate,
    decimal? InvestedCapital,
    string? InvestedCurrency,
    string? ValuationMethod,
    DateOnly? LastDistributionDate,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record InsurancePensionDetailWrite(
    string? ProviderName,
    string? ProductName,
    string ProductType,
    string? PolicyReference,
    DateOnly? StartDate,
    DateOnly? MaturityDate,
    decimal? RegularContribution,
    string? ContributionCycle,
    decimal? GuaranteedValue,
    DateOnly? GuaranteedValueDate,
    string? Notes);

public sealed record InsurancePensionDetailView(
    Guid AssetId,
    string? ProviderName,
    string? ProductName,
    string ProductType,
    string? PolicyReference,
    DateOnly? StartDate,
    DateOnly? MaturityDate,
    decimal? RegularContribution,
    string? ContributionCycle,
    decimal? GuaranteedValue,
    DateOnly? GuaranteedValueDate,
    string? Notes,
    DateTimeOffset UpdatedAt);
