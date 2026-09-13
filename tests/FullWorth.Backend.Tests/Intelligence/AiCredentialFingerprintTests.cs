using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// The label shown next to a stored API key must say WHICH key it is and nothing about the key.
///
/// It used to end in the last four characters of the plaintext. Four characters is not much — and it
/// is four an attacker no longer has to guess, handed to anyone who can load the AI settings screen,
/// for every credential on it. A hash prefix tells two keys apart just as well, which is the only
/// thing the label is for.
/// </summary>
public sealed class AiCredentialFingerprintTests
{
    private const string Secret = "sk-abcdefghijklmnopqrstuvwxyz-TAIL";

    [Fact]
    public async Task The_fingerprint_contains_no_part_of_the_secret()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();

        var created = await store.CreateCredentialAsync(
            Guid.NewGuid(), "openai", "Test", Secret, CancellationToken.None);

        Assert.StartsWith("sha256:", created.SecretFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL", created.SecretFingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret[^4..], created.SecretFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret[..8], created.SecretFingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// It still has to do its job: two different keys must not look the same in the list, or the
    /// label is worse than no label.
    /// </summary>
    [Fact]
    public async Task Two_different_secrets_get_two_different_fingerprints()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var owner = Guid.NewGuid();

        var first = await store.CreateCredentialAsync(
            owner, "openai", "First", Secret, CancellationToken.None);
        var second = await store.CreateCredentialAsync(
            owner, "openai", "Second", Secret + "-other", CancellationToken.None);

        Assert.NotEqual(first.SecretFingerprint, second.SecretFingerprint);
    }
}
