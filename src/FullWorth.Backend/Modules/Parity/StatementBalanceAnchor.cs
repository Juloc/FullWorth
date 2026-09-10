namespace FullWorth.Backend.Modules.Parity;

/// <summary>What to do with the closing balance a statement states.</summary>
internal enum StatementBalanceOutcome
{
    /// <summary>Store it. The account gets its value from this statement.</summary>
    Apply,

    /// <summary>The statement is in a currency this account does not hold.</summary>
    CurrencyMismatch,

    /// <summary>A live connection already reported a balance for this day or later.</summary>
    NewerProviderBalance,

    /// <summary>An owner-entered balance is already at least as recent.</summary>
    NewerManualBalance
}

/// <summary>
/// Decides whether a statement's closing balance may become the account's balance.
///
/// The rule the owner asked for is "the manual or imported balance is replaced by the first reliable
/// live balance", and it has to hold in both directions: importing last month's statement must not
/// overwrite what the bank reported this morning, and importing today's statement into an account that
/// has no connection must anchor it.
///
/// Selection of the current balance is by capture time, not by as-of date, so an import written now
/// would out-rank an older-dated provider balance simply because it was written later. That is why the
/// comparison here is on the AS-OF dates: which figure describes the more recent state of the account.
/// </summary>
internal static class StatementBalanceAnchor
{
    /// <summary>One existing balance, reduced to what the decision needs.</summary>
    internal readonly record struct ExistingBalance(string? Source, string Currency, DateOnly AsOf);

    internal static StatementBalanceOutcome Decide(
        StatementBalance statement,
        string accountCurrency,
        IEnumerable<ExistingBalance> existing)
    {
        if (!string.Equals(statement.Currency, accountCurrency, StringComparison.OrdinalIgnoreCase))
            return StatementBalanceOutcome.CurrencyMismatch;

        // "At least as recent" loses on purpose: re-importing the same statement should not restate a
        // figure the bank has since confirmed itself.
        foreach (var balance in existing)
        {
            if (!string.Equals(balance.Currency, accountCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            if (balance.AsOf < statement.AsOf) continue;
            return balance.Source == Accounts.BalanceSources.Provider
                ? StatementBalanceOutcome.NewerProviderBalance
                : StatementBalanceOutcome.NewerManualBalance;
        }

        return StatementBalanceOutcome.Apply;
    }

    /// <summary>The reason code the API reports when a statement balance was not applied.</summary>
    internal static string? SkipReason(StatementBalanceOutcome outcome) => outcome switch
    {
        StatementBalanceOutcome.CurrencyMismatch => "currency_mismatch",
        StatementBalanceOutcome.NewerProviderBalance => "newer_provider_balance",
        StatementBalanceOutcome.NewerManualBalance => "newer_manual_balance",
        _ => null
    };
}
