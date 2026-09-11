using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>Both projections of a comparison in one body, so the two sides arrive together and are read against the same facts.</summary>
public sealed record BavComparisonRequest(BavProjectionRequest Left, BavProjectionRequest Right);

/// <summary>
/// The projection half of the Altersvorsorge API (docs/PENSION.md step 3). Same group, same result
/// mapping and the same status order as <see cref="PensionEndpoints"/>: not-found → forbidden →
/// conflict, and a non-member gets 404 so the existence of a contract does not leak.
///
/// Two deliberate differences from the rest of the module, both worth not "fixing":
///
/// <list type="bullet">
///   <item><b>These are POSTs even though they read.</b> A projection takes a body, and a GET would
///     invite a cache and a browser history entry for a number that is an assumption — the one kind of
///     number that must never look like a stored value.</item>
///   <item><b>Space membership is enough; the owner role is not required.</b> Every route here is
///     read-only — <c>PensionProjectionStore</c> contains no write at all — so demanding the write role
///     would lock a member out of a number they are already allowed to see on the Übersicht.</item>
/// </list>
/// </summary>
public static class PensionProjectionEndpoints
{
    public static IEndpointRouteBuilder MapPensionProjectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pension").WithTags("Pension");

        group.MapPost("/projection", async (
            Guid fullWorthSpaceId,
            BavProjectionRequest request,
            CurrentUserContext currentUser,
            PensionProjectionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.ProjectAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return Map(outcome.Result, outcome.Projection, outcome.Error, "Invalid projection request.");
        });

        group.MapPost("/projection/compare", async (
            Guid fullWorthSpaceId,
            BavComparisonRequest request,
            CurrentUserContext currentUser,
            PensionProjectionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.CompareAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, request.Left, request.Right, ct);
            return Map(outcome.Result, outcome.Comparison, outcome.Error, "Invalid comparison request.");
        });

        return app;
    }

    private static IResult Map<T>(BavMutationResult result, T? value, string? error, string fallback) => result switch
    {
        BavMutationResult.Success => Results.Ok(value),
        BavMutationResult.NotFound => Results.NotFound(),
        BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BavMutationResult.Conflict => Results.Conflict(new { error = error ?? fallback }),
        _ => Results.BadRequest(new { error = error ?? fallback })
    };
}
