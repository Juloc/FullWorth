using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Users;

public static class UserOnboarding
{
    public const int CurrentVersion = 1;
}

public static class UserOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapUserOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/onboarding").WithTags("Onboarding");

        group.MapGet("/status", async (CurrentUserContext currentUser, UserOnboardingStore store, CancellationToken ct) =>
        {
            var state = await store.ReadAsync(currentUser.RequireUserId(), ct);
            if (state is null) return Results.NotFound();
            return Results.Ok(new
            {
                currentVersion = UserOnboarding.CurrentVersion,
                completed = state.Version >= UserOnboarding.CurrentVersion && state.CompletedAt.HasValue,
                completedVersion = state.Version,
                OnboardingCompletedAt = state.CompletedAt
            });
        });

        group.MapPost("/complete", async (CurrentUserContext currentUser, UserOnboardingStore store, CancellationToken ct) =>
        {
            var state = await store.CompleteAsync(currentUser.RequireUserId(), UserOnboarding.CurrentVersion, ct);
            return state is null
                ? Results.NotFound()
                : Results.Ok(new { completed = true, completedVersion = state.Version, OnboardingCompletedAt = state.CompletedAt });
        });

        return app;
    }
}
