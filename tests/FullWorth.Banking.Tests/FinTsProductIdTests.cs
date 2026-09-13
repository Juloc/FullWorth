using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// The FinTS product id has one runtime source of truth: FinTs:ProductId from the instance
/// configuration published by the admin settings surface.
/// </summary>
public sealed class FinTsProductIdTests
{
    [Fact]
    public void A_configured_id_is_used()
        => Assert.Equal("FROM-CONFIG", IngFinTsService.ResolveProductId("FROM-CONFIG"));

    [Fact]
    public void Surrounding_whitespace_never_reaches_the_bank()
        => Assert.Equal("TRIMMED", IngFinTsService.ResolveProductId("  TRIMMED  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_product_id_is_reported_as_missing(string? configured)
        => Assert.Null(IngFinTsService.ResolveProductId(configured));
}
