using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Backend;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";
    public string BaseUrl { get; set; } = FullWorth.Shared.UnifiedHost.LoopbackBaseUrl;
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
    string RequiredPsuHeadersJson = "[]",
    Guid? AuthorizationUserId = null,
    DateTimeOffset? AuthorizationStateExpiresAt = null);

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
public sealed record AccountBatchItem(string IdentificationHash, string ProviderAccountId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool HasDetails = true, IReadOnlyList<string>? IdentificationHashes = null, string? Usage = null, string? PsuStatus = null, decimal? CreditLimitAmount = null, string? CreditLimitCurrency = null, string? Iban = null);
public sealed record BalanceBatchItem(string IdentificationHash, decimal Amount, string Currency, string BalanceType, DateOnly? ReferenceDate, DateTimeOffset CapturedAt);
public sealed record TransactionBatchItem(string IdentificationHash, string ExternalKey, string? ProviderTransactionId, string Status, DateOnly? BookingDate, DateOnly? ValueDate, decimal Amount, string Currency, string? Counterparty, string? Description, string? MerchantCategoryCode, string? EntryReference, string RawJson, string? CounterpartyAccountIdentifier = null);
/// <summary>Mirrors the backend record of the same name - see IngestionModule.</summary>
public sealed record PendingReconciliation(string IdentificationHash, IReadOnlyList<string> SeenExternalKeys, DateOnly? WindowFrom);
public sealed record FinanceIngestBatch(IngestConnectionDto Connection, IReadOnlyList<AccountBatchItem> Accounts, IReadOnlyList<BalanceBatchItem> Balances, IReadOnlyList<TransactionBatchItem> Transactions, IReadOnlyList<PendingReconciliation>? PendingReconciliations = null);
public sealed record FinTsHoldingSnapshotDto(string ProviderKey, string Name, string? Isin, string? Wkn, string Currency, decimal Quantity, decimal? Price, DateOnly? PriceDate, decimal? MarketValue, string? Exchange, decimal? CostPrice = null);
public sealed record FinTsInvestmentSnapshotDto(Guid ConnectionId, string DepotKey, string Name, string Currency, DateOnly AsOf, IReadOnlyList<FinTsHoldingSnapshotDto> Holdings);
public sealed record AccountSyncState(DateOnly? LatestBookingDate);

/// <summary>Ein Konto dieser Verbindung, so weit die Kontenauswahl es braucht.</summary>
public sealed record ConnectionAccountState(string IdentificationHash, Guid AccountId, string DisplayName, bool IsActive);
public sealed record ConsumeStateBody(string State);
public sealed record AuthorizeBody(Guid FullWorthSpaceId, Guid? ConnectionId, Guid? EnableBankingProfileId = null);
public sealed record DeleteConnectionBody(Guid FullWorthSpaceId);
public sealed record CloseConnectionBody(Guid FullWorthSpaceId);
public sealed record TransactionProviderPointer(Guid ConnectionId, string ProviderAccountId, string? ProviderTransactionId);
public sealed record BankSyncHistoryWrite(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Result,
    string? ErrorCode,
    // #167: Auslöser und Verbindungsweg. Hand gespiegelt wie der Rest - zwischen diesen Projekten
    // faengt kein Build eine Abweichung, also grept man bei einer Aenderung die andere Seite.
    string? Trigger = null,
    string? Connector = null);

/// <summary>
/// Die gespiegelte Form von <c>BankingProviderStatusRow</c> im Backend (#165).
///
/// Hand gespiegelt, weil zwischen diesen Projekten bewusst keine gemeinsame Vertrags-Bibliothek
/// liegt - kein Build faengt eine Umbenennung, also grept man bei einer Aenderung die andere Seite.
/// </summary>
public sealed record BankingProviderStatusRowDto(string Country, string Brand, string PsuType, string Status);

public sealed record ControlPanelPrincipalDto(Guid UserId);

