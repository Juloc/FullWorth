using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

public sealed class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Period { get; set; } = "monthly";
    public bool CarryOver { get; set; }
    public bool CarryOverOverspend { get; set; }
    public bool IsActive { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
