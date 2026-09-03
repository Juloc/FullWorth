using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class PasskeyIntegratedProtocolTests : IClassFixture<FullWorthWebFactory>
{
    private const string RpId = "localhost";
    private const string ConfiguredOrigin = "http://localhost";
    private readonly FullWorthWebFactory factory;

    public PasskeyIntegratedProtocolTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task RealProgramFido2Configuration_AcceptsConfiguredOriginAndRpId()
    {
        var fido2 = factory.Services.GetRequiredService<IFido2>();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, ConfiguredOrigin);

        var result = await VerifyAsync(fido2, options, fixture);

        Assert.Equal(fixture.CredentialId, result.CredentialId);
    }

    [Fact]
    public async Task RealProgramFido2Configuration_RejectsWrongOrigin()
    {
        var fido2 = factory.Services.GetRequiredService<IFido2>();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, RpId, "https://evil.example");

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture));
    }

    [Fact]
    public async Task RealProgramFido2Configuration_RejectsWrongRpIdHash()
    {
        var fido2 = factory.Services.GetRequiredService<IFido2>();
        var options = CreateOptions(fido2);
        var fixture = CreateAssertion(options, "wrong.localhost", ConfiguredOrigin);

        await Assert.ThrowsAsync<Fido2VerificationException>(() => VerifyAsync(fido2, options, fixture));
    }

    private static AssertionOptions CreateOptions(IFido2 fido2) =>
        fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required
        });

    private static Task<VerifyAssertionResult> VerifyAsync(
        IFido2 fido2,
        AssertionOptions options,
        AssertionFixture fixture) =>
        fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = fixture.Response,
            OriginalOptions = options,
            StoredPublicKey = fixture.StoredPublicKey,
            StoredSignatureCounter = 0,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                args.CredentialId.SequenceEqual(fixture.CredentialId)
                && args.UserHandle.SequenceEqual(fixture.UserHandle))
        });

    private static AssertionFixture CreateAssertion(
        AssertionOptions options,
        string rpIdForAuthenticatorData,
        string origin)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new CredentialPublicKey(key, COSE.Algorithm.ES256).GetBytes();
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var userHandle = RandomNumberGenerator.GetBytes(16);

        var clientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = Base64Url(options.Challenge),
            origin
        });

        var authenticatorData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpIdForAuthenticatorData)).CopyTo(authenticatorData, 0);
        authenticatorData[32] = 0x05; // user present + user verified
        BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33, 4), 1);

        var clientHash = SHA256.HashData(clientDataJson);
        var signedData = new byte[authenticatorData.Length + clientHash.Length];
        authenticatorData.CopyTo(signedData, 0);
        clientHash.CopyTo(signedData, authenticatorData.Length);
        var signature = key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new AssertionFixture(
            new AuthenticatorAssertionRawResponse
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
            },
            publicKey,
            credentialId,
            userHandle);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AssertionFixture(
        AuthenticatorAssertionRawResponse Response,
        byte[] StoredPublicKey,
        byte[] CredentialId,
        byte[] UserHandle);
}