/// <summary>
/// Ein Institut aus dem lokalen Katalog (#169). Die drei <see cref="JsonElement"/>-Felder tragen die
/// Form des Anbieters: PSU-Typen sind eine Liste, die Gruppe kann Text oder Objekt sein, die
/// Anmeldeverfahren sind verschachtelte Protokollangaben. Sie werden unveraendert durchgereicht - die
/// Oberflaeche liest sie genauso, wie sie sie vom Anbieter gelesen hat.
/// </summary>
public sealed record BankingInstitutionRowDto(
    string Country,
    string Name,
    System.Text.Json.JsonElement? PsuTypes,
    System.Text.Json.JsonElement? Group,
    string? Logo,
    bool Beta,
    System.Text.Json.JsonElement? AuthMethods);

public sealed record BankingInstitutionCatalogDto(
    bool Known,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    IReadOnlyList<BankingInstitutionRowDto> Institutions);

public sealed record BankingInstitutionCountriesDto(IReadOnlyList<string> Countries);

/// <param name="Known">Falsch, solange nie erfolgreich geprueft wurde. Eine leere Liste allein waere
/// zweideutig: der Bankdialog wuerde nach einer frischen Installation jede Bank als gesund ausgeben,
/// obwohl niemand nachgesehen hat.</param>
public sealed record BankingProviderStatusSnapshotDto(
    bool Known,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    IReadOnlyList<BankingProviderStatusRowDto> Statuses);

