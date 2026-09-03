using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Loans;

// Loan amortization projection (UI_UX_SPEC §13/§14.4): the endpoint projects payoff + total interest
// from the current balance, and refuses to invent a schedule when the payment cannot cover interest.
public sealed class LoanAmortizationTests
{
    [Fact]
    public async Task ProjectsPayoffAndTotalInterest()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var (userId, loanId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateContext();
        await SeedAsync(db, userId, loanId, balance: 1000m, payment: 100m, rate: 12m);

        var outcome = await new LoanStore(db).GetAmortizationForUserAsync(userId, FullWorthSpaceDefaults.LegacyId, loanId, new DateOnly(2026, 1, 1), CancellationToken.None);
        Assert.Equal(AmortizationStatus.Ok, outcome.Status);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(outcome.Result!);
        Assert.True(json.GetProperty("periodCount").GetInt32() > 0);
        Assert.True(json.GetProperty("totalExpectedInterest").GetDecimal() > 0m);
        Assert.True(json.GetProperty("totalPrincipal").GetDecimal() > 0m);
    }

    [Fact]
    public async Task RefusesWhenPaymentCannotCoverInterest()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var (userId, loanId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateContext();
        // 1000 @ 12%/yr => 10/month interest; a 5/month payment can never amortize.
        await SeedAsync(db, userId, loanId, balance: 1000m, payment: 5m, rate: 12m);

        var outcome = await new LoanStore(db).GetAmortizationForUserAsync(userId, FullWorthSpaceDefaults.LegacyId, loanId, new DateOnly(2026, 1, 1), CancellationToken.None);
        Assert.Equal(AmortizationStatus.Insufficient, outcome.Status);
    }

    [Fact]
    public async Task UnknownLoanIsNotFound()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var (userId, loanId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateContext();
        await SeedAsync(db, userId, loanId, balance: 1000m, payment: 100m, rate: 12m);

        var outcome = await new LoanStore(db).GetAmortizationForUserAsync(userId, FullWorthSpaceDefaults.LegacyId, Guid.NewGuid(), new DateOnly(2026, 1, 1), CancellationToken.None);
        Assert.Equal(AmortizationStatus.NotFound, outcome.Status);
    }

    private static async Task SeedAsync(FullWorth.Backend.Data.FullWorthDbContext db, Guid userId, Guid loanId, decimal balance, decimal payment, decimal rate)
    {
        db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
        db.Loans.Add(new Loan
        {
            Id = loanId,
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Name = "Car loan",
            OriginalPrincipal = 1500m,
            CurrentBalance = balance,
            PaymentAmount = payment,
            NominalInterestRate = rate,
            StartDate = new DateOnly(2025, 1, 1),
            PaymentFrequency = "monthly",
            Currency = "EUR",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }
}
