using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Bootstrap;

public sealed class BootstrapEndpointsTests
{
    [Fact]
    public async Task FirstAdminBootstrapCreatesUserSpaceAndOwnerMembership()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(BootstrapRequest(new
        {
            email = "owner@example.com",
            displayName = "Owner",
            spaceName = "Home",
            baseCurrency = "EUR"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResult>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.FinanceUserId);
        Assert.NotEqual(Guid.Empty, body.FullWorthSpaceId);

        await factory.SeedAsync(async db =>
        {
            var user = await db.Set<FullWorthUser>().AsNoTracking().SingleAsync(x => x.Id == body.FinanceUserId);
            Assert.Equal("OWNER@EXAMPLE.COM", user.EmailNormalized);
            Assert.True(user.IsActive);

            var space = await db.Set<FullWorthSpace>().AsNoTracking().SingleAsync(x => x.Id == body.FullWorthSpaceId);
            Assert.Equal("Home", space.Name);

            var owner = await db.Set<FullWorthSpaceMember>().AsNoTracking()
                .SingleAsync(x => x.FullWorthSpaceId == body.FullWorthSpaceId && x.UserId == body.FinanceUserId);
            Assert.Equal(FullWorthSpaceRoles.Owner, owner.Role);

            // The atomic space creation also seeds default categories for the new space.
            Assert.True(await db.Set<FinanceCategory>().AnyAsync(x => x.FullWorthSpaceId == body.FullWorthSpaceId));
        });
    }

    [Fact]
    public async Task SecondBootstrapIsRejectedOnceAUserExists()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(BootstrapRequest(new { email = "a@example.com", displayName = "A" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.SendAsync(BootstrapRequest(new { email = "b@example.com", displayName = "B" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.Set<FullWorthUser>().CountAsync()));
    }

    [Fact]
    public async Task BootstrapWithoutInternalKeyIsUnauthorized()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bootstrap/first-admin")
        {
            Content = JsonContent.Create(new { email = "x@example.com", displayName = "X" })
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.False(await db.Set<FullWorthUser>().AnyAsync()));
    }

    private static HttpRequestMessage BootstrapRequest(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bootstrap/first-admin");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record BootstrapResult(Guid FinanceUserId, Guid FullWorthSpaceId);
}
