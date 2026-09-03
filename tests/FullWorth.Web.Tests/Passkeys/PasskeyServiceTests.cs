using FullWorth.Web.Modules.Passkeys;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class PasskeyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginRegistration_requires_existing_eligible_user()
    {
        var service = Create(out _, out _, out _, out _);
        await Assert.ThrowsAsync<PasskeyRegistrationException>(() => service.BeginRegistrationAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BeginRegistration_generates_server_challenge_and_persists_state()
    {
        var user = User();
        var service = Create(user, out _, out var challenges, out _, out _);
        var result = await service.BeginRegistrationAsync(user.Id);

        Assert.NotEqual(Guid.Empty, result.ChallengeId);
        Assert.NotEmpty(result.PublicKey.Challenge);
        var state = Assert.Single(challenges.Items);
        Assert.Equal(result.ChallengeId, state.Id);
        Assert.Equal(user.Id, state.AuthUserId);
        Assert.Equal(Now.AddMinutes(5), state.ExpiresAt);
    }

    [Fact]
    public async Task Registration_policy_requires_uv_and_discoverable_credentials_without_platform_lock()
    {
        var user = User();
        var service = Create(user, out _, out _, out _, out _);
        var result = await service.BeginRegistrationAsync(user.Id);

        Assert.Equal(UserVerificationRequirement.Required, result.PublicKey.AuthenticatorSelection!.UserVerification);
        Assert.Equal(ResidentKeyRequirement.Required, result.PublicKey.AuthenticatorSelection.ResidentKey);
        Assert.Null(result.PublicKey.AuthenticatorSelection.AuthenticatorAttachment);
        Assert.Equal(AttestationConveyancePreference.None, result.PublicKey.Attestation);
    }

    [Fact]
    public async Task Registration_challenge_expires()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out _, out var clock);
        ConfigureSuccessfulRegistration(fido2, [1, 2, 3]);
        var begin = await service.BeginRegistrationAsync(user.Id);
        clock.Now = clock.Now.AddMinutes(6);

        await Assert.ThrowsAsync<PasskeyChallengeException>(() => service.CompleteRegistrationAsync(
            user.Id, new(begin.ChallengeId, "Laptop", null!)));
    }

    [Fact]
    public async Task Registration_challenge_is_single_use_and_response_cannot_be_replayed()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out _, out _);
        ConfigureSuccessfulRegistration(fido2, [3, 2, 1]);
        var begin = await service.BeginRegistrationAsync(user.Id);
        var request = new PasskeyCompleteRegistrationRequest(begin.ChallengeId, "Laptop", null!);

        await service.CompleteRegistrationAsync(user.Id, request);
        await Assert.ThrowsAsync<PasskeyChallengeException>(() => service.CompleteRegistrationAsync(user.Id, request));
    }

    [Fact]
    public async Task Successful_registration_stores_only_public_credential_material()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out var store, out _);
        ConfigureSuccessfulRegistration(fido2, [7, 7, 7], [9, 8, 7]);
        var begin = await service.BeginRegistrationAsync(user.Id);

        await service.CompleteRegistrationAsync(user.Id, new(begin.ChallengeId, "Windows Hello", null!));

        var saved = Assert.Single(store.Items);
        Assert.Equal(user.Id, saved.AuthUserId);
        Assert.Equal([7, 7, 7], saved.CredentialId);
        Assert.Equal([9, 8, 7], saved.PublicKey);
        Assert.Equal(PasskeyService.CreateUserHandle(user.Id), saved.UserHandle);
        Assert.DoesNotContain(typeof(PasskeyCredential).GetProperties(), x => x.Name.Contains("Private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Duplicate_credential_id_is_rejected_for_same_user()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out _, out _);
        ConfigureSuccessfulRegistration(fido2, [4, 4]);
        var first = await service.BeginRegistrationAsync(user.Id);
        await service.CompleteRegistrationAsync(user.Id, new(first.ChallengeId, "First", null!));
        var second = await service.BeginRegistrationAsync(user.Id);

        await Assert.ThrowsAsync<PasskeyRegistrationException>(() =>
            service.CompleteRegistrationAsync(user.Id, new(second.ChallengeId, "Second", null!)));
    }

    [Fact]
    public async Task Credential_id_cannot_be_assigned_to_two_users()
    {
        var firstUser = User();
        var secondUser = User();
        var service = Create([firstUser, secondUser], out var fido2, out _, out _, out _);
        ConfigureSuccessfulRegistration(fido2, [5, 5]);
        var first = await service.BeginRegistrationAsync(firstUser.Id);
        await service.CompleteRegistrationAsync(firstUser.Id, new(first.ChallengeId, "First", null!));
        var second = await service.BeginRegistrationAsync(secondUser.Id);

        await Assert.ThrowsAsync<PasskeyRegistrationException>(() =>
            service.CompleteRegistrationAsync(secondUser.Id, new(second.ChallengeId, "Second", null!)));
    }

    [Fact]
    public async Task List_returns_only_own_credentials_and_safe_dto()
    {
        var user = User();
        var other = User();
        var service = Create([user, other], out _, out _, out var store, out _);
        await store.CreateAsync(Credential(user.Id, [1], "Mine"));
        await store.CreateAsync(Credential(other.Id, [2], "Other"));

        var result = await service.ListAsync(user.Id);

        var item = Assert.Single(result);
        Assert.Equal("Mine", item.DisplayName);
        var names = typeof(PasskeyCredentialDto).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PublicKey", names);
        Assert.DoesNotContain("UserHandle", names);
        Assert.DoesNotContain("SignatureCounter", names);
        Assert.DoesNotContain("CredentialId", names);
    }

    [Fact]
    public async Task Foreign_credential_delete_returns_not_found_semantics()
    {
        var user = User();
        var other = User();
        var service = Create([user, other], out _, out _, out var store, out _);
        var foreign = Credential(other.Id, [8], "Other");
        await store.CreateAsync(foreign);

        Assert.False(await service.DeleteAsync(user.Id, foreign.Id));
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Own_credential_delete_succeeds_even_when_it_is_last_passkey()
    {
        var user = User();
        var service = Create(user, out _, out _, out var store, out _);
        var own = Credential(user.Id, [8], "Mine");
        await store.CreateAsync(own);

        Assert.True(await service.DeleteAsync(user.Id, own.Id));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task BeginLogin_generates_discoverable_uv_required_challenge()
    {
        var service = Create(out _, out var challenges, out _, out _);
        var result = await service.BeginLoginAsync();

        Assert.NotEmpty(result.PublicKey.Challenge);
        Assert.Empty(result.PublicKey.AllowCredentials);
        Assert.Equal(UserVerificationRequirement.Required, result.PublicKey.UserVerification);
        var state = Assert.Single(challenges.Items);
        Assert.Null(state.AuthUserId);
        Assert.Equal(PasskeyChallengeType.Login, state.Type);
    }

    [Fact]
    public async Task Login_hint_does_not_resolve_or_enumerate_accounts()
    {
        var service = Create(out _, out _, out _, out _);
        var knownShape = await service.BeginLoginAsync("known@example.invalid");
        var unknownShape = await service.BeginLoginAsync("unknown@example.invalid");

        Assert.Empty(knownShape.PublicKey.AllowCredentials);
        Assert.Empty(unknownShape.PublicKey.AllowCredentials);
        Assert.Equal(knownShape.PublicKey.UserVerification, unknownShape.PublicKey.UserVerification);
        Assert.Equal(knownShape.PublicKey.RpId, unknownShape.PublicKey.RpId);
    }

    [Fact]
    public async Task Valid_verified_assertion_returns_correct_user_and_updates_counter()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out var store, out _);
        var credential = Credential(user.Id, [6, 1], "Phone");
        credential.SignatureCounter = 4;
        await store.CreateAsync(credential);
        ConfigureSuccessfulAssertion(fido2, credential, 5);
        var begin = await service.BeginLoginAsync();

        var result = await service.CompleteLoginAsync(new(begin.ChallengeId, Assertion(credential)));

        Assert.Equal(user.Id, result.AuthUserId);
        Assert.Equal((uint)5, Assert.Single(store.Items).SignatureCounter);
        Assert.Equal(Now, Assert.Single(store.Items).LastUsedAt);
    }

    [Fact]
    public async Task Wrong_login_challenge_fails_before_assertion_verification()
    {
        var user = User();
        var service = Create(user, out _, out _, out var store, out _);
        var credential = Credential(user.Id, [6, 2], "Phone");
        await store.CreateAsync(credential);

        await Assert.ThrowsAsync<PasskeyAuthenticationException>(() =>
            service.CompleteLoginAsync(new(Guid.NewGuid(), Assertion(credential))));
    }

    [Fact]
    public async Task Assertion_replay_fails_after_successful_completion()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out var store, out _);
        var credential = Credential(user.Id, [6, 3], "Phone");
        await store.CreateAsync(credential);
        ConfigureSuccessfulAssertion(fido2, credential, 0);
        var begin = await service.BeginLoginAsync();
        var request = new PasskeyCompleteLoginRequest(begin.ChallengeId, Assertion(credential));

        await service.CompleteLoginAsync(request);
        await Assert.ThrowsAsync<PasskeyAuthenticationException>(() => service.CompleteLoginAsync(request));
    }

    [Fact]
    public async Task Expired_login_challenge_fails()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out var store, out var clock);
        var credential = Credential(user.Id, [6, 4], "Phone");
        await store.CreateAsync(credential);
        ConfigureSuccessfulAssertion(fido2, credential, 0);
        var begin = await service.BeginLoginAsync();
        clock.Now = clock.Now.AddMinutes(6);

        await Assert.ThrowsAsync<PasskeyAuthenticationException>(() =>
            service.CompleteLoginAsync(new(begin.ChallengeId, Assertion(credential))));
    }

    [Fact]
    public async Task Disabled_user_cannot_login_with_passkey()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out var store, out _, out var users);
        var credential = Credential(user.Id, [6, 5], "Phone");
        await store.CreateAsync(credential);
        ConfigureSuccessfulAssertion(fido2, credential, 0);
        var begin = await service.BeginLoginAsync();
        users.Disable(user.Id);

        await Assert.ThrowsAsync<PasskeyAuthenticationException>(() =>
            service.CompleteLoginAsync(new(begin.ChallengeId, Assertion(credential))));
    }

    [Fact]
    public void User_handle_is_stable_and_not_derived_from_email()
    {
        var id = Guid.NewGuid();
        Assert.Equal(PasskeyService.CreateUserHandle(id), PasskeyService.CreateUserHandle(id));
        Assert.Equal(16, PasskeyService.CreateUserHandle(id).Length);
    }

    [Fact]
    public async Task Credential_name_is_bounded()
    {
        var user = User();
        var service = Create(user, out var fido2, out _, out _, out _);
        ConfigureSuccessfulRegistration(fido2, [9]);
        var begin = await service.BeginRegistrationAsync(user.Id);

        await Assert.ThrowsAsync<PasskeyRegistrationException>(() => service.CompleteRegistrationAsync(
            user.Id,
            new(begin.ChallengeId, new string('x', PasskeyOptions.CredentialDisplayNameMaxLength + 1), null!)));
    }

    [Fact]
    public void Passkey_module_does_not_define_recovery_totp_jwt_or_second_session_models()
    {
        var types = typeof(PasskeyService).Assembly.GetTypes()
            .Where(x => x.Namespace == typeof(PasskeyService).Namespace)
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain(types, x => x.Contains("RecoveryCode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, x => x.Contains("Totp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, x => x.Contains("Jwt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("UserSession", types);
    }

    private static PasskeyUserAccount User() =>
        new(Guid.NewGuid(), $"user-{Guid.NewGuid():N}@example.invalid", "Finance User");

    private static PasskeyCredential Credential(Guid userId, byte[] id, string name) => new()
    {
        Id = Guid.NewGuid(),
        AuthUserId = userId,
        CredentialId = id,
        PublicKey = [1, 2, 3],
        UserHandle = PasskeyService.CreateUserHandle(userId),
        DisplayName = name,
        CreatedAt = Now
    };

    private static AuthenticatorAssertionRawResponse Assertion(PasskeyCredential credential) => new()
    {
        Id = Convert.ToBase64String(credential.CredentialId),
        RawId = credential.CredentialId,
        Type = PublicKeyCredentialType.PublicKey,
        Response = new AuthenticatorAssertionRawResponse.AssertionResponse
        {
            AuthenticatorData = [1],
            Signature = [1],
            ClientDataJson = [1],
            UserHandle = credential.UserHandle
        },
        ClientExtensionResults = new AuthenticationExtensionsClientOutputs()
    };

    private static void ConfigureSuccessfulRegistration(StubFido2 fido2, byte[] credentialId, byte[]? publicKey = null)
    {
        fido2.RegistrationHandler = (args, _) => Task.FromResult(new RegisteredPublicKeyCredential
        {
            Id = credentialId,
            PublicKey = publicKey ?? [1, 2, 3],
            User = args.OriginalOptions.User,
            SignCount = 0,
            AaGuid = Guid.NewGuid(),
            IsBackupEligible = true,
            IsBackedUp = true
        });
    }

    private static void ConfigureSuccessfulAssertion(StubFido2 fido2, PasskeyCredential credential, uint newCounter)
    {
        fido2.AssertionHandler = async (args, cancellationToken) =>
        {
            var owned = await args.IsUserHandleOwnerOfCredentialIdCallback(
                new IsUserHandleOwnerOfCredentialIdParams(credential.CredentialId, credential.UserHandle),
                cancellationToken);
            Assert.True(owned);
            Assert.Equal(credential.SignatureCounter, args.StoredSignatureCounter);
            return new VerifyAssertionResult
            {
                CredentialId = credential.CredentialId,
                SignCount = newCounter,
                IsBackedUp = true
            };
        };
    }

    private static PasskeyService Create(
        out StubFido2 fido2,
        out InMemoryChallengeStore challenges,
        out InMemoryPasskeyStore store,
        out MutableTimeProvider clock) =>
        Create([], out fido2, out challenges, out store, out clock, out _);

    private static PasskeyService Create(
        PasskeyUserAccount user,
        out StubFido2 fido2,
        out InMemoryChallengeStore challenges,
        out InMemoryPasskeyStore store,
        out MutableTimeProvider clock) =>
        Create([user], out fido2, out challenges, out store, out clock, out _);

    private static PasskeyService Create(
        PasskeyUserAccount user,
        out StubFido2 fido2,
        out InMemoryChallengeStore challenges,
        out InMemoryPasskeyStore store,
        out MutableTimeProvider clock,
        out TestUserLookup users) =>
        Create([user], out fido2, out challenges, out store, out clock, out users);

    private static PasskeyService Create(
        PasskeyUserAccount[] userItems,
        out StubFido2 fido2,
        out InMemoryChallengeStore challenges,
        out InMemoryPasskeyStore store,
        out MutableTimeProvider clock) =>
        Create(userItems, out fido2, out challenges, out store, out clock, out _);

    private static PasskeyService Create(
        PasskeyUserAccount[] userItems,
        out StubFido2 fido2,
        out InMemoryChallengeStore challenges,
        out InMemoryPasskeyStore store,
        out MutableTimeProvider clock,
        out TestUserLookup users)
    {
        fido2 = new StubFido2();
        challenges = new InMemoryChallengeStore();
        store = new InMemoryPasskeyStore();
        clock = new MutableTimeProvider(Now);
        users = new TestUserLookup(userItems);
        return PasskeyTestFactory.CreateService(fido2, store, challenges, users, clock);
    }
}
