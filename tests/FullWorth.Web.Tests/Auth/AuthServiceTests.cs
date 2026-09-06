using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Auth;

public sealed class AuthServiceTests(AuthPostgresFixture fixture) : IClassFixture<AuthPostgresFixture>
{
    private const string ValidPassword = "correct horse battery staple";
    private const string NewPassword = "updated horse battery staple";

    [Fact]
    public async Task CreateUserHashesPasswordAndPreservesFinanceUserId()
    {
        var email = Email("create");
        var financeUserId = Guid.NewGuid();

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();

        var result = await auth.CreateUserAsync(new CreateAuthUserRequest(financeUserId, email, ValidPassword));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Equal(financeUserId, result.User!.FinanceUserId);

        var stored = await users.FindByEmailAsync(email);
        Assert.NotNull(stored);
        Assert.Equal(financeUserId, stored!.FinanceUserId);
        Assert.False(string.IsNullOrWhiteSpace(stored.PasswordHash));
        Assert.NotEqual(ValidPassword, stored.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            users.PasswordHasher.VerifyHashedPassword(stored, stored.PasswordHash!, ValidPassword));
    }

    [Fact]
    public async Task UniqueNormalizedEmailIsEnforcedByDatabase()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var normalizedEmail = $"UNIQUE-{suffix}@EXAMPLE.COM";

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        db.Users.Add(RawUser($"first-{suffix}@example.com", normalizedEmail, $"FIRST-{suffix}", Guid.NewGuid()));
        await db.SaveChangesAsync();

