using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Transactions;

public static class AccountIdentifierLookup
{
    public static string? Create(string? identifier, FieldCipher cipher)
    {
        var normalized = Normalize(identifier);
        if (normalized is null) return null;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return cipher.BlindIndex(digest);
    }

    public static string? Last4(string? identifier)
    {
        var normalized = Normalize(identifier);
        return normalized is { Length: >= 4 } ? normalized[^4..] : normalized;
    }

    private static string? Normalize(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        var normalized = new string(identifier.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/transfers/detect", async (
            Guid fullWorthSpaceId,
            bool? apply,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.DetectForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, apply ?? false, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Summary),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");

        app.MapGet("/api/transfers/candidates", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.CandidatesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Pairs),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");
        return app;
    }
}
