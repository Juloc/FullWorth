using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Push;

public sealed class PushSenderTests
{
    [Fact]
    public async Task SendToUser_IsNoOp_WhenVapidNotConfigured()
    {
        // Unconfigured push must short-circuit before touching the database, so this never connects.
        var db = new FullWorthDbContext(new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var sender = new VapidPushSender(db, Options.Create(new PushOptions()), NullLogger<VapidPushSender>.Instance);

        await sender.SendToUserAsync(Guid.NewGuid(), new PushMessage("Title", "Body"), CancellationToken.None);
    }

    [Fact]
    public void PushOptions_IsConfigured_RequiresBothKeys()
    {
        Assert.False(new PushOptions().IsConfigured);
        Assert.False(new PushOptions { VapidPublicKey = "pk" }.IsConfigured);
        Assert.True(new PushOptions { VapidPublicKey = "pk", VapidPrivateKey = "sk" }.IsConfigured);
    }
}