/// <summary>
/// Eine Antwort der Bank fuer den verschluesselten Rohspeicher. Von Hand gespiegelt aus
/// FullWorth.Backend.Modules.BankConnections - zwischen den Projekten gibt es keine Kopplung, die
/// eine Umbenennung auffangen wuerde.
/// </summary>
public sealed record FinTsRawResponseWrite(string Kind, string? Label, string Payload);

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
    DateTimeOffset UpdatedAt,
    string? ControlPanelRefreshToken = null);

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
    DateTimeOffset VerifiedAt,
    string? ControlPanelRefreshToken = null);

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

    public async Task RecordSyncHistoryAsync(Guid connectionId, BankSyncHistoryWrite body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, $"/internal/banking/connections/{connectionId:D}/sync-history", body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Wessen Control-Panel-Zugang der Statusdienst benutzen darf (#165). Der Feed gilt fuer die
    /// ganze Installation, die Zugangsdaten dafuer gehoeren aber einem Nutzer.
    /// </summary>
    public async Task<Guid?> GetControlPanelPrincipalAsync(CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, "/internal/banking/profiles/control-panel-principal");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadFromJsonAsync<ControlPanelPrincipalDto>(cancellationToken: ct);
        return body?.UserId;
    }

    /// <summary>Der zuletzt gespeicherte Anbieterzustand (#165) - der einzige Lesepfad der Oberflaeche.</summary>
    public async Task<BankingProviderStatusSnapshotDto?> GetProviderStatusAsync(string? country, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(country) ? string.Empty : $"?country={Uri.EscapeDataString(country)}";
        using var request = Create(HttpMethod.Get, $"/internal/banking/provider-status/{query}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<BankingProviderStatusSnapshotDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Wessen Anwendungs-Zugangsdaten der Katalogdienst benutzen darf (#169). Anderer Zugang als beim
    /// Statusdienst: der Katalog kommt ueber das Profil, der Gesundheitsfeed ueber das Control Panel.
    /// </summary>
    public async Task<Guid?> GetProviderPrincipalAsync(CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, "/internal/banking/profiles/provider-principal");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadFromJsonAsync<ControlPanelPrincipalDto>(cancellationToken: ct);
        return body?.UserId;
    }

    /// <summary>Der lokale Institutionenkatalog eines Landes (#169).</summary>
    public async Task<BankingInstitutionCatalogDto?> GetInstitutionCatalogAsync(string country, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"/internal/banking/institutions/{Uri.EscapeDataString(country)}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<BankingInstitutionCatalogDto>(cancellationToken: ct);
    }

    /// <summary>Welche Laender gepflegt werden muessen - die abgefragten plus die mit Verbindungen.</summary>
    public async Task<IReadOnlyList<string>> GetInstitutionCountriesAsync(CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, "/internal/banking/institutions/countries");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return [];
        var body = await response.Content.ReadFromJsonAsync<BankingInstitutionCountriesDto>(cancellationToken: ct);
        return body?.Countries ?? [];
    }

    /// <summary>Nach einem erfolgreichen Katalogabruf: das Land uebernehmen.</summary>
    public async Task ReplaceInstitutionCatalogAsync(
        string country, IReadOnlyList<BankingInstitutionRowDto> institutions, CancellationToken ct)
    {
        using var request = Create(
            HttpMethod.Put, $"/internal/banking/institutions/{Uri.EscapeDataString(country)}", new { institutions });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Nach einem gescheiterten Katalogabruf: nur den Versuch vermerken. Der gespeicherte Katalog
    /// bleibt - eine Bankauswahl, die bei jedem Anbieterausfall leer waere, haette genau den Fehler,
    /// den #169 abstellt.
    /// </summary>
    public async Task RecordInstitutionFailureAsync(string country, string reason, CancellationToken ct)
    {
        using var request = Create(
            HttpMethod.Post, $"/internal/banking/institutions/{Uri.EscapeDataString(country)}/failures",
            new { reason });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Nach einem erfolgreichen Abruf: den ganzen Feed ersetzen.</summary>
    public async Task ReplaceProviderStatusAsync(
        IReadOnlyList<BankingProviderStatusRowDto> statuses, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Put, "/internal/banking/provider-status/", new { statuses });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Nach einem gescheiterten Abruf: nur den Versuch vermerken. Die gespeicherten Zeilen bleiben -
    /// ein Ausfall des Control Panels ist keine Aussage ueber die Banken.
    /// </summary>
    public async Task RecordProviderStatusFailureAsync(string reason, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/provider-status/failures", new { reason });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Legt eine Antwort der Bank im verschluesselten Rohspeicher ab.
    ///
    /// Absichtlich OHNE EnsureSuccessStatusCode: das hier ist ein Hilfsmittel zum Nachsehen. Ein
    /// Abruf, der Daten gebracht hat, darf nicht daran scheitern, dass sein Andenken nicht
    /// gespeichert werden konnte.
    /// </summary>
    public async Task RecordRawResponseAsync(Guid connectionId, FinTsRawResponseWrite body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, $"/internal/banking/connections/{connectionId:D}/raw-responses", body);
        using var response = await http.SendAsync(request, ct);
        _ = response.StatusCode;
    }

    /// <summary>Was FullWorth von den Konten dieser Verbindung schon kennt.</summary>
    public async Task<IReadOnlyList<ConnectionAccountState>> ListConnectionAccountsAsync(Guid connectionId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"/internal/banking/connections/{connectionId}/accounts");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ConnectionAccountState>>(cancellationToken: ct) ?? [];
    }

    public async Task<AccountSyncState?> GetAccountSyncStateAsync(Guid connectionId, string identificationHash, CancellationToken ct)
    {
        using var request = Create(
            HttpMethod.Get,
            $"/internal/banking/connections/{connectionId}/accounts/sync-state?identificationHash={Uri.EscapeDataString(identificationHash)}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountSyncState>(cancellationToken: ct);
    }

    public async Task<bool> CloseConnectionRetainingDataAsync(
        Guid connectionId,
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        using var request = Create(
            HttpMethod.Post,
            $"/internal/banking/connections/{connectionId}/close-retain",
            new CloseConnectionBody(fullWorthSpaceId));
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> DeleteConnectionDataAsync(Guid connectionId, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, $"/internal/banking/connections/{connectionId:D}/delete", new DeleteConnectionBody(fullWorthSpaceId));
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<TransactionProviderPointer?> GetTransactionProviderPointerAsync(
        Guid transactionId,
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        using var request = Create(
            HttpMethod.Get,
            $"/internal/banking/transactions/{transactionId:D}/provider-pointer?fullWorthSpaceId={fullWorthSpaceId:D}");
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransactionProviderPointer>(cancellationToken: ct);
    }

    public async Task IngestFinTsInvestmentSnapshotAsync(FinTsInvestmentSnapshotDto body, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "/internal/banking/fints/investment-snapshot", body);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
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
