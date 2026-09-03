using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Notifications;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Notifications;

public sealed class BudgetNotificationParityTests
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

    [Fact]
    public async Task WholeSpaceBudgetDoesNotLeakThresholdToPartialAccountMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var restrictedId = Guid.NewGuid();
        var visibleAccountId = Guid.NewGuid();
        var hiddenAccountId = Guid.NewGuid();
        var budgetId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(User(ownerId, "owner"), User(restrictedId, "restricted"));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Household", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = ownerId, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = restrictedId, Role = FullWorthSpaceRoles.Member });
            db.Accounts.AddRange(Account(spaceId, visibleAccountId, "Visible"), Account(spaceId, hiddenAccountId, "Hidden"));
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = visibleAccountId, UserId = ownerId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = hiddenAccountId, UserId = ownerId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = visibleAccountId, UserId = restrictedId, OwnershipType = AccountOwnershipTypes.Viewer });
            db.Budgets.Add(new Budget
            {
                Id = budgetId,
                FullWorthSpaceId = spaceId,
                Name = "Shared household",
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Transactions.Add(Tx(hiddenAccountId, -120m, new DateOnly(2026, 8, 10)));
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var dispatcher = new NotificationDispatcher(db, new PreferenceStore(db), fake, NullLogger<NotificationDispatcher>.Instance);
        var service = new BudgetNotificationService(db, new BudgetStore(db), dispatcher, NullLogger<BudgetNotificationService>.Instance);

        await service.EvaluateAndDispatchAsync(spaceId, new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.Single(fake.Sent);
        Assert.Equal(ownerId, fake.Sent[0].UserId);
        Assert.DoesNotContain(fake.Sent, sent => sent.UserId == restrictedId);
    }

    [Fact]
    public async Task AdvancedNearThresholdIsUsedInsteadOfHardcodedEightyPercent()
    {
        using var factory = new BackendWebApplicationFactory();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var budgetId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(User(ownerId, "owner"));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = ownerId, Role = FullWorthSpaceRoles.Owner });
            db.Accounts.Add(Account(spaceId, accountId, "Main"));
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = ownerId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Budgets.Add(new Budget
            {
                Id = budgetId,
                FullWorthSpaceId = spaceId,
                Name = "Low warning",
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Transactions.Add(Tx(accountId, -50m, new DateOnly(2026, 8, 10)));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetAdvancedSettings"
("BudgetId","AlertNearPercent","AlertCriticalPercent","ScopeVersion","UpdatedAt")
VALUES ({budgetId},{45m},{90m},{1},{DateTimeOffset.UtcNow})
""");
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var dispatcher = new NotificationDispatcher(db, new PreferenceStore(db), fake, NullLogger<NotificationDispatcher>.Instance);
        var service = new BudgetNotificationService(db, new BudgetStore(db), dispatcher, NullLogger<BudgetNotificationService>.Instance);

        await service.EvaluateAndDispatchAsync(spaceId, new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.Single(fake.Sent);
        Assert.Contains("nahe", fake.Sent[0].Message.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static FullWorthUser User(Guid id, string name) => new()
    {
        Id = id,
        EmailNormalized = $"{id:N}@EXAMPLE.COM",
        DisplayName = name,
        IsActive = true
    };

    private static FinanceAccount Account(Guid spaceId, Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "manual",
        IdentificationHash = $"budget-notify-{id:N}",
        ProviderAccountId = $"budget-notify-{id:N}",
        InstitutionName = "Test",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true
    };

    private static FinanceTransaction Tx(Guid accountId, decimal amount, DateOnly date) => new()
    {
        AccountId = accountId,
        ExternalKey = $"budget-notify:{Guid.NewGuid():N}",
        Status = "BOOK",
        BookingDate = date,
        ValueDate = date,
        Amount = amount,
        Currency = "EUR",
        Counterparty = "Merchant",
        NormalizedCounterparty = "MERCHANT",
        RawJson = "{}"
    };
}
