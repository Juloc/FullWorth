using System.Text.Json;
using FullWorth.Backend.Modules.Portfolio;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class JsonPathTests
{
    [Fact]
    public void Reads_a_nested_object_path()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":{"currency":"EUR"}}}""");
        var value = JsonPath.Read(document.RootElement, "chart.result.currency");
        Assert.Equal("EUR", value!.Value.GetString());
    }

    [Fact]
    public void Reads_an_array_index()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":[{"currency":"USD"}]}}""");
        var value = JsonPath.Read(document.RootElement, "chart.result[0].currency");
        Assert.Equal("USD", value!.Value.GetString());
    }

    [Fact]
    public void Missing_segment_returns_null_instead_of_throwing()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":{}}}""");
        Assert.Null(JsonPath.Read(document.RootElement, "chart.result.missing.deeper"));
    }

    [Fact]
    public void Index_out_of_range_returns_null_instead_of_throwing()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":[1,2]}}""");
        Assert.Null(JsonPath.Read(document.RootElement, "chart.result[5]"));
    }

    [Fact]
    public void Traversing_into_a_non_object_returns_null_instead_of_throwing()
    {
        using var document = JsonDocument.Parse("""{"chart":"not-an-object"}""");
        Assert.Null(JsonPath.Read(document.RootElement, "chart.result"));
    }

    [Fact]
    public void Malformed_bracket_returns_null_instead_of_throwing()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":[1,2]}}""");
        Assert.Null(JsonPath.Read(document.RootElement, "chart.result[abc]"));
        Assert.Null(JsonPath.Read(document.RootElement, "chart.result[0"));
    }

    [Fact]
    public void ReadArray_returns_empty_when_path_is_absent_or_not_an_array()
    {
        using var document = JsonDocument.Parse("""{"chart":{"result":{"value":1}}}""");
        Assert.Empty(JsonPath.ReadArray(document.RootElement, "chart.result.missing"));
        Assert.Empty(JsonPath.ReadArray(document.RootElement, "chart.result.value"));
    }

    [Fact]
    public void ReadArray_returns_the_elements_of_a_present_array()
    {
        using var document = JsonDocument.Parse("""{"timestamp":[1,2,3]}""");
        var items = JsonPath.ReadArray(document.RootElement, "timestamp");
        Assert.Equal(3, items.Count);
        Assert.Equal(2, items[1].GetInt32());
    }
}
