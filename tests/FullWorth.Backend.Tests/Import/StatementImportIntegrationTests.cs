using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// The fallback for an account FullWorth cannot connect to at all — Ikano over FinTS, or any institution
/// a private Enable Banking application is not enabled for. A statement import has to do the two things
/// a CSV import cannot: state the account's balance as of a date, and keep doing so without ever
/// creating a second account for the same money.
/// </summary>
public sealed class StatementImportIntegrationTests
{
    private const string Mt940 = """
        :20:STARTUMS
        :25:DE02120300000000202051/EUR
        :28C:00012/001
        :60F:C260901EUR1000,00
        :61:2609020902D42,19NMSCNONREF//BREF-1
        :86:?00KARTENZAHLUNG?20Einkauf?32REWE Markt GmbH
        :61:2609050905C2810,44NTRFLohn//BREF-2
        :86:?00GUTSCHRIFT?20Gehalt September?32Arbeitgeber AG
        :62F:C260905EUR3768,25
        -
        """;

    [Fact]
    public async Task A_statement_import_books_into_the_chosen_account_and_anchors_its_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account, includeInNetWorth: false, isActive: false);

        var result = await UploadAndCommitAsync(client, owner, account, Mt940);

        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        Assert.True(result.GetProperty("balanceApplied").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var transactions = await db.Transactions.AsNoTracking()
                .Where(x => x.AccountId == account).OrderBy(x => x.BookingDate).ToListAsync();
            Assert.Equal([-42.19m, 2810.44m], transactions.Select(x => x.Amount).ToArray());
            Assert.Equal("REWE Markt GmbH", transactions[0].Counterparty);

            var balance = await db.BalanceSnapshots.AsNoTracking().SingleAsync(x => x.AccountId == account);
            Assert.Equal(3768.25m, balance.Amount);
            Assert.Equal("EUR", balance.Currency);
            Assert.Equal(BalanceSources.Import, balance.Source);
            Assert.Equal(new DateOnly(2026, 9, 5), balance.ReferenceDate);

            // An account kept current by statement is a real account, not an archived history container.
            var stored = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == account);
            Assert.True(stored.IsActive);
            Assert.True(stored.IncludeInNetWorth);
        });
    }

    // The whole point of importing into an EXISTING account: no second account for the same money, ever.
    [Fact]
    public async Task A_statement_import_never_creates_an_account()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account);

        await UploadAndCommitAsync(client, owner, account, Mt940);

        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.Accounts.AsNoTracking()
                .CountAsync(x => x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId && x.Id == account)));
    }

    // A monthly statement overlaps the previous one. Re-importing must not double the history.
    [Fact]
    public async Task Re_importing_the_same_statement_adds_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account);

        await UploadAndCommitAsync(client, owner, account, Mt940);
        var second = await UploadAndCommitAsync(client, owner, account, Mt940);

        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(2, second.GetProperty("duplicates").GetInt32());
        await factory.SeedAsync(async db =>
            Assert.Equal(2, await db.Transactions.AsNoTracking().CountAsync(x => x.AccountId == account)));
    }

    // Importing last month's statement must not overwrite what the bank reported this morning: the live
    // balance is the more recent word on the account.
    [Fact]
    public async Task An_older_statement_does_not_replace_a_newer_provider_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account);
        await factory.SeedAsync(async db =>
        {
            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = account,
                Amount = 5_000m,
                Currency = "EUR",
                BalanceType = "closingBooked",
                Source = BalanceSources.Provider,
                ReferenceDate = new DateOnly(2026, 9, 9),
                CapturedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();
        });

        var result = await UploadAndCommitAsync(client, owner, account, Mt940);

        Assert.False(result.GetProperty("balanceApplied").GetBoolean());
        Assert.Equal("newer_provider_balance", result.GetProperty("balanceSkipped").GetString());
        await factory.SeedAsync(async db =>
        {
            var balance = await db.BalanceSnapshots.AsNoTracking().SingleAsync(x => x.AccountId == account);
            Assert.Equal(5_000m, balance.Amount);
        });
    }

    // A statement in a currency the account does not hold would corrupt every total that sums by the
    // account's currency, so it is reported rather than stored.
    [Fact]
    public async Task A_statement_in_another_currency_is_not_applied_as_a_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account, currency: "CHF");

        var result = await UploadAndCommitAsync(client, owner, account, Mt940);

        Assert.False(result.GetProperty("balanceApplied").GetBoolean());
        Assert.Equal("currency_mismatch", result.GetProperty("balanceSkipped").GetString());
        await factory.SeedAsync(async db =>
            Assert.False(await db.BalanceSnapshots.AsNoTracking().AnyAsync(x => x.AccountId == account)));
    }

    [Fact]
    public async Task An_unreadable_file_is_refused_before_a_job_exists()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedAsync(factory, owner, account);

        using var response = await UploadAsync(client, owner, "not a statement at all", "statement.sta");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonElement> UploadAndCommitAsync(
        HttpClient client, Guid owner, Guid account, string statement)
    {
        using var upload = await UploadAsync(client, owner, statement, "statement.sta");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var jobId = uploaded.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal("mt940", uploaded.RootElement.GetProperty("adapter").GetString());

        using var commit = UserRequest(
            HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            owner);
        commit.Content = JsonContent.Create(new { accountId = account, candidateIds = (Guid[]?)null });
        using var response = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid owner, string statement, string fileName)
    {
        var request = UserRequest(
            HttpMethod.Post,
            $"/api/import-jobs/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            owner);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(statement));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", fileName);
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task SeedAsync(
        BackendWebApplicationFactory factory,
        Guid userId,
        Guid accountId,
        string currency = "EUR",
        bool includeInNetWorth = true,
        bool isActive = true)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Statement owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"statement-{accountId:N}",
                ProviderAccountId = $"statement-{accountId:N}",
                InstitutionName = "Ikano Bank",
                DisplayName = "Extra Konto",
                Currency = currency,
                IsActive = isActive,
                IncludeInNetWorth = includeInNetWorth
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });
    }
}
