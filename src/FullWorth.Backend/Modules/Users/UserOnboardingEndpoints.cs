using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

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

        group.MapGet("/status", async (
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var user = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId)
                .Select(x => new
                {
                    x.OnboardingVersion,
                    x.OnboardingCompletedAt
                })
                .SingleOrDefaultAsync(ct);

            if (user is null) return Results.NotFound();
            return Results.Ok(new
            {
                currentVersion = UserOnboarding.CurrentVersion,
                completed = user.OnboardingVersion >= UserOnboarding.CurrentVersion &&
                            user.OnboardingCompletedAt.HasValue,
                completedVersion = user.OnboardingVersion,
                user.OnboardingCompletedAt
            });
        });

        group.MapPost("/complete", async (
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null) return Results.NotFound();

            user.OnboardingVersion = UserOnboarding.CurrentVersion;
            user.OnboardingCompletedAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                completed = true,
                completedVersion = user.OnboardingVersion,
                user.OnboardingCompletedAt
            });
        });

        return app;
    }
}
