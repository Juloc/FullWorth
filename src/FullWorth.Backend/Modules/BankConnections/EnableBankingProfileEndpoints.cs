using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.BankConnections;

public static class EnableBankingProfileEndpoints
{
    public static IEndpointRouteBuilder MapEnableBankingProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/banking/profiles").WithTags("Internal banking");

        group.MapGet("/users/{userId:guid}", async (Guid userId, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            var profile = await store.GetForUserAsync(userId, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/{id:guid}", async (Guid id, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            var profile = await store.GetByIdAsync(id, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/", async (EnableBankingProfileWrite request, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.UpsertVerifiedAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("/users/{userId:guid}", async (Guid userId, EnableBankingProfileStore store, CancellationToken ct) =>
            await store.DeleteForUserAsync(userId, ct) switch
            {
                EnableBankingProfileDeleteResult.Deleted => Results.NoContent(),
                EnableBankingProfileDeleteResult.InUse => Results.Conflict(new { error = "profile_in_use" }),
                _ => Results.NotFound()
            });

        return app;
    }
}
