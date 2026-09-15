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
    bool IsDepot = false,
    /// <summary>
    /// Die Bankleitzahl - der Kreditinstitutscode der klassischen Kontoverbindung (#130 §3).
    ///
    /// An dieser Stelle stand bisher der BIC. Das ist nicht dasselbe Feld: bei Laenderkennzeichen 280
    /// erwartet der Server dort acht Ziffern, und "INGDDEFFXXX" sind sie nicht. Die Bank weist die
    /// Nachricht ab oder liefert nichts.
    /// </summary>
    string? BankCode = null)
{
    /// <summary>
    /// Die Bankleitzahl, die in eine Kontoverbindung gehoert: die genannte, sonst die aus der IBAN.
    ///
    /// Eine deutsche IBAN traegt sie an Stelle 5 bis 12 - DE, zwei Pruefziffern, acht Stellen BLZ. Das
    /// ist keine Schaetzung, sondern die Definition.
    /// </summary>
    public string? ResolvedBankCode =>
        !string.IsNullOrWhiteSpace(BankCode) ? BankCode!.Trim()
        : Iban is { Length: >= 12 } && Iban.StartsWith("DE", StringComparison.OrdinalIgnoreCase)
            ? Iban.Substring(4, 8)
            : null;
}

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
    /// <summary>
    /// Die Version, in der ein Geschaeftsvorfall zu schicken ist: die, die die Bank angekuendigt hat,
    /// sonst der Rueckfall. <paramref name="minimum"/> ist die aelteste Version, die es fuer diesen
    /// Vorfall ueberhaupt gibt - eine aeltere anzunehmen erzeugt eine Nachricht, die kein Server kennt.
    /// </summary>
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

/// <summary>
/// Der AUFBAU einer gesendeten Nachricht: welche Segmente in welcher Version, und wie viele
/// Datenelemente jedes hatte. Keine Werte - kein Benutzername, keine PIN, keine TAN, keine Betraege.
///
/// Dafuer, dass "Unbekannter Aufbau der Kundennachricht" (9110) beantwortbar wird, ohne die Nachricht
/// selbst zu protokollieren. Die traegt in PIN/TAN-Verfahren die PIN im Signaturblock, und die gehoert
/// in kein Protokoll - auch nicht in ein Fehlerprotokoll.
/// </summary>
public sealed record FinTsSegmentShape(string Type, int Version, int Elements)
{
    public override string ToString() => $"{Type}:v{Version}:{Elements}";
}

/// <summary>Ein Rueckmeldecode der Bank, so wie er kam.</summary>
public sealed record FinTsBankCode(string Code, string? SegmentReference, string Text);

public sealed class FinTsException(
    string message,
    string? code = null,
    Exception? inner = null,
    string? bankCode = null,
    string? segmentReference = null,
    string? bankMessage = null,
    IReadOnlyList<FinTsBankCode>? bankCodes = null,
    IReadOnlyList<FinTsSegmentShape>? sentShape = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
    public string? BankCode { get; } = bankCode;
    public string? SegmentReference { get; } = segmentReference;
    public string? BankMessage { get; } = bankMessage;

    /// <summary>
    /// ALLE Rueckmeldungen der Bank, in der Reihenfolge, in der sie kamen (#130 §9).
    ///
    /// Frueher stand hier nur die erste. Bei ING heisst die erste regelmaessig 9800 "Der Dialog wurde
    /// abgebrochen" - das ist die Sammelmeldung und nicht der Grund. Der Grund steht in den Codes
    /// dahinter, und die wurden weggeworfen: im Protokoll stand, DASS es scheiterte, nie WORAN.
    /// </summary>
    public IReadOnlyList<FinTsBankCode> BankCodes { get; } = bankCodes ?? [];

    /// <summary>
    /// Der Aufbau der Nachricht, die zu diesem Fehler gefuehrt hat (#130 §11).
    ///
    /// Bei 9110 "Unbekannter Aufbau der Kundennachricht" ist das die einzige Spur, die weiterhilft:
    /// die Bank sagt, dass sie die Nachricht nicht lesen kann, und ohne ihren Bauplan bleibt nur Raten.
    /// </summary>
    public IReadOnlyList<FinTsSegmentShape> SentShape { get; } = sentShape ?? [];

    /// <summary>Der Bauplan als "HKIDN:v2:4, HKVVB:v3:5, HKTAN:v7:11".</summary>
    public string SentShapeSummary => string.Join(", ", SentShape);

    /// <summary>Alle Codes als "9800 Der Dialog wurde abgebrochen; 9010 …" - fuer ein Protokoll, eine Zeile.</summary>
    public string BankCodeSummary => BankCodes.Count == 0
        ? BankCode ?? string.Empty
        : string.Join("; ", BankCodes.Select(entry =>
            string.IsNullOrWhiteSpace(entry.SegmentReference)
                ? $"{entry.Code} {entry.Text}"
                : $"{entry.Code}@{entry.SegmentReference} {entry.Text}"));
}

public sealed record FinTsOpenResult(FinTsResultKind Kind, FinTsSessionState Session, FinTsTanChallenge? Challenge = null)
{
    public bool IsOpen => Kind == FinTsResultKind.Success;
}
