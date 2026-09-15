using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

// ReferenceDate is the as-of date, CapturedAt when FullWorth recorded it. Both are shown: an owner
// who anchors an account with last month's statement needs to see that it is last month's figure.
public sealed record BalanceView(
    decimal Amount,
    string Currency,
    string BalanceType,
    DateTimeOffset CapturedAt,
    DateOnly? ReferenceDate = null,
    string? Source = null,
    string? Note = null)
{
    /// <summary>
    /// What this figure IS (<see cref="BalanceMeanings"/>): available, booked, expected or just recorded.
    /// A computed property on purpose — no caller passes it, so it can never be stamped with something
    /// the balance type does not say, and every surface labels the amount from the same rule.
    /// </summary>
    public string Meaning => CurrentBalances.Meaning(BalanceType);
}

// BaseValue/BaseCurrency (§18): the latest balance converted into the space's base currency, for the
// "native first, smaller converted base underneath" row display. Null when the account is already in
// the base currency or no conversion rate is available (the row then shows only its native amount).
//
// LatestBalance is the HEADLINE figure only. Balances carries the account's current balance in every
// currency it holds - PayPal, Wise and Revolut report a wallet per currency - and it is what any total
// must be built from. It used to be one row per account, so every other wallet was invisible.
// DuplicateOf* names the account this one is the same bank account as, reached through another
// provider (same IBAN) or declared so by the owner. Both connections are kept on purpose - each brings
// data the other does not - so the row has to say why one of them is not in the totals.
// DuplicateLinkExplicit separates the two sources: only a link the owner made can be taken back, and
// the row's actions must not offer "unlink" for a plain IBAN match.
public sealed record AccountListItem(Guid Id, Guid FullWorthSpaceId, Guid? BankConnectionId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool IncludeInNetWorth, int SortOrder, DateTimeOffset UpdatedAt, string Provider, BalanceView? LatestBalance, Guid? GroupId = null, string? GroupName = null, decimal? BaseValue = null, string? BaseCurrency = null, IReadOnlyList<BalanceView>? Balances = null, Guid? DuplicateOfAccountId = null, string? DuplicateOfDisplayName = null, bool DuplicateLinkExplicit = false);

public sealed record AccountCreateRequest(Guid FullWorthSpaceId, Guid? BankConnectionId, string DisplayName, string? Currency, bool? IncludeInNetWorth, int? SortOrder, string? InstitutionName = null, decimal? InitialBalance = null);

public sealed record AccountSettingsRequest(string? DisplayName, bool? IsActive, bool? IncludeInNetWorth, int? SortOrder);

// AsOf defaults to today when omitted, so an existing caller keeps its behaviour. Note is the
// owner's remark about where the figure came from.
public sealed record ManualBalanceRequest(decimal Amount, string? Currency, DateOnly? AsOf = null, string? Note = null);

public sealed record AccountGroupDto(Guid Id, Guid FullWorthSpaceId, string Name, int SortOrder);

public sealed record AccountGroupWrite(string Name, int? SortOrder);

public sealed record AccountGroupAssignRequest(Guid? GroupId);

public enum AccountGroupResult { Ok, NotFound, Forbidden }

// --- Explicit "these two accounts are the same" link (O-4 / O-5) ---

/// <summary>The account this one is to be counted as. Any account of the space qualifies, wallets and
/// cash included - the link is deliberately not keyed on an IBAN.</summary>
public sealed record AccountLinkRequest(Guid DuplicateOfAccountId);

/// <summary>A candidate for the picker: every other account of the space the caller owns.</summary>
public sealed record AccountLinkCandidate(
    Guid Id,
    string DisplayName,
    string InstitutionName,
    string Currency,
    string? IbanLast4,
    bool IsActive,
    bool IncludeInNetWorth,
    bool IsLinked);

/// <summary>
/// What the owner needs to decide: whether this account is already linked (and by whom - a decision of
/// theirs or the automatic IBAN rule), which accounts declare themselves the same as this one, and what
/// can be picked.
/// </summary>
public sealed record AccountLinkState(
    Guid AccountId,
    Guid? DuplicateOfAccountId,
    string? DuplicateOfDisplayName,
    bool Explicit,
    IReadOnlyList<AccountLinkCandidate> Candidates,
    IReadOnlyList<AccountLinkCandidate> LinkedToThis);

public enum AccountLinkResult
{
    Ok,
    NotFound,
    Forbidden,
    /// <summary>The picked account is itself declared a duplicate of a third one.</summary>
    TargetIsLinked,
    /// <summary>Other accounts are already counted as this one, so it cannot become a duplicate itself.</summary>
    IsLinkTarget,
    /// <summary>The picked account is out of the totals, so linking would count the money zero times.</summary>
    TargetNotCounted,
    /// <summary>Unlink on an account that carries no explicit link.</summary>
    NotLinked
}

public enum ManualBalanceResult
{
    Ok,
    NotFound,
    Forbidden,
    NotManual
}
