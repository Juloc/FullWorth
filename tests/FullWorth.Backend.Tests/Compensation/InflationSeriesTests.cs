using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class InflationSeriesTests
{
    [Fact]
    public void Every_series_has_one_value_for_every_month_from_first_to_last()
    {
        var first = DateOnly.ParseExact(InflationSeries.FirstMonth + "-01", "yyyy-MM-dd");
        var last = DateOnly.ParseExact(InflationSeries.LastMonth + "-01", "yyyy-MM-dd");
        var months = (last.Year - first.Year) * 12 + last.Month - first.Month + 1;

        Assert.All(InflationSeries.All, series => Assert.Equal(months, series.Values.Length));
        Assert.Equal(InflationSeries.All.Count, InflationSeries.All.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public void The_headline_series_agrees_with_the_index_the_salary_comparison_uses()
    {
        var headline = InflationSeries.All.Single(s => s.Key == "gesamt");
        for (var month = 1; month <= 7; month++)
        {
            var offset = (2026 - 2022) * 12 + month - 1;
            Assert.Equal(InflationIndex.GetIndex(new DateOnly(2026, month, DateTime.DaysInMonth(2026, month))), headline.Values[offset]);
        }
    }
}
