using FullWorth.Web.Data;
using FullWorth.Web.Modules.Admin;
using FullWorth.Web.Tests.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// Which external sign-in providers this installation offers is stored, not deployed.
///
/// Google and Apple were six environment variables in the deploy stack's compose file, so turning a login
/// button on meant editing YAML and restarting the whole stack — and a compose file ended up carrying
/// credentials that say nothing about how the containers are wired.
///
/// Two rules matter more than the storage itself: a secret never travels back to a browser, and an empty
/// field means "leave the stored one alone" rather than "delete it".
/// </summary>
public sealed class ExternalAuthSettingsTests
{
    [Fact]
    public async Task An_installation_with_no_providers_reports_none()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        var view = await store.GetViewAsync(CancellationToken.None);

        Assert.Equal(string.Empty, view.GoogleClientId);
        Assert.False(view.GoogleClientSecretStored);
        Assert.False(view.ApplePrivateKeyStored);
        Assert.Null(await store.GetGoogleAsync(CancellationToken.None));
        Assert.Null(await store.GetAppleAsync(CancellationToken.None));
    }

    /// <summary>
    /// The secret is stored encrypted and reported only as "there is one". A credential that has been
    /// saved has no reason to go back to a browser, and a form that round-trips it turns every page load
    /// into another chance to leak it.
    /// </summary>
    [Fact]
    public async Task A_saved_secret_is_encrypted_and_never_read_back_out()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        var view = await store.SetAsync(
            new ExternalAuthSettingsWrite("google-client-id", "the-google-secret", null, null, null, null),
            CancellationToken.None);

        Assert.Equal("google-client-id", view.GoogleClientId);
        Assert.True(view.GoogleClientSecretStored);

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await db.Set<ExternalAuthSettings>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain("the-google-secret", row.GoogleClientSecretProtected);

        // And it still comes back for the thing that actually needs it.
        var credentials = await store.GetGoogleAsync(CancellationToken.None);
        Assert.Equal("the-google-secret", credentials!.ClientSecret);
    }

    /// <summary>
    /// An untouched secret field is null, and null means "keep it". Treating it as an empty value would
    /// wipe the secret every time somebody corrected a typo in the client id.
    /// </summary>
    [Fact]
    public async Task Saving_without_a_secret_keeps_the_stored_one()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        await store.SetAsync(
            new ExternalAuthSettingsWrite("first-id", "keep-me", null, null, null, null), CancellationToken.None);
        await store.SetAsync(
            new ExternalAuthSettingsWrite("second-id", null, null, null, null, null), CancellationToken.None);

        var credentials = await store.GetGoogleAsync(CancellationToken.None);
        Assert.Equal("second-id", credentials!.ClientId);
        Assert.Equal("keep-me", credentials.ClientSecret);
    }

    /// <summary>An explicit empty string is the deliberate "remove it".</summary>
    [Fact]
    public async Task An_empty_secret_removes_the_stored_one()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        await store.SetAsync(
            new ExternalAuthSettingsWrite("an-id", "remove-me", null, null, null, null), CancellationToken.None);
        await store.SetAsync(
            new ExternalAuthSettingsWrite("an-id", "", null, null, null, null), CancellationToken.None);

        Assert.Null(await store.GetGoogleAsync(CancellationToken.None));
        Assert.False((await store.GetViewAsync(CancellationToken.None)).GoogleClientSecretStored);
    }

    /// <summary>
    /// Apple needs all four parts. Three of them is not "partly configured", it is not configured — and
    /// registering the scheme anyway makes every request on the instance fail, because ASP.NET validates
    /// a remote handler's options while asking it whether it wants the path.
    /// </summary>
    [Fact]
    public async Task Apple_needs_every_part_before_it_counts_as_configured()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        await store.SetAsync(
            new ExternalAuthSettingsWrite(null, null, "service", "team", "key-id", null), CancellationToken.None);
        Assert.Null(await store.GetAppleAsync(CancellationToken.None));

        await store.SetAsync(
            new ExternalAuthSettingsWrite(null, null, "service", "team", "key-id", "-----BEGIN PRIVATE KEY-----"),
            CancellationToken.None);
        Assert.NotNull(await store.GetAppleAsync(CancellationToken.None));
    }

    /// <summary>
    /// There is exactly one row, and the database enforces it. A second "instance" row would make which
    /// providers this installation offers depend on insertion order.
    /// </summary>
    [Fact]
    public async Task Saving_twice_updates_the_one_row()
    {
        var (services, database) = await BuildAsync();
        await using var _services = services;
        await using var _database = database;
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();

        await store.SetAsync(new ExternalAuthSettingsWrite("a", "x", null, null, null, null), CancellationToken.None);
        await store.SetAsync(new ExternalAuthSettingsWrite("b", "y", null, null, null, null), CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(1, await db.Set<ExternalAuthSettings>().CountAsync());
    }

    /// <summary>
    /// Built through the shared auth test services, not a hand-rolled ServiceCollection.
    ///
    /// Identity's own column widths come from IdentityOptions.Stores.MaxLengthForKeys, which only
    /// exists when AddIdentityCore has run. A container without it produces a DIFFERENT model - text
    /// instead of character varying(128) - and EF then refuses to migrate with
    /// PendingModelChangesWarning, which reads like a broken migration rather than a test that built
    /// the wrong container.
    /// </summary>
    private static async Task<(ServiceProvider Services, PostgresAuthDatabase Database)> BuildAsync()
    {
        var database = await PostgresAuthDatabase.CreateAsync();
        var services = AuthTestServices.Build(database.ConnectionString);

        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
        return (services, database);
    }
}
