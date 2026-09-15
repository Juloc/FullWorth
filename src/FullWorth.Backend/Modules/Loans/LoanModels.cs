using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Loans;

public sealed class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal OriginalPrincipal { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal PaymentAmount { get; set; }
    public decimal NominalInterestRate { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? FixedTermMonths { get; set; }
    public decimal Fees { get; set; }
    public string PaymentFrequency { get; set; } = "monthly";
    public string Currency { get; set; } = "EUR";
    public Guid? CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
