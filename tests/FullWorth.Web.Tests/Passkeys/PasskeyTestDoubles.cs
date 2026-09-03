using FullWorth.Web.Modules.Passkeys;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Passkeys;

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestUserLookup(params PasskeyUserAccount[] users) : IPasskeyUserLookup
{
    private readonly Dictionary<Guid, PasskeyUserAccount> items = users.ToDictionary(x => x.Id);
    private readonly HashSet<Guid> disabled = [];

    public void Disable(Guid id) => disabled.Add(id);

    public Task<PasskeyUserAccount?> GetEligibleAsync(Guid authUserId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(disabled.Contains(authUserId) ? null : items.GetValueOrDefault(authUserId));
    }
}

internal sealed class InMemoryPasskeyStore : IPasskeyStore
{
    private readonly object gate = new();
    private readonly List<PasskeyCredential> items = [];

    public IReadOnlyList<PasskeyCredential> Items
    {
        get { lock (gate) return items.ToArray(); }
    }

    public Task<PasskeyCredential?> GetByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult(items.SingleOrDefault(x => x.CredentialId.SequenceEqual(credentialId)));
    }

    public Task<IReadOnlyList<PasskeyCredential>> ListAsync(Guid authUserId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<PasskeyCredential>>(items.Where(x => x.AuthUserId == authUserId).ToArray());
    }

    public Task<bool> CredentialIdExistsAsync(byte[] credentialId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult(items.Any(x => x.CredentialId.SequenceEqual(credentialId)));
    }

    public Task CreateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (items.Any(x => x.CredentialId.SequenceEqual(credential.CredentialId)))
                throw new InvalidOperationException("Duplicate credential ID.");
            items.Add(credential);
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAfterAssertionAsync(
        Guid authUserId,
        byte[] credentialId,
        uint expectedSignatureCounter,
        uint newSignatureCounter,
        bool isBackedUp,
        DateTimeOffset lastUsedAt,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var item = items.SingleOrDefault(x => x.AuthUserId == authUserId
                && x.CredentialId.SequenceEqual(credentialId)
                && x.SignatureCounter == expectedSignatureCounter);
            if (item is null)
                return Task.FromResult(false);
            item.SignatureCounter = newSignatureCounter;
            item.IsBackedUp = isBackedUp;
            item.LastUsedAt = lastUsedAt;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid authUserId, Guid credentialRecordId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var item = items.SingleOrDefault(x => x.AuthUserId == authUserId && x.Id == credentialRecordId);
            if (item is null)
                return Task.FromResult(false);
            items.Remove(item);
            return Task.FromResult(true);
        }
    }
}

internal sealed class InMemoryChallengeStore : IPasskeyChallengeStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, PasskeyChallenge> items = [];

    public IReadOnlyCollection<PasskeyChallenge> Items
    {
        get { lock (gate) return items.Values.ToArray(); }
    }

    public Task CreateAsync(PasskeyChallenge challenge, CancellationToken cancellationToken = default)
    {
        lock (gate) items.Add(challenge.Id, challenge);
        return Task.CompletedTask;
    }

    public Task<PasskeyChallenge?> ConsumeAsync(
        Guid challengeId,
        PasskeyChallengeType type,
        Guid? authUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!items.TryGetValue(challengeId, out var item)
                || item.Type != type
                || item.AuthUserId != authUserId
                || item.ConsumedAt is not null
                || item.ExpiresAt <= now)
                return Task.FromResult<PasskeyChallenge?>(null);

            item.ConsumedAt = now;
            return Task.FromResult<PasskeyChallenge?>(item);
        }
    }
}

internal sealed class StubFido2 : IFido2
{
    private readonly IFido2 real;

    public StubFido2(string rpId = "localhost", string origin = "https://localhost")
    {
        real = new Fido2NetLib.Fido2(new Fido2Configuration
        {
            ServerDomain = rpId,
            ServerName = "FullWorth Test",
            Origins = new HashSet<string> { origin }
        });
    }

    public Func<MakeNewCredentialParams, CancellationToken, Task<RegisteredPublicKeyCredential>>? RegistrationHandler { get; set; }
    public Func<MakeAssertionParams, CancellationToken, Task<VerifyAssertionResult>>? AssertionHandler { get; set; }

    public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams getAssertionOptionsParams) =>
        real.GetAssertionOptions(getAssertionOptionsParams);

    public Task<VerifyAssertionResult> MakeAssertionAsync(MakeAssertionParams makeAssertionParams, CancellationToken cancellationToken = default) =>
        AssertionHandler?.Invoke(makeAssertionParams, cancellationToken)
        ?? throw new InvalidOperationException("Assertion handler not configured.");

    public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(MakeNewCredentialParams makeNewCredentialParams, CancellationToken cancellationToken = default) =>
        RegistrationHandler?.Invoke(makeNewCredentialParams, cancellationToken)
        ?? throw new InvalidOperationException("Registration handler not configured.");

    public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams requestNewCredentialParams) =>
        real.RequestNewCredential(requestNewCredentialParams);
}

internal static class PasskeyTestFactory
{
    public static PasskeyOptions Options => new()
    {
        RelyingPartyId = "localhost",
        RelyingPartyName = "FullWorth Test",
        Origins = ["https://localhost"],
        ChallengeLifetime = TimeSpan.FromMinutes(5)
    };

    public static PasskeyService CreateService(
        StubFido2 fido2,
        InMemoryPasskeyStore credentials,
        InMemoryChallengeStore challenges,
        TestUserLookup users,
        MutableTimeProvider clock) =>
        new(fido2, credentials, challenges, users, clock, Microsoft.Extensions.Options.Options.Create(Options));
}
