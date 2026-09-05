namespace FullWorth.FinTs;

public static class KnownBanks
{
    public static readonly FinTsBankProfile Ing = new(
        "ing-de",
        "ING",
        "50010517",
        "INGDDEFFXXX",
        new Uri("https://fints.ing.de/fints/"),
        new HashSet<FinTsCapability>
        {
            FinTsCapability.Accounts,
            FinTsCapability.Balances,
            FinTsCapability.Transactions,
            FinTsCapability.Portfolio,
            FinTsCapability.Tan,
            FinTsCapability.DecoupledTan
        });

    public static IReadOnlyList<FinTsBankProfile> All { get; } = [Ing];

    public static FinTsBankProfile Get(string id)
        => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Unknown FinTS bank profile '{id}'.");
}
