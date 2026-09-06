namespace FullWorth.Web.Tests;

public sealed class ContractMobileUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public ContractMobileUxBaselineTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task ContractsSuggestions_AreBatchedAndDoNotShowConfidencePercentages()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.Contains("DETECTED_BATCH_SIZE = 3", js);
        Assert.Contains("data-detected-more", js);
        Assert.DoesNotContain("ctx.get('contracts.confidence')", js);
        Assert.DoesNotContain("data-detect", js);
    }

    [Fact]
    public async Task ContractRows_DoNotDuplicateGlobalCoachAction()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.DoesNotContain("contract-coach", js);
        Assert.Contains("askCoachAboutContract(contract, activity)", js);
    }

    [Fact]
    public async Task ContractsMobileCss_UsesCompactScrollableControls()
    {
        var css = await GetAsync("/app.css");

        Assert.Contains("contracts-toolbar", css);
        Assert.Contains("flex-wrap:nowrap;overflow-x:auto", css);
        Assert.Contains("contracts-more-suggestions", css);
        Assert.Contains("detected-actions [data-accept]", css);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
