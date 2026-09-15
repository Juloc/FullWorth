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

    /// <summary>
    /// Ab wann der Uebertrag gerechnet wird - eine andere Frage als der Periodenbeginn (#115).
    /// "as-far-back-as-possible", "this-period" oder "from-date". NULL heisst so weit zurueck wie
    /// moeglich; das ist, was die Rechnung vor dieser Spalte immer getan hat.
    /// </summary>
    public string? CarryOverStart { get; set; }

    /// <summary>Das selbst gewaehlte Startdatum, wenn <see cref="CarryOverStart"/> "from-date" ist.</summary>
    public DateOnly? CarryOverFrom { get; set; }
    public bool IsActive { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
