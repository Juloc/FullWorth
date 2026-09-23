using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Die Depotliste - beide Haelften.
///
/// Sie hat eine Sichtbarkeitsregel (ein Depot an einem fremden Konto gehoert nicht hierher) und einen
/// Inhalt (Wert, Einstand, Gewinn). Bis 2026-09-23 gab es fuer jede Haelfte eine eigene Fassung: eine
/// Middleware setzte die Regel durch und beantwortete die Route gleich selbst - mit nur den
/// Stammdaten. Der gemappte Handler, der die Werte berechnet, lief nie. Getestet war nur die Regel,
/// und deshalb war alles gruen, waehrend die Vermoegensseite leere Spalten zeigte.
/// </summary>
public sealed class InvestmentPortfolioVisibilityTests
{
    [Fact]
    public async Task LegacyPortfolioListDoesNotExposePortfoliosLinkedToHiddenAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var visibleAccountId = Guid.NewGuid();
        var hiddenAccountId = Guid.NewGuid();
        var visiblePortfolioId = Guid.NewGuid();
        var hiddenPortfolioId = Guid.NewGuid();
        var unlinkedPortfolioId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Investment viewer",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });

            db.Accounts.AddRange(
                Account(visibleAccountId, "Visible investment account"),
                Account(hiddenAccountId, "Hidden investment account"));
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = visibleAccountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Viewer
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES
({visiblePortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Visible Depot"},{"EUR"},{visibleAccountId},{true},{true},{false},{now},{now}),
({hiddenPortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Hidden Depot"},{"EUR"},{hiddenAccountId},{true},{true},{false},{now},{now}),
({unlinkedPortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Manual Depot"},{"EUR"},{null},{true},{true},{false},{now},{now})
""");
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/portfolios?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = document.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid())
            .ToHashSet();

        Assert.Contains(visiblePortfolioId, ids);
        Assert.Contains(unlinkedPortfolioId, ids);
        Assert.DoesNotContain(hiddenPortfolioId, ids);
    }

    [Fact]
    public async Task PortfolioListCarriesEveryFieldTheWealthPageReadsOffARow()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        await SeedPlainPortfolioAsync(factory, userId, portfolioId);

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/portfolios?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = document.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == portfolioId);
        var fields = row.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        // Was die Vermoegensseite an einer Depotzeile anfasst. Zwischen ihr und dieser Antwort steht
        // kein Vertrag - ein fehlendes Feld ist dort eine leere Spalte und kein Fehler.
        var read = Regex.Matches(WealthPage(), @"\bportfolio\.([a-zA-Z][a-zA-Z0-9]*)")
            .Select(match => match.Groups[1].Value)
            .Concat(["totalValue", "costBasis", "unrealizedResult", "gainIncomplete"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var missing = read.Where(name => !fields.Contains(name)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            "GET /api/investments/portfolios liefert diese Felder nicht:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
    }

    private static string WealthPage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Web", "wwwroot", "pages", "networth", "page.js"));
    }

    private static async Task SeedPlainPortfolioAsync(BackendWebApplicationFactory factory, Guid userId, Guid portfolioId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Portfolio owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","AccountId","IsArchived","CreatedAt","UpdatedAt")
VALUES ('{portfolioId}','{FullWorthSpaceDefaults.LegacyId}','Depot','EUR',NULL,false,now(),now());
""");
        });
    }

    private static FinanceAccount Account(Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Provider = "manual",
        IdentificationHash = $"investment-visibility-{id:N}",
        ProviderAccountId = $"investment-visibility-{id:N}",
        InstitutionName = "Manual",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
