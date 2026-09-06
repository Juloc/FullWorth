using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class CompensationHistoryIntegrationTests
{
    [Fact]
    public async Task EarlierSalaryCorrectionFlowsThroughLaterTaxEvent()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        var baseline = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2024, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));

        _ = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2025, 1, 1), "tax", "Steuerklasse IV", null,
            Profile(50_000m) with { TaxClass = 4, TaxClass4Factor = 0.9m }));

        var correctedBaseline = new CompensationHistoryWrite(
            new DateOnly(2024, 1, 1), "salary", "Startgehalt korrigiert", null, Profile(52_000m));
        using var update = await client.SendAsync(UserRequest(
            HttpMethod.Put,
            $"/api/compensation/history/{baseline.Id}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            correctedBaseline));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var history = await GetHistoryAsync(client, userId);
        var taxEvent = Assert.Single(history, x => x.EventType == "tax");

        Assert.Equal(52_000m, taxEvent.ResolvedProfile.AnnualGross);
        Assert.Equal(4, taxEvent.ResolvedProfile.TaxClass);
        Assert.Equal(0.9m, taxEvent.ResolvedProfile.TaxClass4Factor);
    }

    [Fact]
    public async Task MovingTaxEventAfterRaiseDoesNotResetLaterSalary()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        _ = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2024, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));

        var tax = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2025, 1, 1), "tax", "Steuerklasse IV", null,
            Profile(50_000m) with { TaxClass = 4, TaxClass4Factor = 0.9m }));

        _ = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2026, 1, 1), "salary", "Gehaltserhöhung", null,
            Profile(60_000m) with { TaxClass = 4, TaxClass4Factor = 0.9m }));

        var move = new CompensationHistoryWrite(
            new DateOnly(2026, 6, 1), "tax", "Steuerklasse IV verschoben", null,
            tax.ResolvedProfile);
        using var update = await client.SendAsync(UserRequest(
            HttpMethod.Put,
            $"/api/compensation/history/{tax.Id}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            move));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var history = await GetHistoryAsync(client, userId);
        var moved = Assert.Single(history, x => x.Id == tax.Id);

        Assert.Equal(new DateOnly(2026, 6, 1), moved.EffectiveDate);
        Assert.Equal(60_000m, moved.ResolvedProfile.AnnualGross);
        Assert.Equal(4, moved.ResolvedProfile.TaxClass);
    }

    [Fact]
    public async Task TimelineIncludesInflationAndAnnualCompensationMetrics()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        _ = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));
        _ = await CreateAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2026, 1, 1), "salary", "Gehaltserhöhung", null, Profile(55_000m)));

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/compensation/timeline?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&from=2023-01-01&to=2026-07-31",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var timeline = await response.Content.ReadFromJsonAsync<CompensationTimelineResult>();
        Assert.NotNull(timeline);
        Assert.NotNull(timeline.Summary);
        Assert.True(timeline.Points.Count > 3);
        Assert.True(timeline.Summary.InflationPercent > 0m);
        Assert.True(timeline.Points[^1].TaxesAnnual >= 0m);
        Assert.True(timeline.Points[^1].EmployerTotalCostAnnual > timeline.Points[^1].ContractualGrossAnnual);
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
                DisplayName = "Compensation history user",
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

    private static async Task<CompensationHistoryEntry> CreateAsync(
        HttpClient client, Guid userId, CompensationHistoryWrite write)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/compensation/history?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            write));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompensationHistoryEntry>())!;
    }

    private static async Task<IReadOnlyList<CompensationHistoryEntry>> GetHistoryAsync(
        HttpClient client, Guid userId)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/compensation/history?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<CompensationHistoryEntry>>())!;
    }

    private static HttpRequestMessage UserRequest(
        HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static CompensationProfileInput Profile(decimal gross) => new(
        Name: "Historie",
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
