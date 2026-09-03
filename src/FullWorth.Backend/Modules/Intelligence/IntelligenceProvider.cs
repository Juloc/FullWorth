using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Intelligence;

[Flags]
public enum IntelligenceProviderCapabilities
{
    None = 0,
    TextClassification = 1 << 0,
    StructuredExtraction = 1 << 1,
    Vision = 1 << 2,
    WebResearch = 1 << 3,
}

public sealed record IntelligenceProviderDescriptor(
    string Provider,
    IntelligenceProviderCapabilities Capabilities,
    int MaximumInputBytes,
    bool ReportsUsage);

public sealed record IntelligenceProviderTestResult(bool Success, string? ErrorCode = null, string? Message = null);

public sealed record IntelligenceProviderRequest(
    string Model,
    string Capability,
    string SystemInstruction,
    string InputJson,
    string? JsonSchema = null);

public sealed record IntelligenceProviderResponse(
    string OutputJson,
    long? InputTokens,
    long? OutputTokens,
    string? ProviderRequestId);

public interface IIntelligenceProvider
{
    IntelligenceProviderDescriptor Descriptor { get; }
    Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken);
    Task<IntelligenceProviderResponse> ExecuteAsync(IntelligenceProviderRequest request, string credential, CancellationToken cancellationToken);
}

public sealed class IntelligenceProviderRegistry(IEnumerable<IIntelligenceProvider> providers)
{
    private readonly IReadOnlyDictionary<string, IIntelligenceProvider> _providers = providers
        .ToDictionary(x => x.Descriptor.Provider, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IntelligenceProviderDescriptor> Descriptors => _providers.Values.Select(x => x.Descriptor).ToArray();

    public IIntelligenceProvider GetRequired(string provider) =>
        _providers.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new InvalidOperationException($"Unsupported intelligence provider '{provider}'.");
}

public sealed class OpenAiIntelligenceProvider(HttpClient httpClient) : IIntelligenceProvider
{
    private const int MaximumInputBytes = 2 * 1024 * 1024;

    // Vision and web-research are intentionally not advertised until dedicated, bounded adapters
    // enforce their own input/tool policies. The generic executor below is text/structured-output only.
    public IntelligenceProviderDescriptor Descriptor { get; } = new(
        IntelligenceProviders.OpenAi,
        IntelligenceProviderCapabilities.TextClassification |
        IntelligenceProviderCapabilities.StructuredExtraction,
        MaximumInputBytes,
        ReportsUsage: true);

    public async Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential))
            return new(false, "credential_missing", "Credential is empty.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return new(true);

            return new(false, $"http_{(int)response.StatusCode}", "Provider rejected the credential or request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "timeout", "Provider connection timed out.");
        }
        catch (HttpRequestException)
        {
            return new(false, "network_error", "Provider could not be reached.");
        }
    }

    public async Task<IntelligenceProviderResponse> ExecuteAsync(IntelligenceProviderRequest request, string credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("AI credential is not configured.");
        if (Encoding.UTF8.GetByteCount(request.InputJson) > MaximumInputBytes)
            throw new InvalidOperationException("AI input exceeds the provider safety limit.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["instructions"] = request.SystemInstruction,
            ["input"] = request.InputJson,
            ["store"] = false,
        };

        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            using var schemaDocument = JsonDocument.Parse(request.JsonSchema);
            payload["text"] = new Dictionary<string, object?>
            {
                ["format"] = new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["name"] = "fullworth_intelligence",
                    ["strict"] = true,
                    ["schema"] = schemaDocument.RootElement.Clone(),
                }
            };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "responses");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IntelligenceProviderException($"Provider request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is not ("completed" or null))
            throw new IntelligenceProviderException($"Provider response ended with status '{status.GetString()}'.");

        var outputText = TryReadOutputText(root) ?? throw new IntelligenceProviderException("Provider returned no text output.");
        long? inputTokens = null;
        long? outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt64(out var parsedInput)) inputTokens = parsedInput;
            if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt64(out var parsedOutput)) outputTokens = parsedOutput;
        }

        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        return new(outputText, inputTokens, outputTokens, requestId);
    }

    private static string? TryReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                    part.TryGetProperty("text", out var text))
                    return text.GetString();
            }
        }
        return null;
    }
}

public sealed class IntelligenceProviderException : Exception
{
    public IntelligenceProviderException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
    public int? StatusCode { get; }
}
