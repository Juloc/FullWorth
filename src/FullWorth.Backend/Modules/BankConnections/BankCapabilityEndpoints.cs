using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Was die angebundenen Institute koennen - Salden, Buchungen, vorgemerkte Posten, Fremdwaehrung,
/// wie weit die Historie reicht - plus die Institute, die geplant und noch nicht geprueft sind.
///
/// Lag in Modules/Parity/BankingExperienceParityModule.cs zusammen mit drei Routen zur Darstellung
/// von Konten. Vier Routen, zwei Fachbereiche, eine Datei: die drei anderen sind nach Accounts
/// gezogen, diese eine hierher.
/// </summary>
public static class BankCapabilityEndpoints
{
    private static readonly object[] PlannedInstitutions =
    [
        new { institutionKey = "dkb", provider = "enable-banking", displayName = "DKB", country = "DE", iconAssetKey = "dkb", validated = false },
        new { institutionKey = "ing", provider = "enable-banking", displayName = "ING", country = "DE", iconAssetKey = "ing", validated = false },
        new { institutionKey = "paypal", provider = "enable-banking", displayName = "PayPal", country = "DE", iconAssetKey = "paypal", validated = false },
        new { institutionKey = "c24", provider = "enable-banking", displayName = "C24 Bank", country = "DE", iconAssetKey = "c24", validated = false },
        new { institutionKey = "revolut", provider = "enable-banking", displayName = "Revolut", country = "LT", iconAssetKey = "revolut", validated = false }
    ];

    public static IEndpointRouteBuilder MapBankCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bank-capabilities", GetCapabilities).WithTags("Banking");
        return app;
    }

    private static async Task<IResult> GetCapabilities(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access,
        BankCapabilityStore store, CancellationToken ct)
    {
        if (!await access.IsMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct))
            return Results.NotFound();

        var rows = await store.ListAsync(ct);
        return Results.Ok(rows.Count == 0 ? PlannedInstitutions : rows);
    }
}
