using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class BankingSyncOptions
{
    public const string SectionName = "Sync";
    public int IntervalMinutes { get; set; } = 15;
    public int MinimumBackgroundSyncIntervalMinutes { get; set; } = 360;
    public int RateLimitCooldownMinutes { get; set; } = 360;
    public int OverlapDays { get; set; } = 7;
    public int PersistBatchSize { get; set; } = 250;
    public int MaxPagesPerAccount { get; set; } = 250;
}

public sealed record ConnectBankRequest(
    string InstitutionName,
    string? Country,
    int? ValidDays,
    string? AuthMethod,
    string? PsuId,
    Dictionary<string, string>? Credentials,
    Guid? ReconnectConnectionId = null,
    Guid? EnableBankingProfileId = null,
    string? PsuType = null,
    string? Language = null,
    bool? CredentialsAutosubmit = null);

public sealed record BankSyncResult(int Synced, int Skipped, int Failed, bool AlreadyRunning);

public enum ManualSyncStatus { Started, Cooldown, AlreadyRunning, ReauthorizationRequired, NotFound }
public sealed record ManualSyncResult(ManualSyncStatus Status, DateTimeOffset? NextSyncAllowedAt = null);

public enum DisconnectStatus { Deleted, NotFound, ProviderFailed }

public sealed record ProviderTransactionDetailsView(
    string? TransactionId,
    string? EntryReference,
    string? Status,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    decimal? Amount,
    string? Currency,
    string? CreditDebitIndicator,
    string? Creditor,
    string? Debtor,
    string? CreditorAccountLast4,
    string? DebtorAccountLast4,
    IReadOnlyList<string> RemittanceInformation,
    string? MerchantCategoryCode,
    string? BankTransactionCode,
    string? BankTransactionDescription);

