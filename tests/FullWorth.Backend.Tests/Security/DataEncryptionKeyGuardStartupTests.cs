using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// The guard at the only place it matters: a real host start, against a real database.
///
/// The unit tests call the guard directly. This one boots the actual backend — migrations, seeder and
/// all — twice against the SAME database with two different data encryption keys, which is exactly
/// what a wrong or empty fullworth-platform-secrets volume looks like from inside the container. The
/// second start has to fail, loudly, instead of coming up healthy with every encrypted column
/// silently unreadable.
/// </summary>
public sealed class DataEncryptionKeyGuardStartupTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 5)).ToArray());

    [Fact]
    public async Task A_second_start_with_a_different_key_refuses_to_come_up()
    {
        await using var first = new BackendWebApplicationFactory(Configuration(KeyA));
        using (var client = first.CreateClient()) { }   // forces the real startup path

        // The first start recorded which key this database belongs to.
        await using (var scope = first.Services.CreateAsyncScope())
        {
            var marker = await scope.ServiceProvider.GetRequiredService<FullWorthDbContext>()
                .Set<InstallationEncryptionMarker>().SingleAsync();
            Assert.False(string.IsNullOrWhiteSpace(marker.KeyFingerprint));
        }

        // Same database, different key: the volume was swapped, restored wrong, or never mounted.
        await using var second = new BackendWebApplicationFactory(
            first.ConnectionString, Configuration(KeyB));

        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = second.CreateClient();
        });

        Assert.Contains("encrypted", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fullworth-platform-secrets", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the same key starts again, which is the case that happens every single day. A guard that
    /// only ever says no is not a guard, it is an outage.
    /// </summary>
    [Fact]
    public async Task A_second_start_with_the_same_key_comes_up_normally()
    {
        await using var first = new BackendWebApplicationFactory(Configuration(KeyA));
        using (var client = first.CreateClient()) { }

        await using var second = new BackendWebApplicationFactory(
            first.ConnectionString, Configuration(KeyA));
        using var reopened = second.CreateClient();

        await using var scope = second.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<FullWorthDbContext>()
            .Set<InstallationEncryptionMarker>().ToListAsync());
    }

    private static Dictionary<string, string?> Configuration(string key) => new()
    {
        ["Security:DataEncryptionKey"] = key
    };
}
