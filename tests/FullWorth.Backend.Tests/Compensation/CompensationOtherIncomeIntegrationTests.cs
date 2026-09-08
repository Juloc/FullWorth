using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// End-to-end tests for the "sonstige regelmäßige Einkünfte" track (needs FULLWORTH_TEST_POSTGRES).
/// </summary>
public sealed class CompensationOtherIncomeIntegrationTests
{
    private const string Space = "fullWorthSpaceId";

    [Fact]
    public async Task OtherIncomeCrudRoundTripsThroughTheApi()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", "Halbwaisenrente DRV", 412.55m,
            new DateOnly(2024, 3, 1), null, true, "Bis zum Ende des Studiums"));

        Assert.Equal("halbwaisenrente", created.Type);
        Assert.Equal(412.55m, created.MonthlyAmount);
        Assert.Equal(4_950.60m, created.AnnualAmount);
        Assert.Null(created.ValidTo);
        Assert.True(created.CountsTowardPersonalIncome);

        var listed = Assert.Single(await ListOtherIncomeAsync(client, userId));
        Assert.Equal(created.Id, listed.Id);

        using var update = await client.SendAsync(UserRequest(
            HttpMethod.Put,
            $"/api/compensation/other-income/{created.Id}?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            new CompensationOtherIncomeWrite(
                "Halbwaisenrente", null, 430m,
                new DateOnly(2024, 3, 1), new DateOnly(2030, 2, 28), false, null)));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<CompensationOtherIncomeEntry>())!;
        Assert.Equal(430m, updated.MonthlyAmount);
        Assert.Equal(new DateOnly(2030, 2, 28), updated.ValidTo);
        Assert.False(updated.CountsTowardPersonalIncome);

        using var invalid = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/compensation/other-income?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            new CompensationOtherIncomeWrite(
                "rente", null, 100m, new DateOnly(2025, 1, 1), new DateOnly(2024, 1, 1), true, null)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var delete = await client.SendAsync(UserRequest(
            HttpMethod.Delete,
            $"/api/compensation/other-income/{created.Id}?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await ListOtherIncomeAsync(client, userId));
    }

    /// <summary>
    /// The hard requirement: "Der Gehaltsrechner selbst sollte dadurch nicht verändert werden."
    /// Every salary/employer figure on every timeline point — including the employer total package — must
    /// be structurally identical with and without other-income records, and the point grid must not gain
    /// or lose a single point.
    /// </summary>
    [Fact]
    public async Task EmployerPackageFiguresAreByteIdenticalWithAndWithoutOtherIncome()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateHistoryAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));
        await CreateHistoryAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2025, 4, 1), "salary", "Gehaltserhöhung", null, Profile(58_000m)));

        var before = await TimelineAsync(client, userId);

        // Several records, one of them starting mid-month on a date that is NOT on the point grid, to
        // prove the grid itself is not influenced by the other-income track.
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", null, 412.55m, new DateOnly(2024, 2, 17), null, true, null));
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Mieteinnahmen", null, 750m, new DateOnly(2023, 5, 1), new DateOnly(2026, 1, 31), false, null));

        var after = await TimelineAsync(client, userId);

        Assert.Equal(before.Points.Count, after.Points.Count);
        Assert.Equal(
            before.Points.Select(StripOtherIncome).ToArray(),
            after.Points.Select(StripOtherIncome).ToArray());
        Assert.Equal(StripOtherIncome(before.Summary!), StripOtherIncome(after.Summary!));

        // Sanity: the salary figures we just froze are non-trivial, and the new track really did arrive.
        Assert.True(after.Points[^1].EmployerTotalCostAnnual > after.Points[^1].ContractualGrossAnnual);
        Assert.True(after.Points[^1].OtherRegularIncomeAnnual > 0m);
        Assert.Equal(2, after.OtherIncome.Count);
    }

    [Fact]
    public async Task TimelineExposesOtherIncomeOnlyInsideItsWindow()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateHistoryAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", null, 300m,
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), true, null));

        var timeline = await TimelineAsync(client, userId, "2023-01-01", "2026-01-01");

        var inside = timeline.Points.Single(x => x.Date == new DateOnly(2024, 6, 1));
        Assert.Equal(3_600m, inside.OtherRegularIncomeAnnual);
        Assert.Equal(3_600m, inside.OtherRegularIncomeCountedAnnual);
        Assert.Equal(
            inside.EstimatedCashNetAnnual + 3_600m,
            inside.PersonallyAvailableTotalIncomeAnnual);

        foreach (var date in new[] { new DateOnly(2023, 6, 1), new DateOnly(2025, 6, 1) })
        {
            var outside = timeline.Points.Single(x => x.Date == date);
            Assert.Equal(0m, outside.OtherRegularIncomeAnnual);
            Assert.Equal(0m, outside.OtherRegularIncomeCountedAnnual);
            Assert.Equal(outside.EstimatedCashNetAnnual, outside.PersonallyAvailableTotalIncomeAnnual);
        }
    }

    [Fact]
    public async Task OpenEndedOtherIncomeKeepsContributingAtTheLatestPoint()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateHistoryAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", null, 300m, new DateOnly(2024, 1, 1), null, true, null));

        var timeline = await TimelineAsync(client, userId, "2023-01-01", "2030-01-01");

        Assert.Equal(0m, timeline.Points.Single(x => x.Date == new DateOnly(2023, 6, 1)).OtherRegularIncomeAnnual);
        foreach (var date in new[] { new DateOnly(2024, 1, 1), new DateOnly(2027, 7, 1), new DateOnly(2030, 1, 1) })
            Assert.Equal(3_600m, timeline.Points.Single(x => x.Date == date).OtherRegularIncomeAnnual);

        Assert.Equal(3_600m, timeline.Summary!.CurrentOtherRegularIncomeAnnual);
        Assert.Equal(
            timeline.Summary.CurrentNetAnnual + 3_600m,
            timeline.Summary.CurrentPersonallyAvailableTotalIncomeAnnual);
    }

    [Fact]
    public async Task UnflaggedOtherIncomeIsTrackedButExcludedFromThePersonallyAvailableTotal()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateHistoryAsync(client, userId, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt", null, Profile(50_000m)));
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", null, 300m, new DateOnly(2023, 1, 1), null, true, null));
        await CreateOtherIncomeAsync(client, userId, new CompensationOtherIncomeWrite(
            "Mieteinnahmen", null, 500m, new DateOnly(2023, 1, 1), null, false, null));

        var current = (await TimelineAsync(client, userId, "2023-01-01", "2026-01-01")).Points[^1];

        Assert.Equal(9_600m, current.OtherRegularIncomeAnnual);
        Assert.Equal(3_600m, current.OtherRegularIncomeCountedAnnual);
        Assert.Equal(
            current.EstimatedCashNetAnnual + 3_600m,
            current.PersonallyAvailableTotalIncomeAnnual);
    }

    [Fact]
    public async Task JointScopeSumsOtherIncomeOfEverySpaceMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var first = await SeedUserAsync(factory);
        var second = await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        await CreateHistoryAsync(client, first, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt A", null, Profile(50_000m)));
        await CreateHistoryAsync(client, second, new CompensationHistoryWrite(
            new DateOnly(2023, 1, 1), "salary", "Startgehalt B", null, Profile(40_000m)));

        await CreateOtherIncomeAsync(client, first, new CompensationOtherIncomeWrite(
            "Halbwaisenrente", null, 300m, new DateOnly(2023, 1, 1), null, true, null));
        await CreateOtherIncomeAsync(client, second, new CompensationOtherIncomeWrite(
            "Witwenrente", null, 200m, new DateOnly(2023, 1, 1), null, true, null));
        await CreateOtherIncomeAsync(client, second, new CompensationOtherIncomeWrite(
            "Unterhalt", null, 150m, new DateOnly(2023, 1, 1), null, false, null));

        var own = (await TimelineAsync(client, first, "2023-01-01", "2026-01-01")).Points[^1];
        Assert.Equal(3_600m, own.OtherRegularIncomeAnnual);

        var jointTimeline = await TimelineAsync(client, first, "2023-01-01", "2026-01-01", scope: "joint");
        var joint = jointTimeline.Points[^1];

        Assert.Equal((300m + 200m + 150m) * 12m, joint.OtherRegularIncomeAnnual);
        Assert.Equal((300m + 200m) * 12m, joint.OtherRegularIncomeCountedAnnual);
        Assert.Equal(
            joint.EstimatedCashNetAnnual + (300m + 200m) * 12m,
            joint.PersonallyAvailableTotalIncomeAnnual);
        Assert.Equal(3, jointTimeline.OtherIncome.Count);

        // The joint salary side is still the plain sum of both members' calculator results.
        Assert.Equal(90_000m, joint.ContractualGrossAnnual);
    }

    private static CompensationTimelinePoint StripOtherIncome(CompensationTimelinePoint point) => point with
    {
        OtherRegularIncomeAnnual = 0m,
        OtherRegularIncomeCountedAnnual = 0m,
        PersonallyAvailableTotalIncomeAnnual = 0m
    };

    private static CompensationTimelineSummary StripOtherIncome(CompensationTimelineSummary summary) => summary with
    {
        CurrentOtherRegularIncomeAnnual = 0m,
        CurrentPersonallyAvailableTotalIncomeAnnual = 0m
    };

    private static async Task<CompensationTimelineResult> TimelineAsync(
        HttpClient client, Guid userId, string? from = null, string? to = null, string? scope = null)
    {
        var query = $"/api/compensation/timeline?{Space}={FullWorthSpaceDefaults.LegacyId:D}";
        if (from is not null) query += $"&from={from}";
        if (to is not null) query += $"&to={to}";
        if (scope is not null) query += $"&scope={scope}";

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get, query, userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompensationTimelineResult>())!;
    }

    private static async Task<CompensationOtherIncomeEntry> CreateOtherIncomeAsync(
        HttpClient client, Guid userId, CompensationOtherIncomeWrite write)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/compensation/other-income?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            write));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompensationOtherIncomeEntry>())!;
    }

    private static async Task<IReadOnlyList<CompensationOtherIncomeEntry>> ListOtherIncomeAsync(
        HttpClient client, Guid userId)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/compensation/other-income?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<CompensationOtherIncomeEntry>>())!;
    }

    private static async Task CreateHistoryAsync(
        HttpClient client, Guid userId, CompensationHistoryWrite write)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/compensation/history?{Space}={FullWorthSpaceDefaults.LegacyId:D}",
            userId,
            write));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
                DisplayName = "Other income user",
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
