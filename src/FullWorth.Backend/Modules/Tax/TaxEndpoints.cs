using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Modules.Tax;

public static class TaxEndpoints
{
    public static IEndpointRouteBuilder MapTaxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tax").WithTags("Tax Assistant");

        group.MapGet("/settings", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        group.MapPut("/settings", async (Guid fullWorthSpaceId, TaxSettingsUpdateRequest request, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var country = request.CountryCode.Trim().ToUpperInvariant();
            var maxYear = DateTime.UtcNow.Year + 1;
            if (country.Length != 2) return Results.BadRequest(new { error = "CountryCode must be a two-letter country code." });
            if (request.DefaultTaxYear is < 2000 || request.DefaultTaxYear > maxYear)
                return Results.BadRequest(new { error = $"DefaultTaxYear must be between 2000 and {maxYear}." });

            var normalized = request with { CountryCode = country };
            var result = await store.UpdateSettingsAsync(currentUser.RequireUserId(), fullWorthSpaceId, normalized, ct);
            if (!result.Found) return Results.NotFound();
            if (result.Forbidden) return Results.Forbid();
            return Results.Ok(result.Value);
        });

        group.MapGet("/profile/settings", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var profile = await store.EnsurePersonalProfileAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return profile is null
                ? Results.NotFound()
                : Results.Ok(new TaxProfileSettingsView(profile.AssistantEnabled));
        });

        group.MapPut("/profile/settings", async (Guid fullWorthSpaceId, TaxProfileSettingsUpdateRequest request, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var profile = await store.UpdatePersonalProfileSettingsAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return profile is null
                ? Results.NotFound()
                : Results.Ok(new TaxProfileSettingsView(profile.AssistantEnabled));
        });

        group.MapGet("/profiles", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var profiles = await store.ListProfilesAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return profiles is null ? Results.NotFound() : Results.Ok(profiles);
        });

        group.MapGet("/categories", async (Guid fullWorthSpaceId, int? year, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var taxYear = year ?? DateTime.UtcNow.Year;
            var categories = await store.ListCategoriesAsync(currentUser.RequireUserId(), fullWorthSpaceId, taxYear, ct);
            return categories is null ? Results.NotFound() : Results.Ok(categories);
        });

        group.MapGet("/candidates", async (Guid fullWorthSpaceId, int? year, string? status, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            var taxYear = year ?? DateTime.UtcNow.Year;
            if (!string.IsNullOrWhiteSpace(status) && !TaxCandidateStatuses.IsValid(status))
                return Results.BadRequest(new { error = "Unknown tax candidate status." });
            var candidates = await new TaxCandidateViewStore(db, store)
                .ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, taxYear, status, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

        group.MapGet("/candidates/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            var candidate = await new TaxCandidateViewStore(db, store)
                .GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return candidate is null ? Results.NotFound() : Results.Ok(candidate);
        });

        group.MapGet("/candidates/{id:guid}/document-target", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            var target = await new TaxDocumentTargetService(db, store)
                .ResolveAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return target is null ? Results.NotFound() : Results.Ok(target);
        });

        group.MapPut("/candidates/{id:guid}", async (Guid id, Guid fullWorthSpaceId, TaxCandidateUpdateRequest request, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            if (request.EligiblePercentage is < 0m or > 100m)
                return Results.BadRequest(new { error = "EligiblePercentage must be between 0 and 100." });
            if (!string.IsNullOrWhiteSpace(request.Status) && !TaxCandidateStatuses.IsValid(request.Status))
                return Results.BadRequest(new { error = "Unknown tax candidate status." });
            var userId = currentUser.RequireUserId();
            var result = await store.UpdateCandidateAsync(userId, fullWorthSpaceId, id, request, ct);
            if (!result.Found) return Results.NotFound();
            return Results.Ok(await new TaxCandidateViewStore(db, store).GetAsync(userId, fullWorthSpaceId, id, ct));
        });

        group.MapPost("/candidates/{id:guid}/confirm", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var result = await store.UpdateCandidateAsync(
                userId, fullWorthSpaceId, id,
                new TaxCandidateUpdateRequest(null, null, TaxCandidateStatuses.Confirmed), ct);
            if (!result.Found) return Results.NotFound();
            return Results.Ok(await new TaxCandidateViewStore(db, store).GetAsync(userId, fullWorthSpaceId, id, ct));
        });

        group.MapPost("/candidates/{id:guid}/reject", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var result = await store.UpdateCandidateAsync(
                userId, fullWorthSpaceId, id,
                new TaxCandidateUpdateRequest(null, null, TaxCandidateStatuses.Rejected), ct);
            if (!result.Found) return Results.NotFound();
            return Results.Ok(await new TaxCandidateViewStore(db, store).GetAsync(userId, fullWorthSpaceId, id, ct));
        });

        group.MapPost("/analyze", async (Guid fullWorthSpaceId, int? year, CurrentUserContext currentUser, TaxStore store, TaxAnalysisService analysis, FullWorthDbContext db, IServiceProvider services, CancellationToken ct) =>
        {
            var taxYear = year ?? DateTime.UtcNow.Year;
            if (taxYear is < 2000 || taxYear > 2100) return Results.BadRequest(new { error = "Invalid tax year." });
            var ai = services.GetServices<ITaxAiResolver>().FirstOrDefault();
            var result = await new TaxAnalysisCoordinator(db, store, analysis, ai)
                .AnalyzeAsync(currentUser.RequireUserId(), fullWorthSpaceId, taxYear, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapGet("/years/{year:int}/summary", async (int year, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var summary = await store.GetSummaryAsync(currentUser.RequireUserId(), fullWorthSpaceId, year, ct);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });

        group.MapGet("/years/{year:int}/review", async (int year, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            if (year is < 2000 or > 2100) return Results.BadRequest(new { error = "Invalid tax year." });
            var review = await new TaxYearReviewService(db, store)
                .BuildAsync(currentUser.RequireUserId(), fullWorthSpaceId, year, ct);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });

        group.MapGet("/years/{year:int}/export", async (int year, string? format, Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, FullWorthDbContext db, CancellationToken ct) =>
        {
            if (year is < 2000 or > 2100) return Results.BadRequest(new { error = "Invalid tax year." });
            var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
            if (normalizedFormat is not ("csv" or "json"))
                return Results.BadRequest(new { error = "Format must be csv or json." });

            var export = await new TaxExportService(db, store).BuildAsync(currentUser.RequireUserId(), fullWorthSpaceId, year, ct);
            if (export is null) return Results.NotFound();
            if (normalizedFormat == "json") return Results.Ok(export);

            return Results.File(
                TaxExportService.ToCsv(export),
                "text/csv; charset=utf-8",
                $"fullworth-tax-{year}.csv");
        });

        group.MapDelete("/data", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, TaxStore store, CancellationToken ct) =>
        {
            var result = await store.DeleteTaxDataAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            if (!result.Found) return Results.NotFound();
            if (result.Forbidden) return Results.Forbid();
            return Results.NoContent();
        });

        return app;
    }
}
