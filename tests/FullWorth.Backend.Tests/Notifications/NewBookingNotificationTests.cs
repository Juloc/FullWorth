using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Modules.Notifications;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FullWorth.Backend.Tests.Notifications;

/// <summary>
/// "Neue Buchungen": one notification per fetch and person, grouped when there are several. The ingest decides
/// what is news (created in THIS fetch, on an account that already had history, pending entry not announced
/// before); the service adds the rest of the filter and the audience.
/// </summary>
public sealed class NewBookingNotificationTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed class FakePushSender : IPushSender
    {
        public List<(Guid UserId, PushMessage Message)> Sent { get; } = [];
        public Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct)
        {
            Sent.Add((userId, message));
            return Task.CompletedTask;
        }
    }

    private sealed record World(BackendWebApplicationFactory Factory, Guid Space, Guid Owner, Guid Other, Guid Connection, FakePushSender Push);

    private static async Task<World> CreateAsync(string? preferenceJson = null, string? displayJson = null)
    {
        var factory = new BackendWebApplicationFactory();
        var world = new World(factory, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new FakePushSender());
        await factory.SeedAsync(async db =>
        {
            foreach (var user in new[] { world.Owner, world.Other })
                db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@EX.COM".ToUpperInvariant(), DisplayName = "U", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = world.Space, UserId = world.Owner, Role = FullWorthSpaceRoles.Owner });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = world.Space, UserId = world.Other, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection
            {
                Id = world.Connection, FullWorthSpaceId = world.Space, Provider = "enable-banking", InstitutionName = "Bank",
                Country = "DE", ProviderSessionId = $"s-{world.Connection:N}", AuthorizationUserId = world.Owner
            });
            if (preferenceJson is not null)
                db.Set<UserPreference>().Add(new UserPreference { FinanceUserId = world.Owner, FullWorthSpaceId = world.Space, Key = "notifications.types", ValueJson = preferenceJson });
            if (displayJson is not null)
                db.Set<UserPreference>().Add(new UserPreference { FinanceUserId = world.Owner, FullWorthSpaceId = world.Space, Key = "notifications.display", ValueJson = displayJson });
            await db.SaveChangesAsync();
        });
        return world;
    }

    private static TransactionBatchItem Booking(string key, decimal amount, string name, string status = "BOOK", DateOnly? date = null) =>
        new("acct-hash", key, "pointer", status, date ?? Today, date ?? Today, amount, "EUR", name, null, null, null, "{}");

    private static async Task IngestAsync(World world, params TransactionBatchItem[] bookings)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var dispatcher = new NotificationDispatcher(db, new PreferenceStore(db), world.Push, NullLogger<NotificationDispatcher>.Instance);
        var notifications = new NewBookingNotificationService(db, new PreferenceStore(db), dispatcher, NullLogger<NewBookingNotificationService>.Instance);
        var service = new IngestionService(db, newBookingNotifications: notifications);

        await service.IngestAsync(
            new FinanceIngestBatch(
                new BankConnectionBatch(world.Connection, "enable-banking", "Bank", "DE", $"s-{world.Connection:N}", "AUTHORIZED", null, null, null, world.Space),
                [new AccountBatchItem("acct-hash", "acct-1", "Bank", "Main", null, null, "EUR", null, true)],
                [],
                bookings),
            CancellationToken.None);
    }

    [Fact]
    public async Task The_first_fetch_of_an_account_is_an_import_and_says_nothing()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;

        await IngestAsync(w, Booking("a", -10m, "REWE"), Booking("b", -20m, "Penny"));

        Assert.Empty(w.Push.Sent);
    }

    [Fact]
    public async Task A_later_booking_is_announced_with_the_merchant_and_without_the_amount_by_default()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("new", -42.19m, "REWE Markt"));

        var (user, message) = Assert.Single(w.Push.Sent);
        Assert.Equal(w.Owner, user);
        Assert.Equal("Neue Buchung", message.Title);
        Assert.Equal("REWE Markt", message.Body);
        Assert.Equal("/transactions", message.Url);
    }

    [Fact]
    public async Task The_amount_appears_only_when_the_person_allowed_it()
    {
        var w = await CreateAsync(displayJson: "{\"showAmounts\":true}");
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("new", -42.19m, "REWE Markt"));

        Assert.Equal("REWE Markt −42,19 €", Assert.Single(w.Push.Sent).Message.Body);
    }

    [Fact]
    public async Task Several_new_bookings_of_one_fetch_become_one_grouped_notification()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w,
            Booking("1", -1m, "A"), Booking("2", -2m, "B"), Booking("3", -3m, "C"), Booking("4", -4m, "D"), Booking("5", -5m, "E"));

        var message = Assert.Single(w.Push.Sent).Message;
        Assert.Equal("5 neue Buchungen", message.Title);
        Assert.Equal(4, message.Body.Split('\n').Length);
        Assert.EndsWith("+ 2 weitere", message.Body);
    }

    // The pending entry was announced in an earlier fetch; the booking that replaces it is the same payment.
    [Fact]
    public async Task A_booking_that_replaces_an_announced_pending_entry_is_not_announced_again()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));
        await IngestAsync(w, Booking("fp:pending", -25m, "Werkstatt", status: "PDNG"));
        Assert.Single(w.Push.Sent);

        await IngestAsync(w, Booking("er:booked", -25m, "Werkstatt"));

        Assert.Single(w.Push.Sent);
    }

    // Pending and booking arrive together: one payment, one announcement.
    [Fact]
    public async Task A_pending_entry_and_its_booking_in_the_same_fetch_are_announced_once()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("fp:pending", -25m, "Werkstatt", status: "PDNG"), Booking("er:booked", -25m, "Werkstatt"));

        var message = Assert.Single(w.Push.Sent).Message;
        Assert.Equal("Neue Buchung", message.Title);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_person_switched_the_type_off()
    {
        var w = await CreateAsync(preferenceJson: "{\"types\":{\"transactions_new\":false}}");
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("new", -42.19m, "REWE Markt"));

        Assert.Empty(w.Push.Sent);
    }

    // A reconnection after a long gap brings old payments; they are not news.
    [Fact]
    public async Task A_booking_older_than_a_week_is_not_announced()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("late", -9m, "Nachzügler", date: Today.AddDays(-20)));

        Assert.Empty(w.Push.Sent);
    }

    // A push must not show one person's payments to someone who is only a member of the space.
    [Fact]
    public async Task Only_the_owners_of_the_account_are_told()
    {
        var w = await CreateAsync();
        using var factory = w.Factory;
        await IngestAsync(w, Booking("old", -5m, "Bäcker"));

        await IngestAsync(w, Booking("new", -42.19m, "REWE Markt"));

        Assert.DoesNotContain(w.Push.Sent, sent => sent.UserId == w.Other);
    }

    [Fact]
    public void The_message_text_is_short_and_stays_under_the_lock_screen_limit_for_long_names()
    {
        var lines = Enumerable.Range(0, 10).Select(i => new NewBookingLine(new string('x', 200) + i, -1m, "EUR")).ToList();

        var message = NotificationMessages.NewBookings(lines, showAmounts: true);

        Assert.True(message.Body.Split('\n').All(line => line.Length < 120));
    }
}
