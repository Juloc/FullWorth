using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class CompensationEndpoints
{
    public static IEndpointRouteBuilder MapCompensationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation").WithTags("Compensation");

        group.MapPost("/calculate", (CompensationProfileInput request, CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(GermanCompensationCalculator.Calculate(request)));
        });

        group.MapPost("/compare", (CompensationComparisonRequest request, CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(GermanCompensationCalculator.Compare(request)));
        });

        group.MapPost("/negotiation", (SalaryNegotiationRequest request, CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(InflationIndex.Analyze(request)));
        });

        group.MapPost("/insights", (CompensationInsightRequest request, CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(CompensationInsights.Analyze(request)));
        });

        group.MapGet("/inflation", (CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Results.Ok(InflationIndex.Metadata());
        });

        group.MapGet("/profile", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new CompensationStore(db);
            var profile = await store.GetProfileAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPut("/profile", async (Guid fullWorthSpaceId, CompensationProfileInput request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationStore(db);
                var profile = await store.SaveProfileAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return profile is null ? Results.NotFound() : Results.Ok(profile);
            }
            catch (Exception exception) when (IsInputException(exception))
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapGet("/scenarios", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new CompensationStore(db);
            var scenarios = await store.ListScenariosAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return scenarios is null ? Results.NotFound() : Results.Ok(scenarios);
        });

        group.MapPost("/scenarios", async (Guid fullWorthSpaceId, CompensationScenarioWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationStore(db);
                var scenario = await store.CreateScenarioAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return scenario is null ? Results.NotFound() : Results.Created($"/api/compensation/scenarios/{scenario.Id}?fullWorthSpaceId={fullWorthSpaceId}", scenario);
            }
            catch (Exception exception) when (IsInputException(exception))
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPut("/scenarios/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CompensationScenarioWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationStore(db);
                var scenario = await store.UpdateScenarioAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
                return scenario is null ? Results.NotFound() : Results.Ok(scenario);
            }
            catch (Exception exception) when (IsInputException(exception))
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/scenarios/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new CompensationStore(db);
            var deleted = await store.DeleteScenarioAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return deleted switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Results.NotFound()
            };
        });

        app.MapCompensationPayslipEndpoints();
        return app;
    }

    private static IResult Safe(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (IsInputException(exception))
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static bool IsInputException(Exception exception) => exception is ArgumentException;
}
