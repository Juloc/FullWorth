using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Banking.Backend;
using FullWorth.FinTs;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class FinTsOptions
{
    public const string SectionName = "FinTs";
    public string ProductId { get; set; } = string.Empty;
    public int HistoryDays { get; set; } = 90;
    public int MaxPages { get; set; } = 50;
}

public sealed record ConnectIngFinTsRequest(
    string UserId,
    string Pin,
    string? TanMedium = null,
    Guid? ReconnectConnectionId = null);

public sealed record FinTsTanSubmitRequest(string Tan);

/// <summary>Die Schluessel der Konten, die NICHT angezeigt werden sollen. Alles andere wird uebernommen.</summary>
public sealed record FinTsImportRequest(IReadOnlyList<string>? Hidden);

/// <summary>
/// Ein Konto, das die Bank gemeldet hat - vor der Entscheidung, ob es uebernommen wird.
///
/// <paramref name="Key"/> ist derselbe Wert wie der <c>IdentificationHash</c> des Kontos und wie der
/// <c>DepotKey</c> des Depotstands. Ein Schluessel fuer alles.
///
/// <paramref name="AccountId"/> ist gesetzt, wenn es das Konto schon gibt; dann sagt
/// <paramref name="Visible"/>, wie es derzeit steht. Fuer ein neues Konto ist es der Vorschlag.
///
/// Voll ausgeschrieben steht hier nichts: <paramref name="IbanLast4"/> sind vier Stellen, keine
/// Kontonummer.
/// </summary>
public sealed record FinTsDiscoveredAccount(
    string Key,
    string Kind,
    string Name,
    string? IbanLast4,
    string Currency,
    Guid? AccountId,
    bool Visible);

/// <summary>
/// Was die Uebernahme hinterlassen hat - gezaehlt an den Konten, nicht an der Ankuendigung.
///
/// <paramref name="Missing"/> und <paramref name="Error"/> sind der Grund, warum dieser Datensatz
/// nicht nur zaehlt: der Abruf kann mittendrin scheitern. Dann stehen die bis dahin angelegten
/// Konten da, das Depot fehlt - und die Antwort meldete trotzdem eine glatte Zahl. "4 Konten
/// uebernommen" war wahr und trotzdem die falsche Auskunft, weil fuenf gemeldet worden waren.
/// </summary>
public sealed record FinTsImportOutcome(int Accounts, int Depots, int Hidden, int Missing, string? Error);

public sealed record FinTsConnectionResult(
    Guid ConnectionId,
    string Status,
    FinTsTanChallenge? Challenge,
    IReadOnlyList<FinTsDiscoveredAccount> Discovered,
    FinTsImportOutcome? Imported = null);

internal sealed record FinTsConnectionSecret(
    string BankId,
    string UserId,
    string Pin,
    string ProductId,
    FinTsBankParameters Parameters,
    FinTsSessionState? Session = null,
    FinTsTanChallenge? Challenge = null,
    /// <summary>
    /// Die Schluessel der Konten, die der Eigentuemer NICHT sehen will.
    ///
    /// Sie stehen hier und nicht nur am Konto, weil sie schon gebraucht werden, bevor es das Konto
    /// gibt: die Uebernahme legt es mit <c>IsActive=false</c> an. Danach ist das Konto die Wahrheit -
    /// diese Liste sorgt nur dafuer, dass eine wiederholte oder durch eine TAN unterbrochene
    /// Uebernahme dieselbe Entscheidung trifft.
    ///
    /// Ein altes Geheimnis ohne dieses Feld liest sich als "nichts ausgeblendet" - genau das bisherige
    /// Verhalten, also keine Migration.
    /// </summary>
    IReadOnlyList<string>? HiddenAccountKeys = null);

