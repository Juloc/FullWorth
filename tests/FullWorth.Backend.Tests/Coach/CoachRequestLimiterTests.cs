using FullWorth.Backend.Modules.Coach;

namespace FullWorth.Backend.Tests.Coach;

public sealed class CoachRequestLimiterTests
{
    [Fact]
    public void LimitsAnswerGenerationPerUserWithoutAffectingAnotherUser()
    {
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();

        for (var i = 0; i < CoachRequestLimiter.PermitLimit; i++)
            Assert.True(CoachRequestLimiter.TryAcquire(firstUser, out _));

        Assert.False(CoachRequestLimiter.TryAcquire(firstUser, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(CoachRequestLimiter.TryAcquire(secondUser, out _));
    }
}
