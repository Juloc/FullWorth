using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public enum CategoryMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record CategoryMutationOutcome<T>(CategoryMutationResult Result, T? Value = default, string? Error = null);

public sealed record CategoryWrite(string Key, string Name, Guid? ParentId, string? Icon, int? SortOrder);

public sealed record CategoryUpdate(string Name, Guid? ParentId, string? Icon, int? SortOrder);

public sealed record ReapplyResult(int Evaluated, int Changed, bool Applied);

public sealed record RulePreviewMatch(Guid Id, DateOnly? Date, string? Label, decimal Amount, string Currency, string? CurrentCategory);

public sealed record RulePreviewResult(int Evaluated, int Matched, IReadOnlyList<RulePreviewMatch> Sample, bool ScanCapped);

public sealed record RuleWrite(string Name, bool IsEnabled, int Priority, string Target, string MatchField, string MatchMode, string Pattern, string Direction, decimal? MinAmount, decimal? MaxAmount, string? MerchantCategoryCode, Guid CategoryId, bool MarkAsTransfer, bool StopProcessing);
