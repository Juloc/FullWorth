using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// Die Berechnung selbst deckt <see cref="JobRadCalculatorTests"/> ab - hier geht es nur um den Weg dorthin:
/// dass der Endpunkt existiert, JSON auf dem Draht wie erwartet bindet und ohne Anmeldung nichts herausgibt.
/// </summary>
public sealed class JobRadEndpointTests
{
    [Fact]
    public async Task CompareReturnsTheHeadlineNumbers()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);
        using var client = factory.CreateClient();

        var request = new JobRadComparisonRequest(
            new CompensationProfileInput("Test", AnnualGross: 60_000m, StateCode: "BW", TaxYear: 2026),
            new JobRadLeasingInput(ListPriceGross: 3_000m, MonthlyLeasingRateGross: 100m),
            new JobRadCashPurchaseInput(2_400m));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/compensation/jobrad/compare")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        httpRequest.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));

        using var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JobRadResult>();
        Assert.NotNull(result);
        Assert.True(result!.NetMonthlyImpact < 0m);
        Assert.Equal(7.00m, result.MonthlyTaxableBenefit);
        Assert.False(result.Pension.AboveContributionCeiling);
    }

    [Fact]
    public async Task CompareWithoutTheInternalKeyIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new JobRadComparisonRequest(
            new CompensationProfileInput("Test", AnnualGross: 60_000m),
            new JobRadLeasingInput(ListPriceGross: 3_000m, MonthlyLeasingRateGross: 100m),
            new JobRadCashPurchaseInput(2_400m));

        using var response = await client.PostAsJsonAsync("/api/compensation/jobrad/compare", request);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
