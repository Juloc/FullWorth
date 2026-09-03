using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Pin;
using FullWorth.Web.Tests.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Pin;

// The app-lock PIN is stored as an Identity authentication token and hashed with the password hasher.
// These tests exercise set/verify/remove and the brute-force lockout against a real Postgres schema.
public sealed class PinServiceTests : IClassFixture<AuthPostgresFixture>
{
    private readonly AuthPostgresFixture fixture;

    public PinServiceTests(AuthPostgresFixture fixture) => this.fixture = fixture;

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private static PinService CreatePins(IServiceProvider sp, TimeProvider clock) =>
        new(sp.GetRequiredService<UserManager<AuthUser>>(), sp.GetRequiredService<IPasswordHasher<AuthUser>>(), clock);

    private static async Task<Guid> CreateUserAsync(IServiceProvider sp)
    {
        var auth = sp.GetRequiredService<AuthService>();
        var email = $"pin-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, "correct horse battery staple"));
        Assert.True(created.Succeeded);
        var user = await sp.GetRequiredService<UserManager<AuthUser>>().FindByEmailAsync(email);
        return user!.Id;
    }

    [Fact]
    public async Task SetVerifyRemove_RoundTrips_AndRejectsInvalidPins()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var userId = await CreateUserAsync(sp);
        var pins = CreatePins(sp, new MutableClock(DateTimeOffset.UtcNow));

        Assert.False(await pins.HasPinAsync(userId));
        Assert.False(await pins.SetPinAsync(userId, "123"));     // too short
        Assert.False(await pins.SetPinAsync(userId, "12ab"));    // not digits
        Assert.False(await pins.SetPinAsync(userId, null));

        Assert.True(await pins.SetPinAsync(userId, "1234"));
        Assert.True(await pins.HasPinAsync(userId));
        Assert.Equal(PinVerifyStatus.Success, await pins.VerifyPinAsync(userId, "1234"));
        Assert.Equal(PinVerifyStatus.WrongPin, await pins.VerifyPinAsync(userId, "9999"));

        Assert.True(await pins.RemovePinAsync(userId));
        Assert.False(await pins.HasPinAsync(userId));
        Assert.Equal(PinVerifyStatus.NotSet, await pins.VerifyPinAsync(userId, "1234"));
    }

    [Fact]
    public async Task RepeatedWrongPins_LockOut_ThenRecoverAfterWindow()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var userId = await CreateUserAsync(sp);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var pins = CreatePins(sp, clock);
        Assert.True(await pins.SetPinAsync(userId, "135790"));

        for (var attempt = 0; attempt < 4; attempt++)
            Assert.Equal(PinVerifyStatus.WrongPin, await pins.VerifyPinAsync(userId, "000000"));

        // The 5th consecutive failure trips the lockout, and even the correct PIN is refused while locked.
        Assert.Equal(PinVerifyStatus.Locked, await pins.VerifyPinAsync(userId, "000000"));
        Assert.Equal(PinVerifyStatus.Locked, await pins.VerifyPinAsync(userId, "135790"));

        // Once the lockout window elapses the correct PIN unlocks again.
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(PinVerifyStatus.Success, await pins.VerifyPinAsync(userId, "135790"));
    }
}
