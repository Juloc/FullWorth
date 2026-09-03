using System.Net;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class AuditCapabilityTests
{
    [Fact]
    public async Task ViewerCannotReadAuditWithoutExplicitGrant()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await Seed(factory, userId, false);

        using var request = UserRequest($"/api/audit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExplicitAuditGrantAllowsMemberToReadAudit()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await Seed(factory, userId, true);

        using var request = UserRequest($"/api/audit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("test.audit", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Seed(BackendWebApplicationFactory factory, Guid userId, bool grant)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Audit user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });
            db.Set<AuditEvent>().Add(new AuditEvent
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                ActorUserId = userId,
                Action = "test.audit",
                EntityType = "Test"
            });
            await db.SaveChangesAsync();
            if (grant)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"audit.read"},{true},{DateTimeOffset.UtcNow})
""");
            }
        });
    }

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
