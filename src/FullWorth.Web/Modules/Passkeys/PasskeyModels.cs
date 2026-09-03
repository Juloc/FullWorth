using Fido2NetLib;
using Fido2NetLib.Objects;

namespace FullWorth.Web.Modules.Passkeys;

public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";
    public const int CredentialDisplayNameMaxLength = 80;
    public static readonly TimeSpan DefaultChallengeLifetime = TimeSpan.FromMinutes(5);

    public string RelyingPartyId { get; set; } = string.Empty;
    public string RelyingPartyName { get; set; } = "FullWorth";
    public string[] Origins { get; set; } = [];
    public TimeSpan ChallengeLifetime { get; set; } = DefaultChallengeLifetime;

    public void Validate(bool production = false)
    {
        if (string.IsNullOrWhiteSpace(RelyingPartyId))
            throw new InvalidOperationException("Passkeys:RelyingPartyId is required.");
        if (string.IsNullOrWhiteSpace(RelyingPartyName))
            throw new InvalidOperationException("Passkeys:RelyingPartyName is required.");
        if (Origins.Length == 0 || Origins.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("At least one explicit Passkeys:Origins value is required.");
        if (ChallengeLifetime <= TimeSpan.Zero || ChallengeLifetime > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Passkey challenge lifetime must be greater than zero and no more than 10 minutes.");

        foreach (var configuredOrigin in Origins)
        {
            if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
                || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
                || origin.AbsolutePath != "/"
                || !string.IsNullOrEmpty(origin.Query)
                || !string.IsNullOrEmpty(origin.Fragment))
                throw new InvalidOperationException("Passkeys:Origins values must be absolute HTTP(S) origins without path, query, or fragment.");

            if (production && origin.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Production Passkeys:Origins values must use HTTPS.");
        }
    }
}

public enum PasskeyChallengeType
{
    Registration = 1,
    Login = 2
}

public sealed class PasskeyCredential
{
    public Guid Id { get; set; }
    public Guid AuthUserId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public uint SignatureCounter { get; set; }
    public byte[] UserHandle { get; set; } = [];
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public Guid Aaguid { get; set; }
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
}

public sealed class PasskeyChallenge
{
    public Guid Id { get; set; }
    public Guid? AuthUserId { get; set; }
    public PasskeyChallengeType Type { get; set; }
    public string OptionsJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed record PasskeyBeginRegistrationResponse(Guid ChallengeId, CredentialCreateOptions PublicKey);

public sealed record PasskeyCompleteRegistrationRequest(
    Guid ChallengeId,
    string DisplayName,
    AuthenticatorAttestationRawResponse Credential);

public sealed record PasskeyBeginLoginResponse(Guid ChallengeId, AssertionOptions PublicKey);

public sealed record PasskeyCompleteLoginRequest(
    Guid ChallengeId,
    AuthenticatorAssertionRawResponse Credential);

public sealed record PasskeyCredentialDto(
    Guid Id,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    Guid? Aaguid);

public sealed record PasskeyAuthenticationResult(Guid AuthUserId);

public sealed record PasskeyLoginResponse(bool Succeeded);

public sealed class PasskeyAuthenticationException : Exception
{
    public PasskeyAuthenticationException() : base("Passkey authentication failed.") { }
    public PasskeyAuthenticationException(Exception innerException) : base("Passkey authentication failed.", innerException) { }
}

public sealed class PasskeyRegistrationException : Exception
{
    public PasskeyRegistrationException(string message) : base(message) { }
    public PasskeyRegistrationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class PasskeyChallengeException : Exception
{
    public PasskeyChallengeException() : base("Passkey challenge is invalid, expired, or already consumed.") { }
}
