using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Loans;

public sealed record LoanView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    decimal OriginalPrincipal,
    decimal CurrentBalance,
    decimal PaymentAmount,
    decimal NominalInterestRate,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? FixedTermMonths,
    decimal Fees,
    string PaymentFrequency,
    string Currency,
    Guid? CategoryId,
    Guid? AccountId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum LoanMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public enum AmortizationStatus
{
    Ok,
    NotFound,
    Insufficient
}

public sealed record AmortizationOutcome(AmortizationStatus Status, object? Result = null);

public sealed record LoanMutationOutcome(LoanMutationResult Result, LoanView? Loan = null, string? Error = null);

public sealed record LoanWrite(
    string Name,
    decimal OriginalPrincipal,
    decimal CurrentBalance,
    decimal PaymentAmount,
    decimal NominalInterestRate,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? FixedTermMonths,
    decimal Fees,
    string PaymentFrequency,
    string Currency,
    Guid? CategoryId,
    Guid? AccountId,
    bool IsActive);
