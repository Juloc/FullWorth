using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class AutopilotArchitectureGuardTests
{
    [Fact]
    public void Deploy8DefaultsSignalsInsightsActionsAndContractMergeExecutionToOn()
    {
        var configuration = new ConfigurationBuilder().Build();
        var settings = new AutopilotRolloutSettings(configuration);

        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.Signals));
        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.Insights));
        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.Actions));
        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.ContractMergeExecution));
        foreach (var feature in AutopilotFeatures.All.Where(x =>
                     x != AutopilotFeatures.Signals &&
                     x != AutopilotFeatures.Insights &&
                     x != AutopilotFeatures.Actions &&
                     x != AutopilotFeatures.ContractMergeExecution))
            Assert.Equal(AutopilotRolloutState.Off, settings.Get(feature));
    }

    [Fact]
    public void RolloutSupportsIndependentShadowAndOnStates()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Signals}"] = "shadow",
                [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Insights}"] = "on"
            })
            .Build();

        var settings = new AutopilotRolloutSettings(configuration);

        Assert.Equal(AutopilotRolloutState.Shadow, settings.Get(AutopilotFeatures.Signals));
        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.Insights));
        Assert.False(settings.IsOn(AutopilotFeatures.Signals));
        Assert.True(settings.IsShadowOrOn(AutopilotFeatures.Signals));
        Assert.True(settings.IsOn(AutopilotFeatures.Insights));
        Assert.Equal(AutopilotRolloutState.On, settings.Get(AutopilotFeatures.Actions));
    }

    [Fact]
    public void ContractMergeExecutionCanBeDisabledWithoutDisablingInsights()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.ContractMergeExecution}"] = "off"
            })
            .Build();

        var settings = new AutopilotRolloutSettings(configuration);

        Assert.True(settings.IsOn(AutopilotFeatures.Insights));
        Assert.False(settings.IsOn(AutopilotFeatures.ContractMergeExecution));
        Assert.Equal(AutopilotRolloutState.Off, settings.Get(AutopilotFeatures.Actions));
    }

    [Fact]
    public void GenericActionsCanBeDisabledWithoutDisablingInsightsOrContractMerge()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Actions}"] = "off"
            })
            .Build();

        var settings = new AutopilotRolloutSettings(configuration);

        Assert.False(settings.IsOn(AutopilotFeatures.Actions));
        Assert.True(settings.IsOn(AutopilotFeatures.Insights));
        Assert.True(settings.IsOn(AutopilotFeatures.ContractMergeExecution));
    }

    [Fact]
    public void InvalidRolloutStateFailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Signals}"] = "sometimes"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => new AutopilotRolloutSettings(configuration));
        Assert.Contains(AutopilotFeatures.Signals, error.Message);
    }

    [Fact]
    public void DeterministicAutopilotLayersMayNotDependOnAiProviders()
    {
        var forbidden = new[]
        {
            "IIntelligenceProvider",
            "IntelligenceProviderRegistry",
            "IntelligenceStore",
            "AiCredential",
            "AiInstanceSettings",
            "OpenAiIntelligenceProvider",
            "OpenAiCompatibleIntelligenceProvider",
            "CodexBridgeIntelligenceProvider"
        };

        foreach (var file in PlannedLayerFiles("Context", "Signals", "Simulation"))
        {
            var content = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActionProposalHandlersAreFixedAndCannotDispatchArbitraryEndpointsOrProviders()
    {
        var root = Path.Combine(Root(), "src", "FullWorth.Backend", "Modules", "Intelligence", "Actions");
        var models = File.ReadAllText(Path.Combine(root, "ActionProposalModels.cs"));
        var handlers = File.ReadAllText(Path.Combine(root, "ActionProposalHandlers.cs"));
        var service = File.ReadAllText(Path.Combine(root, "ActionProposalService.cs"));

        Assert.Contains("transaction-category-change", models);
        Assert.Contains("categorization-rule-upsert", models);
        Assert.Contains("transfer-link", models);
        Assert.Contains("ActionProposalHandlerNames.All.SetEquals", handlers);

        foreach (var content in new[] { handlers, service })
        {
            Assert.DoesNotContain("IIntelligenceProvider", content, StringComparison.Ordinal);
            Assert.DoesNotContain("IntelligenceProviderRegistry", content, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Activator.CreateInstance", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Type.GetType", content, StringComparison.Ordinal);
            Assert.DoesNotContain("/bff/", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProviderFacingAutopilotLayersMayNotReachFinanceDbDirectly()
    {
        // Explanations and natural-language compilation receive bounded DTOs/services. They may prepare
        // proposals, but direct FullWorthDbContext access would bypass the preview/confirmation boundary.
        foreach (var file in PlannedLayerFiles("Explanations", "Rules"))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("FullWorthDbContext", content, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> PlannedLayerFiles(params string[] layerNames)
    {
        var moduleRoot = Path.Combine(Root(), "src", "FullWorth.Backend", "Modules", "Intelligence");
        foreach (var layer in layerNames)
        {
            var root = Path.Combine(moduleRoot, layer);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
