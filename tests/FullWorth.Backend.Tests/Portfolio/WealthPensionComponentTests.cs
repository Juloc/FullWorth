using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// The pension block of the wealth overview (docs/PENSION.md step 3). It is the same construction as
/// <c>realEstateAssets</c>: a SUBSET of <c>manualAssets</c>, converted with the same FX snapshot and
/// the same day, never a second total. Creating a bAV contract creates exactly one asset of kind
/// <c>insurance_pension</c>, so that kind is the whole contract between the two modules and these
/// tests seed the asset directly.
///
/// What is being protected here is the no-double-counting rule: the moment the slice is computed from
/// its own query, its own rates or its own day, the page can show a tied figure larger than the total
/// it is a part of - or count the balance twice.
/// </summary>
public sealed class WealthPensionComponentTests
{
    [Fact]
    public async Task PensionAssetsAreASubsetOfManualAssetsAndAreCountedOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, pension: 8_000m, pensionCurrency: "EUR");
        using var client = factory.CreateClient();

        var root = await OverviewAsync(client, scenario);

        Assert.Equal(10_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
        Assert.Equal(8_000m, root.GetProperty("pensionAssets").GetProperty("amount").GetDecimal());

        // The slice is inside the total, so net worth is the total once - not the total plus the slice.
        Assert.True(
            root.GetProperty("pensionAssets").GetProperty("amount").GetDecimal()
                <= root.GetProperty("manualAssets").GetProperty("amount").GetDecimal(),
            "the pension slice may never exceed the manual-asset total it is a subset of");
        Assert.Equal(10_000m, root.GetProperty("totalAssets").GetDecimal());
        Assert.Equal(10_000m, root.GetProperty("netWorth").GetDecimal());
        Assert.NotEqual(18_000m, root.GetProperty("netWorth").GetDecimal());
        Assert.True(root.GetProperty("isComplete").GetBoolean());
    }

    /// <summary>
    /// A pension balance in a currency with no rate must make the overview say so. Leaving it out of
    /// the total while still calling the total complete would report a smaller net worth as if it were
    /// a certain one.
    /// </summary>
    [Fact]
    public async Task APensionBalanceWithoutARateMakesTheOverviewIncompleteRatherThanShort()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, pension: 8_000m, pensionCurrency: "USD");
        using var client = factory.CreateClient();

        var root = await OverviewAsync(client, scenario);

        Assert.False(root.GetProperty("isComplete").GetBoolean());
        Assert.Contains("USD", root.GetProperty("missingCurrencies").EnumerateArray().Select(item => item.GetString()));

        // Not 8 000 (a 1:1 guess) and not part of the total, but still named in its own currency.
        var pension = root.GetProperty("pensionAssets");
        Assert.Equal(0m, pension.GetProperty("amount").GetDecimal());
        Assert.False(pension.GetProperty("isComplete").GetBoolean());
        Assert.Contains(pension.GetProperty("originalAmounts").EnumerateArray(), item =>
            item.GetProperty("currency").GetString() == "USD" && item.GetProperty("amount").GetDecimal() == 8_000m);
        Assert.Equal(2_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ASpaceWithoutAPensionContractReportsAnEmptyComponentInsteadOfFailing()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, pension: null, pensionCurrency: "EUR");
        using var client = factory.CreateClient();

        var root = await OverviewAsync(client, scenario);

        var pension = root.GetProperty("pensionAssets");
        Assert.True(pension.ValueKind is JsonValueKind.Null or JsonValueKind.Object);
        if (pension.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(0m, pension.GetProperty("amount").GetDecimal());
            Assert.Empty(pension.GetProperty("originalAmounts").EnumerateArray());
            Assert.True(pension.GetProperty("isComplete").GetBoolean());
        }
        Assert.Equal(2_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
        Assert.True(root.GetProperty("isComplete").GetBoolean());
    }

    /// <summary>
    /// An asset the user excluded from net worth ("nur separat anzeigen") is not in the total, so it
    /// must not be in the slice either - otherwise "davon gebunden" would be a share of a total that
    /// does not contain it.
    /// </summary>
    [Fact]
    public async Task APensionAssetExcludedFromNetWorthIsInNeitherTheTotalNorTheSlice()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, pension: 8_000m, pensionCurrency: "EUR", pensionInNetWorth: false);
        using var client = factory.CreateClient();

        var root = await OverviewAsync(client, scenario);

        Assert.Equal(2_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
        Assert.Equal(0m, root.GetProperty("pensionAssets").GetProperty("amount").GetDecimal());
    }

    private static async Task<JsonElement> OverviewAsync(HttpClient client, Scenario scenario)
    {
        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/overview?fullWorthSpaceId={scenario.Space}&currency=EUR", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The document has to outlive this helper, so the element is cloned out of it.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory,
        decimal? pension,
        string pensionCurrency,
        bool pensionInNetWorth = true)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"PENSION-WEALTH-{scenario.Owner:N}@EXAMPLE.COM",
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Pension wealth test",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });

            db.Assets.Add(new Asset
            {
                FullWorthSpaceId = scenario.Space,
                Name = "Manual property value",
                Kind = AssetKinds.RealEstate,
                CurrentValue = 2_000m,
                Currency = "EUR",
                ValuedAt = new DateOnly(2026, 8, 1),
                IncludeInNetWorth = true
            });
            if (pension.HasValue)
            {
                db.Assets.Add(new Asset
                {
                    FullWorthSpaceId = scenario.Space,
                    Name = "bAV Direktversicherung",
                    Kind = AssetKinds.InsurancePension,
                    CurrentValue = pension.Value,
                    Currency = pensionCurrency,
                    ValuedAt = new DateOnly(2026, 8, 1),
                    IncludeInNetWorth = pensionInNetWorth
                });
            }

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid Owner, Guid Space);
}
