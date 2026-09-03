using System.IO.Compression;
using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class ExportPortabilityRegressionTests
{
    [Fact]
    public async Task LegacyJsonSnapshotRequiresExplicitExportCapability()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "No export user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(
            HttpMethod.Get,
            $"/api/export/snapshot?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CsvZipContainsOnlyVisibleAccountsAndNeverRawProviderPayloads()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var visibleAccountId = Guid.NewGuid();
        var hiddenAccountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "CSV export user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });

            var visibleConnection = new BankConnection
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "test",
                InstitutionName = "Visible CSV Bank",
                Country = "DE",
                ProviderSessionId = "visible-csv-session"
            };
            var hiddenConnection = new BankConnection
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "test",
                InstitutionName = "Hidden CSV Bank",
                Country = "DE",
                ProviderSessionId = "hidden-csv-session"
            };
            db.BankConnections.AddRange(visibleConnection, hiddenConnection);
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = visibleAccountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    BankConnectionId = visibleConnection.Id,
                    Provider = "test",
                    ProviderAccountId = "visible-csv-account",
                    IdentificationHash = "visible-csv-hash",
                    InstitutionName = "Visible CSV Bank",
                    DisplayName = "VISIBLE_CSV_ACCOUNT",
                    Currency = "EUR"
                },
                new FinanceAccount
                {
                    Id = hiddenAccountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    BankConnectionId = hiddenConnection.Id,
                    Provider = "test",
                    ProviderAccountId = "hidden-csv-account",
                    IdentificationHash = "hidden-csv-hash",
                    InstitutionName = "Hidden CSV Bank",
                    DisplayName = "HIDDEN_CSV_ACCOUNT",
                    Currency = "EUR"
                });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = visibleAccountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Viewer
            });
            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    AccountId = visibleAccountId,
                    ExternalKey = "visible-csv-transaction",
                    BookingDate = new DateOnly(2026, 8, 1),
                    Amount = -12.34m,
                    Currency = "EUR",
                    Counterparty = "VISIBLE_CSV_MERCHANT",
                    RawJson = "{\"secretMarker\":\"CSV_RAW_PAYLOAD_MUST_NEVER_EXPORT\"}"
                },
                new FinanceTransaction
                {
                    AccountId = hiddenAccountId,
                    ExternalKey = "hidden-csv-transaction",
                    BookingDate = new DateOnly(2026, 8, 1),
                    Amount = -99.99m,
                    Currency = "EUR",
                    Counterparty = "HIDDEN_CSV_MERCHANT",
                    RawJson = "{\"hidden\":true}"
                });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"export.read"},{true},{DateTimeOffset.UtcNow})
""");
        });

        using var request = UserRequest(
            HttpMethod.Get,
            $"/api/export/csv-zip-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&includePurchases=false&includeInvestments=false",
            userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "accounts.csv");
        Assert.Contains(archive.Entries, entry => entry.FullName == "transactions.csv");
        Assert.Contains(archive.Entries, entry => entry.FullName == "transaction_splits.csv");
        Assert.Contains(archive.Entries, entry => entry.FullName == "tags.csv");

        var text = new StringBuilder();
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            text.Append(await reader.ReadToEndAsync());
        }
        var exported = text.ToString();
        Assert.Contains("VISIBLE_CSV_ACCOUNT", exported, StringComparison.Ordinal);
        Assert.Contains("VISIBLE_CSV_MERCHANT", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_CSV_ACCOUNT", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_CSV_MERCHANT", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("CSV_RAW_PAYLOAD_MUST_NEVER_EXPORT", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("secretMarker", exported, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
