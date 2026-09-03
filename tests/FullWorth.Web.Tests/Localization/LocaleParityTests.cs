using System.Text.Json;

namespace FullWorth.Web.Tests.Localization;

/// <summary>
/// Release-readiness guard for the DE/EN locale files: the two translations must expose exactly the
/// same key tree (no missing or extra keys) and no empty values, so no user-facing string silently
/// falls back or renders blank in one language.
/// </summary>
public sealed class LocaleParityTests
{
    [Fact]
    public void DeAndEnHaveIdenticalKeyTrees()
    {
        var de = KeyPaths("de.json");
        var en = KeyPaths("en.json");

        var missingInEn = de.Except(en).Order().ToList();
        var missingInDe = en.Except(de).Order().ToList();

        Assert.True(missingInEn.Count == 0, "Keys present in de.json but missing from en.json: " + string.Join(", ", missingInEn));
        Assert.True(missingInDe.Count == 0, "Keys present in en.json but missing from de.json: " + string.Join(", ", missingInDe));
    }

    [Theory]
    [InlineData("de.json")]
    [InlineData("en.json")]
    public void LocaleHasNoEmptyValues(string locale)
    {
        var empties = new List<string>();
        Walk(Root(locale), "", (path, value) =>
        {
            if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                empties.Add(path);
        });
        Assert.True(empties.Count == 0, $"{locale} has empty/blank values at: " + string.Join(", ", empties));
    }

    private static SortedSet<string> KeyPaths(string locale)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        Walk(Root(locale), "", (path, value) =>
        {
            if (value.ValueKind != JsonValueKind.Object)
                keys.Add(path);
        });
        return keys;
    }

    private static void Walk(JsonElement element, string prefix, Action<string, JsonElement> onLeaf)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Walk(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", onLeaf);
        }
        else
        {
            onLeaf(prefix, element);
        }
    }

    private static JsonElement Root(string locale)
    {
        var path = Path.Combine(LocalesDirectory(), locale);
        Assert.True(File.Exists(path), $"locale file not found: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string LocalesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot", "locales");
    }
}
