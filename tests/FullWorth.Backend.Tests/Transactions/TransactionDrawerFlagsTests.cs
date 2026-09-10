using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// The transaction drawer decides two things from the row: whether to offer "Bankdetails" and whether to
/// offer delete. Neither field existed, so <c>isManual</c> was always undefined — the bank-details button
/// rendered for EVERY transaction (and the request 404s for one with no provider pointer, which reached
/// the user as a toast reading "404"), while the delete action for a manual transaction rendered for none.
/// </summary>
public sealed class TransactionDrawerFlagsTests
{
    [Fact]
    public async Task A_manual_transaction_is_marked_manual_and_offers_no_bank_details()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var row = await RowAsync(client, scenario, scenario.ManualTransaction);

        Assert.True(row.GetProperty("isManual").GetBoolean());
        Assert.False(row.GetProperty("hasProviderDetails").GetBoolean());
    }

    // An imported transaction sits on a real account but has no provider pointer, so its details cannot be
    // fetched - this is the row that produced the bare "404".
    [Fact]
    public async Task An_imported_transaction_without_a_provider_pointer_offers_no_bank_details()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var row = await RowAsync(client, scenario, scenario.ImportedTransaction);

        Assert.False(row.GetProperty("isManual").GetBoolean());
        Assert.False(row.GetProperty("hasProviderDetails").GetBoolean());
    }

    [Fact]
    public async Task A_synced_transaction_with_a_provider_pointer_offers_bank_details()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var row = await RowAsync(client, scenario, scenario.SyncedTransaction);

        Assert.False(row.GetProperty("isManual").GetBoolean());
        Assert.True(row.GetProperty("hasProviderDetails").GetBoolean());
    }

    // FinTS details are already part of the imported transaction and the endpoint refuses them, so the
    // button must not appear for a FinTS connection either.
    [Fact]
    public async Task A_fints_transaction_offers_no_bank_details()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var row = await RowAsync(client, scenario, scenario.FinTsTransaction);

        Assert.False(row.GetProperty("hasProviderDetails").GetBoolean());
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Space,
        Guid ManualTransaction,
        Guid ImportedTransaction,
        Guid SyncedTransaction,
        Guid FinTsTransaction);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var manualAccount = Guid.NewGuid();
        var bankAccount = Guid.NewGuid();
        var finTsAccount = Guid.NewGuid();
        var bankConnection = Guid.NewGuid();
        var finTsConnection = Guid.NewGuid();
        var manualTx = Guid.NewGuid();
        var importedTx = Guid.NewGuid();
        var syncedTx = Guid.NewGuid();
        var finTsTx = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Drawer owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Drawer", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = bankConnection,
                FullWorthSpaceId = space,
                Provider = "enable-banking",
                InstitutionName = "Bank",
                Country = "DE",
                ProviderSessionId = $"drawer-{bankConnection:N}"
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = finTsConnection,
                FullWorthSpaceId = space,
                Provider = "fints",
                InstitutionName = "ING",
                Country = "DE",
                ProviderSessionId = $"drawer-{finTsConnection:N}"
            });

            foreach (var (id, connection, name) in new[]
                     {
                         (manualAccount, (Guid?)null, "Bargeld"),
                         (bankAccount, bankConnection, "Giro"),
                         (finTsAccount, finTsConnection, "Depot-Konto")
                     })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = id,
                    FullWorthSpaceId = space,
                    BankConnectionId = connection,
                    Provider = connection is null ? "manual" : "test",
                    IdentificationHash = $"drawer-{id:N}",
                    ProviderAccountId = $"drawer-{id:N}",
                    InstitutionName = "Bank",
                    DisplayName = name,
                    Currency = "EUR",
                    IsActive = true,
                    IncludeInNetWorth = true
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = id,
                    UserId = owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }

            foreach (var (id, accountId, providerId) in new[]
                     {
                         (manualTx, manualAccount, (string?)null),
                         (importedTx, bankAccount, null),
                         (syncedTx, bankAccount, "provider-tx-1"),
                         (finTsTx, finTsAccount, "provider-tx-2")
                     })
                db.Transactions.Add(new FinanceTransaction
                {
                    Id = id,
                    AccountId = accountId,
                    ExternalKey = $"drawer-{id:N}",
                    Status = "BOOK",
                    BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Amount = -10m,
                    Currency = "EUR",
                    ProviderTransactionId = providerId
                });
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space, manualTx, importedTx, syncedTx, finTsTx);
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, Scenario scenario, Guid transactionId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?fullWorthSpaceId={scenario.Space}&limit=50");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == transactionId)
            .Clone();
    }
}