public sealed class IngFinTsService(
    FinTsClient finTs,
    FullWorthBackendClient backend,
    IOptionsMonitor<FinTsOptions> options,
    IOptions<BankingSyncOptions> syncOptions,
    BankSyncConcurrencyGate syncGate,
    ILogger<IngFinTsService> logger)
{
    private readonly IOptionsMonitor<FinTsOptions> _options = options;
    private readonly BankingSyncOptions _sync = syncOptions.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Die Verbindung steht, aber der Eigentuemer hat noch nicht gesagt, welche Konten er will.
    ///
    /// Ein eigener Zustand und nicht einfach ein Fehler: die Zugangsdaten stimmen, die Sitzung ist
    /// brauchbar, und die eine Handlung, die weiterhilft, ist die Auswahl - nicht ein erneutes
    /// Verbinden, das die PIN neu erfraegt. Aus demselben Grund gibt es <c>FINTS_TAN_REQUIRED</c>.
    /// </summary>
    public const string SelectionPending = "FINTS_SELECTION_PENDING";

    /// <summary>Ob diese Verbindung noch auf die Auswahl des Eigentuemers wartet.</summary>
    public static bool IsPendingSelection(BankConnectionDto connection)
        => string.Equals(connection.LastError, SelectionPending, StringComparison.Ordinal);

    public async Task<FinTsConnectionResult> ConnectAsync(
        ConnectIngFinTsRequest request,
        BankingCaller caller,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Pin))
            throw new ArgumentException("ING login and PIN/password are required.");
        var productId = ProductId();
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, request.ReconnectConnectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);

        var bank = KnownBanks.Ing;
        var credentials = new FinTsCredentials(request.UserId.Trim(), request.Pin, productId);
        var parameters = await LogFinTsAsync("synchronize", () => finTs.SynchronizeAsync(bank, credentials, ct));
        LogAnnouncedCapabilities(parameters);
        if (!string.IsNullOrWhiteSpace(request.TanMedium)) parameters = parameters with { TanMedium = request.TanMedium.Trim() };
        var opened = await LogFinTsAsync("open", () => finTs.OpenAsync(bank, credentials, parameters, ct));
        var secret = new FinTsConnectionSecret(bank.Id, credentials.UserId, credentials.Pin, credentials.ProductId,
            opened.Session.Parameters, opened.IsOpen ? null : opened.Session, opened.Challenge);

        var status = opened.IsOpen ? "AUTHORIZED" : "TAN_REQUIRED";
        var connection = await backend.UpsertConnectionAsync(new BankConnectionWrite(
            request.ReconnectConnectionId,
            "fints",
            "ING",
            "DE",
            null,
            JsonSerializer.Serialize(secret, Json),
            SessionKey(caller.FullWorthSpaceId, credentials.UserId),
            status,
            null,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes)),
            0,
            // Die Verbindung wartet auf die Auswahl. Bis dahin holt sie nichts - weder hier noch im
            // Hintergrund - und die Zeile bietet "Auswahl abschliessen" an statt "Neu verbinden".
            SelectionPending,
            request.ReconnectConnectionId.HasValue ? null : caller.FullWorthSpaceId,
            caller.UserId,
            null,
            null,
            "personal",
            "fints-pin-tan",
            "[]"), ct);

        return new(connection.Id, connection.Status, opened.Challenge,
            await DescribeAsync(connection, opened.Session.Parameters.Accounts, ct));
    }

    /// <summary>
    /// Was die Bank gemeldet hat - ohne sie noch einmal zu fragen.
    ///
    /// Die Kontenliste kommt aus HIUPD und liegt seit dem Verbinden verschluesselt an der Verbindung.
    /// Ein abgebrochener Ablauf ist damit ohne Bankkontakt wieder aufnehmbar.
    /// </summary>
    public async Task<FinTsConnectionResult> DiscoveredAsync(Guid connectionId, BankingCaller caller, CancellationToken ct)
    {
        var connection = await FindAsync(connectionId, ct) ?? throw new BankAccessException(false);
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);

        var secret = ReadSecret(connection) ?? throw new InvalidOperationException("FINTS_SECRET_MISSING");
        return new(connection.Id, connection.Status, secret.Challenge,
            await DescribeAsync(connection, secret.Parameters.Accounts, ct));
    }

    /// <summary>
    /// Die Uebernahme: die Auswahl festhalten und dann erst die Daten holen.
    ///
    /// Der lange Teil liegt bewusst HINTER der Auswahl. Vorher steckte er im Verbinden, und damit sahen
    /// eine falsche PIN und ein Abruf von neunzig Tagen Umsaetzen gleich aus - der Knopf war grau, und
    /// sonst passierte nichts.
    ///
    /// Gelaufen wird ueber <c>RequestManualSyncAsync</c> und nicht direkt: dort sitzt das Gatter, das
    /// zwei Synchronisationen derselben Verbindung auseinanderhaelt. Das Verbinden lief bisher daran
    /// vorbei.
    /// </summary>
    public async Task<FinTsConnectionResult> ImportAsync(
        Guid connectionId, IReadOnlyList<string> hidden, BankingCaller caller, CancellationToken ct)
    {
        var connection = await FindAsync(connectionId, ct) ?? throw new BankAccessException(false);
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);

        var secret = ReadSecret(connection) ?? throw new InvalidOperationException("FINTS_SECRET_MISSING");
        var chosen = hidden.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        connection = await backend.UpsertConnectionAsync(ToWrite(connection,
            authorizationId: JsonSerializer.Serialize(secret with { HiddenAccountKeys = chosen }, Json),
            lastError: null), ct);

        connection = await SyncGatedAsync(connection, ct);
        var saved = ReadSecret(connection);
        var accounts = saved?.Parameters.Accounts ?? [];
        var described = await DescribeAsync(connection, accounts, ct);

        return new(connection.Id, connection.Status, saved?.Challenge, described,
            Outcome(described, connection.LastError));
    }

    /// <summary>
    /// Was die Uebernahme hinterlassen hat, gelesen an den Konten - nicht an der Ankuendigung.
    ///
    /// Eine Id hat nur, was es wirklich gibt. Daran haengt alles: die Zaehlung, und die Erkenntnis,
    /// dass etwas fehlt. Die alte Fassung zaehlte nur und verschwieg zweierlei - dass die Bank mehr
    /// gemeldet hatte, und dass der Abruf mittendrin abgebrochen war. "4 Konten uebernommen" war
    /// wahr und trotzdem die falsche Auskunft: fuenf waren gemeldet, das Depot fehlte, und im Dialog
    /// stand nichts davon.
    ///
    /// Eigene Funktion, weil sie genau das ist, was schiefging - und ohne Bank und ohne Datenbank
    /// pruefbar sein muss.
    /// </summary>
    public static FinTsImportOutcome Outcome(IReadOnlyList<FinTsDiscoveredAccount> described, string? lastError)
    {
        var cash = described.Count(x => x.Kind == "cash" && x.AccountId.HasValue);
        var depots = described.Count(x => x.Kind == "depot" && x.AccountId.HasValue);
        return new(
            cash,
            depots,
            described.Count(x => x.AccountId.HasValue && !x.Visible),
            described.Count - cash - depots,
            lastError);
    }

    /// <summary>
    /// Die gemeldeten Konten, verbunden mit dem, was FullWorth davon schon kennt.
    ///
    /// Die Verbindung stellt der <c>IdentificationHash</c> her - derselbe Wert, den
    /// <see cref="AccountHash"/> berechnet. Ohne diese Zuordnung koennte die Oberflaeche nicht sagen,
    /// welches der gefundenen Konten es schon gibt und wie es derzeit steht.
    /// </summary>
    private async Task<IReadOnlyList<FinTsDiscoveredAccount>> DescribeAsync(
        BankConnectionDto connection, IReadOnlyList<FinTsAccount> accounts, CancellationToken ct)
    {
        var known = await backend.ListConnectionAccountsAsync(connection.Id, ct);
        var byHash = known.ToDictionary(x => x.IdentificationHash, StringComparer.Ordinal);
        var hidden = (ReadSecret(connection)?.HiddenAccountKeys ?? []).ToHashSet(StringComparer.Ordinal);

        return accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Iban) || !string.IsNullOrWhiteSpace(account.AccountNumber))
            .Select(account =>
            {
                var key = AccountHash(account);
                var existing = byHash.GetValueOrDefault(key);
                return new FinTsDiscoveredAccount(
                    key,
                    account.IsDepot ? "depot" : "cash",
                    account.ProductName ?? (account.IsDepot ? "ING Direkt-Depot" : "ING Konto"),
                    account.IsDepot ? DepotLast4(account) : Last4(account.Iban),
                    account.Currency,
                    existing?.AccountId,
                    existing?.IsActive ?? !hidden.Contains(key));
            })
            .ToArray();
    }

    /// <summary>
    /// The TAN challenge a stored connection is waiting on, so it can be answered later instead of only
    /// in the dialog that started it. A sync that hits a TAN parks the challenge in the connection
    /// secret and there was no way to read it back — the connection was simply stuck.
    ///
    /// Returns the challenge ONLY. The same secret holds the login and PIN, which never leave here.
    /// </summary>
    public async Task<FinTsConnectionResult?> PendingChallengeAsync(
        Guid connectionId,
        BankingCaller caller,
        CancellationToken ct)
    {
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);
        var connection = await FindAsync(connectionId, ct) ?? throw new BankAccessException(false);
        if (!string.Equals(connection.Provider, "fints", StringComparison.OrdinalIgnoreCase))
            return null;

        var secret = ReadSecret(connection);
        if (secret?.Challenge is null || secret.Session is null) return null;
        return new FinTsConnectionResult(
            connection.Id,
            connection.Status,
            secret.Challenge,
            await DescribeAsync(connection, secret.Parameters.Accounts, ct));
    }

    public async Task<FinTsConnectionResult> ContinueTanAsync(
        Guid connectionId,
        BankingCaller caller,
        string? tan,
        bool poll,
        CancellationToken ct)
    {
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);
        var connection = await FindAsync(connectionId, ct) ?? throw new BankAccessException(false);
        if (!string.Equals(connection.Provider, "fints", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connection is not a FinTS connection.");
        var secret = ReadSecret(connection) ?? throw new InvalidOperationException("FinTS connection secret is unavailable.");
        if (secret.Session is null || secret.Challenge is null)
            throw new InvalidOperationException("No FinTS TAN challenge is pending.");

        var credentials = Credentials(secret);
        FinTsOpenResult result;
        if (poll)
        {
            if (!secret.Challenge.IsDecoupled) throw new InvalidOperationException("This TAN method cannot be polled.");
            result = await LogFinTsAsync("poll", () => finTs.PollDialogTanAsync(KnownBanks.Get(secret.BankId), credentials, secret.Session, secret.Challenge, ct));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(tan)) throw new ArgumentException("TAN is required.");
            result = await LogFinTsAsync("submit-tan", () => finTs.SubmitDialogTanAsync(KnownBanks.Get(secret.BankId), credentials, secret.Session, secret.Challenge, tan.Trim(), ct));
        }

        secret = secret with
        {
            Parameters = result.Session.Parameters,
            Session = result.IsOpen ? null : result.Session,
            Challenge = result.IsOpen ? null : result.Challenge
        };
        // Wartete die Verbindung auf die Auswahl, tut sie es nach der TAN immer noch: die TAN hat den
        // Dialog geoeffnet, nicht die Frage beantwortet, welche Konten der Eigentuemer will. Eine TAN
        // MITTEN im Abruf ist der andere Fall - dort steht die Auswahl schon, und der Abruf laeuft
        // weiter.
        var stillChoosing = IsPendingSelection(connection);
        connection = await backend.UpsertConnectionAsync(ToWrite(connection,
            authorizationId: JsonSerializer.Serialize(secret, Json),
            status: result.IsOpen ? "AUTHORIZED" : "TAN_REQUIRED",
            lastError: stillChoosing ? SelectionPending : null), ct);

        if (result.IsOpen && !stillChoosing) connection = await SyncGatedAsync(connection, ct);
        var after = ReadSecret(connection);
        return new(connection.Id, connection.Status, after?.Challenge,
            await DescribeAsync(connection, after?.Parameters.Accounts ?? [], ct));
    }

    /// <summary>
    /// Ein Abruf, den der Eigentuemer ausgeloest hat - im selben Gatter wie jeder andere.
    ///
    /// Ohne das lief er neben dem Hintergrundlauf her: zwei FinTS-Dialoge mit denselben Zugangsdaten
    /// gleichzeitig, und die Bank bricht den zweiten ab. <see cref="BankSyncConcurrencyGate.EnterAsync"/>
    /// und nicht <c>TryEnterAsync</c>, weil hier jemand vor dem Dialog wartet: abzubrechen, weil im
    /// Hintergrund gerade etwas laeuft, waere eine Sackgasse mitten in der Einrichtung.
    ///
    /// Nicht in <see cref="SyncConnectionAsync"/> selbst: der Hintergrundlauf haelt das Gatter dann
    /// schon, und ein <see cref="SemaphoreSlim"/> ist nicht wiedereintrittsfaehig.
    /// </summary>
    private async Task<BankConnectionDto> SyncGatedAsync(BankConnectionDto connection, CancellationToken ct)
    {
        using var lease = await syncGate.EnterAsync(ct);
        return await SyncConnectionAsync(connection, bypassCadence: true, ct);
    }

    public async Task<BankConnectionDto> SyncConnectionAsync(BankConnectionDto connection, bool bypassCadence, CancellationToken ct)
    {
        if (!string.Equals(connection.Provider, "fints", StringComparison.OrdinalIgnoreCase)) return connection;

        var now = DateTimeOffset.UtcNow;
        if (!bypassCadence && connection.NextSyncAllowedAt is { } next && next > now) return connection;
        var startedAt = now;

        var secret = ReadSecret(connection);
        if (secret is null)
        {
            var failed = await FailAsync(connection, "FINTS_SECRET_MISSING", ct);
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", "FINTS_SECRET_MISSING", CancellationToken.None);
            return failed;
        }

        var nextAllowed = now.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        connection = await backend.UpsertConnectionAsync(ToWrite(connection, lastAttemptAt: now, nextSyncAllowedAt: nextAllowed), ct);

        try
        {
            var bank = KnownBanks.Get(secret.BankId);
            var credentials = Credentials(secret);
            var opened = await finTs.OpenAsync(bank, credentials, secret.Parameters, ct);
            if (!opened.IsOpen)
            {
                secret = secret with { Parameters = opened.Session.Parameters, Session = opened.Session, Challenge = opened.Challenge };
                var pendingTan = await backend.UpsertConnectionAsync(ToWrite(connection,
                    authorizationId: JsonSerializer.Serialize(secret, Json), status: "TAN_REQUIRED", lastError: "FINTS_TAN_REQUIRED",
                    nextSyncAllowedAt: null, clearNextSyncAllowedAt: true), ct);
                await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", "FINTS_TAN_REQUIRED", CancellationToken.None);
                return pendingTan;
            }

            var session = opened.Session;
            var cashAccounts = 0;
            var depots = 0;
            // Die Auswahl des Eigentuemers. Sie wirkt nur bei der ANLAGE: ein vorhandenes Konto
            // aendert nur er selbst, und die Ingestion ruehrt IsActive danach nicht mehr an.
            var hidden = (secret.HiddenAccountKeys ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var source in session.Parameters.Accounts)
            {
                // Ein Depot hat KEINE IBAN - es wird ueber seine Depotnummer angesprochen. Hier stand
                // "ohne IBAN ueberspringen", und damit fiel jedes Depot stillschweigend heraus: im
                // Protokoll stand "0 depots", als haette die Bank keines.
                if (string.IsNullOrWhiteSpace(source.Iban) && string.IsNullOrWhiteSpace(source.AccountNumber)) continue;
                var account = string.IsNullOrWhiteSpace(source.Bic) ? source with { Bic = bank.Bic } : source;
                var visible = !hidden.Contains(AccountHash(account));
                if (account.IsDepot)
                {
                    session = await SyncDepotAsync(connection, bank, credentials, session, account, visible, ct);
                    depots++;
                }
                else
                {
                    session = await SyncCashAccountAsync(connection, bank, credentials, session, account, visible, ct);
                    cashAccounts++;
                }
            }
            await finTs.EndAsync(bank, credentials, session, ct);
            secret = secret with { Parameters = session.Parameters, Session = null, Challenge = null };
            // Der Kontenbauplan gehoert AUCH hierhin, nicht nur ans Verbinden: eine Synchronisation im
            // Hintergrund, die ein Depot verliert, war sonst unsichtbar - die Zahlen darueber zaehlen
            // Versuche, nicht Ergebnisse.
            logger.LogInformation(
                "FinTS sync finished for ING: {CashAccounts} cash accounts, {Depots} depots. Accounts={Accounts}",
                cashAccounts, depots, Shape(session.Parameters.Accounts));
            var completed = await backend.UpsertConnectionAsync(ToWrite(connection,
                authorizationId: JsonSerializer.Serialize(secret, Json), status: "AUTHORIZED",
                lastSyncedAt: DateTimeOffset.UtcNow, nextSyncAllowedAt: nextAllowed, consecutiveFailures: 0, lastError: null), ct);
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "success", null, CancellationToken.None);
            return completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", "CANCELLED", CancellationToken.None);
            throw;
        }
        catch (FinTsInteractiveRequiredException interactive)
        {
            secret = secret with { Parameters = interactive.Session.Parameters, Session = interactive.Session, Challenge = interactive.Challenge };
            var pendingTan = await backend.UpsertConnectionAsync(ToWrite(connection,
                authorizationId: JsonSerializer.Serialize(secret, Json), status: "TAN_REQUIRED",
                clearNextSyncAllowedAt: true, consecutiveFailures: 0, lastError: "FINTS_TAN_REQUIRED"), ct);
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", "FINTS_TAN_REQUIRED", CancellationToken.None);
            return pendingTan;
        }
        catch (FinTsException ex)
        {
            var terminal = ex.Code is "pin_wrong" or "access_locked";
            var errorCode = "FINTS_" + (ex.Code ?? "BANK_ERROR").ToUpperInvariant();
            LogFinTsFailure("sync", ex);
            var failed = await backend.UpsertConnectionAsync(ToWrite(connection,
                status: terminal ? "INVALID" : connection.Status,
                nextSyncAllowedAt: terminal ? null : nextAllowed,
                clearNextSyncAllowedAt: terminal,
                consecutiveFailures: connection.ConsecutiveFailures + 1,
                lastError: errorCode), ct);
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", errorCode, CancellationToken.None);
            return failed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FinTS sync failed for ING.");
            var failed = await FailAsync(connection, "FINTS_SYNC_FAILED", ct);
            await RecordSyncHistorySafeAsync(connection.Id, startedAt, "error", "FINTS_SYNC_FAILED", CancellationToken.None);
            return failed;
        }
    }

    private async Task RecordSyncHistorySafeAsync(
        Guid connectionId,
        DateTimeOffset startedAt,
        string result,
        string? errorCode,
        CancellationToken ct)
    {
        try
        {
            await backend.RecordSyncHistoryAsync(
                connectionId,
                new BankSyncHistoryWrite(startedAt, DateTimeOffset.UtcNow, result, errorCode),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist FinTS sync history for connection {ConnectionId}.", connectionId);
        }
    }

    private async Task<FinTsSessionState> SyncCashAccountAsync(
        BankConnectionDto connection,
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount account,
        bool visible,
        CancellationToken ct)
    {
        var balanceResult = await finTs.GetBalanceAsync(bank, credentials, session, account, ct);
        session = RequireDataOrInteractive(balanceResult);
        var balance = balanceResult.Value;

        var allTransactions = new List<FinTsTransaction>();
        string? touchdown = null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-Math.Clamp(_options.CurrentValue.HistoryDays, 1, 90));
        for (var page = 0; page < Math.Max(1, _options.CurrentValue.MaxPages); page++)
        {
            var result = await finTs.GetTransactionsAsync(bank, credentials, session, account, from, today, touchdown, ct);
            session = RequireDataOrInteractive(result);
            if (result.Value is not null) allTransactions.AddRange(result.Value);
            touchdown = result.Touchdown;
            if (string.IsNullOrWhiteSpace(touchdown)) break;
        }

        var hash = AccountHash(account);
        var providerAccountId = "fints:" + hash;
        var balances = balance is null ? Array.Empty<BalanceBatchItem>() :
            [new BalanceBatchItem(hash, balance.Amount, balance.Currency, "closingBooked", balance.Date, DateTimeOffset.UtcNow)];
        var transactions = allTransactions.Select(tx => new TransactionBatchItem(
            hash,
            "fints:" + tx.ExternalKey,
            tx.ExternalKey,
            tx.Pending ? "PDNG" : "BOOK",
            tx.BookingDate,
            tx.ValueDate,
            tx.Amount,
            tx.Currency,
            tx.Counterparty,
            tx.Description,
            null,
            tx.ExternalKey,
            JsonSerializer.Serialize(new { source = "MT940", raw = tx.RawSource }, Json))).ToArray();
        var product = account.ProductName;
        var type = product?.Contains("Extra", StringComparison.OrdinalIgnoreCase) == true ? "savings" : "checking";
        await backend.IngestAsync(new FinanceIngestBatch(
            new(connection.Id, "fints", "ING", "DE", connection.ProviderSessionId, "AUTHORIZED", null, DateTimeOffset.UtcNow, null),
            [new AccountBatchItem(hash, providerAccountId, "ING", product ?? "ING Konto", product, type,
                account.Currency, Last4(account.Iban), visible, true, [hash], "private", "enabled")],
            balances,
            transactions), ct);
        return session;
    }

    private async Task<FinTsSessionState> SyncDepotAsync(
        BankConnectionDto connection,
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount depot,
        bool visible,
        CancellationToken ct)
    {
        var holdings = new List<FinTsHolding>();
        string? touchdown = null;
        for (var page = 0; page < Math.Max(1, _options.CurrentValue.MaxPages); page++)
        {
            var result = await finTs.GetPortfolioAsync(bank, credentials, session, depot, touchdown, ct);
            session = RequireDataOrInteractive(result);
            if (result.Value is not null) holdings.AddRange(result.Value);
            touchdown = result.Touchdown;
            if (string.IsNullOrWhiteSpace(touchdown)) break;
        }
        var depotKey = AccountHash(depot);
        var depotName = depot.ProductName ?? "ING Direkt-Depot";

        // Das Depot wird ein KONTO, ueber denselben Weg wie das Girokonto. Vorher endete dieser Zweig
        // allein im Depotstand, und damit stand das Depot nirgends bei den Konten - nur unter
        // Vermoegen. Reihenfolge ist tragend: das Konto muss da sein, bevor der Depotstand kommt,
        // sonst findet dessen Verknuepfung nichts.
        //
        // Salden schickt dieser Aufruf keine. Die Bank nennt fuer ein Depot keinen Saldo; sein Wert
        // ist die Bewertung der Positionen und wird dort geschrieben, wo sie entsteht.
        await backend.IngestAsync(new FinanceIngestBatch(
            new(connection.Id, "fints", "ING", "DE", connection.ProviderSessionId, "AUTHORIZED", null, DateTimeOffset.UtcNow, null),
            [new AccountBatchItem(depotKey, "fints:" + depotKey, "ING", depotName, depot.ProductName, "securities",
                depot.Currency, DepotLast4(depot), visible, true, [depotKey], "private", "enabled")],
            [],
            []), ct);

        await backend.IngestFinTsInvestmentSnapshotAsync(new(
            connection.Id,
            depotKey,
            depotName,
            depot.Currency,
            DateOnly.FromDateTime(DateTime.UtcNow),
            holdings.Select(h => new FinTsHoldingSnapshotDto(
                HoldingKey(h), h.Name, h.Isin, h.Wkn,
                h.PriceCurrency ?? h.MarketValueCurrency ?? depot.Currency,
                h.Quantity, h.Price, h.PriceDate, h.MarketValue, h.Exchange)).ToArray()), ct);
        return session;
    }

    private static FinTsSessionState RequireDataOrInteractive<T>(FinTsResult<T> result)
    {
        if (result.Kind is FinTsResultKind.TanRequired or FinTsResultKind.TanPending)
            throw new FinTsInteractiveRequiredException(result.Session, result.Challenge ?? throw new InvalidOperationException("FinTS TAN challenge missing."));
        return result.Session;
    }

    private async Task<T> LogFinTsAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (FinTsException ex)
        {
            LogFinTsFailure(operation, ex);
            throw;
        }
    }

    /// <summary>
    /// Was die Bank in der Synchronisation ueber sich ankuendigt.
    ///
    /// Nach ihr wird die Anmeldenachricht gebaut: die Sicherheitsfunktion landet im Signaturkopf, die
    /// Segmentversion des TAN-Verfahrens im Kopf von HKTAN. Lehnt die Bank danach einen Auftrag mit
    /// "nicht unterstuetzt" ab, ist die erste Frage, ob wir richtig gelesen haben, was sie anbietet -
    /// und ohne diese Zeile ist das nicht zu beantworten.
    ///
    /// Es sind Faehigkeiten der Bank, keine Zugangsdaten: kein Benutzername, keine PIN, keine Konten.
    /// </summary>
    private void LogAnnouncedCapabilities(FinTsBankParameters parameters)
        => logger.LogInformation(
            "ING FinTS announced. SecurityFunction={SecurityFunction}, TanMethods={TanMethods}, SegmentVersions={SegmentVersions}, Accounts={Accounts}",
            parameters.SecurityFunction,
            string.Join("; ", parameters.TanMethods.Select(x =>
                $"{x.SecurityFunction}:v{x.SegmentVersion}:{x.Name}{(x.IsDecoupled ? ":decoupled" : string.Empty)}{(x.NeedsTanMedium ? ":medium" : string.Empty)}")),
            string.Join("; ", parameters.SegmentVersions.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}")),
            Shape(parameters.Accounts));

    /// <summary>
    /// Was die Bank an Konten meldet - in Form, nicht im Inhalt.
    ///
    /// Ein Depot fehlte in der Oberflaeche, und ohne diese Zeile ist nicht zu unterscheiden, ob die
    /// Bank keines meldet, ob es als Girokonto eingeordnet wurde oder ob es unterwegs herausfiel.
    /// Genau diese drei Faelle sehen im Protokoll sonst gleich aus.
    ///
    /// Keine IBAN, keine Kontonummer, kein Name: nur Art, Waehrung und ob eine Kennung da ist.
    /// </summary>
    private static string Shape(IReadOnlyList<FinTsAccount> accounts)
        => accounts.Count == 0
            ? "keine"
            : string.Join("; ", accounts.Select(x =>
                $"{(x.IsDepot ? "depot" : "konto")}:{x.Currency}:{(string.IsNullOrWhiteSpace(x.Iban) ? "ohne-iban" : "mit-iban")}:{(string.IsNullOrWhiteSpace(x.AccountNumber) ? "ohne-nummer" : "mit-nummer")}"));

    private void LogFinTsFailure(string operation, FinTsException ex)
    {
        // AlleCodes steht daneben, nicht statt BankCode: die fuehrende Meldung bleibt an ihrem Platz,
        // und dahinter steht, was die Bank sonst noch geschickt hat (#130 §9). Ohne das stand im
        // Protokoll bei ING nur 9800 "Der Dialog wurde abgebrochen" - die Sammelmeldung, nie der Grund.
        logger.LogWarning(
            "ING FinTS {Operation} failed. ErrorCode={ErrorCode}, BankCode={BankCode}, SegmentReference={SegmentReference}, BankMessage={BankMessage}, AllCodes={AllCodes}, SentShape={SentShape}",
            operation,
            ex.Code ?? "bank_error",
            ex.BankCode ?? "-",
            ex.SegmentReference ?? "-",
            SanitizeBankMessage(ex.BankMessage ?? ex.Message),
            SanitizeBankMessage(ex.BankCodeSummary),
            ex.SentShapeSummary);
    }

    private static string SanitizeBankMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "-";
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length > 500 ? normalized[..500] : normalized;
    }

    private async Task<BankConnectionDto> FailAsync(BankConnectionDto connection, string code, CancellationToken ct)
    {
        var next = DateTimeOffset.UtcNow.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        return await backend.UpsertConnectionAsync(ToWrite(connection, nextSyncAllowedAt: next,
            consecutiveFailures: connection.ConsecutiveFailures + 1, lastError: code), ct);
    }

    /// <summary>
    /// The FinTS product id this installation identifies itself with. The installation settings in
    /// the admin menu publish the canonical FinTs:ProductId value into reloadable configuration.
    /// </summary>
    private string ProductId()
    {
        return ResolveProductId(_options.CurrentValue.ProductId)
            ?? throw new InvalidOperationException(
                "No FinTS product id. Register FullWorth as a FinTS product and enter its product id in the admin menu.");
    }

    internal static string? ResolveProductId(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

    private static FinTsCredentials Credentials(FinTsConnectionSecret secret)
        => new(secret.UserId, secret.Pin, secret.ProductId);

    private static string SessionKey(Guid spaceId, string userId)
        => $"fints:ing:{spaceId:N}:{Hash(userId.Trim())}";

    private static string AccountHash(FinTsAccount account)
        => Hash("ING|" + (!string.IsNullOrWhiteSpace(account.Iban)
            ? account.Iban.Replace(" ", string.Empty).ToUpperInvariant()
            : $"{account.AccountNumber}|{account.SubAccount}"));

    private static string HoldingKey(FinTsHolding holding)
        => "fints:" + Hash(holding.Isin ?? holding.Wkn ?? holding.Name);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Last4(string value)
    {
        var normalized = value.Replace(" ", string.Empty);
        return normalized.Length >= 4 ? normalized[^4..] : null;
    }

    /// <summary>
    /// Die letzten vier Stellen, an denen der Eigentuemer sein DEPOT wiedererkennt.
    ///
    /// Ein Depot hat keine IBAN, es wird ueber seine Depotnummer angesprochen - <see cref="Last4"/>
    /// damit zu fuettern waere eine NullReferenceException mitten im Sync. Ohne die vier Stellen
    /// faellt die Anzeige auf eine technische Kennung zurueck, die in keinem Bankauszug steht.
    /// </summary>
    private static string? DepotLast4(FinTsAccount depot)
        => !string.IsNullOrWhiteSpace(depot.AccountNumber) ? Last4(depot.AccountNumber)
            : !string.IsNullOrWhiteSpace(depot.Iban) ? Last4(depot.Iban)
            : null;

    private static FinTsConnectionSecret? ReadSecret(BankConnectionDto connection)
    {
        if (string.IsNullOrWhiteSpace(connection.AuthorizationId)) return null;
        try { return JsonSerializer.Deserialize<FinTsConnectionSecret>(connection.AuthorizationId, Json); }
        catch (JsonException) { return null; }
    }

    private async Task<BankConnectionDto?> FindAsync(Guid id, CancellationToken ct)
        => (await backend.ListConnectionsAsync(ct)).FirstOrDefault(x => x.Id == id);

    private static BankConnectionWrite ToWrite(
        BankConnectionDto connection,
        string? authorizationId = null,
        string? status = null,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? lastSyncedAt = null,
        DateTimeOffset? nextSyncAllowedAt = null,
        bool clearNextSyncAllowedAt = false,
        int? consecutiveFailures = null,
        string? lastError = null)
        => new(connection.Id, connection.Provider, connection.InstitutionName, connection.Country,
            connection.AuthorizationState, authorizationId ?? connection.AuthorizationId, connection.ProviderSessionId,
            status ?? connection.Status, connection.ValidUntil, lastAttemptAt ?? connection.LastAttemptAt,
            lastSyncedAt ?? connection.LastSyncedAt,
            clearNextSyncAllowedAt ? null : nextSyncAllowedAt ?? connection.NextSyncAllowedAt,
            consecutiveFailures ?? connection.ConsecutiveFailures, lastError,
            AuthorizationUserId: connection.AuthorizationUserId,
            AuthorizationStateExpiresAt: connection.AuthorizationStateExpiresAt,
            EnableBankingProfileId: null, PsuType: "personal", AuthMethod: "fints-pin-tan", RequiredPsuHeadersJson: "[]");

    private sealed class FinTsInteractiveRequiredException(FinTsSessionState session, FinTsTanChallenge challenge) : Exception
    {
        public FinTsSessionState Session { get; } = session;
        public FinTsTanChallenge Challenge { get; } = challenge;
    }
}