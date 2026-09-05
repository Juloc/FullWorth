using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class BankingSyncOptions
{
    public const string SectionName = "Sync";
    public int IntervalMinutes { get; set; } = 360;
    public int MinimumBackgroundSyncIntervalMinutes { get; set; } = 360;
    public int RateLimitCooldownMinutes { get; set; } = 360;
    public int OverlapDays { get; set; } = 7;
    public int PersistBatchSize { get; set; } = 250;
    public int MaxPagesPerAccount { get; set; } = 250;
}

// RedirectUrl from the browser is intentionally NOT part of this contract any more — the server
// derives it from EnableBanking:RedirectUrl (P0.2). A legacy field is tolerated on the wire but ignored.
// ReconnectConnectionId (§17): when set, this re-authorizes an EXISTING connection (expired/error/
// reauthorization-required) in place instead of creating a duplicate row for the same institution.
public sealed record ConnectBankRequest(string InstitutionName, string? Country, int? ValidDays, string? AuthMethod, string? PsuId, Dictionary<string, string>? Credentials, Guid? ReconnectConnectionId = null);
public sealed record BankSyncResult(int Synced, int Skipped, int Failed, bool AlreadyRunning);

public enum ManualSyncStatus { Started, Cooldown, AlreadyRunning, ReauthorizationRequired, NotFound }
public sealed record ManualSyncResult(ManualSyncStatus Status, DateTimeOffset? NextSyncAllowedAt = null);

/// <summary>Trusted caller identity for banking write operations, set by FullWorth.Web from the session.</summary>
public sealed record BankingCaller(Guid UserId, Guid FullWorthSpaceId);

/// <summary>Thrown when the caller may not create/drive a connection. Forbidden=owner-required, else not-found.</summary>
public sealed class BankAccessException(bool forbidden) : Exception
{
    public bool Forbidden { get; } = forbidden;
}