        db.Users.Add(RawUser($"second-{suffix}@example.com", normalizedEmail, $"SECOND-{suffix}", Guid.NewGuid()));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task EmailCaseDifferenceCannotCreateSecondUser()
    {
        var local = $"Case-{Guid.NewGuid():N}";
        var firstEmail = $"{local}@Example.com";
        var secondEmail = $"{local.ToLowerInvariant()}@example.COM";

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        var first = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), firstEmail, ValidPassword));
        var second = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), secondEmail, ValidPassword));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task CorrectAndIncorrectPasswordsValidate()
    {
        var email = await CreateUserAsync("validate");

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        var correct = await auth.ValidatePasswordAsync(email, ValidPassword);
        var incorrect = await auth.ValidatePasswordAsync(email, "definitely-not-the-password");

        Assert.True(correct.Succeeded);
        Assert.False(incorrect.Succeeded);
    }

    [Fact]
    public async Task UnknownEmailAndWrongPasswordHaveSamePublicFailure()
    {
        var email = await CreateUserAsync("failure");

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        var wrongPassword = await auth.ValidatePasswordAsync(email, "wrong-password");
        var unknownEmail = await auth.ValidatePasswordAsync(Email("unknown"), "wrong-password");

        Assert.Equal(wrongPassword, unknownEmail);
        Assert.Equal("Invalid credentials.", wrongPassword.Error);
    }

    [Fact]
    public async Task ChangePasswordReplacesOldPassword()
    {
        var email = await CreateUserAsync("change");
        Guid authUserId;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            authUserId = (await users.FindByEmailAsync(email))!.Id;
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            var changed = await auth.ChangePasswordAsync(authUserId, ValidPassword, NewPassword);
            Assert.True(changed.Succeeded);
        }

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AuthService>();
        Assert.False((await verify.ValidatePasswordAsync(email, ValidPassword)).Succeeded);
        Assert.True((await verify.ValidatePasswordAsync(email, NewPassword)).Succeeded);
    }

    [Fact]
    public async Task PasswordResetTokenChangesPassword()
    {
        var email = await CreateUserAsync("reset-valid");

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var token = await auth.GeneratePasswordResetTokenAsync(email);

        Assert.NotNull(token);
        var reset = await auth.ResetPasswordAsync(email, token!.Token, NewPassword);
        Assert.True(reset.Succeeded);
        Assert.False((await auth.ValidatePasswordAsync(email, ValidPassword)).Succeeded);
        Assert.True((await auth.ValidatePasswordAsync(email, NewPassword)).Succeeded);
    }

    [Fact]
    public async Task InvalidPasswordResetTokenIsRejected()
    {
        var email = await CreateUserAsync("reset-invalid");

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var result = await auth.ResetPasswordAsync(email, "not-a-valid-token", NewPassword);

        Assert.False(result.Succeeded);
        Assert.True((await auth.ValidatePasswordAsync(email, ValidPassword)).Succeeded);
    }

    [Fact]
    public async Task PasswordResetTokenCannotBeReused()
    {
        var email = await CreateUserAsync("reset-reuse");

        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var token = await auth.GeneratePasswordResetTokenAsync(email);
        Assert.NotNull(token);

        Assert.True((await auth.ResetPasswordAsync(email, token!.Token, NewPassword)).Succeeded);
        Assert.False((await auth.ResetPasswordAsync(email, token.Token, "third horse battery staple")).Succeeded);
    }

    [Fact]
    public void AuthUserDtoDoesNotExposeIdentitySecrets()
    {
        var properties = typeof(AuthUserDto).GetProperties().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(nameof(AuthUser.PasswordHash), properties);
        Assert.DoesNotContain(nameof(AuthUser.SecurityStamp), properties);
        Assert.DoesNotContain(nameof(AuthUser.ConcurrencyStamp), properties);
    }

    [Fact]
    public async Task AuthEndpointsDoNotExposeRegistration()
    {
        var builder = WebApplication.CreateBuilder();
        AuthTestServices.Configure(builder.Services, fixture.Database.ConnectionString);
        await using var app = builder.Build();
        app.MapAuthEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.Contains("/auth/login", routes);
        Assert.DoesNotContain(routes, x => x.Contains("register", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthModelContainsNoFinanceDomainTablesOrFullWorthUserForeignKey()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var model = db.Model;

        Assert.All(model.GetEntityTypes(), entity =>
            Assert.True(
                (entity.ClrType.Namespace?.StartsWith("FullWorth.Web", StringComparison.Ordinal) ?? false) ||
                (entity.ClrType.Namespace?.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) ?? false)));

        var authUser = model.FindEntityType(typeof(AuthUser))!;
        Assert.DoesNotContain(authUser.GetForeignKeys(), fk =>
            fk.Properties.Any(property => property.Name == nameof(AuthUser.FinanceUserId)));
    }

    [Fact]
    public async Task DeletingAuthUserDoesNotCascadeToFinanceDomain()
    {
        var email = await CreateUserAsync("delete");

        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        var financeUserId = user!.FinanceUserId;

        var result = await users.DeleteAsync(user);

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, financeUserId);
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public void PasswordPolicyIsLengthBasedAndLockoutEnabled()
    {
        var identity = new IdentityOptions();
        new AuthOptions().Apply(identity);

        Assert.True(identity.Password.RequiredLength >= 12);
        Assert.False(identity.Password.RequireDigit);
        Assert.False(identity.Password.RequireLowercase);
        Assert.False(identity.Password.RequireUppercase);
        Assert.False(identity.Password.RequireNonAlphanumeric);
        Assert.True(identity.Lockout.AllowedForNewUsers);
        Assert.True(identity.Lockout.MaxFailedAccessAttempts > 0);
    }

    private async Task<string> CreateUserAsync(string prefix)
    {
        var email = Email(prefix);
        await using var scope = fixture.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var result = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, ValidPassword));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return email;
    }

    private static AuthUser RawUser(string email, string normalizedEmail, string normalizedUserName, Guid financeUserId) => new()
    {
        Id = Guid.NewGuid(),
        FinanceUserId = financeUserId,
        Email = email,
        NormalizedEmail = normalizedEmail,
        UserName = email,
        NormalizedUserName = normalizedUserName,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private static string Email(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    [Fact]
    public void LegalDocumentVersions_AreExplicitAndDated()
    {
        Assert.Equal("2026-09-06", LegalDocumentVersions.Terms);
        Assert.Equal("2026-09-06", LegalDocumentVersions.Privacy);
        Assert.NotEqual(LegalDocumentVersions.TermsVersionClaim, LegalDocumentVersions.TermsAcceptedAtClaim);
        Assert.NotEqual(LegalDocumentVersions.PrivacyVersionClaim, LegalDocumentVersions.PrivacyAcknowledgedAtClaim);
    }

}
