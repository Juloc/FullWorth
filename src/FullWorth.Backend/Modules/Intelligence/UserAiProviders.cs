using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record OpenAiCompatibleCredential(
    string BaseUrl,
    string AuthType,
    string? Username,
    string? Secret);

public static class OpenAiCompatibleCredentialCodec
{
    public static string Encode(OpenAiCompatibleCredential credential)
    {
        Validate(credential);
        return JsonSerializer.Serialize(credential);
    }

    public static OpenAiCompatibleCredential Decode(string value)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OpenAiCompatibleCredential>(value)
                ?? throw new ArgumentException("Custom AI credential is invalid.");
            Validate(parsed);
            return parsed;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Custom AI credential is invalid.", ex);
        }
    }

    public static void Validate(OpenAiCompatibleCredential credential)
    {
        if (credential.BaseUrl.Length is < 8 or > 2048 ||
            !Uri.TryCreate(credential.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Custom AI endpoint must be an absolute http(s) base URL without embedded credentials, query or fragment.");

        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (host is "metadata.google.internal" or "metadata" or "instance-data.ec2.internal")
            throw new ArgumentException("Cloud metadata endpoints are not allowed as custom AI endpoints.");
        if (IPAddress.TryParse(host, out var address) && IsBlockedAddress(address))
            throw new ArgumentException("Link-local, multicast and cloud metadata addresses are not allowed as custom AI endpoints.");

        var authType = credential.AuthType.Trim().ToLowerInvariant();
        if (authType is not ("bearer" or "basic" or "none"))
            throw new ArgumentException("Custom AI auth type must be bearer, basic or none.");

        if (authType == "bearer" && string.IsNullOrWhiteSpace(credential.Secret))
            throw new ArgumentException("Bearer authentication requires a token.");
        if (authType == "basic" &&
            (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrWhiteSpace(credential.Secret)))
            throw new ArgumentException("Basic authentication requires username and password.");
        if ((credential.Username?.Length ?? 0) > 256 || (credential.Secret?.Length ?? 0) > 8192)
            throw new ArgumentException("Custom AI credentials exceed safe limits.");
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 100 && bytes[1] == 100 && bytes[2] == 100 && bytes[3] == 200) return true;
            return false;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast;
    }
}

public sealed class OpenAiCompatibleIntelligenceProvider(IHttpClientFactory clients) : IIntelligenceProvider
{
    private const int MaximumInputBytes = 2 * 1024 * 1024;

    public IntelligenceProviderDescriptor Descriptor { get; } = new(
        IntelligenceProviders.OpenAiCompatible,
        IntelligenceProviderCapabilities.TextClassification |
        IntelligenceProviderCapabilities.StructuredExtraction,
        MaximumInputBytes,
        ReportsUsage: true);

    public async Task<IntelligenceProviderTestResult> TestCredentialAsync(
        string credential,
        CancellationToken cancellationToken)
    {
        OpenAiCompatibleCredential config;
        try { config = OpenAiCompatibleCredentialCodec.Decode(credential); }
        catch (ArgumentException ex) { return new(false, "credential_invalid", ex.Message); }

        using var request = BuildRequest(HttpMethod.Get, config, "models");
        try
        {
            using var response = await clients.CreateClient().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode
                ? new(true)
                : new(false, $"http_{(int)response.StatusCode}", "Custom AI endpoint rejected the credentials or request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "timeout", "Custom AI endpoint timed out.");
        }
        catch (HttpRequestException)
        {
            return new(false, "network_error", "Custom AI endpoint could not be reached.");
        }
    }

    public async Task<IntelligenceProviderResponse> ExecuteAsync(
        IntelligenceProviderRequest request,
        string credential,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(request.InputJson) > MaximumInputBytes)
            throw new InvalidOperationException("AI input exceeds the provider safety limit.");

        var config = OpenAiCompatibleCredentialCodec.Decode(credential);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["instructions"] = request.SystemInstruction,
            ["input"] = request.InputJson,
            ["store"] = false
        };
        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            using var schema = JsonDocument.Parse(request.JsonSchema);
            payload["text"] = new Dictionary<string, object?>
            {
                ["format"] = new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["name"] = "fullworth_intelligence",
                    ["strict"] = true,
                    ["schema"] = schema.RootElement.Clone()
                }
            };
        }

        using var httpRequest = BuildRequest(HttpMethod.Post, config, "responses");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await clients.CreateClient().SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IntelligenceProviderException(
                $"Custom AI provider request failed with HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var outputText = TryReadOutputText(root)
            ?? throw new IntelligenceProviderException("Custom AI provider returned no text output.");

        long? inputTokens = null;
        long? outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt64(out var parsedInput))
                inputTokens = parsedInput;
            if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt64(out var parsedOutput))
                outputTokens = parsedOutput;
        }

        var requestId = response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        return new(outputText, inputTokens, outputTokens, requestId);
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        OpenAiCompatibleCredential credential,
        string relativePath)
    {
        var baseUri = new Uri(credential.BaseUrl.TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        switch (credential.AuthType.Trim().ToLowerInvariant())
        {
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
                break;
            case "basic":
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{credential.Username}:{credential.Secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                break;
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? TryReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text" &&
                    part.TryGetProperty("text", out var text))
                    return text.GetString();
        }
        return null;
    }
}

