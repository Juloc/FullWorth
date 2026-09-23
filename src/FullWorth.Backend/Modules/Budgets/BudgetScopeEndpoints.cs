using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Budgets;

public sealed record BudgetScopeWrite(
    IReadOnlyList<CategoryScopeWrite>? Categories,
    IReadOnlyList<Guid>? AccountIds,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<string>? Merchants,
    Guid? IncomeScheduleId,
    decimal AlertNearPercent = 80,
    decimal AlertCriticalPercent = 100,
    Guid? GroupId = null);

public sealed record BudgetGroupWrite(string Name, int SortOrder);

/// <summary>
/// Worauf ein Budget zaehlt, und wie viel davon schon verbraucht ist.
///
/// Hier steht die Rechnung, nicht die Abfrage: welche Buchung in ein Budget faellt, wie sich eine
/// Erstattung auswirkt, und wann welches Zeitfenster gilt.
/// </summary>
public static class BudgetScopeEndpoints
{
    /// <summary>Ab drei gemeinsamen Dimensionen ist es eine echte Ueberschneidung und kein Zufall.</summary>
    private const int OverlapDimensions = 3;

    public static IEndpointRouteBuilder MapBudgetScopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budget-scopes").WithTags("Budgets");
        group.MapGet("/{budgetId:guid}", GetScope);
        group.MapPut("/{budgetId:guid}", PutScope);
        group.MapGet("/{budgetId:guid}/status", GetScopedStatus);
        group.MapGet("/{budgetId:guid}/overlaps", GetOverlaps);

