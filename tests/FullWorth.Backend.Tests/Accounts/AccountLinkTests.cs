using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Every automatic path that decides "these two accounts are the same" is keyed on the IBAN token, so
/// for PayPal, Wise, Revolut, cash and manual accounts - which have no IBAN at all - de-duplication did
/// not exist, and even where an IBAN existed the decision was made once at creation with no way for the
/// owner to make it or take it back.
///
/// These tests pin the explicit link: no IBAN anywhere, counted once, fully reversible, and it never
/// touches a booking or a balance.
/// </summary>
public sealed class AccountLinkTests
{
    [Fact]
    public async Task Two_wallets_without_an_iban_can_be_declared_the_same_account()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "PayPal", 300m);
        var same = await CreateWalletAsync(client, scenario, "PayPal (manuell)", 300m);

        // Both wallets have no IBAN, so the automatic rule cannot see them: 600 for 300 of real money.
        Assert.Equal(600m, await AccountsTotalAsync(client, scenario));

        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        Assert.Equal(300m, await AccountsTotalAsync(client, scenario));

        var rows = await ListAsync(client, scenario);
        // Still visible, and the row says which account carries it now.
        var secondary = rows.Single(row => row.Id == same.Id);
        Assert.False(secondary.IncludeInNetWorth);
        Assert.Equal(keep.Id, secondary.DuplicateOfAccountId);
        Assert.Equal("PayPal", secondary.DuplicateOfDisplayName);
        Assert.True(secondary.DuplicateLinkExplicit);
        Assert.Equal(300m, secondary.LatestBalance?.Amount);
        Assert.True(rows.Single(row => row.Id == keep.Id).IncludeInNetWorth);
    }

    [Fact]
    public async Task Linking_moves_no_transaction_and_no_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "Wise", 100m);
        var same = await CreateWalletAsync(client, scenario, "Wise alt", 100m);
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction { AccountId = same.Id, ExternalKey = "link-a", Amount = -5m, Currency = "EUR", BookingDate = new DateOnly(2026, 6, 1), Status = "BOOK" });
            db.Transactions.Add(new FinanceTransaction { AccountId = same.Id, ExternalKey = "link-b", Amount = -7m, Currency = "EUR", BookingDate = new DateOnly(2026, 6, 2), Status = "BOOK" });
            await db.SaveChangesAsync();
        });

        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        await factory.SeedAsync(async db =>
        {
            // Linking is a statement about counting. Nothing is re-parented, nothing is deleted.
            Assert.Equal(2, await db.Transactions.CountAsync(x => x.AccountId == same.Id));
            Assert.Equal(0, await db.Transactions.CountAsync(x => x.AccountId == keep.Id));
            Assert.Equal(1, await db.BalanceSnapshots.CountAsync(x => x.AccountId == same.Id));
            Assert.Equal(1, await db.BalanceSnapshots.CountAsync(x => x.AccountId == keep.Id));
        });
    }

    [Fact]
    public async Task Unlinking_restores_the_state_the_account_had_before()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "Revolut", 50m);
        var same = await CreateWalletAsync(client, scenario, "Revolut alt", 50m);

        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);
        Assert.Equal(50m, await AccountsTotalAsync(client, scenario));

        using var unlink = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);

        Assert.Equal(100m, await AccountsTotalAsync(client, scenario));
        var restored = (await ListAsync(client, scenario)).Single(row => row.Id == same.Id);
        Assert.True(restored.IncludeInNetWorth);
        Assert.Null(restored.DuplicateOfAccountId);
        Assert.False(restored.DuplicateLinkExplicit);
    }

    // The stored previous state, not a guessed "true": an account that the automatic same-IBAN rule had
    // already taken out of the totals at creation goes back to excluded, not to counted.
    [Fact]
    public async Task Unlinking_an_account_the_iban_rule_excluded_puts_it_back_to_excluded()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);
        var rows = await ListAsync(client, scenario);
        var counted = rows.Single(row => row.IncludeInNetWorth);
        var auto = rows.Single(row => !row.IncludeInNetWorth);
        Assert.False(auto.DuplicateLinkExplicit);

        // The owner confirms out loud what the IBAN rule guessed …
        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{auto.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(counted.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);
        Assert.True((await ListAsync(client, scenario)).Single(row => row.Id == auto.Id).DuplicateLinkExplicit);

        // … and takes it back. The account returns to what it was: excluded by the automatic rule,
        // which the row still explains, but no longer by a decision of the owner's.
        using var unlink = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{auto.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);

        var after = (await ListAsync(client, scenario)).Single(row => row.Id == auto.Id);
        Assert.False(after.IncludeInNetWorth);
        Assert.False(after.DuplicateLinkExplicit);
        Assert.Equal(counted.Id, after.DuplicateOfAccountId);
        Assert.Equal(1000m, await AccountsTotalAsync(client, scenario));
    }

    // The automatic rule fires only at creation, so nothing a sync does may overrule the owner: an
    // account they unlinked (and switched back on) stays on, and one they linked stays linked.
    [Fact]
    public async Task A_sync_neither_re_excludes_an_unlinked_account_nor_drops_an_explicit_link()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);
        var rows = await ListAsync(client, scenario);
        var counted = rows.Single(row => row.IncludeInNetWorth);
        var auto = rows.Single(row => !row.IncludeInNetWorth);

        // Switching the automatic duplicate back on is the owner deciding it is NOT a duplicate.
        using var reinclude = await client.SendAsync(UserRequest(HttpMethod.Patch,
            $"/api/accounts/{auto.Id}?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountSettingsRequest(null, null, true, null)));
        Assert.Equal(HttpStatusCode.NoContent, reinclude.StatusCode);

        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);
        Assert.All(await ListAsync(client, scenario), row => Assert.True(row.IncludeInNetWorth));

        // The other direction: an explicit link is not undone by a sync either.
        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{auto.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(counted.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);

        var after = (await ListAsync(client, scenario)).Single(row => row.Id == auto.Id);
        Assert.False(after.IncludeInNetWorth);
        Assert.True(after.DuplicateLinkExplicit);
        Assert.Equal(1000m, await AccountsTotalAsync(client, scenario));
    }

    [Fact]
    public async Task The_picker_offers_every_account_of_the_space_wallets_included()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var wallet = await CreateWalletAsync(client, scenario, "PayPal", 10m);
        var cash = await CreateWalletAsync(client, scenario, "Bargeld", 20m);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<AccountLinkState>();

        Assert.NotNull(state);
        Assert.Null(state!.DuplicateOfAccountId);
        Assert.False(state.Explicit);
        // No IBAN, no bank connection, and still a candidate — the whole point of the mechanism.
        Assert.Contains(state.Candidates, candidate => candidate.Id == cash.Id);
        Assert.DoesNotContain(state.Candidates, candidate => candidate.Id == wallet.Id);
    }

    [Fact]
    public async Task The_state_endpoint_names_the_link_and_the_accounts_counted_as_this_one()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "PayPal", 10m);
        var same = await CreateWalletAsync(client, scenario, "PayPal alt", 10m);
        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        using var secondary = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        var secondaryState = await secondary.Content.ReadFromJsonAsync<AccountLinkState>();
        Assert.Equal(keep.Id, secondaryState!.DuplicateOfAccountId);
        Assert.Equal("PayPal", secondaryState.DuplicateOfDisplayName);
        Assert.True(secondaryState.Explicit);

        using var primary = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{keep.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        var primaryState = await primary.Content.ReadFromJsonAsync<AccountLinkState>();
        Assert.False(primaryState!.Explicit);
        Assert.Contains(primaryState.LinkedToThis, row => row.Id == same.Id);
        // An account already counted as this one is not offered as a target again.
        Assert.Contains(primaryState.Candidates, candidate => candidate.Id == same.Id && candidate.IsLinked);
    }

    [Fact]
    public async Task Switching_a_linked_account_back_on_drops_the_link_instead_of_contradicting_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "PayPal", 10m);
        var same = await CreateWalletAsync(client, scenario, "PayPal alt", 10m);
        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        using var patch = await client.SendAsync(UserRequest(HttpMethod.Patch,
            $"/api/accounts/{same.Id}?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountSettingsRequest(null, null, true, null)));
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var row = (await ListAsync(client, scenario)).Single(item => item.Id == same.Id);
        Assert.True(row.IncludeInNetWorth);
        Assert.Null(row.DuplicateOfAccountId);
        Assert.False(row.DuplicateLinkExplicit);
    }

    [Fact]
    public async Task A_chain_is_refused_from_both_ends()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "PayPal", 10m);
        var same = await CreateWalletAsync(client, scenario, "PayPal alt", 10m);
        var third = await CreateWalletAsync(client, scenario, "PayPal ganz alt", 10m);
        using var link = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        // A duplicate cannot become someone else's original …
        using var toDuplicate = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{third.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(same.Id)));
        Assert.Equal(HttpStatusCode.Conflict, toDuplicate.StatusCode);

        // … and an original cannot become a duplicate while something is counted as it.
        using var fromOriginal = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{keep.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(third.Id)));
        Assert.Equal(HttpStatusCode.Conflict, fromOriginal.StatusCode);
    }

    // "Counted once" means once, not zero times: linking into an account that is itself out of the
    // totals would take the money off net worth with nothing on screen saying where it went.
    [Fact]
    public async Task An_account_that_is_out_of_the_totals_cannot_be_the_one_that_counts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var excluded = await CreateWalletAsync(client, scenario, "Nicht gezählt", 10m, includeInNetWorth: false);
        var wallet = await CreateWalletAsync(client, scenario, "PayPal", 10m);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(excluded.Id)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Self_link_is_rejected_and_unlinking_an_unlinked_account_conflicts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var wallet = await CreateWalletAsync(client, scenario, "PayPal", 10m);

        using var self = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(wallet.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        using var empty = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(Guid.Empty)));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var unlink = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
    }

    // Same gate and same ordering as PUT /api/accounts/{id}/balance: not-found → forbidden → conflict.
    [Fact]
    public async Task A_viewer_is_forbidden_and_a_stranger_gets_not_found()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var keep = await CreateWalletAsync(client, scenario, "PayPal", 10m);
        var same = await CreateWalletAsync(client, scenario, "PayPal alt", 10m);
        foreach (var accountId in new[] { keep.Id, same.Id })
        {
            using var share = await client.SendAsync(UserRequest(HttpMethod.Post,
                $"/api/accounts/{accountId}/owners?fullWorthSpaceId={scenario.Space}", scenario.Owner,
                new AddAccountOwnerRequest(scenario.Viewer, AccountOwnershipTypes.Viewer)));
            Assert.Equal(HttpStatusCode.NoContent, share.StatusCode);
        }

        using var viewer = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Viewer,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        using var stranger = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Outsider,
            new AccountLinkRequest(keep.Id)));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

        using var strangerState = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Outsider));
        Assert.Equal(HttpStatusCode.NotFound, strangerState.StatusCode);

        using var viewerUnlink = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{same.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Viewer));
        Assert.Equal(HttpStatusCode.Forbidden, viewerUnlink.StatusCode);
    }

    [Fact]
    public async Task An_account_from_another_space_is_not_a_valid_target()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var wallet = await CreateWalletAsync(client, scenario, "PayPal", 10m);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{wallet.Id}/link?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new AccountLinkRequest(scenario.ForeignAccount)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private const string SharedIban = "DE02120300000000202051";

    private sealed record LinkScenario(
        Guid Space, Guid ForeignSpace, Guid Owner, Guid Viewer, Guid Outsider,
        Guid FirstConnection, Guid SecondConnection, Guid ForeignAccount);

    private static async Task<LinkScenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new LinkScenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Viewer, scenario.Outsider })
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"User {userId:N}",
                    IsActive = true
                });

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.Space, Name = "Link space", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.ForeignSpace, Name = "Foreign space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Viewer, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.ForeignSpace, UserId = scenario.Outsider, Role = FullWorthSpaceRoles.Owner });

            foreach (var (id, provider) in new[]
                     { (scenario.FirstConnection, "enable-banking"), (scenario.SecondConnection, "fints") })
                db.BankConnections.Add(new BankConnection
                {
                    Id = id,
                    FullWorthSpaceId = scenario.Space,
                    Provider = provider,
                    InstitutionName = "ING",
                    Country = "DE",
                    ProviderSessionId = $"session-{id:N}",
                    // The ingest only assigns an owner to a new account when the connection names the
                    // authorizing user, exactly as the real connect flow does.
                    AuthorizationUserId = scenario.Owner
                });

            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.ForeignAccount,
                FullWorthSpaceId = scenario.ForeignSpace,
                Provider = "manual",
                IdentificationHash = $"manual:{scenario.ForeignAccount:N}",
                ProviderAccountId = $"manual:{scenario.ForeignAccount:N}",
                InstitutionName = "Fremd",
                DisplayName = "Fremdkonto",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = scenario.ForeignAccount,
                UserId = scenario.Outsider,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static async Task<AccountListItem> CreateWalletAsync(
        HttpClient client, LinkScenario scenario, string name, decimal balance, bool includeInNetWorth = true)
    {
        using var response = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Owner,
            new AccountCreateRequest(scenario.Space, null, name, "EUR", includeInNetWorth, 0, name, balance)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AccountListItem>();
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<List<AccountListItem>> ListAsync(HttpClient client, LinkScenario scenario)
    {
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<List<AccountListItem>>() ?? [];
    }

    private static async Task<decimal> AccountsTotalAsync(HttpClient client, LinkScenario scenario)
    {
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accounts").GetDecimal();
    }

    private static async Task IngestAsync(
        HttpClient client,
        LinkScenario scenario,
        Guid connectionId,
        string provider,
        string hash,
        decimal balance,
        string iban = SharedIban)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/ingest")
        {
            Content = JsonContent.Create(new
            {
                connection = new
                {
                    connectionId,
                    provider,
                    institutionName = "ING",
                    country = "DE",
                    providerSessionId = $"session-{connectionId:N}",
                    status = "AUTHORIZED",
                    validUntil = (DateTimeOffset?)null,
                    lastSyncedAt = DateTimeOffset.UtcNow,
                    lastError = (string?)null,
                    fullWorthSpaceId = scenario.Space
                },
                accounts = new[]
                {
                    new
                    {
                        identificationHash = hash,
                        providerAccountId = hash,
                        institutionName = "ING",
                        displayName = $"ING {provider}",
                        product = (string?)null,
                        accountType = "checking",
                        currency = "EUR",
                        ibanLast4 = iban[^4..],
                        isActive = true,
                        iban
                    }
                },
                balances = new[]
                {
                    new
                    {
                        identificationHash = hash,
                        amount = balance,
                        currency = "EUR",
                        balanceType = "closingBooked",
                        referenceDate = (DateOnly?)null,
                        capturedAt = DateTimeOffset.UtcNow
                    }
                },
                transactions = Array.Empty<object>()
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
