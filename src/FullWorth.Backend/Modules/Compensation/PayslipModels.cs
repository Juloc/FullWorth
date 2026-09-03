namespace FullWorth.Backend.Modules.Compensation;

public sealed record PayslipExtractionResult(
    DateOnly? Period,
    decimal? GrossPay,
    decimal? NetPay,
    decimal? Payout,
    decimal? WageTax,
    decimal? SolidaritySurcharge,
    decimal? ChurchTax,
    decimal? PensionInsurance,
    decimal? UnemploymentInsurance,
    decimal? HealthInsurance,
    decimal? CareInsurance,
    decimal? CompanyCarTaxableBenefit,
    decimal? BavEmployee,
    decimal? BavEmployer,
    decimal? Bonus,
    decimal ConfidencePercent,
    IReadOnlyList<string> DetectedLabels,
    IReadOnlyList<string> Warnings);

public sealed record PayslipRecordWrite(
    DateOnly Period,
    decimal GrossPay,
    decimal NetPay,
    decimal Payout,
    decimal WageTax = 0m,
    decimal SolidaritySurcharge = 0m,
    decimal ChurchTax = 0m,
    decimal PensionInsurance = 0m,
    decimal UnemploymentInsurance = 0m,
    decimal HealthInsurance = 0m,
    decimal CareInsurance = 0m,
    decimal CompanyCarTaxableBenefit = 0m,
    decimal BavEmployee = 0m,
    decimal BavEmployer = 0m,
    decimal Bonus = 0m,
    string? Note = null,
    string Source = "manual");

public sealed record PayslipRecordView(
    Guid Id,
    Guid FullWorthSpaceId,
    PayslipRecordWrite Payslip,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PayslipDelta(
    PayslipRecordView Previous,
    PayslipRecordView Current,
    decimal GrossDelta,
    decimal NetDelta,
    decimal PayoutDelta,
    decimal TaxDelta,
    decimal SocialInsuranceDelta,
    decimal BavDelta,
    decimal CompanyCarDelta,
    IReadOnlyList<string> Explanations);
