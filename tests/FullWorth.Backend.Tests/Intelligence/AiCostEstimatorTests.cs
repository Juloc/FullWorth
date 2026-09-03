using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class AiCostEstimatorTests
{
    [Fact]
    public void Returns_configured_model_capability_estimate()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Intelligence:CostEstimates:openai:gpt-test:text-classification:EstimatedCallCostEur"] = "0.0125"
            })
            .Build();

        var estimator = new AiCostEstimator(configuration);

        Assert.Equal(0.0125m, estimator.GetEstimatedCallCostEur("openai", "gpt-test", "text-classification"));
        Assert.Null(estimator.GetEstimatedCallCostEur("openai", "other-model", "text-classification"));
    }

    [Fact]
    public void Rejects_invalid_or_negative_estimate()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Intelligence:CostEstimates:openai:gpt-test:text-classification:EstimatedCallCostEur"] = "-1"
            })
            .Build();

        var estimator = new AiCostEstimator(configuration);

        Assert.Throws<InvalidOperationException>(() =>
            estimator.GetEstimatedCallCostEur("openai", "gpt-test", "text-classification"));
    }
}
