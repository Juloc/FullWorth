using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// A balance said what it was (the bank's balance_type) but never where it came from, and a manual one was
/// always stamped with today. So a figure the owner read off last month's statement was indistinguishable
/// from a live bank balance and claimed to be current — which matters most on the accounts that have no
/// bank at all, where an entered figure is the only balance there is.
/// </summary>
public sealed class BalanceProvenanceTests
{
    [Fact]
    public async Task A_manual_balance_keeps_the_owners_as_of_date_and_note()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40);

        using var response = await SetBalanceAsync(client, scenario, 1234.56m, asOf, "Kontoauszug 07/2026");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var snapshot = await db.BalanceSnapshots.AsNoTracking()
                .SingleAsync(balance => balance.AccountId == scenario.Account);
            Assert.Equal(BalanceSources.Manual, snapshot.Source);
            Assert.Equal(asOf, snapshot.ReferenceDate);
            Assert.Equal("Kontoauszug 07/2026", snapshot.Note);
        });
    }

    // The row has to carry the provenance out to the client, or the list cannot say it.
    [Fact]
    public async Task The_account_row_reports_the_as_of_date_the_source_and_the_note()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

        using var write = await SetBalanceAsync(client, scenario, 500m, asOf, "Bargeld gezählt");
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);

        var balance = (await RowAsync(client, scenario)).GetProperty("latestBalance");

        Assert.Equal("manual", balance.GetProperty("source").GetString());
        Assert.Equal(asOf.ToString("yyyy-MM-dd"), balance.GetProperty("referenceDate").GetString());
        Assert.Equal("Bargeld gezählt", balance.GetProperty("note").GetString());
    }

    // Nobody has seen tomorrow's balance. Clamping it silently would leave a wrong date in the history.
    [Fact]
    public async Task A_future_as_of_date_is_refused()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await SetBalanceAsync(
            client, scenario, 10m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.SeedAsync(async db =>
            Assert.False(await db.BalanceSnapshots.AsNoTracking()
                .AnyAsync(balance => balance.AccountId == scenario.Account)));
    }

    // Omitting the date must behave exactly as before this existed.
    [Fact]
    public async Task Without_an_as_of_date_the_balance_is_dated_today()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await SetBalanceAsync(client, scenario, 42m, null, null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var snapshot = await db.BalanceSnapshots.AsNoTracking()
                .SingleAsync(balance => balance.AccountId == scenario.Account);
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), snapshot.ReferenceDate);
            Assert.Null(snapshot.Note);
        });
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Account);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var account = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Provenance owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Provenance", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                Provider = "manual",
                IdentificationHash = $"prov-{account:N}",
                ProviderAccountId = $"prov-{account:N}",
                InstitutionName = "Bargeld",
                DisplayName = "Bargeld",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space, account);
    }

    private static async Task<HttpResponseMessage> SetBalanceAsync(
        HttpClient client, Scenario scenario, decimal amount, DateOnly? asOf, string? note)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/accounts/{scenario.Account:D}/balance?fullWorthSpaceId={scenario.Space:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            amount,
            currency = "EUR",
            asOf = asOf?.ToString("yyyy-MM-dd"),
            note
        });
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, Scenario scenario)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/accounts?fullWorthSpaceId={scenario.Space:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == scenario.Account)
            .Clone();
    }
}
