using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

public sealed class ContractDetectionCandidateLifecycleTests
{
    [Fact]
    public async Task AcceptedCandidate_IsSuppressedFromFutureDetectionResults()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/contracts/detection?fullWorthSpaceId={s.Space}";

        using var before = await client.SendAsync(Request(HttpMethod.Get, path, s.Owner));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var candidates = await before.Content.ReadFromJsonAsync<List<JsonElement>>();
        var candidate = Assert.Single(candidates!);
        Assert.Equal("netflix", candidate.GetProperty("counterparty").GetString());

        using var accept = Request(HttpMethod.Post, $"/api/contracts/detection/accept?fullWorthSpaceId={s.Space}", s.Owner);
        accept.Content = JsonContent.Create(candidate);
        using var accepted = await client.SendAsync(accept);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var after = await client.SendAsync(Request(HttpMethod.Get, path, s.Owner));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var remaining = await after.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.DoesNotContain(remaining!, item => item.GetProperty("counterparty").GetString() == "netflix");
    }

    [Fact]
    public async Task EquivalentProviderLegalSuffixes_AreShownAsOneCandidate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            for (var i = 4; i >= 1; i--)
            {
                foreach (var counterparty in new[] { "commerzbank", "commerzbank ag" })
                {
                    db.Transactions.Add(new FinanceTransaction
                    {
                        AccountId = s.Account,
                        ExternalKey = $"{counterparty.Replace(" ", "-")}-{i}",
                        Amount = -942.33m,
                        Currency = "EUR",
                        Counterparty = counterparty.ToUpperInvariant(),
                        NormalizedCounterparty = counterparty,
                        BookingDate = today.AddMonths(-i),
                        CategorizationSource = "none"
                    });
                }
            }

            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/contracts/detection?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var candidates = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        var commerzbank = candidates!
            .Where(item => item.GetProperty("counterparty").GetString()!.StartsWith("commerzbank", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(commerzbank);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = s.Owner,
                EmailNormalized = $"{s.Owner:N}@EXAMPLE.COM",
                DisplayName = "Detection owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = s.Space,
                Name = "Detection lifecycle",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = s.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = s.Connection,
                FullWorthSpaceId = s.Space,
                Provider = "test",
                InstitutionName = "Test Bank",
                Country = "DE",
                ProviderSessionId = $"detect-{s.Connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = s.Account,
                FullWorthSpaceId = s.Space,
                BankConnectionId = s.Connection,
                Provider = "test",
                IdentificationHash = $"detect-{s.Account:N}",
                ProviderAccountId = $"provider-{s.Account:N}",
                InstitutionName = "Test Bank",
                DisplayName = "Detection account",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = s.Account,
                UserId = s.Owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            for (var i = 4; i >= 1; i--)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = s.Account,
                    ExternalKey = $"netflix-{i}",
                    Amount = -12.99m,
                    Currency = "EUR",
                    Counterparty = "NETFLIX",
                    NormalizedCounterparty = "netflix",
                    BookingDate = today.AddMonths(-i),
                    CategorizationSource = "none"
                });
            }

            await db.SaveChangesAsync();
        });

        return s;
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection, Guid Account);
}