        var groups = app.MapGroup("/api/budget-groups").WithTags("Budgets");
        groups.MapGet("/", ListGroups);
        groups.MapPost("/", CreateGroup);
        groups.MapPut("/{id:guid}", UpdateGroup);
        groups.MapDelete("/{id:guid}", ArchiveGroup);
        return app;
    }

    private static async Task<IResult> GetScope(
        Guid budgetId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        BudgetScopeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await store.BudgetExistsAsync(fullWorthSpaceId, budgetId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        return Results.Ok(await RedactAsync(store, await store.LoadScopeAsync(budgetId, ct), visible, ct));
    }

    private static async Task<IResult> PutScope(
        Guid budgetId, Guid fullWorthSpaceId, BudgetScopeWrite request, CurrentUserContext currentUser,
        SpaceAccess space, BudgetScopeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "budgets.manage", ct))
            return Results.StatusCode(403);
        if (!await store.BudgetExistsAsync(fullWorthSpaceId, budgetId, ct)) return Results.NotFound();

        var categories = (request.Categories ?? []).DistinctBy(category => category.CategoryId).ToArray();
        var accounts = (request.AccountIds ?? []).Distinct().ToArray();
        var tags = (request.TagIds ?? []).Distinct().ToArray();
        var merchants = (request.Merchants ?? []).Select(MerchantNormalization.Normalize)
            .Where(merchant => !string.IsNullOrWhiteSpace(merchant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(merchant => merchant!)
            .ToArray();

        if (request.AlertNearPercent < 0 || request.AlertCriticalPercent < request.AlertNearPercent)
            return Results.BadRequest(new { error = "Invalid budget thresholds." });
        if (categories.Length > 0 &&
            await store.CountCategoriesAsync(fullWorthSpaceId, categories.Select(category => category.CategoryId).ToArray(), ct) != categories.Length)
            return Results.BadRequest(new { error = "Budget category scope contains an invalid category." });

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        if (accounts.Any(id => !visible.Contains(id)))
            return Results.BadRequest(new { error = "Budget account scope contains an inaccessible account." });
        if (!await store.TagsExistAsync(fullWorthSpaceId, tags, ct))
            return Results.BadRequest(new { error = "Budget tag scope contains an invalid tag." });
        if (request.IncomeScheduleId.HasValue &&
            !await store.CanReadIncomeScheduleAsync(fullWorthSpaceId, request.IncomeScheduleId.Value, visible, ct))
            return Results.BadRequest(new { error = "Income schedule is invalid or inaccessible." });
        if (request.GroupId.HasValue && !await store.ActiveGroupExistsAsync(fullWorthSpaceId, request.GroupId.Value, ct))
            return Results.BadRequest(new { error = "Budget group is invalid." });

        await store.SaveScopeAsync(userId, fullWorthSpaceId, budgetId, categories, accounts, tags, merchants,
            request.IncomeScheduleId, request.AlertNearPercent, request.AlertCriticalPercent, request.GroupId, ct);

        return Results.Ok(await RedactAsync(store, await store.LoadScopeAsync(budgetId, ct), visible, ct));
    }

    /// <summary>
    /// Derselbe Stand wie auf der Budgetseite - aus <see cref="BudgetReconciliationService"/>.
    ///
    /// Hier stand bis 2026-09-23 eine eigene Rechnung ueber 115 Zeilen: Fenster aufloesen, Buchungen
    /// laden, Bereich anwenden, summieren, hochrechnen. Sie lief nie. Eine Middleware fing die Route
    /// vor der Zuordnung ab und antwortete aus dem Dienst, der jetzt hier steht - und der kann
    /// ausserdem, was diese Fassung nicht konnte: Erstattungen gegenrechnen, Fremdwaehrung als
    /// unvollstaendig kennzeichnen und den Uebertrag aus der Vorperiode beruecksichtigen.
    /// </summary>
    private static async Task<IResult> GetScopedStatus(
        Guid budgetId, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        BudgetReconciliationService budgets, CancellationToken ct)
    {
        var status = await budgets.GetStatusAsync(currentUser.RequireUserId(), fullWorthSpaceId, budgetId, asOf, ct);
        if (status is null) return Results.NotFound();
        return Results.Ok(new
        {
            status.BudgetId,
            status.Name,
            amount = status.BudgetAmount,
            status.Currency,
            status.PeriodStart,
            status.PeriodEnd,
            status.Spent,
            status.Remaining,
            status.PercentUsed,
            status.ProjectedEndSpend,
            status.ProjectedOverUnder,
            status.PartialAccess,
            incompleteFx = status.IncompleteFx,
            status.BaseBudgetAmount,
            status.CarryIn,
            status.CarryOver,
            status.CarryOverOverspend,
            status.Contributing
        });
    }

    /// <summary>
    /// Zwei Budgets ueberschneiden sich, wenn sie in mindestens drei Dimensionen dieselben Buchungen
    /// treffen koennten. Eine leere Dimension gilt als "alles" - ein Budget ohne Kontofilter faengt
    /// auch die Buchungen des anderen.
    /// </summary>
    private static async Task<IResult> GetOverlaps(
        Guid budgetId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        BudgetScopeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var target = await RedactAsync(store, await store.LoadScopeAsync(budgetId, ct), visible, ct);
        var others = await store.OtherActiveBudgetsAsync(fullWorthSpaceId, budgetId, ct);

        var overlaps = new List<object>();
        foreach (var other in others)
        {
            var scope = await RedactAsync(store, await store.LoadScopeAsync(other.Id, ct), visible, ct);
            var dimensions = new List<string>();
            if (IntersectsOrAll(target.Categories.Select(category => category.CategoryId), scope.Categories.Select(category => category.CategoryId)))
                dimensions.Add("categories");
            if (target.AccountIds.Count > 0 && scope.AccountIds.Count > 0 && IntersectsOrAll(target.AccountIds, scope.AccountIds))
                dimensions.Add("accounts");
            if (IntersectsOrAll(target.TagIds, scope.TagIds)) dimensions.Add("tags");
            if (IntersectsOrAll(target.Merchants, scope.Merchants, StringComparer.OrdinalIgnoreCase)) dimensions.Add("merchants");

            if (dimensions.Count >= OverlapDimensions)
                overlaps.Add(new
                {
                    budgetId = other.Id,
                    other.Name,
                    dimensions,
                    partialAccess = target.PartialAccess || scope.PartialAccess
                });
        }
        return Results.Ok(overlaps);
    }

    private static async Task<IResult> ListGroups(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        BudgetScopeStore store, CancellationToken ct)
    {
        if (!await space.IsMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct))
            return Results.NotFound();

        var rows = await store.ListGroupsAsync(fullWorthSpaceId, ct);
        return Results.Ok(rows.Select(row => new
        {
            id = row.Id,
            name = row.Name,
            sortOrder = row.SortOrder,
            isArchived = row.IsArchived
        }));
    }

    private static async Task<IResult> CreateGroup(
        Guid fullWorthSpaceId, BudgetGroupWrite request, CurrentUserContext currentUser, SpaceAccess space,
        BudgetScopeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "budgets.manage", ct))
            return Results.StatusCode(403);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest();

        return Results.Ok(new
        {
            id = await store.CreateGroupAsync(userId, fullWorthSpaceId, request.Name.Trim(), request.SortOrder, ct)
        });
    }

    private static Task<IResult> UpdateGroup(
        Guid id, Guid fullWorthSpaceId, BudgetGroupWrite request, CurrentUserContext currentUser,
        SpaceAccess space, BudgetScopeStore store, CancellationToken ct) =>
        WriteGroupAsync(id, fullWorthSpaceId, request, currentUser, space, store, archive: false, ct);

    private static Task<IResult> ArchiveGroup(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        BudgetScopeStore store, CancellationToken ct) =>
        WriteGroupAsync(id, fullWorthSpaceId, new BudgetGroupWrite("", 0), currentUser, space, store, archive: true, ct);

    private static async Task<IResult> WriteGroupAsync(
        Guid id, Guid fullWorthSpaceId, BudgetGroupWrite request, CurrentUserContext currentUser,
        SpaceAccess space, BudgetScopeStore store, bool archive, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "budgets.manage", ct))
            return Results.StatusCode(403);
        if (!archive && string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest();

        return await store.WriteGroupAsync(userId, fullWorthSpaceId, id, request.Name.Trim(), request.SortOrder, archive, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    /// <summary>
    /// Was der Fragende vom Bereich sehen darf. Konten, die ihm nicht gehoeren, verschwinden aus der
    /// Antwort - und <c>PartialAccess</c> sagt ihm, dass er nicht alles sieht, statt ihn glauben zu
    /// lassen, der Bereich sei kleiner als er ist.
    /// </summary>
    private static async Task<LoadedScope> RedactAsync(
        BudgetScopeStore store, LoadedScope scope, IReadOnlySet<Guid> visible, CancellationToken ct)
    {
        var visibleAccounts = scope.AccountIds.Where(visible.Contains).ToList();
        var partial = visibleAccounts.Count != scope.AccountIds.Count;

        var income = scope.IncomeScheduleId;
        if (income.HasValue && !await store.CanReadIncomeScheduleAsync(Guid.Empty, income.Value, visible, ct))
        {
            income = null;
            partial = true;
        }
        return scope with { AccountIds = visibleAccounts, IncomeScheduleId = income, PartialAccess = partial };
    }
    /// <summary>Eine leere Menge heisst "keine Einschraenkung" und trifft damit auf alles zu.</summary>
    private static bool IntersectsOrAll<T>(IEnumerable<T> left, IEnumerable<T> right, IEqualityComparer<T>? comparer = null)
    {
        var a = left.ToArray();
        var b = right.ToArray();
        if (a.Length == 0 || b.Length == 0) return true;
        var set = new HashSet<T>(a, comparer ?? EqualityComparer<T>.Default);
        return b.Any(set.Contains);
    }
}
