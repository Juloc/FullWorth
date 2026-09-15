using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Merchants;

public enum MerchantResult { Success, NotFound, Forbidden, Invalid }

public sealed record MerchantOutcome<T>(MerchantResult Result, T? Value = default, string? Error = null);

public sealed record MerchantAliasView(Guid Id, string NormalizedAlias);

public sealed record MerchantView(Guid Id, string Name, string NormalizedName, IReadOnlyList<MerchantAliasView> Aliases);

public sealed record ResolveView(string? Normalized, Guid? MerchantId, string? MerchantName);

public sealed record MerchantWrite(string Name);

public sealed record AliasWrite(string Alias);

public sealed record MerchantMergeWrite(Guid SourceMerchantId);
