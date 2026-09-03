using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Tests.Validation;

public sealed class ValidateTests
{
    [Theory]
    [InlineData("EUR", true)]
    [InlineData("usd", true)]     // case-insensitive
    [InlineData(" gbp ", true)]   // trimmed
    [InlineData("EU", false)]
    [InlineData("EURO", false)]
    [InlineData("E1R", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Currency_ValidatesThreeLetterCodes(string? value, bool valid)
    {
        Assert.Equal(valid, Validate.Currency(value) is null);
        Assert.Equal(valid, Validate.IsCurrency(value));
    }

    [Fact]
    public void RequiredName_UsesFieldLabel()
    {
        Assert.Null(Validate.RequiredName("Groceries", "Budget name"));
        Assert.Equal("Budget name is required.", Validate.RequiredName("  ", "Budget name"));
        Assert.Equal("Name is required.", Validate.RequiredName(null));
    }

    [Fact]
    public void PositiveAndNonNegative()
    {
        Assert.Null(Validate.Positive(1m, "Amount"));
        Assert.Equal("Amount must be greater than zero.", Validate.Positive(0m, "Amount"));
        Assert.Equal("Amount must be greater than zero.", Validate.Positive(-1m, "Amount"));
        Assert.Null(Validate.NonNegative(0m, "Balance"));
        Assert.Equal("Balance must not be negative.", Validate.NonNegative(-0.01m, "Balance"));
    }

    [Fact]
    public void DateOrder_RejectsEndBeforeStart()
    {
        Assert.Null(Validate.DateOrder(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        Assert.Null(Validate.DateOrder(new DateOnly(2026, 1, 1), null));
        Assert.Null(Validate.DateOrder(null, new DateOnly(2026, 1, 1)));
        Assert.NotNull(Validate.DateOrder(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
    }

    [Theory]
    [InlineData(null, 100, 500, 100)]  // unset -> fallback
    [InlineData(50, 100, 500, 50)]     // within range
    [InlineData(9000, 100, 500, 500)]  // clamped to max
    [InlineData(0, 100, 500, 1)]       // clamped to min
    [InlineData(-5, 100, 500, 1)]
    public void PageSize_ClampsIntoRange(int? requested, int fallback, int max, int expected) =>
        Assert.Equal(expected, Validate.PageSize(requested, fallback, max));

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-3, 0)]
    [InlineData(42, 42)]
    public void Offset_IsNonNegative(int? requested, int expected) =>
        Assert.Equal(expected, Validate.Offset(requested));
}
