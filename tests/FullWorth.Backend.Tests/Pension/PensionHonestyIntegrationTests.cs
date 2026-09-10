using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Pension.PensionContractIntegrationTests;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// The rules that keep the pension numbers honest (docs/PENSION.md): beitragsfrei is its own state and
/// not cost-free, the employer/employee split is stored exactly as given, a tax or social-insurance
/// effect cannot exist without the source that stated it, a projection cannot be mistaken for a
/// guarantee, and a policy number never leaves the server in the clear.
/// </summary>
public sealed class PensionHonestyIntegrationTests
{
    /// <summary>
    /// "Beitragsfrei" means: no new contributions, but the contract exists, the balance exists and the
    /// costs keep running. It is not <c>terminated</c>, and it is not free of charge.
    /// </summary>
    [Fact]
    public async Task Beitragsfrei_is_not_cancelled_and_keeps_its_costs()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer A",
            policyNumber = "DV-1",
            implementationRoute = "direct_insurance",
            status = "paid_up",
            currency = "EUR"
        });

        // The contribution arrangement ended; the reason says why, and the contract did not.
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

        // Costs that keep running on the capital of a contract with no contributions.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/costs", new
            {
                effectiveDate = "2024-04-01",
                kind = "administration_on_capital",
                basis = "percent_of_capital",
                percent = 0.35m,
                timing = "ongoing",
                continuesWhenPaidUp = true
            })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/costs", new
            {
                effectiveDate = "2024-04-01",
                kind = "administration_fixed",
                basis = "fixed_amount",
                amount = 12m,
                timing = "ongoing",
                isEstimated = true,
                estimateBasis = "Stückkosten aus der Standmitteilung 2024 hochgerechnet"
            })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots",
            new { effectiveDate = "2026-01-01", balance = 31_450m })).StatusCode);

        using var detail = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts/{contractId}?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var contract = json.RootElement.GetProperty("contract");

        Assert.Equal("paid_up", contract.GetProperty("status").GetString());
        Assert.NotEqual("terminated", contract.GetProperty("status").GetString());
        Assert.True(contract.GetProperty("isPaidUp").GetBoolean());
        // Beitragsfrei still holds capital, so it still counts.
        Assert.True(contract.GetProperty("holdsCapital").GetBoolean());
        Assert.Equal(31_450m, contract.GetProperty("currentSnapshot").GetProperty("balance").GetDecimal());

        var costs = json.RootElement.GetProperty("costs").EnumerateArray().ToArray();
        Assert.Equal(2, costs.Length);
        Assert.All(costs, cost => Assert.True(cost.GetProperty("continuesWhenPaidUp").GetBoolean()));
        var estimated = costs.Single(cost => cost.GetProperty("isEstimated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(estimated.GetProperty("estimateBasis").GetString()));

        // A paid-up contract with costs is not a cost-free one: the overview counts it.
        using var overview = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/overview?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        using var overviewJson = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        Assert.Equal(1, overviewJson.RootElement.GetProperty("paidUpCount").GetInt32());
        Assert.Equal(0, overviewJson.RootElement.GetProperty("activeCount").GetInt32());
        Assert.Equal(1, overviewJson.RootElement.GetProperty("contractsWithEstimatedCosts").GetInt32());
        // No contribution is running any more, so nothing leaves the owner's pay.
        Assert.Equal(0m, overviewJson.RootElement.GetProperty("monthlyEmployeeContribution").GetDecimal());
    }

    /// <summary>
    /// 338 € made of 169 € employee and 169 € employer is not a 338 € outflow. Both shares are stored
    /// as given, the employer share is reported separately, and only the employee share is the money
    /// that leaves the owner's pay.
    /// </summary>
    [Fact]
    public async Task An_employee_and_employer_split_is_stored_exactly_as_given()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer B",
            policyNumber = "DV-2",
            implementationRoute = "direct_insurance",
            currency = "EUR"
        });

        using var response = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
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
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(169m, json.RootElement.GetProperty("employeeAmount").GetDecimal());
        Assert.Equal(25.35m, json.RootElement.GetProperty("employerSubsidyAmount").GetDecimal());
        Assert.Equal(143.65m, json.RootElement.GetProperty("employerAmount").GetDecimal());
        Assert.Equal(338m, json.RootElement.GetProperty("partsTotalAmount").GetDecimal());
        Assert.Equal(169m, json.RootElement.GetProperty("employerTotalAmount").GetDecimal());
        Assert.Equal(338m, json.RootElement.GetProperty("statedTotalAmount").GetDecimal());
        Assert.False(json.RootElement.GetProperty("totalMismatch").GetBoolean());
        // Nothing was invented, so there is no tax effect on this row.
        Assert.True(json.RootElement.GetProperty("taxEffectSource").ValueKind == JsonValueKind.Null);

        await factory.SeedAsync(async db =>
        {
            var stored = await db.BavContributions.AsNoTracking().SingleAsync(row => row.BavContractId == contractId);
            Assert.Equal(169m, stored.EmployeeAmount);
            Assert.Equal(25.35m, stored.EmployerSubsidyAmount);
            Assert.Equal(143.65m, stored.EmployerAmount);
            Assert.Null(stored.TaxEffectSource);
        });

        using var overview = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/overview?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        using var overviewJson = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        Assert.Equal(169m, overviewJson.RootElement.GetProperty("monthlyEmployeeContribution").GetDecimal());
        Assert.Equal(169m, overviewJson.RootElement.GetProperty("monthlyEmployerContribution").GetDecimal());
    }

    /// <summary>
    /// A total that disagrees with the shares is reported, not corrected: the document said what it
    /// said, and quietly rewriting either side would hide a reading error.
    /// </summary>
    [Fact]
    public async Task A_stated_total_that_disagrees_with_the_shares_is_reported_not_corrected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer C",
            policyNumber = "DV-3",
            currency = "EUR"
        });

        using var response = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2024-01-01",
                cycle = "monthly",
                employeeAmount = 100m,
                employerAmount = 15m,
                statedTotalAmount = 120m,
                source = "document"
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(115m, json.RootElement.GetProperty("partsTotalAmount").GetDecimal());
        Assert.Equal(120m, json.RootElement.GetProperty("statedTotalAmount").GetDecimal());
        Assert.True(json.RootElement.GetProperty("totalMismatch").GetBoolean());
    }

    /// <summary>
    /// FullWorth does not compute a personal tax or social-insurance effect. One may only be stored
    /// with the source that stated it, and its own arithmetic is stored — and reported — as an explicit
    /// simulation.
    /// </summary>
    [Fact]
    public async Task A_tax_effect_cannot_be_stored_without_a_stated_source()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer D",
            policyNumber = "DV-4",
            currency = "EUR"
        });

        using var bare = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2024-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                taxSavingAmount = 61m,
                socialSecuritySavingAmount = 34m,
                netEffortAmount = 74m
            });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("source", await bare.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var unknown = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2024-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                taxSavingAmount = 61m,
                taxEffectSource = "computed"
            });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.BavContributions.CountAsync(row => row.BavContractId == contractId)));

        // A payslip stated it, so it is a fact and is reported as one.
        using var stated = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2024-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                taxSavingAmount = 61m,
                socialSecuritySavingAmount = 34m,
                netEffortAmount = 74m,
                taxEffectSource = "payslip",
                taxEffectSourceReference = "Abrechnung 01/2024",
                source = "payslip"
            });
        Assert.Equal(HttpStatusCode.OK, stated.StatusCode);
        using var statedJson = JsonDocument.Parse(await stated.Content.ReadAsStringAsync());
        Assert.True(statedJson.RootElement.GetProperty("taxEffectIsStated").GetBoolean());
        Assert.False(statedJson.RootElement.GetProperty("taxEffectIsSimulation").GetBoolean());

        // FullWorth's own arithmetic is storable, but it is labelled a simulation and never a fact.
        using var simulated = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/contributions", new
            {
                validFrom = "2025-01-01",
                cycle = "monthly",
                employeeAmount = 169m,
                netEffortAmount = 80m,
                taxEffectSource = "simulation",
                taxEffectSourceReference = "Steuerklasse 1, 60.000 € Brutto"
            });
        Assert.Equal(HttpStatusCode.OK, simulated.StatusCode);
        using var simulatedJson = JsonDocument.Parse(await simulated.Content.ReadAsStringAsync());
        Assert.False(simulatedJson.RootElement.GetProperty("taxEffectIsStated").GetBoolean());
        Assert.True(simulatedJson.RootElement.GetProperty("taxEffectIsSimulation").GetBoolean());
    }

    /// <summary>
    /// A projected figure without its basis and its return assumption would look exactly like a
    /// guarantee, so it is refused. A projection that is stored never becomes the balance and never
    /// reaches the asset that carries the value into net worth.
    /// </summary>
    [Fact]
    public async Task A_projection_needs_its_assumption_and_never_becomes_wealth()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer E",
            policyNumber = "DV-5",
            currency = "EUR"
        });

        using var bare = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots", new
            {
                effectiveDate = "2026-01-01",
                balance = 10_000m,
                projectedCapitalAtRetirement = 180_000m
            });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);

        using var full = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots", new
            {
                effectiveDate = "2026-01-01",
                balance = 10_000m,
                guaranteedCapitalAtRetirement = 90_000m,
                projectedCapitalAtRetirement = 180_000m,
                projectedMonthlyAnnuity = 640m,
                projectionReturnPercent = 5m,
                projectionBasis = "document_forecast",
                source = "document"
            });
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        using var json = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
        Assert.Equal(10_000m, json.RootElement.GetProperty("balance").GetDecimal());
        Assert.Equal(180_000m, json.RootElement.GetProperty("projectedCapitalAtRetirement").GetDecimal());
        Assert.Equal("document_forecast", json.RootElement.GetProperty("projectionBasis").GetString());
        Assert.False(json.RootElement.GetProperty("projectionIsSimulation").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var contract = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            var asset = await db.Assets.AsNoTracking().SingleAsync(row => row.Id == contract.AssetId!.Value);
            // Only the balance ever becomes wealth.
            Assert.Equal(10_000m, asset.CurrentValue);
        });
    }

    /// <summary>
    /// A snapshot in a currency other than the contract's would silently change what every stored
    /// figure means, because a snapshot keeps its own currency and is never converted in place.
    /// </summary>
    [Fact]
    public async Task A_snapshot_cannot_change_the_contract_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Swiss Provider",
            policyNumber = "CH-1",
            currency = "CHF"
        });

        using var wrong = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots",
            new { effectiveDate = "2026-01-01", balance = 1_000m, currency = "EUR" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var body = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("CHF", body, StringComparison.Ordinal);
        Assert.Contains("EUR", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CHF balance with no rate in the table must not be added to a EUR total at face value. The
    /// total says it is incomplete, names the currency, and reports the untouched original separately.
    /// </summary>
    [Fact]
    public async Task A_balance_without_an_fx_rate_makes_the_total_incomplete_rather_than_wrong()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var euro = await CreateContractAsync(client, scenario, new
        {
            providerName = "Euro Provider",
            policyNumber = "EU-1",
            currency = "EUR"
        });
        var swiss = await CreateContractAsync(client, scenario, new
        {
            providerName = "Swiss Provider",
            policyNumber = "CH-2",
            currency = "CHF"
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{euro}/snapshots",
            new { effectiveDate = "2026-01-01", balance = 10_000m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{swiss}/snapshots",
            new { effectiveDate = "2026-01-01", balance = 5_000m })).StatusCode);

        using var overview = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/overview?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        using var json = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());

        Assert.Equal("EUR", json.RootElement.GetProperty("currency").GetString());
        Assert.Equal(10_000m, json.RootElement.GetProperty("totalBalance").GetDecimal());
        Assert.False(json.RootElement.GetProperty("isComplete").GetBoolean());
        Assert.Contains("CHF", json.RootElement.GetProperty("missingCurrencies").EnumerateArray().Select(x => x.GetString()));
        var unconverted = json.RootElement.GetProperty("unconvertedBalances").EnumerateArray().Single();
        Assert.Equal("CHF", unconverted.GetProperty("currency").GetString());
        Assert.Equal(5_000m, unconverted.GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// A policy number identifies a person to their provider. It is encrypted at rest, matched through
    /// a keyed blind index, and the API only ever returns its last four characters.
    /// </summary>
    [Fact]
    public async Task A_policy_number_is_never_stored_or_returned_in_the_clear()
    {
        const string key = "gk9k8ZTQ0Q4t5NfC7bJ2vX1sR6yH3mA9pL0wE4uI7oQ=";
        using var factory = new BackendWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DataEncryptionKey"] = key
        });
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        const string policyNumber = "BAV-2018-4711-XYZ";
        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Encrypted Provider",
            policyNumber,
            currency = "EUR"
        });

        using var list = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(policyNumber, listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BAV20184711XYZ", listBody, StringComparison.OrdinalIgnoreCase);
        using var listJson = JsonDocument.Parse(listBody);
        var row = listJson.RootElement.EnumerateArray().Single();
        Assert.True(row.GetProperty("hasPolicyNumber").GetBoolean());
        // "BAV-2018-4711-XYZ" normalises to "BAV20184711XYZ", so the last four are "1XYZ".
        Assert.Equal("1XYZ", row.GetProperty("policyNumberLast4").GetString());

        await factory.SeedAsync(async db =>
        {
            var stored = await db.BavContracts.AsNoTracking().SingleAsync(x => x.Id == contractId);
            Assert.NotNull(stored.PolicyNumberEncrypted);
            Assert.StartsWith("v1:", stored.PolicyNumberEncrypted);
            Assert.DoesNotContain("4711", stored.PolicyNumberEncrypted!, StringComparison.Ordinal);
            Assert.NotNull(stored.PolicyNumberLookup);
            Assert.DoesNotContain("4711", stored.PolicyNumberLookup!, StringComparison.Ordinal);
        });

        // The blind index still identifies the contract, so detection works on encrypted data.
        using var match = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/match?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { policyNumber = "bav 2018 4711 xyz", providerName = "Encrypted Provider" }));
        using var matchJson = JsonDocument.Parse(await match.Content.ReadAsStringAsync());
        Assert.True(matchJson.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(contractId, matchJson.RootElement.GetProperty("contractId").GetGuid());
    }

    /// <summary>
    /// An estimated cost has to say what the estimate is based on, so a display can never show it as a
    /// contract value; and a cost figure has to be a figure of something.
    /// </summary>
    [Fact]
    public async Task An_estimated_cost_must_name_its_basis_and_a_cost_needs_a_figure()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Cost Provider",
            policyNumber = "CP-1",
            currency = "EUR"
        });

        using var bareEstimate = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/costs", new
            {
                effectiveDate = "2026-01-01",
                kind = "acquisition",
                basis = "percent_of_sum",
                percent = 2.5m,
                isEstimated = true
            });
        Assert.Equal(HttpStatusCode.BadRequest, bareEstimate.StatusCode);

        using var percentWithAmount = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/costs", new
            {
                effectiveDate = "2026-01-01",
                kind = "acquisition",
                basis = "percent_of_sum",
                amount = 400m
            });
        Assert.Equal(HttpStatusCode.BadRequest, percentWithAmount.StatusCode);

        using var fixedWithoutAmount = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/costs", new
            {
                effectiveDate = "2026-01-01",
                kind = "administration_fixed",
                basis = "fixed_amount"
            });
        Assert.Equal(HttpStatusCode.BadRequest, fixedWithoutAmount.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.BavCosts.CountAsync(row => row.BavContractId == contractId)));
    }

    /// <summary>
    /// Funds carry ISIN, weighting, cost and asset class, and a malformed ISIN is refused rather than
    /// stored as an identifier that identifies nothing.
    /// </summary>
    [Fact]
    public async Task A_fund_allocation_carries_isin_weight_cost_and_class()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Fund Provider",
            policyNumber = "FP-1",
            currency = "EUR"
        });

        using var ok = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/allocations", new
            {
                effectiveDate = "2026-01-01",
                fundName = "Global Equity Index",
                isin = "ie00b4l5y983",
                weightPercent = 70m,
                amount = 7_000m,
                ongoingChargesPercent = 0.2m,
                assetClass = "equity",
                source = "document"
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var json = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal("IE00B4L5Y983", json.RootElement.GetProperty("isin").GetString());
        Assert.Equal(70m, json.RootElement.GetProperty("weightPercent").GetDecimal());
        Assert.Equal(0.2m, json.RootElement.GetProperty("ongoingChargesPercent").GetDecimal());
        Assert.Equal("equity", json.RootElement.GetProperty("assetClass").GetString());
        Assert.False(json.RootElement.GetProperty("ongoingChargesEstimated").GetBoolean());

        using var badIsin = await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/allocations", new
            {
                effectiveDate = "2026-01-01",
                fundName = "Broken",
                isin = "12345",
                assetClass = "equity"
            });
        Assert.Equal(HttpStatusCode.BadRequest, badIsin.StatusCode);
    }

    /// <summary>
    /// A Unterstützungskasse is its own contract with its own route, even when the same employer's
    /// Direktversicherung is reported in the same document. Neither is folded into the other.
    /// </summary>
    [Fact]
    public async Task Two_routes_at_the_same_employer_stay_two_contracts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var direct = await CreateContractAsync(client, scenario, new
        {
            providerName = "Konzern Versorgung",
            policyNumber = "DV-100",
            implementationRoute = "direct_insurance",
            employerName = "Muster GmbH",
            currency = "EUR"
        });
        var provident = await CreateContractAsync(client, scenario, new
        {
            providerName = "Konzern Versorgung",
            policyNumber = "UK-100",
            implementationRoute = "provident_fund",
            employerName = "Muster GmbH",
            currency = "EUR"
        });

        Assert.NotEqual(direct, provident);
        await factory.SeedAsync(async db =>
        {
            var routes = await db.BavContracts.AsNoTracking()
                .Where(row => row.FullWorthSpaceId == scenario.Space)
                .Select(row => row.ImplementationRoute)
                .ToListAsync();
            Assert.Equal(2, routes.Count);
            Assert.Contains(BavImplementationRoutes.DirectInsurance, routes);
            Assert.Contains(BavImplementationRoutes.ProvidentFund, routes);
        });
    }
}
