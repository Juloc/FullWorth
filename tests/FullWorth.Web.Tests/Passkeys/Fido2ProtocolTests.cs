using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class Fido2ProtocolTests
{
    private const string RpId = "localhost";
    private const string Origin = "https://localhost";

    [Fact]
    public async Task Real_library_accepts_valid_uv_assertion()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, Origin, options.Challenge, signCount: 1, userVerified: true);

        var result = await VerifyAsync(fido2, options, fixture, storedCounter: 0);

        Assert.Equal((uint)1, result.SignCount);
        Assert.Equal(fixture.CredentialId, result.CredentialId);
    }

    [Fact]
    public async Task Real_library_rejects_wrong_challenge()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, Origin, RandomNumberGenerator.GetBytes(32), 1, true);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture, 0));
    }

    [Fact]
    public async Task Real_library_rejects_wrong_origin()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, "https://evil.example", options.Challenge, 1, true);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture, 0));
    }

    [Fact]
    public async Task Real_library_rejects_wrong_rp_id_hash()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, "wrong.localhost", Origin, options.Challenge, 1, true);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture, 0));
    }

    [Fact]
    public async Task Real_library_enforces_user_verification_required()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, Origin, options.Challenge, 1, userVerified: false);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture, 0));
    }

    [Fact]
    public async Task Real_library_accepts_zero_counter_for_modern_passkey_semantics()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, Origin, options.Challenge, signCount: 0, userVerified: true);

        var result = await VerifyAsync(fido2, options, fixture, storedCounter: 7);

        Assert.Equal((uint)0, result.SignCount);
    }

    [Fact]
    public async Task Real_library_rejects_nonzero_counter_that_does_not_advance()
    {
        var fido2 = CreateFido2();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, Origin, options.Challenge, signCount: 5, userVerified: true);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture, storedCounter: 5));
    }

    private static IFido2 CreateFido2() => new Fido2NetLib.Fido2(new Fido2Configuration
    {
        ServerDomain = RpId,
        ServerName = "FullWorth Test",
        Origins = new HashSet<string> { Origin }
    });

    private static AssertionOptions CreateOptions(IFido2 fido2) =>
        fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required
        });

    private static async Task<VerifyAssertionResult> VerifyAsync(
        IFido2 fido2,
        AssertionOptions options,
        AssertionFixture fixture,
        uint storedCounter) =>
        await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = fixture.Response,
            OriginalOptions = options,
            StoredPublicKey = fixture.StoredPublicKey,
            StoredSignatureCounter = storedCounter,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                args.CredentialId.SequenceEqual(fixture.CredentialId)
                && args.UserHandle.SequenceEqual(fixture.UserHandle))
        });

    private static AssertionFixture CreateAssertion(
        AssertionOptions options,
        string rpIdForAuthenticatorData,
        string origin,
        byte[] clientChallenge,
        uint signCount,
        bool userVerified)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new CredentialPublicKey(key, COSE.Algorithm.ES256).GetBytes();
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var userHandle = RandomNumberGenerator.GetBytes(16);

        var clientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = Base64Url(clientChallenge),
            origin
        });

        var authenticatorData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpIdForAuthenticatorData)).CopyTo(authenticatorData, 0);
        authenticatorData[32] = userVerified ? (byte)0x05 : (byte)0x01; // UP + optional UV
        BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33, 4), signCount);

        var clientHash = SHA256.HashData(clientDataJson);
        var signedData = new byte[authenticatorData.Length + clientHash.Length];
        authenticatorData.CopyTo(signedData, 0);
        clientHash.CopyTo(signedData, authenticatorData.Length);
        var signature = key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var response = new AuthenticatorAssertionRawResponse
        {
            Id = Base64Url(credentialId),
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                Signature = signature,
                ClientDataJson = clientDataJson,
                UserHandle = userHandle
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs()
        };

        return new AssertionFixture(response, publicKey, credentialId, userHandle);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AssertionFixture(
        AuthenticatorAssertionRawResponse Response,
        byte[] StoredPublicKey,
        byte[] CredentialId,
        byte[] UserHandle);
}
