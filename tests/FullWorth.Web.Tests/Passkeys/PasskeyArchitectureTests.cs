using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Passkeys;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class PasskeyArchitectureTests
{
    [Fact]
    public void Password_fallback_service_remains_available()
    {
        Assert.NotNull(typeof(AuthService).GetMethod(nameof(AuthService.ValidatePasswordAsync)));
        Assert.NotNull(typeof(AuthService).GetMethod(nameof(AuthService.ResetPasswordAsync)));
        Assert.NotNull(typeof(AuthService).GetMethod(nameof(AuthService.ChangePasswordAsync)));
    }

    [Fact]
    public void Recovery_codes_are_reused_not_duplicated_in_passkey_module()
    {
        Assert.NotNull(typeof(RecoveryService));
        var passkeyTypes = typeof(PasskeyService).Assembly.GetTypes()
            .Where(x => x.Namespace == typeof(PasskeyService).Namespace)
            .ToArray();
        Assert.DoesNotContain(passkeyTypes, x => x.Name.Contains("RecoveryCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Passkeys_reuse_existing_session_types_instead_of_defining_new_session_model()
    {
        Assert.NotNull(typeof(SessionService));
        Assert.NotNull(typeof(SessionClaims));
        var passkeyTypes = typeof(PasskeyService).Assembly.GetTypes()
            .Where(x => x.Namespace == typeof(PasskeyService).Namespace)
            .ToArray();
        Assert.DoesNotContain(passkeyTypes, x => x.Name == "UserSession");
        Assert.DoesNotContain(passkeyTypes, x => x.Name.Contains("Jwt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credential_model_contains_no_private_key_field()
    {
        var members = typeof(PasskeyCredential).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(members, x => x.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(PasskeyCredential.PublicKey), members);
    }

    [Fact]
    public void Public_credential_dto_does_not_expose_verifier_material()
    {
        var members = typeof(PasskeyCredentialDto).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(nameof(PasskeyCredential.PublicKey), members);
        Assert.DoesNotContain(nameof(PasskeyCredential.UserHandle), members);
        Assert.DoesNotContain(nameof(PasskeyCredential.SignatureCounter), members);
        Assert.DoesNotContain(nameof(PasskeyCredential.CredentialId), members);
    }

    [Fact]
    public void Production_passkey_origins_require_https()
    {
        var options = PasskeyTestFactory.Options;
        options.Origins = ["http://finance.example"];

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(production: true));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_localhost_origin_remains_supported()
    {
        var options = PasskeyTestFactory.Options;
        options.Origins = ["http://localhost:8098"];

        options.Validate(production: false);
    }
}
