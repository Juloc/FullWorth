using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Merchants;

/// <summary>Deterministic counterparty normalization shared by ingestion and the merchant registry.</summary>
public static class MerchantNormalization
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Trim().ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        var normalized = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }
}
