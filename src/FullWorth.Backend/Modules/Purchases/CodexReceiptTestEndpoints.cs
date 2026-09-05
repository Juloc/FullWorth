using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Security;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Explicitly experimental receipt-Codex surface. It never persists a purchase or modifies the normal
/// receipt extraction provider. All calls are scoped to an authenticated FullWorth Space member and
/// proxy only to the internal FullWorth.CodexBridge container.
/// </summary>
public static class CodexReceiptTestEndpoints
{
    private const string Prefix = "/gpt-test";
    private const string BridgeScopeHeader = "X-FullWorth-Codex-Scope";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet($"{Prefix}/status", StatusAsync);
        group.MapPost($"{Prefix}/login", StartLoginAsync);
        group.MapGet($"{Prefix}/login/{{sessionId:guid}}", LoginStatusAsync);
        group.MapPost($"{Prefix}/logout", LogoutAsync);
        group.MapGet($"{Prefix}/models", ModelsAsync);
        group.MapGet($"{Prefix}/logs", LogsAsync);
        group.MapPost($"{Prefix}/scan", ScanAsync);
    }

    private static async Task<IResult> StatusAsync(
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Results.Ok(new { enabled = false, connected = false, statusText = "GPT receipt test mode is disabled." });
        return await ForwardAsync(HttpMethod.Get, "/status", null, BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> StartLoginAsync(
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        return await ForwardAsync(HttpMethod.Post, "/auth/start", "{}", BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> LoginStatusAsync(
        Guid sessionId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        return await ForwardAsync(HttpMethod.Get, $"/auth/{sessionId:D}", null, BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> LogoutAsync(
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        return await ForwardAsync(HttpMethod.Post, "/logout", "{}", BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> ModelsAsync(
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        return await ForwardAsync(HttpMethod.Get, "/models", null, BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> LogsAsync(
        Guid fullWorthSpaceId,
        int? limit,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        var safeLimit = Math.Clamp(limit ?? 500, 1, 2500);
        return await ForwardAsync(HttpMethod.Get, $"/logs/recent?limit={safeLimit}", null, BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId), configuration, clients, ct);
    }

    private static async Task<IResult> ScanAsync(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        CategoryStore categories,
        IOptions<PurchaseStorageOptions> storageOptions,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(currentUser, fullWorthSpaceId, authorization, ct)) return Results.NotFound();
        if (!Enabled(configuration)) return Disabled();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data is required." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("receipt") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0) return Results.BadRequest(new { error = "Receipt file is required." });
        if (file.Length > storageOptions.Value.MaxReceiptBytes)
            return Results.BadRequest(new { error = $"Receipt exceeds {storageOptions.Value.MaxReceiptBytes} bytes." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".pdf")
            return Results.BadRequest(new { error = "GPT test mode accepts JPEG, PNG, WebP or PDF." });

        // Match the normal receipt upload security boundary: never hand mislabeled arbitrary bytes to
        // Codex/Poppler based only on a browser-provided filename or MIME type.
        var header = new byte[16];
        int headerRead;
        await using (var probe = file.OpenReadStream())
            headerRead = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (!ReceiptSignature.Matches(header.AsSpan(0, headerRead), extension))
            return Results.BadRequest(new { error = "Receipt file content does not match its type." });

        var categoryResult = await categories.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
        if (!categoryResult.Found) return Results.NotFound();
        var categoryPaths = BuildCategoryPaths(categoryResult.Items ?? []);

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(buffer, ct);

        var model = form["model"].FirstOrDefault();
        var payload = JsonSerializer.Serialize(new
        {
            fileName = Path.GetFileName(file.FileName),
            contentType = ContentType(extension),
            dataBase64 = Convert.ToBase64String(buffer.ToArray()),
            model = string.IsNullOrWhiteSpace(model) || string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase) ? null : model.Trim(),
            categories = categoryPaths
        });

        // A failed Codex extraction still returns HTTP 200 here so the explicit debug UI can render
        // the bridge's full structured failure payload instead of reducing it to a generic toast.
        return await ForwardAsync(
            HttpMethod.Post,
            "/scan",
            payload,
            BridgeScope(currentUser.RequireUserId(), fullWorthSpaceId),
            configuration,
            clients,
            ct,
            normalizeFailureForDebug: true);
    }

    private static IReadOnlyList<string> BuildCategoryPaths(IReadOnlyList<FinanceCategory> items)
    {
        var byId = items.ToDictionary(x => x.Id);
        string Build(FinanceCategory category)
        {
            var names = new Stack<string>();
            var seen = new HashSet<Guid>();
            FinanceCategory? current = category;
            while (current is not null && seen.Add(current.Id))
            {
                names.Push(current.Name.Trim());
                current = current.ParentId.HasValue && byId.TryGetValue(current.ParentId.Value, out var parent) ? parent : null;
            }
            return string.Join(" > ", names.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return items
            .Where(x => !x.IsArchived)
            .Select(Build)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Enabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("CodexTest:Enabled");

    private static IResult Disabled() => Results.NotFound(new { error = "GPT receipt test mode is disabled." });

    private static async Task<bool> IsMemberAsync(
        CurrentUserContext currentUser,
        Guid fullWorthSpaceId,
        PurchaseAuthorizationStore authorization,
        CancellationToken ct) =>
        await authorization.IsFullWorthSpaceMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);

    // The browser never sees this value. It gives the shared internal sidecar a stable opaque namespace
    // for one FullWorth user. Space membership is still authorized by the Backend endpoint, while the
    // AI login follows the user across their FullWorth Spaces.
    private static string BridgeScope(Guid userId, Guid fullWorthSpaceId)
    {
        _ = fullWorthSpaceId; // authorization stays space-scoped; AI login itself is user-scoped.
        var input = Encoding.UTF8.GetBytes($"fullworth-ai:{userId:N}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string ContentType(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private static async Task<IResult> ForwardAsync(
        HttpMethod method,
        string path,
        string? json,
        string bridgeScope,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct,
        bool normalizeFailureForDebug = false)
    {
        var baseUrl = (configuration["CodexTest:BaseUrl"] ?? "http://fullworth-codex:8080").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttp)
            return Results.Problem("Codex test bridge URL is invalid.", statusCode: StatusCodes.Status503ServiceUnavailable);

        // Deliberately separate from Security:InternalKey. The Codex sidecar processes untrusted files
        // and must never hold the key that can establish trusted Finance backend user context.
        var bridgeKey = configuration["CodexTest:BridgeKey"];
        if (string.IsNullOrWhiteSpace(bridgeKey))
            return Results.Problem("Codex test bridge key is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            using var message = new HttpRequestMessage(method, new Uri(baseUri, path));
            message.Headers.Add("X-FullWorth-Internal-Key", bridgeKey);
            message.Headers.Add(BridgeScopeHeader, bridgeScope);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json is not null)
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = clients.CreateClient();
            // A Codex run may intentionally take up to four minutes; IHttpClientFactory's unnamed
            // client otherwise inherits HttpClient's ~100 s timeout and aborts valid long scans.
            client.Timeout = TimeSpan.FromMinutes(5);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = normalizeFailureForDebug ? StatusCodes.Status200OK : (int)response.StatusCode;
            return Results.Content(
                string.IsNullOrWhiteSpace(body) ? "{}" : body,
                "application/json; charset=utf-8",
                Encoding.UTF8,
                status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem("Codex test bridge timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem($"Codex test bridge unavailable: {exception.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
