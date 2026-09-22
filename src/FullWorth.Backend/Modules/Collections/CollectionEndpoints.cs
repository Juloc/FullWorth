using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Collections;

public sealed record CollectionWrite(
    string Name, string? Description, string? Icon, string? Color,
    DateOnly? StartDate, DateOnly? EndDate, string? Status);

public sealed record CollectionTransactionsWrite(IReadOnlyList<Guid> TransactionIds);

/// <summary>
/// Sammlungen: „wofuer gehoerte das zusammen?" (#124)
///
/// Die zweite Zuordnungsachse neben der Kategorie. Eine Buchung kann keiner, einer oder mehreren
/// Sammlungen angehoeren; eine Sammlung aendert nie die Kategorie, nie den Betrag und nie die
/// Buchung selbst. Sie loeschen loescht keine Buchung, nur die Zuordnung.
///
/// Es gibt bewusst KEINE Route, die ueber mehrere Sammlungen summiert. Sammlungen ueberschneiden
/// sich - dieselbe Bauhaus-Buchung gehoert zu „Wohnung" UND zu „Badrenovierung" - und eine Summe
/// darueber waere doppelt gezaehlt. Jede Zahl hier gehoert zu genau einer Sammlung.
/// </summary>
public static class CollectionEndpoints
{
    /// <summary>Mehr Buchungen auf einmal zuzuordnen ist keine Auswahl mehr, sondern ein Import.</summary>
    private const int MaxBulk = 1000;

    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collections").WithTags("Collections");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Detail);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/transactions", AddTransactions);
        // Kein DELETE mit Rumpf: minimal APIs leiten fuer DELETE keinen Body ab, und die Anwendung
        // startet dann gar nicht erst ("Body was inferred but the method does not allow inferred body
        // parameters"). Eine Liste von Buchungen gehoert in den Rumpf, also ist es ein POST.
        group.MapPost("/{id:guid}/transactions/remove", RemoveTransactions);
        group.MapGet("/{id:guid}/candidates", Candidates);

        // Die Sammlungen einer Buchung - die Gegenrichtung, fuer das Buchungsdetail.
        app.MapGet("/api/transactions/{transactionId:guid}/collections", OfTransaction).WithTags("Collections");
        app.MapPut("/api/transactions/{transactionId:guid}/collections", SetForTransaction).WithTags("Collections");
        return app;
    }

    private static async Task<IResult> List(
        Guid fullWorthSpaceId, string? status, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, CurrencyConverter converter, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var baseCurrency = await store.BaseCurrencyAsync(fullWorthSpaceId, ct);
        var fx = await Snapshot(converter, baseCurrency, ct);
        var rows = await store.ListAsync(fullWorthSpaceId, visible, baseCurrency, fx, ct);

        if (!string.IsNullOrWhiteSpace(status))
            rows = rows.Where(row => row.Status == CollectionStatuses.Normalize(status)).ToArray();

        return Results.Ok(rows);
    }

    private static async Task<IResult> Detail(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, CurrencyConverter converter, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await store.FindAsync(fullWorthSpaceId, id, ct) is null) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var baseCurrency = await store.BaseCurrencyAsync(fullWorthSpaceId, ct);
        var fx = await Snapshot(converter, baseCurrency, ct);

        var all = await store.ListAsync(fullWorthSpaceId, visible, baseCurrency, fx, ct);
        var row = all.SingleOrDefault(item => item.Id == id);
        if (row is null) return Results.NotFound();

        return Results.Ok(new
        {
            collection = row,
            categories = await store.CategorySplitAsync(id, visible, baseCurrency, fx, ct),
            transactionIds = await store.TransactionIdsAsync(id, visible, ct)
        });
    }

    private static async Task<IResult> Create(
        Guid fullWorthSpaceId, CollectionWrite request, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return Results.BadRequest(new { error = "A collection needs a name of at most 100 characters." });
        if (request.StartDate is { } from && request.EndDate is { } to && to < from)
            return Results.BadRequest(new { error = "The end date cannot be before the start date." });

        var normalized = ProductService.Normalize(name);
        if (await store.NameTakenAsync(fullWorthSpaceId, normalized, null, ct))
            return Results.Conflict(new { error = "A collection with this name already exists." });

        var entity = new FinanceTag
        {
            FullWorthSpaceId = fullWorthSpaceId,
            Name = name,
            NormalizedName = normalized,
            Description = Clean(request.Description, 500),
            Icon = Clean(request.Icon, 64),
            Color = Clean(request.Color, 9),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = CollectionStatuses.Normalize(request.Status)
        };
        store.Add(entity);
        await store.SaveAsync(ct);
        return Results.Ok(new { id = entity.Id });
    }

    private static async Task<IResult> Update(
        Guid id, Guid fullWorthSpaceId, CollectionWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CollectionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var entity = await store.FindAsync(fullWorthSpaceId, id, ct);
        if (entity is null) return Results.NotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return Results.BadRequest(new { error = "A collection needs a name of at most 100 characters." });
        if (request.StartDate is { } from && request.EndDate is { } to && to < from)
            return Results.BadRequest(new { error = "The end date cannot be before the start date." });

        var normalized = ProductService.Normalize(name);
        if (await store.NameTakenAsync(fullWorthSpaceId, normalized, id, ct))
            return Results.Conflict(new { error = "A collection with this name already exists." });

        entity.Name = name;
        entity.NormalizedName = normalized;
        entity.Description = Clean(request.Description, 500);
        entity.Icon = Clean(request.Icon, 64);
        entity.Color = Clean(request.Color, 9);
        entity.StartDate = request.StartDate;
        entity.EndDate = request.EndDate;
        entity.Status = CollectionStatuses.Normalize(request.Status);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Die Sammlung faellt, die Buchungen bleiben. Die Zuordnungen verschwinden mit ihr, weil sie ohne
    /// sie nichts mehr bedeuten - das ist die einzige Zeile, die geloescht wird.
    /// </summary>
    private static async Task<IResult> Delete(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var entity = await store.FindAsync(fullWorthSpaceId, id, ct);
        if (entity is null) return Results.NotFound();

        store.Remove(entity);
        await store.SaveAsync(ct);
        return Results.NoContent();
    }

    private static Task<IResult> AddTransactions(
        Guid id, Guid fullWorthSpaceId, CollectionTransactionsWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CollectionStore store, CancellationToken ct) =>
        Assign(id, fullWorthSpaceId, request, currentUser, space, store, add: true, ct);

    private static Task<IResult> RemoveTransactions(
        Guid id, Guid fullWorthSpaceId, CollectionTransactionsWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CollectionStore store, CancellationToken ct) =>
        Assign(id, fullWorthSpaceId, request, currentUser, space, store, add: false, ct);

    /// <summary>
    /// Hinzufuegen und Entfernen sind dieselbe Pruefung mit einer anderen Anweisung - und beide
    /// ERSETZEN nichts: eine Buchung behaelt ihre anderen Sammlungen.
    /// </summary>
    private static async Task<IResult> Assign(
        Guid id, Guid fullWorthSpaceId, CollectionTransactionsWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CollectionStore store, bool add, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await store.FindAsync(fullWorthSpaceId, id, ct) is null) return Results.NotFound();

        var ids = (request.TransactionIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) return Results.Ok(new { changed = 0 });
        if (ids.Length > MaxBulk)
            return Results.BadRequest(new { error = $"At most {MaxBulk} transactions can be assigned at once." });

        var changed = add
            ? await store.AddAsync(id, ids, "manual", ct)
            : await store.RemoveAsync(id, ids, ct);
        return Results.Ok(new { changed });
    }

    /// <summary>Vorschlaege - und ausdruecklich ohne einen einzigen Schreibvorgang.</summary>
    private static async Task<IResult> Candidates(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, Intelligence.CollectionSuggestionAiAdapter ranking, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var entity = await store.FindAsync(fullWorthSpaceId, id, ct);
        if (entity is null) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var candidates = await store.CandidatesAsync(
            fullWorthSpaceId, id, entity.StartDate, entity.EndDate, visible, ct);

        // Die KI-Stufe aus #124. Sie ordnet und begruendet, was das deterministische System gefunden
        // hat - und liefert es unveraendert zurueck, wenn keine KI da oder das Modul nicht freigegeben
        // ist. Der Aufrufer merkt den Unterschied nur an band/reason.
        var ranked = await ranking.RankAsync(userId, fullWorthSpaceId, id, entity.Name, candidates, ct);
        return Results.Ok(ranked.Select(item => new
        {
            item.Candidate.TransactionId,
            item.Candidate.Date,
            item.Candidate.Amount,
            item.Candidate.Currency,
            item.Candidate.Counterparty,
            item.Candidate.CategoryName,
            item.Candidate.AccountName,
            item.Candidate.Reasons,
            item.Candidate.Score,
            item.Band,
            item.Reason
        }));
    }

    private static async Task<IResult> OfTransaction(
        Guid transactionId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CollectionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var byTransaction = await store.CollectionsOfAsync([transactionId], ct);
        return Results.Ok(byTransaction.GetValueOrDefault(transactionId, []));
    }

    /// <summary>
    /// Die Sammlungen EINER Buchung setzen - das Multi-Select im Buchungsdetail.
    ///
    /// Hier ist Ersetzen richtig: der Benutzer sieht genau diese Liste und aendert sie. Anders als bei
    /// der Massenzuordnung aus der Liste, wo er die anderen Sammlungen gar nicht sieht.
    /// </summary>
    private static async Task<IResult> SetForTransaction(
        Guid transactionId, Guid fullWorthSpaceId, CollectionTransactionsWrite request,
        CurrentUserContext currentUser, SpaceAccess space, CollectionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var wanted = (request.TransactionIds ?? []).Distinct().ToArray();
        foreach (var collectionId in wanted)
            if (await store.FindAsync(fullWorthSpaceId, collectionId, ct) is null) return Results.NotFound();

        var current = (await store.CollectionsOfAsync([transactionId], ct))
            .GetValueOrDefault(transactionId, []);

        foreach (var gone in current.Except(wanted))
            await store.RemoveAsync(gone, [transactionId], ct);
        foreach (var added in wanted.Except(current))
            await store.AddAsync(added, [transactionId], "manual", ct);

        return Results.Ok(new { collections = wanted });
    }

    /// <summary>Ein Kurs-Schnappschuss ueber die ganze Historie: eine Sammlung kann Jahre umspannen.</summary>
    private static Task<FxSnapshot> Snapshot(CurrencyConverter converter, string baseCurrency, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return converter.PrepareAsync(baseCurrency, today.AddYears(-10), today, ct);
    }

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

}
