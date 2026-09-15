using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed class FinanceCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public string? Icon { get; set; }
    public bool IsSystem { get; set; }
    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CategorizationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Target { get; set; } = "transaction";
    public string MatchField { get; set; } = "combined";
    public string MatchMode { get; set; } = "contains";
    public string Pattern { get; set; } = string.Empty;
    public string Direction { get; set; } = "any";
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? MerchantCategoryCode { get; set; }
    public Guid CategoryId { get; set; }
    public bool MarkAsTransfer { get; set; }
    public bool StopProcessing { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
