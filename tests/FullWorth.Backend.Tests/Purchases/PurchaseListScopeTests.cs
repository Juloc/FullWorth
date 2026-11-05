using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// The purchase list pages through the server (the browser cannot hold every purchase with its items) and
/// the two reasons a purchase asks for attention are filters and counts on the server, not arithmetic
/// over a list the page no longer has.
/// </summary>
public sealed class PurchaseListScopeTests
{
    [Fact]
    public async Task Scope_limit_offset_and_counts_agree_on_what_needs_attention()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            var day = new DateOnly(2026, 6, 1);
            Purchase Make(string status, string source, int ageDays) => new()
            {
                FullWorthSpaceId = spaceId, CreatedByUserId = userId, Merchant = "Shop", Source = source, Status = status,
                PurchaseDate = day.AddDays(-ageDays), TotalAmount = 1m
            };
            // three not confirmed, two confirmed receipts with no booking, one confirmed Amazon order
            db.Purchases.AddRange(Make("review", "receipt", 0), Make("review", "receipt", 1), Make("captured", "receipt", 2),
                Make("confirmed", "receipt", 3), Make("confirmed", "receipt", 4), Make("confirmed", "amazon", 5));
            await db.SaveChangesAsync();
        });

        async Task<JsonElement> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}{(path.Contains('?') ? '&' : '?')}fullWorthSpaceId={spaceId:D}");
            request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
            request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
            using var response = await client.SendAsync(request);
            return path.Contains("scope=nonsense")
                ? JsonDocument.Parse($"{{\"status\":{(int)response.StatusCode}}}").RootElement
                : JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        var counts = await Get("/api/purchases/counts");
        Assert.Equal((6, 3, 2), (counts.GetProperty("all").GetInt32(), counts.GetProperty("review").GetInt32(), counts.GetProperty("unlinked").GetInt32()));

        Assert.Equal(6, (await Get("/api/purchases")).GetArrayLength());
        Assert.Equal(3, (await Get("/api/purchases?scope=review")).GetArrayLength());
        Assert.Equal(2, (await Get("/api/purchases?scope=unlinked")).GetArrayLength());

        // Pages of the same list do not overlap and run out where the list does.
        var first = await Get("/api/purchases?scope=review&limit=2&offset=0");
        var second = await Get("/api/purchases?scope=review&limit=2&offset=2");
        Assert.Equal(2, first.GetArrayLength());
        Assert.Equal(1, second.GetArrayLength());
        var ids = first.EnumerateArray().Concat(second.EnumerateArray()).Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(3, ids.Distinct().Count());

        Assert.Equal(400, (await Get("/api/purchases?scope=nonsense")).GetProperty("status").GetInt32());
    }
}