public sealed class BankSyncService(
    EnableBankingClient provider,
    FullWorthBackendClient backend,
    BankSyncConcurrencyGate syncGate,
    IOptions<EnableBankingOptions> providerOptions,
    IOptions<BankingSyncOptions> syncOptions,
    ILogger<BankSyncService> logger)
{
    private readonly EnableBankingOptions _providerOptions = providerOptions.Value;
    private readonly BankingSyncOptions _sync = syncOptions.Value;

    public Task<JsonElement> GetInstitutionsAsync(string? country, CancellationToken ct) =>
        provider.GetInstitutionsAsync((country ?? _providerOptions.DefaultCountry).ToUpperInvariant(), ct);

    public async Task<string> StartConnectionAsync(ConnectBankRequest request, BankingCaller caller, CancellationToken ct)
    {
        // The backend is the authority: only an OWNER of the (server-trusted) space may connect. When
        // reconnecting, this also confirms the target connection actually belongs to the caller's space.
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, request.ReconnectConnectionId, ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);

        var redirectUrl = _providerOptions.RedirectUrl;
        if (string.IsNullOrWhiteSpace(redirectUrl))
            throw new InvalidOperationException("EnableBanking:RedirectUrl is not configured.");

        var country = (request.Country ?? _providerOptions.DefaultCountry).ToUpperInvariant();
        var list = await provider.GetInstitutionsAsync(country, ct);
        var institution = list.ValueKind == JsonValueKind.Object && list.TryGetProperty("aspsps", out var aspsps) && aspsps.ValueKind == JsonValueKind.Array
            ? aspsps.EnumerateArray().FirstOrDefault(x => string.Equals(GetString(x, "name"), request.InstitutionName, StringComparison.OrdinalIgnoreCase))
            : default;
        if (institution.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Institution '{request.InstitutionName}' was not returned for {country}.");

        var maxSeconds = institution.TryGetProperty("maximum_consent_validity", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt64(out var seconds)
            ? seconds
            : (long)TimeSpan.FromDays(90).TotalSeconds;
        var requested = TimeSpan.FromDays(Math.Clamp(request.ValidDays ?? 180, 1, 365));
        var validity = requested < TimeSpan.FromSeconds(maxSeconds) ? requested : TimeSpan.FromSeconds(maxSeconds);
        var validUntil = DateTimeOffset.UtcNow.Add(validity);
        // Cryptographically random, single-use state with a short TTL, bound to user + space below.
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var stateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_providerOptions.AuthorizationStateTtlMinutes, 1, 60));
        var result = await provider.StartAuthorizationAsync(
            GetString(institution, "name") ?? request.InstitutionName,
            country,
            redirectUrl,
            state,
            validUntil,
            request.AuthMethod,
            request.PsuId,
            request.Credentials,
            ct);

        await backend.UpsertConnectionAsync(new(
            Id: request.ReconnectConnectionId,
            Provider: "enable-banking",
            InstitutionName: GetString(institution, "name") ?? request.InstitutionName,
            Country: country,
            AuthorizationState: state,
            AuthorizationId: result.AuthorizationId,
            ProviderSessionId: null,
            Status: "PENDING_AUTHORIZATION",
            ValidUntil: validUntil,
            LastAttemptAt: null,
            LastSyncedAt: null,
            NextSyncAllowedAt: null,
            ConsecutiveFailures: 0,
            LastError: null,
            FullWorthSpaceId: caller.FullWorthSpaceId,
            AuthorizationUserId: caller.UserId,
            AuthorizationStateExpiresAt: stateExpiresAt), ct);

        return result.Url;
    }

    public async Task<BankConnectionDto> CompleteConnectionAsync(string state, string code, CancellationToken ct)
    {
        // One-time atomic consume: replay or an expired state yields null → invalid callback.
        var connection = await backend.ConsumeStateAsync(state, ct)
            ?? throw new InvalidOperationException("Unknown or expired authorization state.");

        var session = await provider.AuthorizeSessionAsync(code, ct);
        var sessionId = GetString(session, "session_id")
            ?? throw new InvalidOperationException("Enable Banking did not return session_id.");
        var validUntil = connection.ValidUntil;
        if (session.ValueKind == JsonValueKind.Object &&
            session.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.Object &&
            access.TryGetProperty("valid_until", out var valid) && valid.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(valid.GetString(), out var parsed))
            validUntil = parsed;

        connection = await backend.UpsertConnectionAsync(ToWrite(
            connection,
            providerSessionId: sessionId,
            status: "AUTHORIZED",
            validUntil: validUntil,
            lastError: null,
            consecutiveFailures: 0), ct);

        // The connection is authorized and persisted at this point — that is the user-visible
        // success. The initial sync is best-effort: if the provider hiccups here, the connection
        // stays AUTHORIZED (with its error recorded) and the background worker retries later.
        using var lease = await syncGate.EnterAsync(ct);
        try
        {
            return await SyncConnectionCoreAsync(connection, bypassCadence: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Initial sync after connecting {Institution} failed; the connection is authorized and will be retried in the background.",
                connection.InstitutionName);
            return connection;
        }
    }

    public async Task<BankSyncResult> SyncAllAsync(CancellationToken ct)
    {
        using var lease = await syncGate.TryEnterAsync(ct);
        if (lease is null)
        {
            logger.LogInformation("Bank synchronization skipped because another synchronization is already running.");
            return new(0, 0, 0, true);
        }

        var connections = await backend.ListConnectionsAsync(ct);
        var synced = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var connection in connections.Where(x => x.Status == "AUTHORIZED" && x.ProviderSessionId is not null && (!x.ValidUntil.HasValue || x.ValidUntil > DateTimeOffset.UtcNow)))
        {
            if (!CanBackgroundSync(connection, DateTimeOffset.UtcNow))
            {
                skipped++;
                continue;
            }

            try
            {
                await SyncConnectionCoreAsync(connection, bypassCadence: false, ct);
                synced++;
            }
            catch (EnableBankingApiException)
            {
                failed++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Sync failed for {Institution}", connection.InstitutionName);
            }
        }

        return new(synced, skipped, failed, false);
    }

    /// <summary>
    /// User-initiated sync of a single connection through the same concurrency gate as background sync,
    /// reporting an explicit status instead of silently doing nothing. When <paramref name="force"/> is
    /// set (the "sync now" button) it bypasses our own background-cadence cooldown so the user gets
    /// current data on demand. Force still cannot bypass a provider rate-limit (that surfaces as a
    /// Cooldown carrying the provider's retry time) or an in-flight run (AlreadyRunning), and never
    /// skips the reauthorization requirement for an expired consent.
    /// </summary>
    public async Task<ManualSyncResult> RequestManualSyncAsync(Guid connectionId, BankingCaller caller, bool force, CancellationToken ct)
    {
        // Authorize FIRST against the caller's space so a foreign connection is indistinguishable
        // from a non-existent one (NotFound) — no existence oracle, no cross-tenant sync.
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, ct);
        if (authorized != BankAuthorizeResult.Authorized) return new(ManualSyncStatus.NotFound);

        var connection = await FindConnectionAsync(connectionId, ct);
        if (connection is null) return new(ManualSyncStatus.NotFound);

        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(connection.Status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(connection.ProviderSessionId) ||
            (connection.ValidUntil.HasValue && connection.ValidUntil.Value <= now))
            return new(ManualSyncStatus.ReauthorizationRequired);

        if (!force && connection.NextSyncAllowedAt.HasValue && connection.NextSyncAllowedAt.Value > now)
            return new(ManualSyncStatus.Cooldown, connection.NextSyncAllowedAt);

        using var lease = await syncGate.TryEnterAsync(ct);
        if (lease is null) return new(ManualSyncStatus.AlreadyRunning);

        // Re-read inside the lease: a run that just finished may have set a fresh cooldown.
        var current = await FindConnectionAsync(connectionId, ct) ?? connection;
        if (!force && current.NextSyncAllowedAt.HasValue && current.NextSyncAllowedAt.Value > DateTimeOffset.UtcNow)
            return new(ManualSyncStatus.Cooldown, current.NextSyncAllowedAt);

        try
        {
            await SyncConnectionCoreAsync(current, bypassCadence: force, ct);
        }
        catch (EnableBankingApiException)
        {
            // The failure handler already persisted a safe cooldown; report it to the caller.
            var afterFailure = await FindConnectionAsync(connectionId, ct);
            return new(ManualSyncStatus.Cooldown, afterFailure?.NextSyncAllowedAt);
        }

        var afterSync = await FindConnectionAsync(connectionId, ct);
        return new(ManualSyncStatus.Started, afterSync?.NextSyncAllowedAt);
    }

    private async Task<BankConnectionDto?> FindConnectionAsync(Guid connectionId, CancellationToken ct) =>
        (await backend.ListConnectionsAsync(ct)).FirstOrDefault(x => x.Id == connectionId);

    private bool CanBackgroundSync(BankConnectionDto connection, DateTimeOffset now)
    {
        if (connection.NextSyncAllowedAt.HasValue && connection.NextSyncAllowedAt.Value > now)
            return false;

        var minimum = TimeSpan.FromMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        return !connection.LastAttemptAt.HasValue || connection.LastAttemptAt.Value + minimum <= now;
    }

    // bypassCadence skips ONLY our own background-cadence gate (the initial sync after connect and the
    // user's "sync now" both set it). It does NOT force a full history re-import — the per-account
    // initial-vs-incremental decision still comes from the stored sync state — and it never bypasses a
    // provider rate-limit, which is handled in the failure path below.
    private async Task<BankConnectionDto> SyncConnectionCoreAsync(BankConnectionDto connection, bool bypassCadence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.ProviderSessionId))
            return connection;

        var now = DateTimeOffset.UtcNow;
        if (!bypassCadence && !CanBackgroundSync(connection, now))
            return connection;

        var nextAllowed = now.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        connection = await backend.UpsertConnectionAsync(ToWrite(
            connection,
            lastAttemptAt: now,
            nextSyncAllowedAt: nextAllowed), ct);

        try
        {
            var session = await provider.GetSessionAsync(connection.ProviderSessionId, ct);
            var status = GetString(session, "status") ?? connection.Status;
            if (!string.Equals(status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase))
            {
                return await backend.UpsertConnectionAsync(ToWrite(
                    connection,
                    status: status,
                    nextSyncAllowedAt: nextAllowed,
                    lastError: null), ct);
            }

            var accounts = ParseSessionAccounts(connection, session);

            var anySkipped = false;
            foreach (var account in accounts)
                anySkipped |= !await SyncAccountAsync(connection, account, ct);

            // A run that had to skip accounts (unresolvable identification hash) must not look
            // healthy — keep the failure counter and record a stable error code so the UI's health
            // badge surfaces it, while lastSyncedAt/nextSyncAllowedAt keep their normal cadence.
            return await backend.UpsertConnectionAsync(ToWrite(
                connection,
                status: status,
                lastSyncedAt: DateTimeOffset.UtcNow,
                nextSyncAllowedAt: nextAllowed,
                consecutiveFailures: anySkipped ? connection.ConsecutiveFailures : 0,
                lastError: anySkipped ? "ACCOUNT_RESOLUTION_FAILED" : null), ct);
        }
        catch (EnableBankingApiException ex)
        {
            await HandleProviderFailureAsync(connection, ex, CancellationToken.None);
            throw;
        }
        catch
        {
            await MarkFailureAsync(connection, "SYNC_FAILED", CancellationToken.None);
            throw;
        }
    }

    private async Task HandleProviderFailureAsync(BankConnectionDto connection, EnableBankingApiException ex, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var minimumCooldown = now.AddMinutes(Math.Max(360, _sync.RateLimitCooldownMinutes));
        var retryAt = ex.RetryAt.HasValue && ex.RetryAt.Value > minimumCooldown ? ex.RetryAt.Value : minimumCooldown;
        var classification = EnableBankingErrorClassifier.Classify(ex);

        logger.LogWarning(
            "Bank sync for {Institution} stopped ({Category}: {Message}); next background attempt not before {RetryAt}.",
            connection.InstitutionName,
            classification.Code,
            classification.SafeMessage,
            retryAt);

        // Persist the stable, safe category (never the raw provider body) as the last error.
        await backend.UpsertConnectionAsync(ToWrite(
            connection,
            nextSyncAllowedAt: retryAt,
            consecutiveFailures: connection.ConsecutiveFailures + 1,
            lastError: classification.Code), ct);
    }

    private async Task MarkFailureAsync(BankConnectionDto connection, string error, CancellationToken ct)
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        await backend.UpsertConnectionAsync(ToWrite(
            connection,
            nextSyncAllowedAt: retryAt,
            consecutiveFailures: connection.ConsecutiveFailures + 1,
            lastError: error), ct);
    }

    /// <summary>Returns false when the account had to be SKIPPED (unresolvable identification hash).</summary>
    private async Task<bool> SyncAccountAsync(BankConnectionDto connection, AccountState account, CancellationToken ct)
    {
        var detailsFetched = false;
        if (account.NeedsHashResolution)
        {
            // The uid is only stable within the current session; persisting it as the account key
            // would mint a duplicate account (with a full history re-ingest) on every new session.
            // Resolve the real identification_hash first, or skip the account for this run.
            var resolved = await TryGetAccountDetailsAsync(connection, account.ProviderAccountId, ct);
            var realHash = resolved is { } json ? GetString(json, "identification_hash") : null;
            if (string.IsNullOrWhiteSpace(realHash))
            {
                logger.LogWarning(
                    "Skipping account {Uid} of {Institution}: identification_hash could not be resolved from the account details.",
                    account.ProviderAccountId,
                    connection.InstitutionName);
                return false;
            }
            account = ApplyDetails(account, resolved!.Value) with { IdentificationHash = realHash, NeedsHashResolution = false, HasDetails = true };
            detailsFetched = true;
        }

        var syncState = await backend.GetAccountSyncStateAsync(connection.Id, account.IdentificationHash, ct);
        var initialSync = syncState?.LatestBookingDate is null;

        if (!detailsFetched && (initialSync || !account.HasDetails))
        {
            // Enrichment only: some ASPSPs (e.g. the sandbox mock) do not serve the account-details
            // resource at all — balances and transactions must still sync with the session data.
            var details = await TryGetAccountDetailsAsync(connection, account.ProviderAccountId, ct);
            if (details is { } json)
                account = ApplyDetails(account, json) with { HasDetails = true };
        }

        var balancesJson = await provider.GetBalancesAsync(account.ProviderAccountId, ct);
        var balances = ParseBalances(account, balancesJson);
        var now = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly? from = syncState?.LatestBookingDate is { } latest
            ? latest.AddDays(-Math.Max(0, _sync.OverlapDays))
            : null;
        if (from.HasValue && from.Value > now)
            from = now.AddDays(-Math.Max(0, _sync.OverlapDays));
        DateOnly? to = initialSync ? null : now;
        string? continuation = null;
        var firstPersist = true;

        for (var page = 0; page < Math.Max(1, _sync.MaxPagesPerAccount); page++)
        {
            var response = await provider.GetTransactionsAsync(account.ProviderAccountId, from, to, initialSync, continuation, ct);
            var parsed = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array
                ? txs.EnumerateArray().Select(x => ParseTransaction(account, x)).Where(x => x is not null).Cast<TransactionBatchItem>().ToList()
                : [];

            foreach (var chunk in parsed.Chunk(Math.Max(25, _sync.PersistBatchSize)))
            {
                await backend.IngestAsync(new(
                    new(connection.Id, connection.Provider, connection.InstitutionName, connection.Country, connection.ProviderSessionId, "AUTHORIZED", connection.ValidUntil, DateTimeOffset.UtcNow, null),
                    [new(account.IdentificationHash, account.ProviderAccountId, connection.InstitutionName, account.DisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4, true, account.HasDetails)],
                    firstPersist ? balances : [],
                    chunk), ct);
                firstPersist = false;
            }

            if (parsed.Count == 0 && firstPersist)
            {
                await backend.IngestAsync(new(
                    new(connection.Id, connection.Provider, connection.InstitutionName, connection.Country, connection.ProviderSessionId, "AUTHORIZED", connection.ValidUntil, DateTimeOffset.UtcNow, null),
                    [new(account.IdentificationHash, account.ProviderAccountId, connection.InstitutionName, account.DisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4, true, account.HasDetails)],
                    balances,
                    []), ct);
                firstPersist = false;
            }

            continuation = GetString(response, "continuation_key");
            if (string.IsNullOrWhiteSpace(continuation))
                break;
        }

        return true;
    }

    private static BankConnectionWrite ToWrite(
        BankConnectionDto connection,
        string? providerSessionId = null,
        string? status = null,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? lastSyncedAt = null,
        DateTimeOffset? nextSyncAllowedAt = null,
        int? consecutiveFailures = null,
        string? lastError = null)
        => new(
            connection.Id,
            connection.Provider,
            connection.InstitutionName,
            connection.Country,
            connection.AuthorizationState,
            connection.AuthorizationId,
            providerSessionId ?? connection.ProviderSessionId,
            status ?? connection.Status,
            validUntil ?? connection.ValidUntil,
            lastAttemptAt ?? connection.LastAttemptAt,
            lastSyncedAt ?? connection.LastSyncedAt,
            nextSyncAllowedAt ?? connection.NextSyncAllowedAt,
            consecutiveFailures ?? connection.ConsecutiveFailures,
            lastError);

    /// <summary>
    /// Extracts the session's accounts across Enable Banking API shapes: current API versions expose
    /// account OBJECTS under "accounts_data" while "accounts" carries only uid STRINGS; older
    /// responses put the objects directly in "accounts". Treating a uid string as an object crashed
    /// the callback ("requires an element of type 'Object'"), so every shape is handled explicitly.
    /// </summary>
    private static List<AccountState> ParseSessionAccounts(BankConnectionDto connection, JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return [];

        var accounts = new List<AccountState>();
        if (session.TryGetProperty("accounts_data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            accounts.AddRange(data.EnumerateArray()
                .Select(item => ParseAccount(connection, item))
                .Where(item => item is not null)
                .Cast<AccountState>());
        }

        // Merge anything from "accounts" not already covered: legacy responses put the account
        // OBJECTS here, current ones only the uid strings — and a partially-parseable accounts_data
        // must not silently drop accounts whose uid is still listed.
        if (session.TryGetProperty("accounts", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var known = accounts.Select(account => account.ProviderAccountId).ToHashSet(StringComparer.Ordinal);
            accounts.AddRange(array.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.Object => ParseAccount(connection, item),
                    JsonValueKind.String => ParseAccountFromUid(connection, item.GetString()),
                    _ => null
                })
                .Where(item => item is not null && known.Add(item!.ProviderAccountId))
                .Cast<AccountState>());
        }

        return accounts;
    }

    private static AccountState? ParseAccount(BankConnectionDto connection, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        var uid = GetString(json, "uid");
        var hash = GetString(json, "identification_hash");
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(hash)) return null;
        var name = GetString(json, "details") ?? GetString(json, "name");
        // accounts_data entries may carry ONLY uid + identification_hash; a missing name marks the
        // account as details-less so the metadata is fetched instead of ingesting placeholders.
        return new(hash, uid, name ?? connection.InstitutionName, GetString(json, "product"), GetString(json, "cash_account_type"), GetString(json, "currency") ?? "EUR", GetIbanLast4(json), HasDetails: name is not null);
    }

    private static AccountState? ParseAccountFromUid(BankConnectionDto connection, string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        // Only the session-scoped uid is known: the placeholder key below is NEVER persisted —
        // SyncAccountAsync resolves the real identification_hash (or skips the account) first.
        return new($"uid:{uid}", uid, connection.InstitutionName, null, null, "EUR", null, HasDetails: false, NeedsHashResolution: true);
    }

    /// <summary>
    /// Fetches GET /accounts/{uid}. A 404 is tolerated (null): the resource is optional per ASPSP —
    /// the sandbox "Mock ASPSP" does not implement it — and must never fail the sync or the connect
    /// callback. Every other provider error still propagates into the normal failure handling.
    /// </summary>
    private async Task<JsonElement?> TryGetAccountDetailsAsync(BankConnectionDto connection, string providerAccountId, CancellationToken ct)
    {
        try
        {
            return await provider.GetAccountAsync(providerAccountId, ct);
        }
        catch (EnableBankingApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                "Account details for {Uid} of {Institution} are unavailable (404); continuing with session data.",
                providerAccountId,
                connection.InstitutionName);
            return null;
        }
    }

    private static AccountState ApplyDetails(AccountState account, JsonElement details) => account with
    {
        DisplayName = GetString(details, "details") ?? GetString(details, "name") ?? account.DisplayName,
        Product = GetString(details, "product") ?? account.Product,
        AccountType = GetString(details, "cash_account_type") ?? account.AccountType,
        Currency = GetString(details, "currency") ?? account.Currency,
        IbanLast4 = GetIbanLast4(details) ?? account.IbanLast4
    };

    private static List<BalanceBatchItem> ParseBalances(AccountState account, JsonElement json)
    {
        var result = new List<BalanceBatchItem>();
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("balances", out var array) || array.ValueKind != JsonValueKind.Array) return result;
        var captured = DateTimeOffset.UtcNow;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("balance_amount", out var amount)) continue;
            result.Add(new(account.IdentificationHash, GetDecimal(amount, "amount"), GetString(amount, "currency") ?? account.Currency, GetString(item, "balance_type") ?? "", ParseDate(item, "reference_date"), captured));
        }
        return result;
    }

    private static TransactionBatchItem? ParseTransaction(AccountState account, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("transaction_amount", out var amountJson)) return null;
        var amount = GetDecimal(amountJson, "amount");
        var indicator = GetString(json, "credit_debit_indicator");
        if (string.Equals(indicator, "DBIT", StringComparison.OrdinalIgnoreCase)) amount = -Math.Abs(amount);
        else if (string.Equals(indicator, "CRDT", StringComparison.OrdinalIgnoreCase)) amount = Math.Abs(amount);
        var booking = ParseDate(json, "booking_date");
        var value = ParseDate(json, "value_date");
        var counterparty = GetCounterparty(json);
        var description = GetDescription(json);
        var transactionId = GetString(json, "transaction_id");
        var entryReference = GetString(json, "entry_reference");
        var currency = GetString(amountJson, "currency") ?? account.Currency;
        var key = transactionId ?? entryReference ?? Fingerprint(account.IdentificationHash, booking, value, amount, currency, counterparty, description);
        return new(account.IdentificationHash, key, transactionId, GetString(json, "status") ?? "BOOK", booking, value, amount, currency, counterparty, description, GetString(json, "merchant_category_code"), entryReference, json.GetRawText());
    }

    private static string? GetCounterparty(JsonElement json)
    {
        var debit = string.Equals(GetString(json, "credit_debit_indicator"), "DBIT", StringComparison.OrdinalIgnoreCase);
        return GetNestedString(json, debit ? "creditor" : "debtor", "name") ?? GetNestedString(json, debit ? "debtor" : "creditor", "name");
    }

    private static string? GetDescription(JsonElement json)
    {
        if (json.TryGetProperty("remittance_information", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join(" | ", lines.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return GetString(json, "note") ?? GetNestedString(json, "bank_transaction_code", "description");
    }

    private static string? GetIbanLast4(JsonElement json)
    {
        var iban = GetNestedString(json, "account_id", "iban")?.Replace(" ", string.Empty, StringComparison.Ordinal);
        return iban is { Length: >= 4 } ? iban[^4..] : null;
    }

    private static string Fingerprint(string accountHash, DateOnly? booking, DateOnly? valueDate, decimal amount, string currency, string? counterparty, string? description)
    {
        var source = $"{accountHash}|{booking:yyyy-MM-dd}|{valueDate:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{currency}|{counterparty}|{description}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    // All helpers tolerate a non-object element: TryGetProperty THROWS on strings/arrays/numbers,
    // and provider JSON shapes have changed between API versions before.
    private static string? GetString(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? GetNestedString(JsonElement e, string child, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(child, out var c) ? GetString(c, name) : null;
    private static DateOnly? ParseDate(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString(), out var date) ? date : null;
    private static decimal GetDecimal(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    }

    // HasDetails: the session payload carried real account metadata (name/currency/...). When false,
    // the details are fetched from GET /accounts/{uid} before ingesting, on EVERY sync — otherwise a
    // sparse session shape (accounts_data has only uid + identification_hash) would overwrite the
    // stored account name/currency with placeholders on each background sync.
    // NeedsHashResolution: only the session-scoped uid is known; the cross-session stable
    // identification_hash must be resolved from the details fetch before any persistence.
    private sealed record AccountState(string IdentificationHash, string ProviderAccountId, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool HasDetails = true, bool NeedsHashResolution = false);
}