public sealed class CodexBridgeIntelligenceProvider(
    IConfiguration configuration,
    IHttpClientFactory clients) : IIntelligenceProvider
{
    private const int MaximumInputBytes = 2 * 1024 * 1024;

    public IntelligenceProviderDescriptor Descriptor { get; } = new(
        IntelligenceProviders.Codex,
        IntelligenceProviderCapabilities.TextClassification |
        IntelligenceProviderCapabilities.StructuredExtraction,
        MaximumInputBytes,
        ReportsUsage: false);

    public async Task<IntelligenceProviderTestResult> TestCredentialAsync(
        string credential,
        CancellationToken cancellationToken)
    {
        if (!ValidScope(credential))
            return new(false, "credential_invalid", "Codex user scope is invalid.");

        var response = await SendAsync(HttpMethod.Get, "/status", credential, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(false, $"http_{(int)response.StatusCode}", "Codex bridge could not validate the login.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("connected", out var connected) &&
               connected.ValueKind == JsonValueKind.True
            ? new(true)
            : new(false, "not_logged_in", "Codex is not logged in.");
    }

    public async Task<IntelligenceProviderResponse> ExecuteAsync(
        IntelligenceProviderRequest request,
        string credential,
        CancellationToken cancellationToken)
    {
        if (!ValidScope(credential))
            throw new InvalidOperationException("Codex user scope is invalid.");
        if (Encoding.UTF8.GetByteCount(request.InputJson) > MaximumInputBytes)
            throw new InvalidOperationException("AI input exceeds the provider safety limit.");

        var payload = JsonSerializer.Serialize(new
        {
            model = request.Model,
            capability = request.Capability,
            systemInstruction = request.SystemInstruction,
            inputJson = request.InputJson,
            jsonSchema = request.JsonSchema
        });

        using var response = await SendAsync(
            HttpMethod.Post,
            "/execute",
            credential,
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IntelligenceProviderException(
                $"Codex bridge request failed with HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new IntelligenceProviderException(
                root.TryGetProperty("error", out var error)
                    ? error.GetString() ?? "Codex execution failed."
                    : "Codex execution failed.");

        var output = root.TryGetProperty("outputJson", out var outputJson)
            ? outputJson.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(output))
            throw new IntelligenceProviderException("Codex returned no output.");

        return new(
            output,
            null,
            null,
            root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string ownerScope,
        string? json,
        CancellationToken ct)
    {
        var baseUrl = (configuration["AiAccess:CodexBridgeBaseUrl"] ??
                       configuration["CodexTest:BaseUrl"] ??
                       "http://fullworth-codex:8080").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttp)
            throw new IntelligenceProviderException("Codex bridge URL is invalid.");

        var key = configuration["AiAccess:CodexBridgeKey"] ??
                  configuration["CodexTest:BridgeKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new IntelligenceProviderException("Codex bridge key is unavailable.");

        var message = new HttpRequestMessage(method, new Uri(baseUri, path));
        message.Headers.Add("X-FullWorth-Internal-Key", key);
        message.Headers.Add("X-FullWorth-Codex-Scope", ownerScope);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await clients.CreateClient().SendAsync(
                message,
                HttpCompletionOption.ResponseContentRead,
                ct);
            message.Dispose();
            return response;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    public static string ScopeForUser(Guid userId)
    {
        var bytes = Encoding.UTF8.GetBytes($"fullworth-ai:{userId:N}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private static bool ValidScope(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
