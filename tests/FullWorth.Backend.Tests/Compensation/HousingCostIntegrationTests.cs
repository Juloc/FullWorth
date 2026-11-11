using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class HousingCostIntegrationTests
{
    [Fact]
    public async Task HousingCostsAreRecordedCorrectedAndDeletedByTheirOwnerOnly()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var space = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            foreach (var (id, name) in new[] { (owner, "Owner"), (colleague, "Colleague") })
            {
                db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EXAMPLE.COM", DisplayName = name, IsActive = true });
            }
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Housing Space", BaseCurrency = "EUR" });
            foreach (var id in new[] { owner, colleague })
                db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = id, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
        });

        var path = $"/api/compensation/housing-costs?fullWorthSpaceId={space:D}";
        using (var created = await client.SendAsync(Request(HttpMethod.Post, path, owner,
            new { kind = "heating", periodFrom = "2024-01-01", periodTo = "2024-04-30", amount = 368.98, note = "Hecon 2024", paperlessDocumentId = 2941 })))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Ungueltig: unbekannte Art, Ende vor Anfang, Betrag null.
        foreach (var bad in new object[]
        {
            new { kind = "pool", periodFrom = "2024-01-01", periodTo = "2024-04-30", amount = 1 },
            new { kind = "heating", periodFrom = "2024-05-01", periodTo = "2024-04-30", amount = 1 },
            new { kind = "heating", periodFrom = "2024-01-01", periodTo = "2024-04-30", amount = 0 }
        })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Post, path, owner, bad));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var list = await ListAsync(client, path, owner);
        var entry = Assert.Single(list);
        Assert.Equal(368.98m, entry.GetProperty("amount").GetDecimal());
        Assert.Equal(2941, entry.GetProperty("paperlessDocumentId").GetInt32());
        var id = entry.GetProperty("id").GetGuid();

        // Ein anderes Mitglied desselben Spaces sieht nichts und aendert nichts.
        Assert.Empty(await ListAsync(client, path, colleague));
        var item = $"/api/compensation/housing-costs/{id:D}?fullWorthSpaceId={space:D}";
        using (var response = await client.SendAsync(Request(HttpMethod.Delete, item, colleague, null)))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using (var response = await client.SendAsync(Request(HttpMethod.Put, item, owner,
            new { kind = "heating", periodFrom = "2024-01-01", periodTo = "2024-04-30", amount = 370.00 })))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(370m, Assert.Single(await ListAsync(client, path, owner)).GetProperty("amount").GetDecimal());

        using (var response = await client.SendAsync(Request(HttpMethod.Delete, item, owner, null)))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListAsync(client, path, owner));
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string path, Guid user)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, path, user, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid user, object? body)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", user.ToString("D"));
        return request;
    }
}
