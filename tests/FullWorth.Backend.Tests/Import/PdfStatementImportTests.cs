using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Documents;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Ein PDF-Kontoauszug laeuft durch denselben Weg wie MT940 und CAMT (#131, Abschnitt 11): Erkennung,
/// Vorschau, Festschreiben, Kontostand. Das Werkzeug, das die Worte liest (poppler), ist hier durch
/// einen festen Auszug ersetzt - geprueft wird, was der Import daraus macht, nicht poppler.
/// </summary>
public sealed class PdfStatementImportTests
{
    // Die ersten Bytes eines PDF - mehr braucht die Erkennung nicht, den Inhalt liefert der Fake.
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% erfunden\n");

    private sealed class FixedPdf(IReadOnlyList<IReadOnlyList<PdfLine>> pages) : IPdfWordSource
    {
        public Task<IReadOnlyList<IReadOnlyList<PdfLine>>> ReadLinesAsync(byte[] pdf, CancellationToken ct) =>
            Task.FromResult(pages);
    }

    [Fact]
    public async Task ThePdfIsRecognisedAsAStatementNotABrokerStatement()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var request = UserRequest(HttpMethod.Post, $"/api/import/detect?{Space}", user);
        request.Content = FileContent();
        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("statement", doc.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("ikano_pdf", doc.RootElement.GetProperty("reasonKey").GetString());
    }

    /// <summary>
    /// Die Pruefnotizen kommen bei der Vorschau an, und uebernommen wird, was der Nutzer stehen laesst -
    /// hier genau die Zeilen, die die Seite vorwaehlt: die echten Bewegungen, ohne die internen
    /// Umbuchungen. Der Kontostand des Auszugs verankert das Konto mit seinem Datum.
    /// </summary>
    [Fact]
    public async Task ThePreselectedRowsAndTheBalanceArriveOnTheAccount()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        using var client = factory.CreateClient();

        using var upload = UserRequest(HttpMethod.Post, $"/api/import-jobs/upload?{Space}", user);
        upload.Content = FileContent();
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadDoc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var jobId = uploadDoc.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal("ikano_pdf", uploadDoc.RootElement.GetProperty("adapter").GetString());
        Assert.Empty(uploadDoc.RootElement.GetProperty("warnings").EnumerateArray());

        using var candidatesRequest = UserRequest(HttpMethod.Get, $"/api/import-jobs/{jobId:D}/candidates?{Space}", user);
        using var candidatesResponse = await client.SendAsync(candidatesRequest);
        using var candidates = JsonDocument.Parse(await candidatesResponse.Content.ReadAsStringAsync());
        var rows = candidates.RootElement.EnumerateArray().ToList();
        Assert.Equal(7, rows.Count);
        var preselected = rows
            .Where(row => row.GetProperty("reviewNote").ValueKind == JsonValueKind.Null)
            .Select(row => row.GetProperty("id").GetGuid())
            .ToList();
        Assert.Equal(3, preselected.Count);
        Assert.All(rows.Where(row => row.GetProperty("reviewNote").ValueKind != JsonValueKind.Null),
            row => Assert.Equal("internal_transfer", row.GetProperty("reviewNote").GetString()));

        using var commit = UserRequest(HttpMethod.Post, $"/api/import-jobs/{jobId:D}/commit?{Space}", user);
        commit.Content = JsonContent.Create(new { accountId = account, candidateIds = preselected });
        using var committed = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var written = await db.Transactions.AsNoTracking().Where(item => item.AccountId == account).ToListAsync();
            Assert.Equal(3, written.Count);
            Assert.Equal(-50m, written.Sum(item => item.Amount));

            var balance = await db.BalanceSnapshots.AsNoTracking().SingleAsync(item => item.AccountId == account);
            Assert.Equal(-1_050m, balance.Amount);
            Assert.Equal(new DateOnly(2026, 9, 23), balance.ReferenceDate);
            Assert.Equal(BalanceSources.Import, balance.Source);
        });
    }

    private static readonly string Space = $"fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}";

    private static BackendWebApplicationFactory Factory() =>
        new(new Dictionary<string, string?>(), services =>
            services.AddSingleton<IPdfWordSource>(new FixedPdf(BankStatementPdfTests.Ikano())));

    private static MultipartFormDataContent FileContent()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "abrechnung.pdf");
        return content;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Guid> SeedAsync(BackendWebApplicationFactory factory)
    {
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@LOCAL.TEST", DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
        return user;
    }

    private static async Task<Guid> SeedAccountAsync(BackendWebApplicationFactory factory, Guid user)
    {
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{account:N}",
                IdentificationHash = $"manual-{account:N}",
                InstitutionName = "Ikano Bank",
                DisplayName = "IKEA Kreditkarte",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();
        });
        return account;
    }
}
