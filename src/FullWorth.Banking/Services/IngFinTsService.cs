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

public sealed record FinTsConnectionResult(
    Guid ConnectionId,
    string Status,
    FinTsTanChallenge? Challenge,
    int Accounts,
    int Depots);

internal sealed record FinTsConnectionSecret(
    string BankId,
    string UserId,
    string Pin,
    string ProductId,
    FinTsBankParameters Parameters,
    FinTsSessionState? Session = null,
    FinTsTanChallenge? Challenge = null);

public sealed class IngFinTsService(
    FinTsClient finTs,
    FullWorthBackendClient backend,
    IOptions<FinTsOptions> options,
    IOptions<BankingSyncOptions> syncOptions,
    ILogger<IngFinTsService> logger)
{
    private readonly FinTsOptions _options = options.Value;
    private readonly BankingSyncOptions _sync = syncOptions.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
        var parameters = await finTs.SynchronizeAsync(bank, credentials, ct);
        if (!string.IsNullOrWhiteSpace(request.TanMedium)) parameters = parameters with { TanMedium = request.TanMedium.Trim() };
        var opened = await finTs.OpenAsync(bank, credentials, parameters, ct);
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
            null,
            request.ReconnectConnectionId.HasValue ? null : caller.FullWorthSpaceId,
            caller.UserId,
            null,
            null,
            "personal",
            "fints-pin-tan",
            "[]"), ct);

        if (!opened.IsOpen)
            return new(connection.Id, status, opened.Challenge, opened.Session.Parameters.Accounts.Count(x => !x.IsDepot), opened.Session.Parameters.Accounts.Count(x => x.IsDepot));

        connection = await SyncConnectionAsync(connection, bypassCadence: true, ct);
        var saved = ReadSecret(connection);
        return new(connection.Id, connection.Status, saved?.Challenge,
            saved?.Parameters.Accounts.Count(x => !x.IsDepot) ?? 0,
            saved?.Parameters.Accounts.Count(x => x.IsDepot) ?? 0);
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
            result = await finTs.PollDialogTanAsync(KnownBanks.Get(secret.BankId), credentials, secret.Session, secret.Challenge, ct);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(tan)) throw new ArgumentException("TAN is required.");
            result = await finTs.SubmitDialogTanAsync(KnownBanks.Get(secret.BankId), credentials, secret.Session, secret.Challenge, tan.Trim(), ct);
        }

        secret = secret with
        {
            Parameters = result.Session.Parameters,
            Session = result.IsOpen ? null : result.Session,
            Challenge = result.IsOpen ? null : result.Challenge
        };
        connection = await backend.UpsertConnectionAsync(ToWrite(connection,
            authorizationId: JsonSerializer.Serialize(secret, Json),
            status: result.IsOpen ? "AUTHORIZED" : "TAN_REQUIRED",
            lastError: null), ct);

        if (result.IsOpen) connection = await SyncConnectionAsync(connection, bypassCadence: true, ct);
        var after = ReadSecret(connection);
        return new(connection.Id, connection.Status, after?.Challenge,
            after?.Parameters.Accounts.Count(x => !x.IsDepot) ?? 0,
            after?.Parameters.Accounts.Count(x => x.IsDepot) ?? 0);
    }

    public async Task<BankConnectionDto> SyncConnectionAsync(BankConnectionDto connection, bool bypassCadence, CancellationToken ct)
    {
        if (!string.Equals(connection.Provider, "fints", StringComparison.OrdinalIgnoreCase)) return connection;
        var secret = ReadSecret(connection);
        if (secret is null) return await FailAsync(connection, "FINTS_SECRET_MISSING", ct);

        var now = DateTimeOffset.UtcNow;
        if (!bypassCadence && connection.NextSyncAllowedAt is { } next && next > now) return connection;
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
                return await backend.UpsertConnectionAsync(ToWrite(connection,
                    authorizationId: JsonSerializer.Serialize(secret, Json), status: "TAN_REQUIRED", lastError: "FINTS_TAN_REQUIRED",
                    nextSyncAllowedAt: null, clearNextSyncAllowedAt: true), ct);
            }

            var session = opened.Session;
            var cashAccounts = 0;
            var depots = 0;
            foreach (var source in session.Parameters.Accounts)
            {
                if (string.IsNullOrWhiteSpace(source.Iban)) continue;
                var account = string.IsNullOrWhiteSpace(source.Bic) ? source with { Bic = bank.Bic } : source;
                if (account.IsDepot)
                {
                    session = await SyncDepotAsync(connection, bank, credentials, session, account, ct);
                    depots++;
                }
                else
                {
                    session = await SyncCashAccountAsync(connection, bank, credentials, session, account, ct);
                    cashAccounts++;
                }
            }
            await finTs.EndAsync(bank, credentials, session, ct);
            secret = secret with { Parameters = session.Parameters, Session = null, Challenge = null };
            logger.LogInformation("FinTS sync finished for ING: {CashAccounts} cash accounts, {Depots} depots.", cashAccounts, depots);
            return await backend.UpsertConnectionAsync(ToWrite(connection,
                authorizationId: JsonSerializer.Serialize(secret, Json), status: "AUTHORIZED",
                lastSyncedAt: DateTimeOffset.UtcNow, nextSyncAllowedAt: nextAllowed, consecutiveFailures: 0, lastError: null), ct);
        }
        catch (FinTsInteractiveRequiredException interactive)
        {
            secret = secret with { Parameters = interactive.Session.Parameters, Session = interactive.Session, Challenge = interactive.Challenge };
            return await backend.UpsertConnectionAsync(ToWrite(connection,
                authorizationId: JsonSerializer.Serialize(secret, Json), status: "TAN_REQUIRED",
                clearNextSyncAllowedAt: true, consecutiveFailures: 0, lastError: "FINTS_TAN_REQUIRED"), ct);
        }
        catch (FinTsException ex)
        {
            var terminal = ex.Code is "pin_wrong" or "access_locked";
            logger.LogWarning("FinTS sync failed for ING ({Code}).", ex.Code ?? "bank_error");
            return await backend.UpsertConnectionAsync(ToWrite(connection,
                status: terminal ? "INVALID" : connection.Status,
                nextSyncAllowedAt: terminal ? null : nextAllowed,
                clearNextSyncAllowedAt: terminal,
                consecutiveFailures: connection.ConsecutiveFailures + 1,
                lastError: "FINTS_" + (ex.Code ?? "BANK_ERROR").ToUpperInvariant()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FinTS sync failed for ING.");
            return await FailAsync(connection, "FINTS_SYNC_FAILED", ct);
        }
    }

    private async Task<FinTsSessionState> SyncCashAccountAsync(
        BankConnectionDto connection,
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount account,
        CancellationToken ct)
    {
        var balanceResult = await finTs.GetBalanceAsync(bank, credentials, session, account, ct);
        session = RequireDataOrInteractive(balanceResult);
        var balance = balanceResult.Value;

        var allTransactions = new List<FinTsTransaction>();
        string? touchdown = null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-Math.Clamp(_options.HistoryDays, 1, 90));
        for (var page = 0; page < Math.Max(1, _options.MaxPages); page++)
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
                account.Currency, Last4(account.Iban), true, true, [hash], "private", "enabled")],
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
        CancellationToken ct)
    {
        var holdings = new List<FinTsHolding>();
        string? touchdown = null;
        for (var page = 0; page < Math.Max(1, _options.MaxPages); page++)
        {
            var result = await finTs.GetPortfolioAsync(bank, credentials, session, depot, depot.Currency, touchdown, ct);
            session = RequireDataOrInteractive(result);
            if (result.Value is not null) holdings.AddRange(result.Value);
            touchdown = result.Touchdown;
            if (string.IsNullOrWhiteSpace(touchdown)) break;
        }
        var depotKey = AccountHash(depot);
        await backend.IngestFinTsInvestmentSnapshotAsync(new(
            connection.Id,
            depotKey,
            depot.ProductName ?? "ING Direkt-Depot",
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

    private async Task<BankConnectionDto> FailAsync(BankConnectionDto connection, string code, CancellationToken ct)
    {
        var next = DateTimeOffset.UtcNow.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        return await backend.UpsertConnectionAsync(ToWrite(connection, nextSyncAllowedAt: next,
            consecutiveFailures: connection.ConsecutiveFailures + 1, lastError: code), ct);
    }

    private string ProductId()
    {
        if (string.IsNullOrWhiteSpace(_options.ProductId))
            throw new InvalidOperationException("FinTs:ProductId is not configured. Register FullWorth as a FinTS product and configure its product id.");
        return _options.ProductId.Trim();
    }

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
