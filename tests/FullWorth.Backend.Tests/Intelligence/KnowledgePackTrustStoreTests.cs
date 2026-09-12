using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// How an installation comes to trust a pack verification key.
///
/// It used to be handed one: a shell script copied the key out of a Docker volume the Cloud and the app
/// stack shared. That is possible on exactly one host — the one running both — and the fallback key
/// compiled into the build has always been empty. So for everyone who is not the Cloud's own operator,
/// the honest description of pack verification was: enroll, download a pack, reject it, repeat every few
/// minutes forever.
///
/// The key is now fetched from the Cloud the instance is already enrolled with and pinned. That is a
/// trust decision, not a detail — the tests below pin down its two halves: the first key is taken, and
/// no later key ever replaces it on its own.
/// </summary>
public sealed class KnowledgePackTrustStoreTests
{
    [Fact]
    public async Task An_installation_with_no_key_fetches_one_from_the_cloud_and_pins_it()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var cloud = fixture.CloudOffering(rsa);
        var store = fixture.Store(cloud);

        var resolved = await store.EnsurePinnedAsync("credential", CancellationToken.None);

        Assert.Equal(rsa.ExportSubjectPublicKeyInfoPem(), resolved);
        var pinned = await store.GetAsync(CancellationToken.None);
        Assert.NotNull(pinned);
        Assert.Equal("https://cloud.test", pinned!.Endpoint);
        Assert.Equal(KnowledgePackTrustStore.FingerprintOf(resolved!), pinned.Fingerprint);

        // And it is not asked for again: the pin is what the next sync reads.
        Assert.Equal(resolved, await store.EnsurePinnedAsync("credential", CancellationToken.None));
        Assert.Equal(1, cloud.PublicKeyRequestCount);
    }

    /// <summary>
    /// The half that makes the pin worth anything. A Cloud that later presents a different key is
    /// recorded and refused — adopting it silently would mean an endpoint takeover could hand this
    /// installation another publisher's packs and have them verify.
    /// </summary>
    [Fact]
    public async Task A_different_key_offered_later_is_recorded_but_never_adopted()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var cloud = fixture.CloudOffering(first);
        var store = fixture.Store(cloud);
        await store.EnsurePinnedAsync("credential", CancellationToken.None);

        cloud.OfferedPublicKey = KnowledgePackTrustFixture.KeyOf(second);
        var changed = await store.NoteOfferedKeyAsync("credential", CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(
            first.ExportSubjectPublicKeyInfoPem(),
            await store.EnsurePinnedAsync("credential", CancellationToken.None));

        var view = await store.GetViewAsync(CancellationToken.None);
        Assert.Equal(KnowledgePackTrustStore.PinnedSource, view.Source);
        Assert.Equal(KnowledgePackTrustStore.FingerprintOf(second.ExportSubjectPublicKeyInfoPem()), view.OfferedFingerprint);
        Assert.NotNull(view.OfferedAt);
    }

    /// <summary>Accepting a rotation is the one act that moves the pin, and it is a person's.</summary>
    [Fact]
    public async Task Accepting_the_offered_key_replaces_the_pin_and_clears_the_warning()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var cloud = fixture.CloudOffering(first);
        var store = fixture.Store(cloud);
        await store.EnsurePinnedAsync("credential", CancellationToken.None);
        cloud.OfferedPublicKey = KnowledgePackTrustFixture.KeyOf(second);
        await store.NoteOfferedKeyAsync("credential", CancellationToken.None);

        var view = await store.AcceptOfferedKeyAsync("credential", CancellationToken.None);

        Assert.Equal(KnowledgePackTrustStore.FingerprintOf(second.ExportSubjectPublicKeyInfoPem()), view.Fingerprint);
        Assert.Null(view.OfferedFingerprint);
        Assert.Equal(
            second.ExportSubjectPublicKeyInfoPem(),
            await store.EnsurePinnedAsync("credential", CancellationToken.None));
    }

    /// <summary>
    /// An operator who states a key keeps the last word. Their configuration is the same decision this
    /// store automates, made by hand — automating it must not overrule it.
    /// </summary>
    [Fact]
    public async Task A_configured_key_wins_over_anything_the_cloud_offers()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        using var configured = RSA.Create(2048);
        using var offered = RSA.Create(2048);
        var cloud = fixture.CloudOffering(offered);
        var store = fixture.Store(cloud, configured);

        var resolved = await store.EnsurePinnedAsync("credential", CancellationToken.None);

        Assert.Equal(configured.ExportSubjectPublicKeyInfoPem(), resolved);
        Assert.Equal(0, cloud.PublicKeyRequestCount);   // nothing was even asked for
        Assert.Null(await store.GetAsync(CancellationToken.None));
        Assert.Equal(KnowledgePackTrustStore.ConfiguredSource, (await store.GetViewAsync(CancellationToken.None)).Source);
    }

    /// <summary>
    /// A Cloud with no key to offer leaves the installation unpinned rather than storing something
    /// unusable. A key pinned once but not importable would fail every verification for good.
    /// </summary>
    [Fact]
    public async Task Nothing_usable_on_offer_pins_nothing()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        var cloud = fixture.Cloud();

        cloud.OfferedPublicKey = null;
        Assert.Null(await fixture.Store(cloud).EnsurePinnedAsync("credential", CancellationToken.None));

        cloud.OfferedPublicKey = new FullWorthCloudPublicKey("RSA-PSS-SHA256", "sha256:nope", "not a pem");
        Assert.Null(await fixture.Store(cloud).EnsurePinnedAsync("credential", CancellationToken.None));

        Assert.Null(await fixture.Store(cloud).GetAsync(CancellationToken.None));
        Assert.Equal(KnowledgePackTrustStore.NoneSource, (await fixture.Store(cloud).GetViewAsync(CancellationToken.None)).Source);
    }

    /// <summary>
    /// The pin belongs to the Cloud it came from. Pointing an instance at a different endpoint must not
    /// inherit the previous one's trust — a pin is a statement about one publisher, not a global setting.
    /// </summary>
    [Fact]
    public async Task A_pin_does_not_carry_over_to_a_different_cloud()
    {
        await using var fixture = await KnowledgePackTrustFixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        await fixture.Store(fixture.CloudOffering(rsa)).EnsurePinnedAsync("credential", CancellationToken.None);

        var elsewhere = fixture.Cloud(new Uri("https://someone-elses.test/"));
        elsewhere.OfferedPublicKey = null;

        Assert.Null(await fixture.Store(elsewhere).EnsurePinnedAsync("credential", CancellationToken.None));
    }

    [Fact]
    public void The_endpoint_of_a_pin_is_the_origin_and_nothing_else()
    {
        Assert.Equal("https://api.fullworth.de", KnowledgePackTrustStore.Normalize(new Uri("https://API.FullWorth.de/")));
        Assert.Equal("https://api.fullworth.de", KnowledgePackTrustStore.Normalize(new Uri("https://api.fullworth.de/v1/x")));
        Assert.Equal("https://api.fullworth.de:8443", KnowledgePackTrustStore.Normalize(new Uri("https://api.fullworth.de:8443/")));
    }
}
