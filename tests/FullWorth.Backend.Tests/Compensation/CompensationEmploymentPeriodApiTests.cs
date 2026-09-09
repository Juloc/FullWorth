using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// The calculator has handled a partial employment year since it existed, and the calculator tests
/// cover the arithmetic. What was never covered is the way in: the two dates travel as JSON strings
/// from the browser, so this posts them the way the page posts them.
/// </summary>
public sealed class CompensationEmploymentPeriodApiTests
{
    [Fact]
    public async Task CalculateReducesTheYearToTheMonthsActuallyEmployed()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        var fullYear = await CalculateAsync(client, userId, employmentStart: null, employmentEnd: null);
        var fromSeptember = await CalculateAsync(client, userId, "2026-09-01", "2026-12-31");

        Assert.Equal(12m, fullYear.GetProperty("monthsEmployedInYear").GetDecimal());
        Assert.Equal(4m, fromSeptember.GetProperty("monthsEmployedInYear").GetDecimal());

        // The contract is worth the same either way; only the year's cash changes.
        Assert.Equal(
            fullYear.GetProperty("contractualGrossAnnual").GetDecimal(),
            fromSeptember.GetProperty("contractualGrossAnnual").GetDecimal());
        Assert.True(
            fromSeptember.GetProperty("estimatedCashNetAnnual").GetDecimal()
            < fullYear.GetProperty("estimatedCashNetAnnual").GetDecimal());
    }

    [Fact]
    public async Task AnEndBeforeTheStartIsRejectedAsABadRequestNotAServerError()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        using var request = Request(userId, "2026-09-01", "2026-03-01");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonElement> CalculateAsync(
        HttpClient client, Guid userId, string? employmentStart, string? employmentEnd)
    {
        using var request = Request(userId, employmentStart, employmentEnd);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    // Raw JSON on purpose: the point is that the dates bind from the wire format the browser sends.
    private static HttpRequestMessage Request(Guid userId, string? employmentStart, string? employmentEnd)
    {
        var payload = new StringBuilder("{\"name\":\"Teiljahr\",\"annualGross\":60000,\"taxYear\":2026,\"taxClass\":1");
        if (employmentStart is not null) payload.Append($",\"employmentStart\":\"{employmentStart}\"");
        if (employmentEnd is not null) payload.Append($",\"employmentEnd\":\"{employmentEnd}\"");
        payload.Append('}');

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/compensation/calculate?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<Guid> SeedUserAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Employment period user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });
        return userId;
    }
}
