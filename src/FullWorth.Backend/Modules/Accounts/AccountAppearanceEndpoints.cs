using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Accounts;

public sealed record AccountAppearanceWrite(string? Icon, string? IconColor, string? BackgroundColor);

/// <summary>Aussehen und Gesehen-Status eines Kontos.
///
/// Kam aus Parity/ExperienceParityModule: eine Datei mit vier Fachbereichen - Konten, Produktwissen,
/// Faehigkeitsfreigaben und einem Tabellenexport. Jeder hat jetzt seinen eigenen Ort.</summary>
public static class AccountAppearanceEndpoints
{
    public static IEndpointRouteBuilder MapAccountAppearanceEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/account-experience").WithTags("Accounts");
        accounts.MapGet("/", AccountExperience);
        accounts.MapPut("/{accountId:guid}/appearance", PutAccountAppearance);
        accounts.MapPost("/{accountId:guid}/seen", MarkSeen);
        return app;
    }

    private static async Task<IResult> AccountExperience(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        AccountAppearanceStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        if (visible.Count == 0) return Results.Ok(Array.Empty<object>());

        var accounts = await store.ListAccountsAsync(visible, ct);
        var ids = accounts.Select(account => account.Id).ToArray();
        var appearances = await store.AppearancesAsync(ids, ct);
        var lastSeen = await store.LastSeenAsync(userId, ids, ct);
        var unseen = await store.UnseenCountsAsync(ids, lastSeen, ct);

        return Results.Ok(accounts.Select(account =>
        {
            var appearance = appearances.GetValueOrDefault(account.Id);
            return new
            {
                accountId = account.Id,
                account.DisplayName,
                account.InstitutionName,
                account.Provider,
                account.AccountType,
                account.Product,
                icon = appearance?.Icon,
                iconColor = appearance?.IconColor,
                backgroundColor = appearance?.BackgroundColor,
                unseenTransactions = unseen.GetValueOrDefault(account.Id),
                lastSeenAt = lastSeen.TryGetValue(account.Id, out var seen) ? seen : (DateTimeOffset?)null
            };
        }));
    }

    private static async Task<IResult> PutAccountAppearance(
        Guid accountId, Guid fullWorthSpaceId, AccountAppearanceWrite request,
        CurrentUserContext currentUser, SpaceAccess space, AccountAppearanceStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        if (!writable.Contains(accountId)) return Results.NotFound();
        if (!ValidColor(request.IconColor) || !ValidColor(request.BackgroundColor))
            return Results.BadRequest(new { error = "Colors must be #RRGGBB or #RRGGBBAA." });

        await store.SetAppearanceAsync(userId, fullWorthSpaceId, accountId, new AccountAppearance(
            string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim(),
            NormalizeColor(request.IconColor),
            NormalizeColor(request.BackgroundColor)), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkSeen(
        Guid accountId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        AccountAppearanceStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        if (!visible.Contains(accountId)) return Results.NotFound();

        await store.MarkSeenAsync(userId, accountId, ct);
        return Results.NoContent();
    }

    private static bool ValidColor(string? value) => string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith('#') && (value.Length == 7 || value.Length == 9) && value.Skip(1).All(Uri.IsHexDigit));
    private static string? NormalizeColor(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
