using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Pension.PensionContractIntegrationTests;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// docs/PENSION.md, "Money direction": a 338 € contribution made of 169 € employee and 169 € employer
/// is not a 338 € outflow. Only the employee's deferred share reduces net pay, so only it may reach
/// fixed costs; the employer share and the §1a subsidy feed the balance and must appear nowhere as a
/// cost. These tests guard the number that would be wrong if anyone ever wrote a total into
/// <c>RecurringContract.Amount</c>, and the history that would be lost if the link were deleted
/// instead of stopped.
/// </summary>
public sealed class PensionEmployeeShareContractTests
{
    /// <summary>
    /// The linked fixed cost is the employee share and nothing else. The assertion names the two
    /// mistakes it guards: the parts total (338) and the employer total (169) must not be the amount.
    /// </summary>
    [Fact]
    public async Task The_employee_share_becomes_the_fixed_cost_and_the_employer_share_becomes_no_cost_at_all()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Allianz Lebensversicherung AG",
            tariffName = "PensionInvest Flex",
            policyNumber = "BAV-4711",
            implementationRoute = "direct_insurance",
            employerName = "Muster GmbH",
            currency = "EUR"
        });

        using var response = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/contributions", new
        {
            validFrom = "2024-01-01",
            cycle = "monthly",
            employeeAmount = 169m,
            employerSubsidyAmount = 25.35m,
            employerAmount = 143.65m,
            statedTotalAmount = 338m,
            source = "document"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var contribution = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var partsTotal = contribution.RootElement.GetProperty("partsTotalAmount").GetDecimal();
        var employerTotal = contribution.RootElement.GetProperty("employerTotalAmount").GetDecimal();
        var statedTotal = contribution.RootElement.GetProperty("statedTotalAmount").GetDecimal();
        Assert.Equal(338m, partsTotal);
        Assert.Equal(169m, employerTotal);
        Assert.Equal(338m, statedTotal);

        Guid linkedId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var linked = await db.Contracts.AsNoTracking()
                .SingleAsync(row => row.FullWorthSpaceId == scenario.Space);
            linkedId = linked.Id;

            // The one number this whole slice exists for: 169, not the 338 the shares add up to and not
            // the 338 the document stated. (The employer total is 169 too in this fixture, because that
            // is the split docs/PENSION.md argues about — the arrangement with a different split is
            // asserted in the newer-arrangement test below.)
            Assert.Equal(169m, linked.Amount);
            Assert.NotEqual(partsTotal, linked.Amount);
            Assert.NotEqual(statedTotal, linked.Amount);

            Assert.Equal("EUR", linked.Currency);
            Assert.Equal("monthly", linked.BillingCycle);
            Assert.Equal(1, linked.Interval);
            Assert.Equal(new DateOnly(2024, 1, 1), linked.StartDate);
            Assert.Null(linked.EndDate);
            Assert.True(linked.IsActive);
            Assert.Equal("insurance", linked.Kind);
            Assert.Equal("Allianz Lebensversicherung AG", linked.ProviderName);
            Assert.Contains("Allianz Lebensversicherung AG", linked.Name);
            // An Entgeltumwandlung is withheld from gross salary: it is nobody's account payment, and
            // it was not detected from one either.
            Assert.Null(linked.AccountId);
            Assert.False(linked.AutoDetected);
            // So nobody hand-edits it and wonders why it changes back.
            Assert.False(string.IsNullOrWhiteSpace(linked.Notes));

            // The employer share produces no second cost and no transaction anywhere.
            Assert.Equal(1, await db.Contracts.CountAsync(row => row.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.Transactions.CountAsync());

            var bav = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            Assert.Equal(linked.Id, bav.RecurringContractId);
        });

        // And the API says which contract it is, so the fixed-costs list and the pension detail agree.
        using var detail = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts/{contractId}?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(linkedId, json.RootElement.GetProperty("contract").GetProperty("recurringContractId").GetGuid());
    }

    /// <summary>
    /// A purely employer-financed contract (a Unterstützungskasse typically has no employee payment at
    /// all) gets no fixed cost. An empty 0 € entry would be noise, and a benefit is not a cost.
    /// </summary>
    [Fact]
    public async Task A_contribution_without_an_employee_share_creates_no_fixed_cost()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Unterstützungskasse Nord",
            policyNumber = "UK-77",
            implementationRoute = "provident_fund",
            currency = "EUR"
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2023-07-01",
                cycle = "monthly",
                employeeAmount = 0m,
                employerAmount = 250m,
                source = "document"
            })).StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Contracts.CountAsync(row => row.FullWorthSpaceId == scenario.Space));
            var bav = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            Assert.Null(bav.RecurringContractId);
        });
    }

    /// <summary>
    /// Contributions are append-only, so a raise in the deferred amount arrives as a newer dated row.
    /// It updates the one linked contract: a second one would count the same deferral twice in the
    /// fixed-costs total.
    /// </summary>
    [Fact]
    public async Task A_newer_arrangement_updates_the_linked_contract_instead_of_adding_a_second_one()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Pensionskasse Muster",
            policyNumber = "PK-1",
            implementationRoute = "pension_fund",
            currency = "EUR"
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2024-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                employerSubsidyAmount = 25.35m
            })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2025-04-01",
                cycle = "quarterly",
                employeeAmount = 300m,
                employerSubsidyAmount = 45m
            })).StatusCode);

        await factory.SeedAsync(async db =>
        {
            var linked = await db.Contracts.AsNoTracking()
                .SingleAsync(row => row.FullWorthSpaceId == scenario.Space);
            // 300 is the employee share alone: not the 345 the shares add up to, and not the 45 subsidy.
            Assert.Equal(300m, linked.Amount);
            Assert.NotEqual(345m, linked.Amount);
            Assert.NotEqual(45m, linked.Amount);
            Assert.Equal("quarterly", linked.BillingCycle);
            Assert.Equal(1, linked.Interval);
            Assert.Equal(new DateOnly(2025, 4, 1), linked.StartDate);
            Assert.True(linked.IsActive);

            // Both arrangements are still on record; only the link follows the current one.
            Assert.Equal(2, await db.BavContributions.CountAsync(row => row.BavContractId == contractId));
            var bav = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            Assert.Equal(linked.Id, bav.RecurringContractId);
        });
    }

    /// <summary>
    /// Beitragsfrei: the contributions stop, the contract, the balance and the costs do not. The fixed
    /// cost is ended and deactivated but never deleted — the payments happened, and the history has to
    /// survive.
    /// </summary>
    [Fact]
    public async Task Ending_the_arrangement_as_paid_up_stops_the_fixed_cost_and_keeps_the_row()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer A",
            policyNumber = "DV-9",
            implementationRoute = "direct_insurance",
            currency = "EUR"
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2019-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                employerSubsidyAmount = 25.35m
            })).StatusCode);

        Guid linkedId = Guid.Empty;
        await factory.SeedAsync(async db =>
            linkedId = await db.Contracts.AsNoTracking()
                .Where(row => row.FullWorthSpaceId == scenario.Space)
                .Select(row => row.Id).SingleAsync());

        // The arrangement is superseded by a row that ends it. That row is the current arrangement even
        // though its period is over, which is exactly why the store may not read "still running" off the
        // older, open-ended row.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2019-01-01",
                validUntil = "2024-03-31",
                endReason = "paid_up",
                cycle = "monthly",
                employeeAmount = 169m,
                employerSubsidyAmount = 25.35m
            })).StatusCode);

        await factory.SeedAsync(async db =>
        {
            var linked = await db.Contracts.AsNoTracking().SingleAsync(row => row.Id == linkedId);
            Assert.False(linked.IsActive);
            Assert.Equal(new DateOnly(2024, 3, 31), linked.EndDate);
            // The amount that was paid while it ran is still readable; the row was not rewritten to 0.
            Assert.Equal(169m, linked.Amount);
            Assert.Equal(1, await db.Contracts.CountAsync(row => row.FullWorthSpaceId == scenario.Space));

            var bav = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            Assert.Equal(linkedId, bav.RecurringContractId);
        });
    }

    /// <summary>
    /// Deleting the bAV contract must not leave an orphan that nobody maintains — and must not delete
    /// the payment history either. The asset goes (it would double-count wealth that is gone), the fixed
    /// cost is deactivated and marked as no longer maintained.
    /// </summary>
    [Fact]
    public async Task Deleting_the_bav_contract_deactivates_the_fixed_cost_instead_of_deleting_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer B",
            policyNumber = "DV-10",
            implementationRoute = "direct_insurance",
            currency = "EUR"
        });
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2022-05-01",
                cycle = "monthly",
                employeeAmount = 100m
            })).StatusCode);

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/pension/contracts/{contractId}?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var linked = await db.Contracts.AsNoTracking()
                .SingleAsync(row => row.FullWorthSpaceId == scenario.Space);
            Assert.False(linked.IsActive);
            Assert.NotNull(linked.EndDate);
            Assert.Equal(100m, linked.Amount);
            Assert.False(string.IsNullOrWhiteSpace(linked.Notes));

            Assert.Equal(0, await db.BavContracts.CountAsync(row => row.Id == contractId));
        });
    }

    /// <summary>
    /// The same ordering as the rest of the store: a non-member is told nothing (404), a member without
    /// the owner role is refused (403), and an invalid write is a 400 that leaves no fixed cost behind —
    /// a refused contribution must not create the cost side it would have implied.
    /// </summary>
    [Fact]
    public async Task A_refused_contribution_creates_no_fixed_cost()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Provider One",
            policyNumber = "P-1",
            currency = "EUR"
        });

        using var outsider = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/{contractId}/contributions?fullWorthSpaceId={scenario.Space}", scenario.Outside,
            new { validFrom = "2024-01-01", employeeAmount = 169m }));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);

        using var member = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/{contractId}/contributions?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new { validFrom = "2024-01-01", employeeAmount = 169m }));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        using var unknownContract = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/{Guid.NewGuid()}/contributions?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { validFrom = "2024-01-01", employeeAmount = 169m }));
        Assert.Equal(HttpStatusCode.NotFound, unknownContract.StatusCode);

        using var invalid = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/contributions", new
        {
            validFrom = "2024-01-01",
            employeeAmount = 169m,
            // An amount without the source that stated it: the store refuses the whole write.
            taxSavingAmount = 60m
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Contracts.CountAsync(row => row.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavContributions.CountAsync(row => row.BavContractId == contractId));
            var bav = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            Assert.Null(bav.RecurringContractId);
        });
    }
}
