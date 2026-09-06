namespace FullWorth.Web.Tests;

public sealed class ContractCancellationUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public ContractCancellationUiBaselineTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task ContractsUi_DistinguishesCancellationFromArchiving()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.Contains("api/contract-parity/cancellations", js);
        Assert.Contains("data-cancellation", js);
        Assert.Contains("openCancellationDialog", js);
        Assert.Contains("cancelEffectiveDate", js);
        Assert.Contains("cancellationSentAt", js);
        Assert.Contains("cancellationConfirmedAt", js);
        Assert.Contains("data-archive", js);
        Assert.DoesNotContain("data-cancel-contract", js);
    }

    [Fact]
    public async Task ContractsLocales_ExposeLifecycleLabels()
    {
        foreach (var path in new[] { "/locales/de.json", "/locales/en.json" })
        {
            var json = await GetAsync(path);
            Assert.Contains("\"status_cancelled\"", json);
            Assert.Contains("\"manageCancellation\"", json);
            Assert.Contains("\"cancelEffectiveDate\"", json);
            Assert.Contains("\"cancellationDeadline\"", json);
            Assert.Contains("\"cancelledOn\"", json);
            Assert.Contains("\"confirmedOn\"", json);
        }
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
