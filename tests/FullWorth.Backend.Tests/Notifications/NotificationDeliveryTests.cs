using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Notifications;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FullWorth.Backend.Tests.Notifications;

// Web-push delivery wiring (UI_UX_SPEC §20). The VAPID sender itself is tested elsewhere; here a fake
// IPushSender captures what each hook + the dispatcher would deliver, proving preference-gating, dedup,
// recipient scoping, and threshold logic.
public sealed class NotificationDeliveryTests
{
    private sealed class FakePushSender : IPushSender
    {
        public List<(Guid UserId, PushMessage Message)> Sent { get; } = [];
        public Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct)
        {
            Sent.Add((userId, message));
            return Task.CompletedTask;
        }
    }

    private static NotificationDispatcher Dispatcher(FullWorthDbContext db, FakePushSender fake) =>
        new(db, new PreferenceStore(db), fake, NullLogger<NotificationDispatcher>.Instance);

    [Fact]
    public async Task Dispatcher_HonorsDisabledType_AndDedupsByKey()
    {
        var space = Guid.NewGuid();
        var user = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = "U@EX.COM", DisplayName = "U", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = user, Role = FullWorthSpaceRoles.Owner });
            db.Set<UserPreference>().Add(new UserPreference { FinanceUserId = user, FullWorthSpaceId = space, Key = "notifications.types", ValueJson = "{\"types\":{\"budget_over\":false}}" });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var dispatcher = Dispatcher(db, fake);
        var msg = new PushMessage("t", "b", "/budgets");

        // Disabled type -> suppressed.
        await dispatcher.DispatchAsync(user, space, NotificationTypes.BudgetOver, msg, null, CancellationToken.None);
        Assert.Empty(fake.Sent);

        // Enabled/absent type -> sends; same dedup key -> once; different key -> again.
        await dispatcher.DispatchAsync(user, space, NotificationTypes.BudgetNear, msg, "k1", CancellationToken.None);
        await dispatcher.DispatchAsync(user, space, NotificationTypes.BudgetNear, msg, "k1", CancellationToken.None);
        await dispatcher.DispatchAsync(user, space, NotificationTypes.BudgetNear, msg, "k2", CancellationToken.None);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Fact]
    public async Task Budget_FiresNearAndOverOncePerCycle_OverSupersedesNear()
    {
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var conn = Guid.NewGuid();
        var account = Guid.NewGuid();
        var catNear = Guid.NewGuid();
        var catOver = Guid.NewGuid();
        var budgetNear = Guid.NewGuid();
        var budgetOver = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = "O@EX.COM", DisplayName = "O", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = conn, FullWorthSpaceId = space, Provider = "test", InstitutionName = "B", Country = "DE", ProviderSessionId = $"b-{conn:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = conn, Provider = "test", IdentificationHash = $"a-{account:N}", ProviderAccountId = $"a-{account:N}", InstitutionName = "B", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = catNear, FullWorthSpaceId = space, Key = $"n-{catNear:N}", Name = "Near" });
            db.Categories.Add(new FinanceCategory { Id = catOver, FullWorthSpaceId = space, Key = $"o-{catOver:N}", Name = "Over" });
            db.Budgets.Add(new Budget { Id = budgetNear, FullWorthSpaceId = space, Name = "Groceries", CategoryId = catNear, Amount = 100m, Currency = "EUR", Period = "monthly", IsActive = true });
            db.Budgets.Add(new Budget { Id = budgetOver, FullWorthSpaceId = space, Name = "Fun", CategoryId = catOver, Amount = 100m, Currency = "EUR", Period = "monthly", IsActive = true });
            db.Transactions.Add(Tx(account, catNear, -85m, new DateOnly(2026, 8, 10)));  // 85% -> near
            db.Transactions.Add(Tx(account, catOver, -120m, new DateOnly(2026, 8, 10))); // 120% -> over
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var service = new BudgetNotificationService(db, new BudgetStore(db), Dispatcher(db, fake), NullLogger<BudgetNotificationService>.Instance);

        await service.EvaluateAndDispatchAsync(space, new DateOnly(2026, 8, 20), CancellationToken.None);
        Assert.Equal(2, fake.Sent.Count);
        Assert.Contains(fake.Sent, s => s.Message.Title.Contains("nahe"));            // near for Groceries
        Assert.Contains(fake.Sent, s => s.Message.Title.Contains("überschritten"));   // over for Fun
        Assert.DoesNotContain(fake.Sent, s => s.Message.Title.Contains("nahe") && s.Message.Body.Contains("Fun")); // Fun never buzzed "near"

        // Second evaluation in the same cycle dedups everything.
        await service.EvaluateAndDispatchAsync(space, new DateOnly(2026, 8, 20), CancellationToken.None);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Fact]
    public async Task Budget_OverDisabled_FallsBackToNear_NotSilence()
    {
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var conn = Guid.NewGuid();
        var account = Guid.NewGuid();
        var cat = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = "O4@EX.COM", DisplayName = "O", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            // Over disabled, near left default-on.
            db.Set<UserPreference>().Add(new UserPreference { FinanceUserId = owner, FullWorthSpaceId = space, Key = "notifications.types", ValueJson = "{\"types\":{\"budget_over\":false}}" });
            db.BankConnections.Add(new BankConnection { Id = conn, FullWorthSpaceId = space, Provider = "test", InstitutionName = "B", Country = "DE", ProviderSessionId = $"b-{conn:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = conn, Provider = "test", IdentificationHash = $"a-{account:N}", ProviderAccountId = $"a-{account:N}", InstitutionName = "B", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = cat, FullWorthSpaceId = space, Key = $"c-{cat:N}", Name = "Fun" });
            db.Budgets.Add(new Budget { Id = Guid.NewGuid(), FullWorthSpaceId = space, Name = "Fun", CategoryId = cat, Amount = 100m, Currency = "EUR", Period = "monthly", IsActive = true });
            db.Transactions.Add(Tx(account, cat, -120m, new DateOnly(2026, 8, 10)));   // straight to 120%
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var service = new BudgetNotificationService(db, new BudgetStore(db), Dispatcher(db, fake), NullLogger<BudgetNotificationService>.Instance);

        await service.EvaluateAndDispatchAsync(space, new DateOnly(2026, 8, 20), CancellationToken.None);
        // "over" is disabled, so the user must still get the "near" alert, not silence.
        Assert.Single(fake.Sent);
        Assert.Contains("nahe", fake.Sent[0].Message.Title);
    }

    [Fact]
    public async Task ContractDue_SelectsInWindowContracts_ToVisibleRecipients_Dedups()
    {
        var today = new DateOnly(2026, 8, 20);
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var conn = Guid.NewGuid();
        var account = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = "O2@EX.COM", DisplayName = "O", IsActive = true });
            db.Users.Add(new FullWorthUser { Id = member, EmailNormalized = "M2@EX.COM", DisplayName = "M", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = conn, FullWorthSpaceId = space, Provider = "test", InstitutionName = "B", Country = "DE", ProviderSessionId = $"b-{conn:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = conn, Provider = "test", IdentificationHash = $"a-{account:N}", ProviderAccountId = $"a-{account:N}", InstitutionName = "B", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Contracts.Add(Contract(space, "Unbound", null, today.AddDays(2), true, null));                 // in window, all members
            db.Contracts.Add(Contract(space, "Bound", account, today.AddDays(1), true, null));                // in window, owner only
            db.Contracts.Add(Contract(space, "TooFar", null, today.AddDays(10), true, null));                 // out of window
            db.Contracts.Add(Contract(space, "Inactive", null, today.AddDays(1), false, null));               // inactive
            db.Contracts.Add(Contract(space, "Ended", null, today.AddDays(1), true, today.AddDays(-1)));      // ended
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var service = new ContractDueNotificationService(db, Dispatcher(db, fake));

        await service.ScanAndDispatchAsync(today, 3, CancellationToken.None);
        // Unbound -> owner + member (2); Bound -> owner only (1). Total 3.
        Assert.Equal(3, fake.Sent.Count);
        Assert.Equal(2, fake.Sent.Count(s => s.Message.Body.Contains("Unbound")));
        Assert.Single(fake.Sent, s => s.Message.Body.Contains("Bound") && s.UserId == owner);
        Assert.DoesNotContain(fake.Sent, s => s.Message.Body.Contains("TooFar") || s.Message.Body.Contains("Inactive") || s.Message.Body.Contains("Ended"));

        await service.ScanAndDispatchAsync(today, 3, CancellationToken.None);   // dedup
        Assert.Equal(3, fake.Sent.Count);
    }

    [Fact]
    public async Task ConnectionTransitions_SyncErrorAndReauth_NotifyOwnersOnce()
    {
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var connA = Guid.NewGuid();
        var connB = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = "O3@EX.COM", DisplayName = "O", IsActive = true });
            db.Users.Add(new FullWorthUser { Id = member, EmailNormalized = "M3@EX.COM", DisplayName = "M", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connA, FullWorthSpaceId = space, Provider = "test", InstitutionName = "Bank A", Country = "DE", ProviderSessionId = "sess-A", Status = "AUTHORIZED", ConsecutiveFailures = 0 });
            db.BankConnections.Add(new BankConnection { Id = connB, FullWorthSpaceId = space, Provider = "test", InstitutionName = "Bank B", Country = "DE", ProviderSessionId = "sess-B", Status = "AUTHORIZED", ConsecutiveFailures = 0 });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var store = new BankConnectionStore(db, null, FieldCipher.Null, Dispatcher(db, fake));

        // First failure episode: 0 -> 1 fires bank_sync_error, to the owner only.
        await store.UpsertAsync(new BankConnectionWrite(connA, "test", "Bank A", "DE", null, null, "sess-A", "AUTHORIZED", null, null, DateTimeOffset.UtcNow, null, 1, "boom"), CancellationToken.None);
        Assert.Single(fake.Sent);
        Assert.Contains("Fehler beim Bankabgleich", fake.Sent[0].Message.Title);
        Assert.Equal(owner, fake.Sent[0].UserId);

        // Further increments (1 -> 2) don't re-fire.
        await store.UpsertAsync(new BankConnectionWrite(connA, "test", "Bank A", "DE", null, null, "sess-A", "AUTHORIZED", null, null, DateTimeOffset.UtcNow, null, 2, "boom"), CancellationToken.None);
        Assert.Single(fake.Sent);

        // Consent slide AUTHORIZED -> EXPIRED fires bank_reauth once.
        fake.Sent.Clear();
        await store.UpsertAsync(new BankConnectionWrite(connB, "test", "Bank B", "DE", null, null, "sess-B", "EXPIRED", null, null, DateTimeOffset.UtcNow, null, 0, null), CancellationToken.None);
        Assert.Single(fake.Sent);
        Assert.Contains("Bank-Neuanmeldung", fake.Sent[0].Message.Title);
        Assert.Equal(owner, fake.Sent[0].UserId);

        await store.UpsertAsync(new BankConnectionWrite(connB, "test", "Bank B", "DE", null, null, "sess-B", "EXPIRED", null, null, DateTimeOffset.UtcNow, null, 0, null), CancellationToken.None);
        Assert.Single(fake.Sent);   // already needs-reauth -> no repeat
    }

    private static FinanceTransaction Tx(Guid accountId, Guid categoryId, decimal amount, DateOnly date) => new()
    {
        AccountId = accountId,
        CategoryId = categoryId,
        ExternalKey = $"tx-{Guid.NewGuid():N}",
        Amount = amount,
        Currency = "EUR",
        BookingDate = date,
        Status = "BOOK",
        RawJson = "{}"
    };

    private static RecurringContract Contract(Guid space, string name, Guid? accountId, DateOnly due, bool active, DateOnly? end) => new()
    {
        FullWorthSpaceId = space,
        Name = name,
        AccountId = accountId,
        Amount = 9.99m,
        Currency = "EUR",
        BillingCycle = "monthly",
        NextDueDate = due,
        IsActive = active,
        EndDate = end
    };
}
