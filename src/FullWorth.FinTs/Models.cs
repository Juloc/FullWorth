namespace FullWorth.FinTs;

public sealed record FinTsBankProfile(
    string Id,
    string Name,
    string Blz,
    string Bic,
    Uri Endpoint,
    IReadOnlySet<FinTsCapability> Capabilities);

public enum FinTsCapability
{
    Accounts,
    Balances,
    Transactions,
    Portfolio,
    Tan,
    DecoupledTan
}

public sealed record FinTsCredentials(string UserId, string Pin, string ProductId)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UserId)) throw new ArgumentException("FinTS user id is required.");
        if (string.IsNullOrWhiteSpace(Pin)) throw new ArgumentException("FinTS PIN/password is required.");
        if (string.IsNullOrWhiteSpace(ProductId)) throw new ArgumentException("FinTS product id is required.");
    }
}

public sealed record FinTsAccount(
    string Iban,
    string Bic,
    string? AccountNumber,
    string? SubAccount,
    string? Owner,
    string? ProductName,
    string Currency,
    bool IsDepot = false);

public sealed record FinTsBalance(decimal Amount, string Currency, DateOnly Date, decimal? Available = null, decimal? CreditLine = null);

public sealed record FinTsTransaction(
    string ExternalKey,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? Counterparty,
    string? Description,
    string RawSource,
    bool Pending = false);

public sealed record FinTsHolding(
    string? Isin,
    string? Wkn,
    string Name,
    decimal Quantity,
    decimal? Price,
    string? PriceCurrency,
    DateOnly? PriceDate,
    decimal? MarketValue,
    string? MarketValueCurrency,
    string? Exchange);

public sealed record FinTsTanMethod(
    string SecurityFunction,
    string Name,
    string TanProcess,
    bool NeedsTanMedium,
    bool IsDecoupled,
    int MaxPolls,
    int WaitBeforeFirstPollSeconds,
    int WaitBeforeNextPollSeconds,
    int SegmentVersion);

public sealed record FinTsTanChallenge(
    string TaskReference,
    string Challenge,
    bool IsDecoupled,
    byte[]? HhdUc = null);

public sealed record FinTsBankParameters(
    int BpdVersion,
    int UpdVersion,
    string SystemId,
    string SecurityFunction,
    string? TanMedium,
    IReadOnlyDictionary<string, int> SegmentVersions,
    IReadOnlyDictionary<string, bool> TanRequired,
    IReadOnlyList<FinTsTanMethod> TanMethods,
    IReadOnlyList<FinTsAccount> Accounts)
{
    public int VersionFor(string responseParameterSegment, int fallback, int minimum = 1)
        => Math.Max(minimum, SegmentVersions.TryGetValue(responseParameterSegment, out var value) ? value : fallback);

    public bool RequiresTan(string requestSegment)
        => TanRequired.TryGetValue(requestSegment, out var required) && required;
}

public sealed record FinTsSessionState(
    string DialogId,
    int MessageNumber,
    FinTsBankParameters Parameters);

public enum FinTsResultKind
{
    Success,
    TanRequired,
    TanPending,
    Empty
}

public sealed record FinTsResult<T>(
    FinTsResultKind Kind,
    T? Value,
    FinTsSessionState Session,
    FinTsTanChallenge? Challenge = null,
    string? Touchdown = null)
{
    public static FinTsResult<T> Success(T value, FinTsSessionState session, string? touchdown = null)
        => new(FinTsResultKind.Success, value, session, null, touchdown);

    public static FinTsResult<T> Empty(FinTsSessionState session)
        => new(FinTsResultKind.Empty, default, session);

    public static FinTsResult<T> TanRequired(FinTsSessionState session, FinTsTanChallenge challenge)
        => new(FinTsResultKind.TanRequired, default, session, challenge);

    public static FinTsResult<T> TanPending(FinTsSessionState session, FinTsTanChallenge challenge)
        => new(FinTsResultKind.TanPending, default, session, challenge);
}

public sealed class FinTsException(string message, string? code = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
}

public sealed record FinTsOpenResult(FinTsResultKind Kind, FinTsSessionState Session, FinTsTanChallenge? Challenge = null)
{
    public bool IsOpen => Kind == FinTsResultKind.Success;
}
