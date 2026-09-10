using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// A company car is a taxable benefit in kind, so payroll adds it to the gross. The history chart drew it
/// as a curve of its own instead — a small, often negative net-cash line that only flattened the scale and
/// answered a different question than "what did this job pay". It now sits on the Brutto figure, and the
/// baseline and the inflation/nominal/real comparisons sit on that same basis so the curves are not
/// comparing two different definitions of gross.
/// </summary>
public sealed class CompensationTimelineCompanyCarTests
{
    private static readonly CompanyCarInput Car = new(
        Enabled: true,
        ListPrice: 50_000m,
        OneWayCommuteKm: 20m);

    [Fact]
    public async Task The_taxable_car_benefit_is_counted_on_the_gross()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateAsync(client, userId, new DateOnly(2025, 1, 1), Profile(60_000m) with { CompanyCar = Car });

        var point = (await TimelineAsync(client, userId)).Points[^1];

        Assert.True(point.CompanyCarTaxableBenefitAnnual > 0m, "the 1 % rule plus the commute is a real benefit");
        Assert.Equal(60_000m, point.ContractualGrossAnnual);
        Assert.Equal(
            point.ContractualGrossAnnual + point.CompanyCarTaxableBenefitAnnual,
            point.GrossIncludingCompanyCarAnnual);
    }

    // Without a car the two gross figures are the same number, so nothing about an ordinary history moves.
    [Fact]
    public async Task Without_a_car_the_gross_is_unchanged()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateAsync(client, userId, new DateOnly(2025, 1, 1), Profile(60_000m));

        var timeline = await TimelineAsync(client, userId);
        var point = timeline.Points[^1];

        Assert.Equal(0m, point.CompanyCarTaxableBenefitAnnual);
        Assert.Equal(point.ContractualGrossAnnual, point.GrossIncludingCompanyCarAnnual);
        Assert.Equal(0m, timeline.Summary!.CurrentCompanyCarTaxableBenefitAnnual);
    }

    // The point of the change: getting a car on an unchanged salary IS a gross increase, and the chart has
    // to show it as one. Ordering by the cash gross alone reported "nothing happened".
    [Fact]
    public async Task Getting_a_car_on_an_unchanged_salary_shows_as_a_gross_increase()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateAsync(client, userId, new DateOnly(2024, 1, 1), Profile(60_000m));
        await CreateAsync(client, userId, new DateOnly(2025, 1, 1), Profile(60_000m) with { CompanyCar = Car });

        var timeline = await TimelineAsync(client, userId);

        Assert.Equal(60_000m, timeline.Summary!.BaselineGrossAnnual);
        Assert.True(
            timeline.Summary.CurrentGrossAnnual > 60_000m,
            $"gross including the car should have risen, got {timeline.Summary.CurrentGrossAnnual}");
        Assert.True(
            timeline.Summary.NominalChangePercent > 0m,
            $"the nominal change must see it too, got {timeline.Summary.NominalChangePercent}");
        Assert.Equal(
            timeline.Summary.CurrentGrossAnnual - 60_000m,
            timeline.Summary.CurrentCompanyCarTaxableBenefitAnnual);
    }

    // A car that was already there at the start must not read as a raise: the baseline carries it too.
    [Fact]
    public async Task A_car_present_from_the_start_is_part_of_the_baseline()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateAsync(client, userId, new DateOnly(2024, 1, 1), Profile(60_000m) with { CompanyCar = Car });
        await CreateAsync(client, userId, new DateOnly(2025, 1, 1), Profile(60_000m) with { CompanyCar = Car });

        var timeline = await TimelineAsync(client, userId);

        Assert.True(timeline.Summary!.BaselineGrossAnnual > 60_000m, "the baseline includes the car");
        Assert.Equal(0m, timeline.Summary.NominalChangePercent);
    }

    private static async Task<CompensationTimelineResult> TimelineAsync(HttpClient client, Guid userId)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/compensation/timeline?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&from=2024-01-01&to=2025-12-31",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = await response.Content.ReadFromJsonAsync<CompensationTimelineResult>();
        Assert.NotNull(timeline);
        Assert.NotEmpty(timeline.Points);
        return timeline;
    }

    private static async Task CreateAsync(
        HttpClient client,
        Guid userId,
        DateOnly effectiveDate,
        CompensationProfileInput profile)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/compensation/history?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            new CompensationHistoryWrite(effectiveDate, "salary", $"Stand {effectiveDate:yyyy}", null, profile)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
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
                DisplayName = "Company car user",
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

    private static CompensationProfileInput Profile(decimal gross) => new(
        Name: "Firmenwagen",
        AnnualGross: gross,
        GrossInputMode: "annual",
        SalaryPaymentsPerYear: 12,
        TaxClass: 1,
        StateCode: "BW",
        ChildrenUnder25: 0,
        Age: 30,
        ChildlessCareSurcharge: true,
        HealthInsuranceAdditionalRatePercent: 2.9m,
        WeeklyHours: 40m,
        VacationDays: 30);
}
