using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Backend;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";
    public string BaseUrl { get; set; } = "http://fullworth-backend:8080";
    public string IngestKey { get; set; } = string.Empty;
}

public sealed record BankConnectionDto(
    Guid Id,
    string Provider,
    string InstitutionName,
    string Country,
    string? AuthorizationState,
    string? AuthorizationId,
    string? ProviderSessionId,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    int ConsecutiveFailures,
    string? LastError,
    Guid? EnableBankingProfileId = null,
    string PsuType = "personal",
    string? AuthMethod = null,
    string RequiredPsuHeadersJson = "[]");

public sealed record BankConnectionWrite(
    Guid? Id,
    string Provider,
    string InstitutionName,
    string Country,
    string? AuthorizationState,
    string? AuthorizationId,
    string? ProviderSessionId,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    int ConsecutiveFailures,
    string? LastError,
    Guid? FullWorthSpaceId = null,
    Guid? AuthorizationUserId = null,
    DateTimeOffset? AuthorizationStateExpiresAt = null,
    Guid? EnableBankingProfileId = null,
    string PsuType = "personal",
    string? AuthMethod = null,
    string RequiredPsuHeadersJson = "[]");

public enum BankAuthorizeResult { Authorized, Forbidden, NotFound }

public sealed record IngestConnectionDto(Guid? ConnectionId, string Provider, string InstitutionName, string Country, string? ProviderSessionId, string Status, DateTimeOffset? ValidUntil, DateTimeOffset? LastSyncedAt, string? LastError);
public sealed record AccountBatchItem(string IdentificationHash, string ProviderAccountId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool HasDetails = true);
public sealed record BalanceBatchItem(string IdentificationHash, decimal Amount, string Currency, string BalanceType, DateOnly? ReferenceDate, DateTimeOffset CapturedAt);
public sealed record TransactionBatchItem(string IdentificationHash, string ExternalKey, string? ProviderTransactionId, string Status, DateOnly? BookingDate, DateOnly? ValueDate, decimal Amount, string Currency, string? Counterparty, string? Description, string? MerchantCategoryCode, string? EntryReference, string RawJson);
public sealed record FinanceIngestBatch(IngestConnectionDto Connection, IReadOnlyList<AccountBatchItem> Accounts, IReadOnlyList<BalanceBatchItem> Balances, IReadOnlyList<TransactionBatchItem> Transactions);
public sealed record AccountSyncState(DateOnly? LatestBookingDate);
public sealed record ConsumeStateBody(string State);
public sealed record AuthorizeBody(Guid FullWorthSpaceId, Guid? ConnectionId, Guid? EnableBankingProfileId = null);

public sealed record EnableBankingProfileDto(
    Guid Id,
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnableBankingProfileWrite(
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset VerifiedAt);

public sealed class FullWorthBackendClient(HttpClient http, IOptions<BackendOptions> options)
{
    private readonly BackendOptions _options = options.Value;

    public async Task<List<BankConnectionDto>> ListConnectionsAsync(CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, "/internal/banking/connections/");
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BankConnectionDto>>(cancellationToken: ct) ?? [];
    }

    /// <summary>
    /// Atomically consumes the authorization state (one-time). Returns null when the state is
    /// unknown, expired or already consumed — the caller must treat that as an invalid callback.
    /// </summary>
    public async Task<BankConnectionDto?> ConsumeStateAsync(string state, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/connections/consume-state", new ConsumeStateBody(state));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<BankConnectionDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Asks the backend (the authority) whether <paramref name="userId"/> may create/drive a
    /// connection in <paramref name="fullWorthSpaceId"/> (owner) and, if given, that the connection
    /// belongs to that space. The user id travels in X-FullWorth-User-Id.
    /// </summary>
    public async Task<BankAuthorizeResult> AuthorizeAsync(Guid userId, Guid fullWorthSpaceId, Guid? connectionId, Guid? enableBankingProfileId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/connections/authorize", new AuthorizeBody(fullWorthSpaceId, connectionId, enableBankingProfileId));
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.NoContent => BankAuthorizeResult.Authorized,
            System.Net.HttpStatusCode.Forbidden => BankAuthorizeResult.Forbidden,
            _ => BankAuthorizeResult.NotFound
        };
    }

    public async Task<EnableBankingProfileDto?> GetEnableBankingProfileForUserAsync(Guid userId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"/internal/banking/profiles/users/{userId:D}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnableBankingProfileDto>(cancellationToken: ct);
    }

    public async Task<EnableBankingProfileDto?> GetEnableBankingProfileAsync(Guid profileId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"/internal/banking/profiles/{profileId:D}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnableBankingProfileDto>(cancellationToken: ct);
    }

    public async Task<EnableBankingProfileDto> UpsertEnableBankingProfileAsync(EnableBankingProfileWrite body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/profiles/", body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnableBankingProfileDto>(cancellationToken: ct))!;
    }

    public async Task<System.Net.HttpStatusCode> DeleteEnableBankingProfileForUserAsync(Guid userId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Delete, $"/internal/banking/profiles/users/{userId:D}");
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode;
    }

    public async Task<BankConnectionDto> UpsertConnectionAsync(BankConnectionWrite body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/connections/", body);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BankConnectionDto>(cancellationToken: ct))!;
    }

    public async Task<AccountSyncState?> GetAccountSyncStateAsync(Guid connectionId, string identificationHash, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"/internal/banking/connections/{connectionId}/accounts/{Uri.EscapeDataString(identificationHash)}/sync-state");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountSyncState>(cancellationToken: ct);
    }

    public async Task IngestAsync(FinanceIngestBatch body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/ingest", body);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Create(HttpMethod method, string path, object? body = null)
    {
        if (string.IsNullOrWhiteSpace(_options.IngestKey)) throw new InvalidOperationException("Backend:IngestKey is not configured.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Ingest-Key", _options.IngestKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
