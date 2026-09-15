using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record ProductIdentityWrite(
    string CanonicalName, string? Brand, string? Barcode, Guid? DefaultCategoryId,
    string? UnitKind, decimal? UnitSize);
public sealed record ProductIdentityAliasWrite(string Text, decimal? Confidence = 1m, string Source = "manual");
public sealed record ProductItemLinkWrite(Guid ProductIdentityId, decimal? Confidence = 1m, string Source = "manual");

/// <summary>
/// Compatibility facade for the parity UI. ProductIdentities was a temporary parallel model; all reads
/// and writes now target the canonical Products/ProductAliases/ProductBarcodes/PurchaseItem.ProductId
/// model so there is one product identity throughout FullWorth.
/// </summary>
public static class ProductIdentityParityEndpoints
{
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    { "piece", "g", "kg", "ml", "l", "m", "cm", "wash_load", "tablet" };

    public static IEndpointRouteBuilder MapProductIdentityParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product-identities").WithTags("Purchases");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Archive);
        group.MapPost("/{id:guid}/aliases", AddAlias);
        group.MapDelete("/{id:guid}/aliases/{aliasId:guid}", DeleteAlias);
        group.MapGet("/{id:guid}/history", History);
        group.MapGet("/suggest", Suggest);
        group.MapPut("/purchase-items/{purchaseItemId:guid}", LinkItem);
        group.MapDelete("/purchase-items/{purchaseItemId:guid}", UnlinkItem);
        return app;
    }

    private static async Task<IResult> List(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, ProductIdentityStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListAsync(fullWorthSpaceId, ct))
            .Select(row => new
            {
                id = row.Id,
                canonicalName = row.CanonicalName,
                brand = row.Brand,
                barcode = row.Barcode,
                defaultCategoryId = row.DefaultCategoryId,
                unitKind = row.UnitKind,
                unitSize = row.UnitSize,
                aliasCount = row.AliasCount
            });
        return Results.Ok(rows);
    }

    private static Task<IResult> Create(
        Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, CancellationToken ct) =>
        Write(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, space, store, false, ct);

    private static Task<IResult> Update(
        Guid id, Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ProductIdentityStore store, CancellationToken ct) =>
        Write(id, fullWorthSpaceId, request, currentUser, space, store, true, ct);

    private static async Task<IResult> Write(
        Guid id, Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ProductIdentityStore store, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.CanonicalName))
            return Results.BadRequest(new { error = "Product name is required." });

        var unit = NormalizeUnit(request.UnitKind);
        if (unit is not null && !Units.Contains(unit))
            return Results.BadRequest(new { error = "Unsupported product unit." });
        if (request.UnitSize is <= 0) return Results.BadRequest(new { error = "Unit size must be positive." });
        if (request.DefaultCategoryId.HasValue
            && !await store.CategoryUsableAsync(fullWorthSpaceId, request.DefaultCategoryId.Value, ct))
            return Results.BadRequest(new { error = "Category is invalid." });

        var barcode = Clean(request.Barcode);
        if (barcode is not null && await store.BarcodeTakenAsync(barcode, id, ct))
            return Results.Conflict(new { error = "This barcode is already linked to another product." });

        return await store.SaveAsync(userId, fullWorthSpaceId, id, request, unit, barcode, update, ct)
            ? Results.Ok(new { id })
            : Results.NotFound();
    }

    private static async Task<IResult> Archive(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.ArchiveAsync(userId, fullWorthSpaceId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> AddAlias(
        Guid id, Guid fullWorthSpaceId, ProductIdentityAliasWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ProductIdentityStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.ExistsAsync(fullWorthSpaceId, id, ct)) return Results.NotFound();

        var alias = Clean(request.Text);
        if (alias is null) return Results.BadRequest(new { error = "Alias is required." });
        var normalized = Normalize(alias);
        if (normalized.Length < 2) return Results.BadRequest(new { error = "Alias is too short." });

        await store.SaveAliasAsync(userId, fullWorthSpaceId, id, alias, normalized,
            NormalizeSource(request.Source), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAlias(
        Guid id, Guid aliasId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.DeleteAliasAsync(userId, fullWorthSpaceId, id, aliasId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> Suggest(
        Guid fullWorthSpaceId, string text, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var normalized = Normalize(text);
        if (normalized.Length < 2) return Results.Ok(null);

        // Eine hinterlegte Schreibweise schlaegt den Produktnamen: sie ist die Entscheidung eines
        // Menschen, der Namensvergleich nur eine Aehnlichkeit.
        var match = await store.FindByAliasAsync(fullWorthSpaceId, normalized, ct)
            ?? await store.FindByCanonicalNameAsync(fullWorthSpaceId, normalized, ct);

        return match is null
            ? Results.Ok(null)
            : Results.Ok(new
            {
                id = match.Id,
                canonicalName = match.CanonicalName,
                brand = match.Brand,
                defaultCategoryId = match.DefaultCategoryId,
                confidence = (decimal?)1m,
                source = match.Source
            });
    }

    private static async Task<IResult> History(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)
            || !await store.ExistsAsync(fullWorthSpaceId, id, ct))
            return Results.NotFound();

        var rows = (await store.HistoryAsync(fullWorthSpaceId, userId, id, ct))
            .Select(row => new
            {
                purchaseItemId = row.PurchaseItemId,
                purchaseDate = row.PurchaseDate,
                merchant = row.Merchant,
                quantity = row.Quantity,
                packageQuantity = row.PackageQuantity,
                packageUnit = row.PackageUnit,
                total = row.Total,
                currency = row.Currency,
                comparableUnitPrice = row.ComparableUnitPrice,
                comparisonSafe = row.ComparisonSafe
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> LinkItem(
        Guid purchaseItemId, Guid fullWorthSpaceId, ProductItemLinkWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ProductIdentityStore store, PurchaseAuthorizationStore purchases, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.ExistsAsync(fullWorthSpaceId, request.ProductIdentityId, ct))
            return Results.BadRequest(new { error = "Product is invalid." });
        if (!await MayWriteItemAsync(userId, fullWorthSpaceId, purchaseItemId, store, purchases, ct))
            return Results.NotFound();

        await store.SetItemProductAsync(userId, fullWorthSpaceId, purchaseItemId, request.ProductIdentityId,
            "purchase.item.product.linked", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlinkItem(
        Guid purchaseItemId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductIdentityStore store, PurchaseAuthorizationStore purchases, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await MayWriteItemAsync(userId, fullWorthSpaceId, purchaseItemId, store, purchases, ct))
            return Results.NotFound();

        await store.SetItemProductAsync(userId, fullWorthSpaceId, purchaseItemId, null,
            "purchase.item.product.unlinked", ct);
        return Results.NoContent();
    }

    /// <summary>Das Recht haengt am Kauf, zu dem die Position gehoert, nicht am Produkt.</summary>
    private static async Task<bool> MayWriteItemAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseItemId, ProductIdentityStore store,
        PurchaseAuthorizationStore purchases, CancellationToken ct) =>
        await store.PurchaseOfItemAsync(purchaseItemId, ct) is { } purchaseId
        && await purchases.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) == PurchaseAccessLevel.Write;

    private static string Normalize(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeUnit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string NormalizeSource(string? value) => string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim().ToLowerInvariant()[..Math.Min(32, value.Trim().Length)];
}
