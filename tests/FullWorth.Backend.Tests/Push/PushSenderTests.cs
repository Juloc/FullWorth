using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Push;

public sealed class PushSenderTests
{
    [Fact]
    public async Task A_user_without_devices_costs_neither_a_send_nor_a_generated_key()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var keys = new PushKeyStore(db, Options.Create(new PushOptions()), FieldCipher.Null);
        var sender = new VapidPushSender(db, Options.Create(new PushOptions()), keys, NullLogger<VapidPushSender>.Instance);

        await sender.SendToUserAsync(Guid.NewGuid(), new PushMessage("Title", "Body"), CancellationToken.None);

        Assert.Equal(0, await db.PushInstanceKeys.CountAsync());
    }

    // Nobody has to generate or place a key: the installation makes one on first need and keeps it, because
    // every subscription is bound to its public half.
    [Fact]
    public async Task Without_configured_keys_the_installation_generates_one_pair_and_keeps_it()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new PushKeyStore(db, Options.Create(new PushOptions()), FieldCipher.Null);

        var first = await store.GetAsync(CancellationToken.None);
        var second = await store.GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(first.PublicKey));
        Assert.False(string.IsNullOrWhiteSpace(first.PrivateKey));
        Assert.Equal(first, second);
        Assert.Equal(1, await db.PushInstanceKeys.CountAsync());
    }

    [Fact]
    public async Task A_configured_pair_wins_and_nothing_is_stored()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new PushKeyStore(db, Options.Create(new PushOptions { VapidPublicKey = "pk", VapidPrivateKey = "sk" }), FieldCipher.Null);

        var keys = await store.GetAsync(CancellationToken.None);

        Assert.Equal(new VapidKeys("pk", "sk"), keys);
        Assert.Equal(0, await db.PushInstanceKeys.CountAsync());
    }

    [Fact]
    public void PushOptions_IsConfigured_RequiresBothKeys()
    {
        Assert.False(new PushOptions().IsConfigured);
        Assert.False(new PushOptions { VapidPublicKey = "pk" }.IsConfigured);
        Assert.True(new PushOptions { VapidPublicKey = "pk", VapidPrivateKey = "sk" }.IsConfigured);
    }
}
