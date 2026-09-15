using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Budgets.Suggestions;

/// <summary>
/// Die beiden lesenden Wege des Budget-Assistenten (#115).
///
/// Beide sind POST, weil die Auswahl eine Liste von Kategorien ist und in keine sinnvolle Adresse
/// passt - geschrieben wird trotzdem nichts. Angelegt wird ein Budget erst ueber die vorhandenen
/// Wege, nach der Bestaetigung des Benutzers.
/// </summary>
public static class BudgetSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapBudgetSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budget-suggestions").WithTags("Budgets");

        group.MapPost("/", async (
            Guid fullWorthSpaceId, BudgetSuggestionRequest request,
            CurrentUserContext currentUser, BudgetSuggestionStore store, CancellationToken ct) =>
            Results.Ok(await store.SuggestAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPost("/preview", async (
            Guid fullWorthSpaceId, BudgetPreviewRequest request,
            CurrentUserContext currentUser, BudgetSuggestionStore store, CancellationToken ct) =>
            Results.Ok(await store.PreviewAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        return app;
    }
}
