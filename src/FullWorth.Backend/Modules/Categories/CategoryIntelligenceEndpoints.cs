using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryExplanation(decimal Confidence, string ReasonCode, string? Detail);
public sealed record ReviewWrite(IReadOnlyList<Guid>? TransactionIds, bool IsReviewed);
public sealed record BulkCategoryAction(
    IReadOnlyList<Guid>? TransactionIds,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    IReadOnlyList<Guid>? AddTagIds = null,
    IReadOnlyList<Guid>? RemoveTagIds = null);
public sealed record LearnCategoryWrite(Guid TransactionId, Guid CategoryId, string Scope);
public sealed record TagWrite(string Name, string? Color);
public sealed record TagAssignmentWrite(IReadOnlyList<Guid>? TagIds);
public sealed record CategoryAppearanceWrite(string? Color);
public sealed record IntelligenceTag(Guid Id, string Name, string? Color);
public sealed record CategoryAppearanceView(Guid CategoryId, string? Color);

/// <summary>
/// Deterministic evidence strength for explainable categorization. Confidence communicates the
/// strength of the evidence, not a statistical or ML probability.
/// </summary>
public static class CategoryIntelligenceExplanation
{
    public static CategoryExplanation Explain(FinanceTransaction transaction, IReadOnlyList<CategorizationRule> rules)
    {
        var source = transaction.CategorizationSource?.Trim().ToLowerInvariant() ?? "none";
        if (source == "manual") return new(1.00m, "manual", null);

        if (source == "rule")
        {
            CategorizationRule? matched = null;
            foreach (var rule in rules)
            {
                if (!TransactionRuleEngine.MatchesRule(transaction, rule)) continue;
                matched = rule;
                if (rule.StopProcessing) break;
            }
            return new(0.99m, "rule", matched?.Name);
        }

        if (source == "catalog")
        {
            var match = GermanyCategorizationCatalog.Classify(transaction);
            if (match is null) return new(0.75m, "catalog", null);
            var reason = match.Value.Reason;
            if (reason.StartsWith("merchant:", StringComparison.OrdinalIgnoreCase))
                return new(0.97m, "merchant", reason[9..]);
            if (reason.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                return new(0.90m, "text", reason[5..]);
            if (reason.StartsWith("mcc:", StringComparison.OrdinalIgnoreCase))
                return new(0.78m, "mcc", reason[4..]);
            return new(0.80m, "catalog", reason);
        }

        if (transaction.CategoryId.HasValue && source != "none")
            return new(0.95m, "imported", transaction.CategorizationSource);

        return new(0m, "unclassified", null);
    }
}

public static class CategoryIntelligenceEndpoints
{
    public static IEndpointRouteBuilder MapCategoryIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-intelligence").WithTags("Category Intelligence");

        group.MapGet("/overview", async (
            Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryIntelligenceService service,
            CancellationToken ct) =>
        {
            var result = await service.OverviewAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/review", async (
            Guid fullWorthSpaceId, ReviewWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SetReviewAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPost("/bulk", async (
            Guid fullWorthSpaceId, BulkCategoryAction request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.BulkAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPost("/learn", async (
            Guid fullWorthSpaceId, LearnCategoryWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.LearnAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/tags", async (
            Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryIntelligenceService service,
            CancellationToken ct) =>
        {
            var result = await service.ListTagsAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/tags", async (
            Guid fullWorthSpaceId, TagWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.CreateTagAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null
                    ? Results.NotFound()
                    : Results.Created($"/api/category-intelligence/tags/{result.Id}", result);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPut("/tags/{tagId:guid}", async (
            Guid tagId, Guid fullWorthSpaceId, TagWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.UpdateTagAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, tagId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapDelete("/tags/{tagId:guid}", async (
            Guid tagId, Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryIntelligenceService service,
            CancellationToken ct) =>
            await service.DeleteTagAsync(currentUser.RequireUserId(), fullWorthSpaceId, tagId, ct)
                ? Results.NoContent()
                : Results.NotFound());

        group.MapPut("/transactions/{transactionId:guid}/tags", async (
            Guid transactionId, Guid fullWorthSpaceId, TagAssignmentWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                return await service.ReplaceTagsAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, transactionId, request, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/category-appearances", async (
            Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryIntelligenceService service,
            CancellationToken ct) =>
        {
            var result = await service.ListAppearancesAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPut("/category-appearances/{categoryId:guid}", async (
            Guid categoryId, Guid fullWorthSpaceId, CategoryAppearanceWrite request, CurrentUserContext currentUser,
            CategoryIntelligenceService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SetAppearanceAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, categoryId, request, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        return app;
    }
}
