using FullWorth.Backend.Modules.Portfolio;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Der Gewinn eines ganzen Depots - und was er verschweigen darf: nichts.
///
/// "Wo ist meine Prozentzahl fuer Gesamtgewinn und -verlust?" Die Summe ueber die Positionen ist
/// leicht falsch zu bilden. Ein Bestand aus einem FinTS-Abruf bringt nicht zwingend einen Einstand
/// mit; wuerde seine fehlende Zahl als 0 mitaddiert, stuende im Depot ein Gewinn in voller Hoehe des
/// Kurswerts - aus nichts.
/// </summary>
public sealed class PortfolioGainTests
{
    /// <summary>Alles bekannt: Einstand und Ergebnis sind die Summen, und nichts fehlt.</summary>
    [Fact]
    public void WithEveryCostBasisKnownTheGainIsTheSum()
    {
        var gain = PortfolioValuationService.Gain([Position(10m, 100m, 20m), Position(5m, 50m, -5m)]);

        Assert.Equal(150m, gain.CostBasis);
        Assert.Equal(15m, gain.UnrealizedResult);
        Assert.False(gain.Incomplete);
    }

    /// <summary>
    /// Ohne jeden Einstand ist das Ergebnis UNBEKANNT - nicht null. Eine 0 hier waere die Aussage
    /// "weder Gewinn noch Verlust", und die hat niemand getroffen.
    /// </summary>
    [Fact]
    public void WithNoCostBasisAtAllTheResultIsUnknownAndNotZero()
    {
        var gain = PortfolioValuationService.Gain([Unknown(10m), Unknown(5m)]);

        Assert.Null(gain.CostBasis);
        Assert.Null(gain.UnrealizedResult);
        Assert.True(gain.Incomplete);
    }

    /// <summary>
    /// Teilweise bekannt: die Summe der uebrigen steht da, und dass sie nicht das ganze Depot
    /// beschreibt, steht daneben. Die fehlende Position wird NICHT als 0 mitgezaehlt.
    /// </summary>
    [Fact]
    public void AMissingCostBasisIsNeverAddedAsZero()
    {
        var gain = PortfolioValuationService.Gain([Position(10m, 100m, 20m), Unknown(400m)]);

        Assert.Equal(100m, gain.CostBasis);
        Assert.Equal(20m, gain.UnrealizedResult);
        Assert.True(gain.Incomplete);
    }

    /// <summary>
    /// Eine Position, die sich selbst als unvollstaendig meldet, zaehlt auch dann als Luecke, wenn
    /// zufaellig ein Einstand dabeisteht - sie weiss es besser als die Summe.
    /// </summary>
    [Fact]
    public void APositionThatDeclaresItselfIncompleteMarksTheWholeDepot()
    {
        var gain = PortfolioValuationService.Gain([Position(10m, 100m, 20m) with { CostBasisIncomplete = true }]);

        Assert.True(gain.Incomplete);
    }

    /// <summary>Ein leeres Depot behauptet keinen Gewinn und meldet auch keine Luecke.</summary>
    [Fact]
    public void AnEmptyDepotClaimsNothing()
    {
        var gain = PortfolioValuationService.Gain([]);

        Assert.Null(gain.CostBasis);
        Assert.Null(gain.UnrealizedResult);
        Assert.False(gain.Incomplete);
    }

    /// <summary>Eine verkaufte Position (0 Stueck) ohne Einstand ist keine offene Luecke.</summary>
    [Fact]
    public void APositionWithoutAnyUnitsIsNotAGap()
    {
        var gain = PortfolioValuationService.Gain([Position(10m, 100m, 20m), Unknown(0m)]);

        Assert.False(gain.Incomplete);
    }

    private static PortfolioPositionView Position(decimal quantity, decimal cost, decimal result) =>
        new(Guid.NewGuid(), "ETF", "etf", quantity, cost, 10m, "EUR", null, "current", cost + result, result, false);

    private static PortfolioPositionView Unknown(decimal quantity) =>
        new(Guid.NewGuid(), "ETF ohne Einstand", "etf", quantity, null, 10m, "EUR", null, "current", quantity * 10m, null, true);
}
