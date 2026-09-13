using System.Net.Http.Json;
using FullWorth.Shared;
using FullWorth.Web.Security.BackendContext;

namespace FullWorth.Web.Modules.Admin;

/// <summary>What the finance backend reports about one of the caller's own stored credentials.</summary>
public sealed record BackendSecretEntry(string Reference, string Group, string Label, string Description);

/// <summary>
/// Fetches the half of the vault that lives in the finance database — the credentials a person entered
/// themselves, encrypted with <c>FieldCipher</c> in a schema this host does not own.
///
/// Two keys travel with every call and they are deliberately different things: the ingest key opens
/// the <c>/internal</c> path at all, and a short-lived <see cref="AdminVaultTicket"/> says that the
/// step-up already happened and names the finance user whose rows may be read. The ticket is signed
/// with a key DERIVED from the internal key rather than with the internal key itself, so learning the
/// internal key for its ordinary purpose does not also mean being able to mint one of these.
///
/// It fails quietly. A backend that is down, migrating or simply not there must degrade the vault to
/// "the infrastructure half only", not take the whole admin page with it.
/// </summary>
public sealed class AdminVaultBackendClient(
    IHttpClientFactory clients,
    BackendContextOptions backendOptions,
    IConfiguration configuration,
    ILogger<AdminVaultBackendClient> logger,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<BackendSecretEntry>> InventoryAsync(
        Guid financeUserId, CancellationToken ct)
    {
        var response = await PostAsync("inventory", financeUserId, content: null, ct);
        if (response is null) return [];

        using (response)
        {
            if (!response.IsSuccessStatusCode) return [];
            return await response.Content.ReadFromJsonAsync<List<BackendSecretEntry>>(ct) ?? [];
        }
    }

    public async Task<string?> RevealAsync(Guid financeUserId, string reference, CancellationToken ct)
    {
        var response = await PostAsync("reveal", financeUserId, new { reference }, ct);
        if (response is null) return null;

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadFromJsonAsync<RevealResponse>(ct);
            return body?.Value;
        }
    }

    private sealed record RevealResponse(string? Value);

    private async Task<HttpResponseMessage?> PostAsync(
        string path, Guid financeUserId, object? content, CancellationToken ct)
    {
        var ingestKey = configuration["Backend:IngestKey"] ?? configuration["Security:IngestKey"];
        if (string.IsNullOrWhiteSpace(ingestKey)) return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(backendOptions.BackendBaseAddress, $"/internal/admin/secrets/{path}"))
        {
            Content = JsonContent.Create(content ?? new { })
        };
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.IngestKey, ingestKey);
        request.Headers.TryAddWithoutValidation(
            AdminVaultTicket.HeaderName,
            AdminVaultTicket.Issue(backendOptions.InternalKey, financeUserId, clock.GetUtcNow()));

        try
        {
            return await clients.CreateClient().SendAsync(request, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("The vault could not reach the finance backend: {Message}", exception.Message);
            return null;
        }
    }
}
