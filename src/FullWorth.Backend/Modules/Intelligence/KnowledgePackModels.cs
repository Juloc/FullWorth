namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Read-only merchant-to-category mapping DTO consumed by the deterministic transaction rule engine.
/// Sourced knowledge distribution lives outside this instance; the engine simply tolerates an empty set
/// when no external mappings are available.
/// </summary>
public sealed record OfficialMerchantCategoryMapping(
    string AliasKey,
    string Direction,
    string CategoryKey,
    decimal Confidence);
