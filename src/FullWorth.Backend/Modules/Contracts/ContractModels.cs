
namespace FullWorth.Backend.Modules.Contracts;

public sealed class RecurringContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string Kind { get; set; } = "contract";
    public Guid? CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? MergedIntoContractId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string BillingCycle { get; set; } = "monthly";
    public int Interval { get; set; } = 1;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? NextDueDate { get; set; }
    public bool AutoDetected { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this contract counts as a fixed cost — against the cashflow, and against the forward
    /// preview on the wealth page. Default true, because that is what every consumer assumed before
    /// the flag existed: they treated every active contract as a fixed cost.
    ///
    /// It exists because not every recurring contract is money leaving each month in a way that
    /// should reduce what is available to spend. A contract that is really a savings plan, one whose
    /// payment is already counted somewhere else, or one the owner simply wants out of the forecast
    /// had no way to say so — and the alternative, deactivating it, also removes it from the list
    /// where it belongs.
    ///
    /// Turning it off changes no history and no transaction. It only says: do not subtract this from
    /// what is available.
    /// </summary>
    public bool CountsAsFixedCost { get; set; } = true;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
