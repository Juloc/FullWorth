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

public sealed class ExportVisibilityTests
{
    [Fact]
    public async Task ExportContainsOnlyVisibleAccountsAndNeverRawTransactionPayloads()
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
                DisplayName = "Export user",
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
                InstitutionName = "Visible Bank",
                Country = "DE",
                ProviderSessionId = "visible-session"
            };
            var hiddenConnection = new BankConnection
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "test",
                InstitutionName = "Hidden Bank",
                Country = "DE",
                ProviderSessionId = "hidden-session"
            };
            db.BankConnections.AddRange(visibleConnection, hiddenConnection);
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = visibleAccountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    BankConnectionId = visibleConnection.Id,
                    Provider = "test",
                    ProviderAccountId = "visible-account",
                    IdentificationHash = "visible-hash",
                    InstitutionName = "Visible Bank",
                    DisplayName = "VISIBLE_EXPORT_ACCOUNT",
                    Currency = "EUR"
                },
                new FinanceAccount
                {
                    Id = hiddenAccountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    BankConnectionId = hiddenConnection.Id,
                    Provider = "test",
                    ProviderAccountId = "hidden-account",
                    IdentificationHash = "hidden-hash",
                    InstitutionName = "Hidden Bank",
                    DisplayName = "HIDDEN_EXPORT_ACCOUNT",
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
                    ExternalKey = "visible-export-transaction",
                    BookingDate = new DateOnly(2026, 8, 1),
                    Amount = -12.34m,
                    Currency = "EUR",
                    Counterparty = "VISIBLE_EXPORT_MERCHANT",
                    RawJson = "{\"secretMarker\":\"RAW_PAYLOAD_MUST_NEVER_EXPORT\"}"
                },
                new FinanceTransaction
                {
                    AccountId = hiddenAccountId,
                    ExternalKey = "hidden-export-transaction",
                    BookingDate = new DateOnly(2026, 8, 1),
                    Amount = -99.99m,
                    Currency = "EUR",
                    Counterparty = "HIDDEN_EXPORT_MERCHANT",
                    RawJson = "{\"hidden\":true}"
                });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"export.read"},{true},{DateTimeOffset.UtcNow})
""");
        });

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/export/xlsx-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&includePurchases=false&includeInvestments=false");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            text.Append(await reader.ReadToEndAsync());
        }
        var workbookXml = text.ToString();
        Assert.Contains("VISIBLE_EXPORT_ACCOUNT", workbookXml, StringComparison.Ordinal);
        Assert.Contains("VISIBLE_EXPORT_MERCHANT", workbookXml, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_EXPORT_ACCOUNT", workbookXml, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_EXPORT_MERCHANT", workbookXml, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_PAYLOAD_MUST_NEVER_EXPORT", workbookXml, StringComparison.Ordinal);
        Assert.DoesNotContain("secretMarker", workbookXml, StringComparison.OrdinalIgnoreCase);
    }
}
