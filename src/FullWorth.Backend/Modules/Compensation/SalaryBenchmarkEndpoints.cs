using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Read-only lookup over the curated German salary reference dataset. No persistence, no writes:
/// the dataset ships with the build (<c>salary-benchmarks-de.json</c>).
/// Every response carries the source label and the quality marker so the UI can label the number
/// as an estimate — do not render a benchmark value without them.
/// </summary>
public static class SalaryBenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapCompensationBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/benchmarks").WithTags("Compensation");

        // GET /api/compensation/benchmarks?profession=…&bundesland=…&year=…&experience=…&annualGross=…
        group.MapGet("", (
            string profession,
            CurrentUserContext currentUser,
            string? bundesland = null,
            int? year = null,
            string? experience = null,
            decimal? annualGross = null) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(SalaryBenchmarkDataset.Lookup(
                new SalaryBenchmarkQuery(profession, bundesland, year, experience, annualGross))));
        });

        group.MapGet("/series", (
            string profession,
            CurrentUserContext currentUser,
            string? bundesland = null,
            string? experience = null) =>
        {
            _ = currentUser.RequireUserId();
            return Safe(() => Results.Ok(SalaryBenchmarkDataset.Series(profession, bundesland, experience)));
        });

        group.MapGet("/metadata", (CurrentUserContext currentUser) =>
        {
            _ = currentUser.RequireUserId();
            return Results.Ok(SalaryBenchmarkDataset.Metadata());
        });

        return app;
    }

    private static IResult Safe(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
