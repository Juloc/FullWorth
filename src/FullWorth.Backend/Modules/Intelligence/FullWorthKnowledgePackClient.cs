using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Intelligence;

public interface IFullWorthKnowledgePackClient
{
    Uri BaseUri { get; }
    Task<KnowledgePackManifest?> GetLatestManifestAsync(
        Guid instanceId,
        string instanceCredential,
        string? currentVersion,
        string? region,
        CancellationToken ct);
    Task<byte[]> DownloadPackAsync(
        Guid instanceId,
        string instanceCredential,
        string packId,
        string version,
        CancellationToken ct);
}

/// <summary>
/// Read client for official FullWorth Knowledge Packs. It deliberately shares the same fixed endpoint
/// policy as the submission client but has a separate interface so pack delivery can evolve without
/// widening the contribution transport contract.
/// </summary>
public sealed class FullWorthKnowledgePackClient : IFullWorthKnowledgePackClient
{
    private readonly HttpClient http;

    public FullWorthKnowledgePackClient(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
    {
        this.http = http;
        http.BaseAddress = FullWorthCloudClient.ResolveBaseUri(configuration, environment);
        http.Timeout = TimeSpan.FromSeconds(45);
    }

    public Uri BaseUri => http.BaseAddress ?? new Uri(FullWorthCloudClient.OfficialBaseUrl);

    public async Task<KnowledgePackManifest?> GetLatestManifestAsync(
        Guid instanceId,
        string instanceCredential,
        string? currentVersion,
        string? region,
        CancellationToken ct)
    {
        var query = new List<string>
        {
            $"instanceId={Uri.EscapeDataString(instanceId.ToString("D"))}",
            $"schemaVersion={Uri.EscapeDataString(KnowledgePackPolicy.CurrentSchemaVersion)}"
        };
        if (!string.IsNullOrWhiteSpace(currentVersion))
            query.Add($"currentVersion={Uri.EscapeDataString(currentVersion.Trim())}");
        if (!string.IsNullOrWhiteSpace(region))
            query.Add($"region={Uri.EscapeDataString(region.Trim().ToUpperInvariant())}");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/knowledge-packs/latest?{string.Join('&', query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct, allowNoContent: true);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified) return null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<KnowledgePackManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                ?? throw new JsonException("Empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new FullWorthCloudException("knowledge_pack_invalid_manifest", response.StatusCode, innerException: ex);
        }
    }

    public async Task<byte[]> DownloadPackAsync(
        Guid instanceId,
        string instanceCredential,
        string packId,
        string version,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Knowledge pack id and version are required.");

        var path = $"v1/knowledge-packs/{Uri.EscapeDataString(packId.Trim())}/{Uri.EscapeDataString(version.Trim())}" +
                   $"?instanceId={Uri.EscapeDataString(instanceId.ToString("D"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct, allowNoContent: false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ReadBoundedAsync(stream, KnowledgePackPolicy.MaximumPackBytes, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool allowNoContent)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FullWorthCloudException("cloud_timeout", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new FullWorthCloudException("cloud_unreachable", transient: true, innerException: ex);
        }

        if (response.IsSuccessStatusCode || (allowNoContent && response.StatusCode == HttpStatusCode.NotModified))
            return response;

        var status = response.StatusCode;
        var code = status switch
        {
            HttpStatusCode.Unauthorized => "cloud_unauthorized",
            HttpStatusCode.Forbidden => "cloud_entitlement_denied",
            HttpStatusCode.NotFound => "knowledge_pack_not_found",
            HttpStatusCode.TooManyRequests => "cloud_rate_limited",
            _ when (int)status >= 500 => "cloud_server_error",
            _ => $"cloud_http_{(int)status}"
        };
        var transient = status == HttpStatusCode.TooManyRequests || (int)status >= 500;
        response.Dispose();
        throw new FullWorthCloudException(code, status, transient: transient);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new FullWorthCloudException("knowledge_pack_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}
