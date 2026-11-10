using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Der eine Weg, ueber die Codex-Bruecke (<c>/execute</c>) aus unvertrautem Dokumenttext Felder nach einem
/// JSON-Schema zu holen - fuer Lohnabrechnungen und Arbeitsvertraege. Die Bruecke sieht nur den Text, das
/// Schema und die Anweisung; sie ist ein Hilfsmittel, kein Muss: ist sie aus, nicht angemeldet, zu langsam
/// oder antwortet sie mit etwas Unbrauchbarem, kommt <c>null</c> zurueck und der Aufrufer faellt auf den
/// deterministischen Parser zurueck.
/// </summary>
public sealed class CodexStructurer(
    IConfiguration configuration,
    IHttpClientFactory clients,
    FullWorth.Backend.Modules.Intelligence.CodexModelResolver models,
    ILogger<CodexStructurer> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsEnabled =>
        Intelligence.CodexBridgeConfiguration.IsEnabled(configuration) &&
        !string.IsNullOrWhiteSpace(Intelligence.CodexBridgeConfiguration.Key(configuration));

    /// <summary>Die <c>outputJson</c> der Bruecke, oder null.</summary>
    public async Task<string?> TryExecuteAsync(Guid userId, string systemInstruction, string inputJson, string schemaJson, CancellationToken ct)
    {
        if (!IsEnabled) return null;
        var key = Intelligence.CodexBridgeConfiguration.Key(configuration);
        var baseUri = Intelligence.CodexBridgeConfiguration.BaseUri(configuration);
        if (string.IsNullOrWhiteSpace(key) || baseUri is null) return null;

        // Honor the model the user picked in the AI-access settings; null means "automatic".
        var body = JsonSerializer.Serialize(new
        {
            systemInstruction,
            inputJson,
            jsonSchema = schemaJson,
            model = await models.ResolveAsync(userId, ct)
        }, Json);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/execute"));
            message.Headers.Add("X-FullWorth-Internal-Key", key);
            message.Headers.Add("X-FullWorth-Codex-Scope", BridgeScope(userId));
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var client = clients.CreateClient(Intelligence.CodexBridgeSupervisor.PassiveClient);
            client.Timeout = TimeSpan.FromMinutes(4);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var envelope = JsonSerializer.Deserialize<ExecuteEnvelope>(raw, Json);
            if (envelope?.Success != true || string.IsNullOrWhiteSpace(envelope.OutputJson))
            {
                logger.LogInformation("Codex structuring unavailable: {Error}", envelope?.Error ?? response.StatusCode.ToString());
                return null;
            }
            return envelope.OutputJson;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Codex structuring failed; falling back to local parsing.");
            return null;
        }
    }

    private static string BridgeScope(Guid userId)
    {
        var input = Encoding.UTF8.GetBytes($"fullworth-ai:{userId:N}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private sealed record ExecuteEnvelope(bool Success, string? RequestId, string? OutputJson, string? Error);
}
