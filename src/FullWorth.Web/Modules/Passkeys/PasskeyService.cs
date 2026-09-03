using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Passkeys;

public sealed class PasskeyService
{
    private readonly IFido2 fido2;
    private readonly IPasskeyStore credentials;
    private readonly IPasskeyChallengeStore challenges;
    private readonly IPasskeyUserLookup users;
    private readonly TimeProvider clock;
    private readonly PasskeyOptions options;
    private readonly PasskeyChallengeCleanup? cleanup;

    public PasskeyService(
        IFido2 fido2,
        IPasskeyStore credentials,
        IPasskeyChallengeStore challenges,
        IPasskeyUserLookup users,
        TimeProvider clock,
        IOptions<PasskeyOptions> options,
        PasskeyChallengeCleanup? cleanup = null)
    {
        this.fido2 = fido2;
        this.credentials = credentials;
        this.challenges = challenges;
        this.users = users;
        this.clock = clock;
        this.options = options.Value;
        this.options.Validate();
        this.cleanup = cleanup;
    }

    public async Task<PasskeyBeginRegistrationResponse> BeginRegistrationAsync(
        Guid authUserId,
        CancellationToken cancellationToken = default)
    {
        if (cleanup is not null)
            await cleanup.PurgeExpiredAsync(cancellationToken);

        var user = await users.GetEligibleAsync(authUserId, cancellationToken)
            ?? throw new PasskeyRegistrationException("Unable to register a passkey.");
        var existing = await credentials.ListAsync(authUserId, cancellationToken);

        var publicKey = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = CreateUserHandle(user.Id),
                Name = user.Name,
                DisplayName = user.DisplayName
            },
            ExcludeCredentials = existing.Select(x => new PublicKeyCredentialDescriptor(x.CredentialId)).ToArray(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = null,
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var now = clock.GetUtcNow();
        var challenge = new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            AuthUserId = authUserId,
            Type = PasskeyChallengeType.Registration,
            OptionsJson = publicKey.ToJson(),
            CreatedAt = now,
            ExpiresAt = now.Add(options.ChallengeLifetime)
        };
        await challenges.CreateAsync(challenge, cancellationToken);
        return new PasskeyBeginRegistrationResponse(challenge.Id, publicKey);
    }

    public async Task<PasskeyCredentialDto> CompleteRegistrationAsync(
        Guid authUserId,
        PasskeyCompleteRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var displayName = NormalizeDisplayName(request.DisplayName);
        var user = await users.GetEligibleAsync(authUserId, cancellationToken)
            ?? throw new PasskeyRegistrationException("Unable to register a passkey.");
        var state = await challenges.ConsumeAsync(
            request.ChallengeId,
            PasskeyChallengeType.Registration,
            authUserId,
            clock.GetUtcNow(),
            cancellationToken) ?? throw new PasskeyChallengeException();

        try
        {
            var originalOptions = CredentialCreateOptions.FromJson(state.OptionsJson);
            var registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Credential,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await credentials.CredentialIdExistsAsync(args.CredentialId, ct)
            }, cancellationToken);

            var expectedHandle = CreateUserHandle(user.Id);
            if (!BytesEqual(registered.User.Id, expectedHandle)
                || await credentials.CredentialIdExistsAsync(registered.Id, cancellationToken))
                throw new PasskeyRegistrationException("Unable to register a passkey.");

            var now = clock.GetUtcNow();
            var credential = new PasskeyCredential
            {
                Id = Guid.NewGuid(),
                AuthUserId = authUserId,
                CredentialId = registered.Id,
                PublicKey = registered.PublicKey,
                SignatureCounter = registered.SignCount,
                UserHandle = expectedHandle,
                DisplayName = displayName,
                CreatedAt = now,
                Aaguid = registered.AaGuid,
                IsBackupEligible = registered.IsBackupEligible,
                IsBackedUp = registered.IsBackedUp
            };

            await credentials.CreateAsync(credential, cancellationToken);
            return ToDto(credential);
        }
        catch (Fido2VerificationException ex)
        {
            throw new PasskeyRegistrationException("Unable to register a passkey.", ex);
        }
        catch (DbUpdateException ex)
        {
            throw new PasskeyRegistrationException("Unable to register a passkey.", ex);
        }
    }

    public async Task<PasskeyBeginLoginResponse> BeginLoginAsync(
        string? emailOrUserHint = null,
        CancellationToken cancellationToken = default)
    {
        if (cleanup is not null)
            await cleanup.PurgeExpiredAsync(cancellationToken);

        _ = emailOrUserHint;
        var publicKey = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required
        });

        var now = clock.GetUtcNow();
        var challenge = new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            AuthUserId = null,
            Type = PasskeyChallengeType.Login,
            OptionsJson = publicKey.ToJson(),
            CreatedAt = now,
            ExpiresAt = now.Add(options.ChallengeLifetime)
        };
        await challenges.CreateAsync(challenge, cancellationToken);
        return new PasskeyBeginLoginResponse(challenge.Id, publicKey);
    }

    public async Task<PasskeyAuthenticationResult> CompleteLoginAsync(
        PasskeyCompleteLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Credential is null || request.Credential.RawId is not { Length: > 0 })
            throw new PasskeyAuthenticationException();

        var state = await challenges.ConsumeAsync(
            request.ChallengeId,
            PasskeyChallengeType.Login,
            null,
            clock.GetUtcNow(),
            cancellationToken) ?? throw new PasskeyAuthenticationException();

        var stored = await credentials.GetByCredentialIdAsync(request.Credential.RawId, cancellationToken);
        if (stored is null)
            throw new PasskeyAuthenticationException();

        var returnedUserHandle = request.Credential.Response?.UserHandle;
        if (returnedUserHandle is null || !BytesEqual(returnedUserHandle, stored.UserHandle))
            throw new PasskeyAuthenticationException();

        var user = await users.GetEligibleAsync(stored.AuthUserId, cancellationToken);
        if (user is null)
            throw new PasskeyAuthenticationException();

        try
        {
            var originalOptions = AssertionOptions.FromJson(state.OptionsJson);
            var verified = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Credential,
                OriginalOptions = originalOptions,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = stored.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    BytesEqual(args.CredentialId, stored.CredentialId)
                    && BytesEqual(args.UserHandle, stored.UserHandle))
            }, cancellationToken);

            if (!BytesEqual(verified.CredentialId, stored.CredentialId))
                throw new PasskeyAuthenticationException();

            var updated = await credentials.UpdateAfterAssertionAsync(
                stored.AuthUserId,
                stored.CredentialId,
                stored.SignatureCounter,
                verified.SignCount,
                verified.IsBackedUp,
                clock.GetUtcNow(),
                cancellationToken);
            if (!updated)
                throw new PasskeyAuthenticationException();

            return new PasskeyAuthenticationResult(stored.AuthUserId);
        }
        catch (Fido2VerificationException ex)
        {
            throw new PasskeyAuthenticationException(ex);
        }
    }

    public async Task<IReadOnlyList<PasskeyCredentialDto>> ListAsync(Guid authUserId, CancellationToken cancellationToken = default)
    {
        if (await users.GetEligibleAsync(authUserId, cancellationToken) is null)
            return Array.Empty<PasskeyCredentialDto>();
        var items = await credentials.ListAsync(authUserId, cancellationToken);
        return items.Select(ToDto).ToArray();
    }

    public async Task<bool> DeleteAsync(Guid authUserId, Guid credentialRecordId, CancellationToken cancellationToken = default)
    {
        if (await users.GetEligibleAsync(authUserId, cancellationToken) is null)
            return false;
        return await credentials.DeleteAsync(authUserId, credentialRecordId, cancellationToken);
    }

    public static byte[] CreateUserHandle(Guid authUserId) => authUserId.ToByteArray();

    private static bool BytesEqual(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private static string NormalizeDisplayName(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > PasskeyOptions.CredentialDisplayNameMaxLength)
            throw new PasskeyRegistrationException($"Passkey name must contain 1-{PasskeyOptions.CredentialDisplayNameMaxLength} characters.");
        return normalized;
    }

    private static PasskeyCredentialDto ToDto(PasskeyCredential credential) =>
        new(credential.Id, credential.DisplayName, credential.CreatedAt, credential.LastUsedAt,
            credential.Aaguid == Guid.Empty ? null : credential.Aaguid);
}