/// <summary>Trusted caller identity for banking write operations, set by FullWorth.Web from the session.</summary>
public sealed record BankingCaller(Guid UserId, Guid FullWorthSpaceId);

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
    ILogger<BankSyncService> logger,
    EnableBankingClientResolver? providerResolver = null)
{
    private readonly EnableBankingOptions _providerOptions = providerOptions.Value;
    private readonly BankingSyncOptions _sync = syncOptions.Value;

    // Kept for unit tests/legacy installations. Browser endpoints should use the caller-aware overload.
    public Task<JsonElement> GetInstitutionsAsync(string? country, CancellationToken ct) =>
        provider.GetInstitutionsAsync((country ?? _providerOptions.DefaultCountry).ToUpperInvariant(), ct);

    public async Task<JsonElement> GetInstitutionsAsync(
        string? country,
        string? psuType,
        BankingCaller caller,
        CancellationToken ct)
    {
        var (client, _) = providerResolver is null
            ? (provider, (EnableBankingProfileDto?)null)
            : await providerResolver.ResolveForUserAsync(caller.UserId, null, requireActive: true, ct);
        return await client.GetInstitutionsAsync(
            (country ?? _providerOptions.DefaultCountry).ToUpperInvariant(),
            string.IsNullOrWhiteSpace(psuType) ? null : psuType,
            ct);
    }

    public async Task<string> StartConnectionAsync(ConnectBankRequest request, BankingCaller caller, CancellationToken ct)
    {
        var requestedProfileId = request.EnableBankingProfileId;
        var authorized = await backend.AuthorizeAsync(
            caller.UserId,
            caller.FullWorthSpaceId,
            request.ReconnectConnectionId,
            requestedProfileId,
            ct);
        if (authorized != BankAuthorizeResult.Authorized)
            throw new BankAccessException(authorized == BankAuthorizeResult.Forbidden);

        BankConnectionDto? existing = request.ReconnectConnectionId is { } reconnectId
            ? await FindConnectionAsync(reconnectId, ct)
            : null;
        if (request.ReconnectConnectionId.HasValue && existing is null)
            throw new BankAccessException(false);

        var profileId = existing?.EnableBankingProfileId ?? requestedProfileId;
        EnableBankingClient client;
        EnableBankingProfileDto? profile;
        if (providerResolver is null)
        {
            client = provider;
            profile = null;
        }
        else
        {
            (client, profile) = await providerResolver.ResolveForUserAsync(
                caller.UserId,
                profileId,
                requireActive: true,
                ct);
            profileId = profile?.Id;
        }

        var redirectUrl = _providerOptions.RedirectUrl;
        if (string.IsNullOrWhiteSpace(redirectUrl))
            throw new InvalidOperationException("EnableBanking:RedirectUrl is not configured.");

        var country = (request.Country ?? _providerOptions.DefaultCountry).ToUpperInvariant();
        var desiredPsuType = string.IsNullOrWhiteSpace(request.PsuType)
            ? _providerOptions.DefaultPsuType
            : request.PsuType.Trim().ToLowerInvariant();

        var list = await client.GetInstitutionsAsync(country, desiredPsuType, ct);
        var institution = FindInstitution(list, request.InstitutionName);
        if (institution.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Institution '{request.InstitutionName}' was not returned for {country}.");

        var supportedPsuTypes = GetStringArray(institution, "psu_types");
        if (supportedPsuTypes.Count > 0 && !supportedPsuTypes.Contains(desiredPsuType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Institution '{request.InstitutionName}' does not support PSU type '{desiredPsuType}'.");

        ValidateAuthMethod(institution, request.AuthMethod, desiredPsuType);
        ValidateCredentials(institution, request.AuthMethod, desiredPsuType, request.Credentials, request.CredentialsAutosubmit == true);
        if (request.Credentials is { Count: > 0 } && string.IsNullOrWhiteSpace(request.AuthMethod))
            throw new InvalidOperationException("Credentials require an explicit Enable Banking auth method.");

        var maxSeconds = institution.TryGetProperty("maximum_consent_validity", out var max) &&
                         max.ValueKind == JsonValueKind.Number &&
                         max.TryGetInt64(out var seconds) &&
                         seconds > 0
            ? seconds
            : (long)TimeSpan.FromDays(90).TotalSeconds;
        var requested = TimeSpan.FromDays(Math.Clamp(request.ValidDays ?? 365, 1, 365));
        var validity = requested < TimeSpan.FromSeconds(maxSeconds)
            ? requested
            : TimeSpan.FromSeconds(maxSeconds);
        var validUntil = DateTimeOffset.UtcNow.Add(validity);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var stateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(_providerOptions.AuthorizationStateTtlMinutes, 1, 60));
        var language = NormalizeLanguage(request.Language);
        var psuId = BuildPseudonymousPsuId(caller.UserId, profileId, client.ApplicationId);

        var result = await client.StartAuthorizationAsync(
            GetString(institution, "name") ?? request.InstitutionName,
            country,
            redirectUrl,
            state,
            validUntil,
            request.AuthMethod,
            psuId,
            request.Credentials,
            ct,
            desiredPsuType,
            language,
            request.CredentialsAutosubmit);

        var requiredPsuHeaders = GetStringArray(institution, "required_psu_headers");
        await backend.UpsertConnectionAsync(new(
            Id: request.ReconnectConnectionId,
            Provider: "enable-banking",
            InstitutionName: GetString(institution, "name") ?? request.InstitutionName,
            Country: country,
            AuthorizationState: state,
            AuthorizationId: result.AuthorizationId,
            ProviderSessionId: existing?.ProviderSessionId,
            Status: "PENDING_AUTHORIZATION",
            ValidUntil: validUntil,
            LastAttemptAt: existing?.LastAttemptAt,
            LastSyncedAt: existing?.LastSyncedAt,
            NextSyncAllowedAt: null,
            ConsecutiveFailures: 0,
            LastError: null,
            FullWorthSpaceId: caller.FullWorthSpaceId,
            AuthorizationUserId: caller.UserId,
            AuthorizationStateExpiresAt: stateExpiresAt,
            EnableBankingProfileId: profileId,
            PsuType: desiredPsuType,
            AuthMethod: request.AuthMethod,
            RequiredPsuHeadersJson: JsonSerializer.Serialize(requiredPsuHeaders)), ct);

        return result.Url;
    }

    public Task<BankConnectionDto> CompleteConnectionAsync(string state, string code, CancellationToken ct) =>
        CompleteConnectionAsync(state, code, null, ct);

    public async Task<BankConnectionDto> CompleteConnectionAsync(
        string state,
        string code,
        PsuContext? psuContext,
        CancellationToken ct)
    {
        var connection = await backend.ConsumeStateAsync(state, ct)
            ?? throw new InvalidOperationException("Unknown or expired authorization state.");

        var client = await ResolveProviderForConnectionAsync(connection, ct);
        var session = await client.AuthorizeSessionAsync(code, ct);
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

        using var lease = await syncGate.EnterAsync(ct);
        try
        {
            // The user has just returned from the ASPSP. Treat the first retrieval as online when the
            // BFF supplied a complete PSU context; otherwise PsuContext itself falls back to no headers.
            return await SyncConnectionCoreAsync(connection, bypassCadence: true, psuContext, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Initial sync after connecting {Institution} failed; the authorization remains valid and the worker will retry.",
                connection.InstitutionName);
            return connection;
        }
    }

    public async Task<BankSyncResult> SyncAllAsync(CancellationToken ct)
    {
        using var lease = await syncGate.TryEnterAsync(ct);
        if (lease is null)
            return new(0, 0, 0, true);

        var connections = await backend.ListConnectionsAsync(ct);
        var synced = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var connection in connections.Where(x =>
                     string.Equals(x.Status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(x.ProviderSessionId) &&
                     (!x.ValidUntil.HasValue || x.ValidUntil > DateTimeOffset.UtcNow)))
        {
            if (!CanBackgroundSync(connection, DateTimeOffset.UtcNow))
            {
                skipped++;
                continue;
            }

            try
            {
                // Scheduled retrieval is deliberately PSU-header free.
                await SyncConnectionCoreAsync(connection, bypassCadence: false, null, ct);
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

    public Task<ManualSyncResult> RequestManualSyncAsync(
        Guid connectionId,
        BankingCaller caller,
        bool force,
        CancellationToken ct) =>
        RequestManualSyncAsync(connectionId, caller, force, null, ct);

    public async Task<ManualSyncResult> RequestManualSyncAsync(
        Guid connectionId,
        BankingCaller caller,
        bool force,
        PsuContext? psuContext,
        CancellationToken ct)
    {
        var authorized = await backend.AuthorizeAsync(
            caller.UserId,
            caller.FullWorthSpaceId,
            connectionId,
            null,
            ct);
        if (authorized != BankAuthorizeResult.Authorized) return new(ManualSyncStatus.NotFound);

        var connection = await FindConnectionAsync(connectionId, ct);
        if (connection is null) return new(ManualSyncStatus.NotFound);

        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(connection.Status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(connection.ProviderSessionId) ||
            (connection.ValidUntil.HasValue && connection.ValidUntil.Value <= now))
            return new(ManualSyncStatus.ReauthorizationRequired);

        // User force may bypass our ordinary 6h background cadence, but never a persisted provider
        // rate-limit window.
        if (connection.NextSyncAllowedAt is { } next && next > now &&
            (!force || string.Equals(connection.LastError, "ASPSP_RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase)))
            return new(ManualSyncStatus.Cooldown, next);

        using var lease = await syncGate.TryEnterAsync(ct);
        if (lease is null) return new(ManualSyncStatus.AlreadyRunning);

        var current = await FindConnectionAsync(connectionId, ct) ?? connection;
        if (current.NextSyncAllowedAt is { } currentNext && currentNext > DateTimeOffset.UtcNow &&
            (!force || string.Equals(current.LastError, "ASPSP_RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase)))
            return new(ManualSyncStatus.Cooldown, currentNext);

        try
        {
            await SyncConnectionCoreAsync(current, bypassCadence: force, psuContext, ct);
        }
        catch (EnableBankingApiException)
        {
            var afterFailure = await FindConnectionAsync(connectionId, ct);
            return new(ManualSyncStatus.Cooldown, afterFailure?.NextSyncAllowedAt);
        }

        var afterSync = await FindConnectionAsync(connectionId, ct);
        return new(ManualSyncStatus.Started, afterSync?.NextSyncAllowedAt);
    }

    public async Task<DisconnectStatus> DisconnectAsync(
        Guid connectionId,
        BankingCaller caller,
        PsuContext? psuContext,
        CancellationToken ct)
    {
        var authorized = await backend.AuthorizeAsync(caller.UserId, caller.FullWorthSpaceId, connectionId, null, ct);
        if (authorized != BankAuthorizeResult.Authorized) return DisconnectStatus.NotFound;

        var connection = await FindConnectionAsync(connectionId, ct);
        if (connection is null) return DisconnectStatus.NotFound;

        if (!string.IsNullOrWhiteSpace(connection.ProviderSessionId))
        {
            var client = await ResolveProviderForConnectionAsync(connection, ct);
            try
            {
                await client.DeleteSessionAsync(
                    connection.ProviderSessionId,
                    psuContext,
                    RequiredPsuHeaders(connection),
                    ct);
            }
            catch (EnableBankingApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // Already gone is an idempotent successful disconnect.
            }
            catch (EnableBankingApiException ex)
            {
                logger.LogWarning("Remote consent close failed for {Institution}: {Code}", connection.InstitutionName, ex.ErrorCode);
                return DisconnectStatus.ProviderFailed;
            }
        }

        return await backend.DeleteConnectionDataAsync(connectionId, caller.UserId, caller.FullWorthSpaceId, ct)
            ? DisconnectStatus.Deleted
            : DisconnectStatus.NotFound;
    }

    public async Task<ProviderTransactionDetailsView> GetTransactionDetailsAsync(
        Guid transactionId,
        BankingCaller caller,
        PsuContext? psuContext,
        CancellationToken ct)
    {
        var pointer = await backend.GetTransactionProviderPointerAsync(
            transactionId,
            caller.UserId,
            caller.FullWorthSpaceId,
            ct) ?? throw new BankAccessException(false);

        if (string.IsNullOrWhiteSpace(pointer.ProviderTransactionId))
            throw new InvalidOperationException("This transaction has no provider transaction detail identifier.");

        var connection = await FindConnectionAsync(pointer.ConnectionId, ct)
            ?? throw new BankAccessException(false);
        var client = await ResolveProviderForConnectionAsync(connection, ct);

        var json = await client.GetTransactionDetailsAsync(
            pointer.ProviderAccountId,
            pointer.ProviderTransactionId,
            psuContext,
            RequiredPsuHeaders(connection),
            ct);

        JsonElement amount = default;
        var hasAmount = json.ValueKind == JsonValueKind.Object &&
                        json.TryGetProperty("transaction_amount", out amount) &&
                        amount.ValueKind == JsonValueKind.Object;
        return new(
            GetString(json, "transaction_id"),
            GetString(json, "entry_reference"),
            GetString(json, "status"),
            ParseDate(json, "booking_date"),
            ParseDate(json, "value_date"),
            hasAmount ? GetDecimal(amount, "amount") : null,
            hasAmount ? GetString(amount, "currency") : null,
            GetString(json, "credit_debit_indicator"),
            GetNestedString(json, "creditor", "name"),
            GetNestedString(json, "debtor", "name"),
            GetPartyAccountLast4(json, "creditor_account"),
            GetPartyAccountLast4(json, "debtor_account"),
            GetRemittanceInformation(json),
            GetString(json, "merchant_category_code"),
            GetNestedString(json, "bank_transaction_code", "code"),
            GetNestedString(json, "bank_transaction_code", "description"));
    }

    private async Task<EnableBankingClient> ResolveProviderForConnectionAsync(BankConnectionDto connection, CancellationToken ct) =>
        providerResolver is null ? provider : await providerResolver.ResolveForConnectionAsync(connection, ct);

    private async Task<BankConnectionDto?> FindConnectionAsync(Guid connectionId, CancellationToken ct) =>
        (await backend.ListConnectionsAsync(ct)).FirstOrDefault(x => x.Id == connectionId);

    private bool CanBackgroundSync(BankConnectionDto connection, DateTimeOffset now)
    {
        if (connection.NextSyncAllowedAt.HasValue && connection.NextSyncAllowedAt.Value > now)
            return false;

        var minimum = TimeSpan.FromMinutes(Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        return !connection.LastAttemptAt.HasValue || connection.LastAttemptAt.Value + minimum <= now;
    }

    private async Task<BankConnectionDto> SyncConnectionCoreAsync(
        BankConnectionDto connection,
        bool bypassCadence,
        PsuContext? psuContext,
        CancellationToken ct)
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

        var client = await ResolveProviderForConnectionAsync(connection, ct);
        try
        {
            var session = await client.GetSessionAsync(connection.ProviderSessionId, ct);
            var status = (GetString(session, "status") ?? connection.Status).ToUpperInvariant();
            if (!string.Equals(status, "AUTHORIZED", StringComparison.Ordinal))
            {
                return await backend.UpsertConnectionAsync(ToWrite(
                    connection,
                    status: status,
                    nextSyncAllowedAt: nextAllowed,
                    lastError: SessionError(status),
                    consecutiveFailures: 0), ct);
            }

            var accounts = ParseSessionAccounts(connection, session);
            var outcome = AccountSyncOutcome.Success;
            foreach (var account in accounts)
            {
                var accountOutcome = await SyncAccountAsync(client, connection, account, psuContext, ct);
                if (accountOutcome > outcome) outcome = accountOutcome;
            }

            var error = outcome switch
            {
                AccountSyncOutcome.AccountResolutionFailed => "ACCOUNT_RESOLUTION_FAILED",
                AccountSyncOutcome.HistoryPageLimitReached => "HISTORY_PAGE_LIMIT_REACHED",
                _ => null
            };

            return await backend.UpsertConnectionAsync(ToWrite(
                connection,
                status: status,
                lastSyncedAt: DateTimeOffset.UtcNow,
                nextSyncAllowedAt: nextAllowed,
                consecutiveFailures: error is null ? 0 : connection.ConsecutiveFailures + 1,
                lastError: error), ct);
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
        var classification = EnableBankingErrorClassifier.Classify(ex);
        var minimumCooldown = now.AddMinutes(
            string.Equals(classification.Code, "ASPSP_RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(360, _sync.RateLimitCooldownMinutes)
                : Math.Max(360, _sync.MinimumBackgroundSyncIntervalMinutes));
        var retryAt = ex.RetryAt.HasValue && ex.RetryAt.Value > minimumCooldown
            ? ex.RetryAt.Value
            : minimumCooldown;

        logger.LogWarning(
            "Bank sync for {Institution} stopped ({Category}); next background attempt not before {RetryAt}.",
            connection.InstitutionName,
            classification.Code,
            retryAt);

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

    private async Task<AccountSyncOutcome> SyncAccountAsync(
        EnableBankingClient client,
        BankConnectionDto connection,
        AccountState account,
        PsuContext? psuContext,
        CancellationToken ct)
    {
        var requiredPsuHeaders = RequiredPsuHeaders(connection);
        var detailsFetched = false;

        if (account.NeedsHashResolution)
        {
            var resolved = await TryGetAccountDetailsAsync(client, connection, account.ProviderAccountId, psuContext, requiredPsuHeaders, ct);
            var realHash = resolved is { } json ? GetString(json, "identification_hash") : null;
            if (string.IsNullOrWhiteSpace(realHash))
            {
                logger.LogWarning(
                    "Skipping account {Uid} of {Institution}: identification_hash could not be resolved.",
                    account.ProviderAccountId,
                    connection.InstitutionName);
                return AccountSyncOutcome.AccountResolutionFailed;
            }
            account = ApplyDetails(account, resolved!.Value) with
            {
                IdentificationHash = realHash,
                IdentificationHashes = GetIdentificationHashes(resolved.Value, realHash),
                NeedsHashResolution = false,
                HasDetails = true
            };
            detailsFetched = true;
        }

        var syncState = await FindAccountSyncStateAsync(connection.Id, AccountIdentificationHashes(account), ct);

        // If the primary hash changed, /details may reveal the previous hash in identification_hashes.
        // Resolve that alias before deciding this is a brand-new account and doing another longest import.
        if (!detailsFetched && (syncState is null || !account.HasDetails))
        {
            var details = await TryGetAccountDetailsAsync(
                client, connection, account.ProviderAccountId, psuContext, requiredPsuHeaders, ct);
            if (details is { } json)
            {
                account = ApplyDetails(account, json) with { HasDetails = true };
                detailsFetched = true;
                if (syncState is null)
                    syncState = await FindAccountSyncStateAsync(
                        connection.Id, AccountIdentificationHashes(account), ct);
            }
        }

        var initialSync = syncState?.LatestBookingDate is null;

        var balancesJson = await client.GetBalancesAsync(
            account.ProviderAccountId, psuContext, requiredPsuHeaders, ct);
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
        var maxPages = Math.Max(1, _sync.MaxPagesPerAccount);
        var pageLimitReached = false;

        for (var page = 0; page < maxPages; page++)
        {
            JsonElement response;
            try
            {
                response = await client.GetTransactionsAsync(
                    account.ProviderAccountId,
                    from,
                    to,
                    initialSync,
                    continuation,
                    psuContext,
                    requiredPsuHeaders,
                    ct);
            }
            catch (EnableBankingApiException ex) when (
                !initialSync &&
                page == 0 &&
                string.Equals(ex.ErrorCode, "WRONG_TRANSACTIONS_PERIOD", StringComparison.OrdinalIgnoreCase) &&
                from.HasValue &&
                from.Value < now.AddDays(-90))
            {
                // Some ASPSPs only allow a bounded online/history period after the initial retrieval.
                // Narrow once to 90 days; never loop/retry the same rejected period.
                from = now.AddDays(-90);
                response = await client.GetTransactionsAsync(
                    account.ProviderAccountId,
                    from,
                    to,
                    initialSync: false,
                    continuationKey: null,
                    psuContext,
                    requiredPsuHeaders,
                    ct);
            }

            var parsed = response.ValueKind == JsonValueKind.Object &&
                         response.TryGetProperty("transactions", out var txs) &&
                         txs.ValueKind == JsonValueKind.Array
                ? txs.EnumerateArray()
                    .Select(x => ParseTransaction(account, x))
                    .Where(x => x is not null)
                    .Cast<TransactionBatchItem>()
                    .ToList()
                : [];

            foreach (var chunk in parsed.Chunk(Math.Max(25, _sync.PersistBatchSize)))
            {
                await backend.IngestAsync(new(
                    new(connection.Id, connection.Provider, connection.InstitutionName, connection.Country,
                        connection.ProviderSessionId, "AUTHORIZED", connection.ValidUntil, DateTimeOffset.UtcNow, null),
                    [new(account.IdentificationHash, account.ProviderAccountId, connection.InstitutionName,
                        account.DisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4,
                        true, account.HasDetails, AccountIdentificationHashes(account))],
                    firstPersist ? balances : [],
                    chunk), ct);
                firstPersist = false;
            }

            if (parsed.Count == 0 && firstPersist)
            {
                await backend.IngestAsync(new(
                    new(connection.Id, connection.Provider, connection.InstitutionName, connection.Country,
                        connection.ProviderSessionId, "AUTHORIZED", connection.ValidUntil, DateTimeOffset.UtcNow, null),
                    [new(account.IdentificationHash, account.ProviderAccountId, connection.InstitutionName,
                        account.DisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4,
                        true, account.HasDetails, AccountIdentificationHashes(account))],
                    balances,
                    []), ct);
                firstPersist = false;
            }

            continuation = GetString(response, "continuation_key");
            if (string.IsNullOrWhiteSpace(continuation))
                break;
            if (page == maxPages - 1)
                pageLimitReached = true;
        }

        return pageLimitReached
            ? AccountSyncOutcome.HistoryPageLimitReached
            : AccountSyncOutcome.Success;
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
            lastError,
            EnableBankingProfileId: connection.EnableBankingProfileId,
            PsuType: connection.PsuType,
            AuthMethod: connection.AuthMethod,
            RequiredPsuHeadersJson: connection.RequiredPsuHeadersJson);

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
        return new(
            hash,
            uid,
            name ?? connection.InstitutionName,
            GetString(json, "product"),
            GetString(json, "cash_account_type"),
            GetString(json, "currency") ?? "EUR",
            GetIbanLast4(json),
            IdentificationHashes: GetIdentificationHashes(json, hash),
            HasDetails: name is not null);
    }

    private static AccountState? ParseAccountFromUid(BankConnectionDto connection, string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        return new($"uid:{uid}", uid, connection.InstitutionName, null, null, "EUR", null,
            IdentificationHashes: [], HasDetails: false, NeedsHashResolution: true);
    }

    private async Task<JsonElement?> TryGetAccountDetailsAsync(
        EnableBankingClient client,
        BankConnectionDto connection,
        string providerAccountId,
        PsuContext? psuContext,
        IReadOnlyCollection<string> requiredPsuHeaders,
        CancellationToken ct)
    {
        try
        {
            return await client.GetAccountAsync(providerAccountId, psuContext, requiredPsuHeaders, ct);
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

    private static AccountState ApplyDetails(AccountState account, JsonElement details)
    {
        var primary = GetString(details, "identification_hash") ?? account.IdentificationHash;
        return account with
        {
            IdentificationHash = primary,
            IdentificationHashes = MergeIdentificationHashes(
                AccountIdentificationHashes(account),
                GetIdentificationHashes(details, primary)),
            DisplayName = GetString(details, "details") ?? GetString(details, "name") ?? account.DisplayName,
            Product = GetString(details, "product") ?? account.Product,
            AccountType = GetString(details, "cash_account_type") ?? account.AccountType,
            Currency = GetString(details, "currency") ?? account.Currency,
            IbanLast4 = GetIbanLast4(details) ?? account.IbanLast4
        };
    }

    private async Task<AccountSyncState?> FindAccountSyncStateAsync(
        Guid connectionId,
        IReadOnlyList<string> identificationHashes,
        CancellationToken ct)
    {
        foreach (var hash in identificationHashes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            var state = await backend.GetAccountSyncStateAsync(connectionId, hash, ct);
            if (state is not null) return state;
        }
        return null;
    }

    private static IReadOnlyList<string> GetIdentificationHashes(JsonElement json, string primary)
    {
        var hashes = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) hashes.Add(primary);
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty("identification_hashes", out var aliases) &&
            aliases.ValueKind == JsonValueKind.Array)
            hashes.AddRange(aliases.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>());
        return hashes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> AccountIdentificationHashes(AccountState account) =>
        MergeIdentificationHashes(
            [account.IdentificationHash],
            account.IdentificationHashes ?? []);

    private static IReadOnlyList<string> MergeIdentificationHashes(
        IEnumerable<string> first,
        IEnumerable<string> second) =>
        first.Concat(second)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static List<BalanceBatchItem> ParseBalances(AccountState account, JsonElement json)
    {
        var result = new List<BalanceBatchItem>();
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("balances", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return result;

        var captured = DateTimeOffset.UtcNow;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("balance_amount", out var amount))
                continue;
            result.Add(new(
                account.IdentificationHash,
                GetDecimal(amount, "amount"),
                GetString(amount, "currency") ?? account.Currency,
                GetString(item, "balance_type") ?? "",
                ParseDate(item, "reference_date"),
                captured));
        }
        return result;
    }

    private static TransactionBatchItem? ParseTransaction(AccountState account, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("transaction_amount", out var amountJson))
            return null;

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
        var status = (GetString(json, "status") ?? "BOOK").ToUpperInvariant();
        var currency = GetString(amountJson, "currency") ?? account.Currency;

        // Enable Banking: entry_reference is the stable account-scoped cross-retrieval identifier.
        // transaction_id is only a pointer to the details resource and can change between retrievals.
        var key = !string.IsNullOrWhiteSpace(entryReference)
            ? $"er:{entryReference}"
            : $"fp:{Fingerprint(account.IdentificationHash, status, booking, value, amount, currency, counterparty, description)}";

        return new(
            account.IdentificationHash,
            key,
            transactionId,
            status,
            booking,
            value,
            amount,
            currency,
            counterparty,
            description,
            GetString(json, "merchant_category_code"),
            entryReference,
            json.GetRawText());
    }

    private static string? GetCounterparty(JsonElement json)
    {
        var debit = string.Equals(GetString(json, "credit_debit_indicator"), "DBIT", StringComparison.OrdinalIgnoreCase);
        return GetNestedString(json, debit ? "creditor" : "debtor", "name")
            ?? GetNestedString(json, debit ? "debtor" : "creditor", "name");
    }

    private static string? GetDescription(JsonElement json)
    {
        if (json.TryGetProperty("remittance_information", out var lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join(" | ", lines.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return GetString(json, "note") ?? GetNestedString(json, "bank_transaction_code", "description");
    }

    private static IReadOnlyList<string> GetRemittanceInformation(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("remittance_information", out var lines) ||
            lines.ValueKind != JsonValueKind.Array)
            return [];
        return lines.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
    }

    private static string? GetPartyAccountLast4(JsonElement json, string child)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(child, out var account))
            return null;
        var value = GetString(account, "iban")
                    ?? GetString(account, "bban")
                    ?? GetString(account, "masked_pan")
                    ?? GetString(account, "pan");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length >= 4 ? normalized[^4..] : normalized;
    }

    private static string? GetIbanLast4(JsonElement json)
    {
        var iban = GetNestedString(json, "account_id", "iban")
            ?.Replace(" ", string.Empty, StringComparison.Ordinal);
        return iban is { Length: >= 4 } ? iban[^4..] : null;
    }

    private static string Fingerprint(
        string accountHash,
        string status,
        DateOnly? booking,
        DateOnly? valueDate,
        decimal amount,
        string currency,
        string? counterparty,
        string? description)
    {
        var source =
            $"{accountHash}|{status}|{booking:yyyy-MM-dd}|{valueDate:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{currency}|{counterparty}|{description}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static JsonElement FindInstitution(JsonElement list, string institutionName)
    {
        if (list.ValueKind != JsonValueKind.Object ||
            !list.TryGetProperty("aspsps", out var aspsps) ||
            aspsps.ValueKind != JsonValueKind.Array)
            return default;

        return aspsps.EnumerateArray().FirstOrDefault(x =>
            string.Equals(GetString(x, "name"), institutionName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateAuthMethod(JsonElement institution, string? requestedMethod, string desiredPsuType)
    {
        if (string.IsNullOrWhiteSpace(requestedMethod)) return;
        if (!institution.TryGetProperty("auth_methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Enable Banking auth method '{requestedMethod}' is not supported by this institution.");

        var found = methods.EnumerateArray().Any(method =>
        {
            if (method.ValueKind == JsonValueKind.String)
                return string.Equals(method.GetString(), requestedMethod, StringComparison.OrdinalIgnoreCase);
            if (method.ValueKind != JsonValueKind.Object) return false;

            var name = GetString(method, "name") ?? GetString(method, "id");
            if (!string.Equals(name, requestedMethod, StringComparison.OrdinalIgnoreCase)) return false;
            if (method.TryGetProperty("hidden_method", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                return false;

            var methodPsuType = GetString(method, "psu_type");
            return string.IsNullOrWhiteSpace(methodPsuType) ||
                   string.Equals(methodPsuType, desiredPsuType, StringComparison.OrdinalIgnoreCase);
        });

        if (!found)
            throw new InvalidOperationException(
                $"Enable Banking auth method '{requestedMethod}' is not available for PSU type '{desiredPsuType}'.");
    }

    private static void ValidateCredentials(
        JsonElement institution,
        string? requestedMethod,
        string desiredPsuType,
        IReadOnlyDictionary<string, string>? supplied,
        bool autosubmit)
    {
        if (supplied is null || supplied.Count == 0) return;
        if (string.IsNullOrWhiteSpace(requestedMethod))
            throw new InvalidOperationException("Enable Banking credentials require an auth method.");
        if (!institution.TryGetProperty("auth_methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The selected Enable Banking auth method has no credential schema.");

        JsonElement selected = default;
        var found = false;
        foreach (var method in methods.EnumerateArray())
        {
            if (method.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(GetString(method, "name"), requestedMethod, StringComparison.OrdinalIgnoreCase)) continue;
            var methodPsuType = GetString(method, "psu_type");
            if (!string.IsNullOrWhiteSpace(methodPsuType) &&
                !string.Equals(methodPsuType, desiredPsuType, StringComparison.OrdinalIgnoreCase))
                continue;
            selected = method;
            found = true;
            break;
        }
        if (!found)
            throw new InvalidOperationException("The selected Enable Banking auth method is unavailable.");

        var schema = new Dictionary<string, (bool Required, string? Template)>(StringComparer.Ordinal);
        if (selected.TryGetProperty("credentials", out var fields) && fields.ValueKind == JsonValueKind.Array)
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(field, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var required = field.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;
                schema[name] = (required, GetString(field, "template"));
            }

        foreach (var (name, value) in supplied)
        {
            if (!schema.TryGetValue(name, out var definition))
                throw new InvalidOperationException($"Credential '{name}' is not accepted by the selected Enable Banking auth method.");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Credential '{name}' cannot be empty.");
            if (!string.IsNullOrWhiteSpace(definition.Template))
            {
                try
                {
                    if (!Regex.IsMatch(value, definition.Template, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                        throw new InvalidOperationException($"Credential '{name}' does not match the bank-required format.");
                }
                catch (ArgumentException)
                {
                    // Enable Banking templates are PCRE. Unsupported .NET constructs are validated
                    // by Enable Banking/ASPSP rather than turning a valid provider schema into a 500.
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new InvalidOperationException($"Credential '{name}' format validation timed out.");
                }
            }
        }

        if (autosubmit)
            foreach (var (name, definition) in schema)
                if (definition.Required && (!supplied.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)))
                    throw new InvalidOperationException(
                        $"Credential '{name}' is required when credentials_autosubmit is enabled.");
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray()
            : [];

    private static IReadOnlyCollection<string> RequiredPsuHeaders(BankConnectionDto connection)
    {
        if (string.IsNullOrWhiteSpace(connection.RequiredPsuHeadersJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(connection.RequiredPsuHeadersJson)
                ?.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildPseudonymousPsuId(Guid userId, Guid? profileId, string applicationId)
    {
        var source = $"{applicationId}|{profileId?.ToString("D") ?? "legacy"}|{userId:D}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string? NormalizeLanguage(string? value)
    {
        var language = value?.Trim().ToLowerInvariant();
        return language is { Length: 2 } && language.All(char.IsAsciiLetter) ? language : null;
    }

    private static string? SessionError(string status) => status switch
    {
        "EXPIRED" => "SESSION_EXPIRED",
        "REVOKED" => "SESSION_REVOKED",
        "CLOSED" => "SESSION_CLOSED",
        "CANCELLED" => "AUTHORIZATION_CANCELLED",
        "INVALID" => "AUTHORIZATION_FAILED",
        _ => null
    };

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? GetNestedString(JsonElement e, string child, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(child, out var c)
            ? GetString(c, name)
            : null;

    private static DateOnly? ParseDate(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String &&
        DateOnly.TryParse(v.GetString(), out var date)
            ? date
            : null;

    private static decimal GetDecimal(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String &&
               decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private enum AccountSyncOutcome
    {
        Success = 0,
        AccountResolutionFailed = 1,
        HistoryPageLimitReached = 2
    }

    private sealed record AccountState(
        string IdentificationHash,
        string ProviderAccountId,
        string DisplayName,
        string? Product,
        string? AccountType,
        string Currency,
        string? IbanLast4,
        IReadOnlyList<string>? IdentificationHashes = null,
        bool HasDetails = true,
        bool NeedsHashResolution = false);
}
