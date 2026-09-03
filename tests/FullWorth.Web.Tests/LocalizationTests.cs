using System.Text.Json;

namespace FullWorth.Web.Tests;

public sealed class LocalizationTests : IClassFixture<FullWorthWebFactory>
{
    private static readonly string[] NavigationKeys =
    [
        "dashboard", "transactions", "purchases", "contracts", "budgets",
        "portfolio", "analytics", "rules", "accounts"
    ];

    private readonly HttpClient _client;

    public LocalizationTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task Locale_Parses_AndContainsRequiredNavigationAndPageKeys(string locale)
    {
        using var json = await LoadAsync(locale);

        var root = json.RootElement;
        var nav = root.GetProperty("nav");
        var pages = root.GetProperty("pages");
        var theme = root.GetProperty("theme");

        foreach (var key in NavigationKeys)
        {
            Assert.True(nav.TryGetProperty(key, out var navValue), $"Missing nav.{key} in {locale}.json");
            Assert.False(string.IsNullOrWhiteSpace(navValue.GetString()), $"Empty nav.{key} in {locale}.json");
            Assert.True(pages.TryGetProperty(key, out var page), $"Missing pages.{key} in {locale}.json");
            Assert.False(string.IsNullOrWhiteSpace(page.GetProperty("title").GetString()), $"Empty pages.{key}.title in {locale}.json");
            Assert.False(string.IsNullOrWhiteSpace(page.GetProperty("subtitle").GetString()), $"Empty pages.{key}.subtitle in {locale}.json");
        }

        foreach (var key in new[] { "system", "light", "dark" })
        {
            Assert.True(theme.TryGetProperty(key, out var value), $"Missing theme.{key} in {locale}.json");
            Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"Empty theme.{key} in {locale}.json");
        }
    }

    [Fact]
    public async Task GermanAndEnglish_KeyStructures_Match()
    {
        using var de = await LoadAsync("de");
        using var en = await LoadAsync("en");

        var deKeys = FlattenKeys(de.RootElement);
        var enKeys = FlattenKeys(en.RootElement);

        Assert.Empty(deKeys.Except(enKeys));
        Assert.Empty(enKeys.Except(deKeys));
    }

    private async Task<JsonDocument> LoadAsync(string locale)
    {
        using var response = await _client.GetAsync($"/locales/{locale}.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static HashSet<string> FlattenKeys(JsonElement root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        AddKeys(root, "", keys);
        return keys;
    }

    private static void AddKeys(JsonElement element, string prefix, HashSet<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            keys.Add(path);
            AddKeys(property.Value, path, keys);
        }
    }
}
