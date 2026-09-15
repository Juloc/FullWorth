using FullWorth.Backend.Modules.Reconciliation;
using System.Text.Json;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics;

public sealed record SavedAnalysisWrite(string Name, AnalysisQueryWrite Query, string ChartType = "bar", int SchemaVersion = 1);

/// <summary>
/// Die freie Auswertung: eine Kennzahl, ueber eine Dimension gruppiert - und die Auswertungen, die
/// sich jemand gemerkt hat.
///
/// Hier steht, was die Antwort ist: welche Kennzahlen und Dimensionen es gibt, wie ein Betrag seinem
/// Schluessel zugeordnet wird, und wie aus den Betraegen eine Zahl wird. Woher die Betraege kommen,
/// entscheidet <see cref="AnalysisContributionService"/>.
/// </summary>
public static class AnalysisQueryEndpoints
{
    private static readonly string[] Measures = ["spend", "income", "net", "count", "average", "median"];
    private static readonly string[] Dimensions =
        ["day", "week", "month", "quarter", "year", "category", "merchant", "account", "tag", "contract"];

    public static IEndpointRouteBuilder MapAnalysisQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analytics/query", Query).WithTags("Analytics");
        app.MapPost("/api/analytics/sankey", Sankey).WithTags("Analytics");
        var saved = app.MapGroup("/api/saved-analyses").WithTags("Analytics");
        saved.MapGet("/", ListSaved);
        saved.MapPost("/", CreateSaved);
        saved.MapPut("/{id:guid}", UpdateSaved);
        saved.MapDelete("/{id:guid}", DeleteSaved);
        return app;
    }

    private static async Task<IResult> Query(
        Guid fullWorthSpaceId, AnalysisQueryWrite request, CurrentUserContext currentUser, SpaceAccess space,
        AnalysisContributionService contributions, AnalysisContributionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var validation = ValidateQuery(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var loaded = await contributions.LoadAsync(userId, fullWorthSpaceId, visible, request, ct);

        var categories = await store.CategoryNamesAsync(fullWorthSpaceId, ct);
        var accounts = await store.AccountNamesAsync(fullWorthSpaceId, ct);
        var tags = await store.TagNamesAsync(fullWorthSpaceId, ct);
        var contracts = await store.ContractNamesAsync(fullWorthSpaceId, ct);

        var buckets = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in loaded.Items)
            foreach (var key in DimensionKeys(contribution, request.Dimension, categories, accounts, tags, contracts))
            {
                if (!buckets.TryGetValue(key, out var values)) buckets[key] = values = [];
                values.Add(contribution.BaseAmount);
            }

        var series = buckets
            .Select(bucket => new { key = bucket.Key, value = Measure(bucket.Value, request.Measure), count = bucket.Value.Count })
            .OrderBy(row => row.key, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new
        {
            currency = loaded.BaseCurrency,
            incomplete = loaded.Incomplete,
            measure = request.Measure,
            dimension = request.Dimension,
            from = loaded.From,
            to = loaded.To,
            series,
            total = Measure(loaded.Items.Select(item => item.BaseAmount).ToList(), request.Measure)
        });
    }

    /// <summary>
    /// Einnahmen fliessen in "verfuegbar", von dort in die obersten Kategorien und der Rest bleibt
    /// stehen. Gruppiert wird bewusst auf der Wurzel: ein Flussdiagramm mit sechzig Unterkategorien
    /// zeigt nichts.
    /// </summary>
    private static async Task<IResult> Sankey(
        Guid fullWorthSpaceId, AnalysisQueryWrite request, CurrentUserContext currentUser, SpaceAccess space,
        AnalysisContributionService contributions, AnalysisContributionStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        request = request with { Measure = "net", Dimension = "category" };
        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var loaded = await contributions.LoadAsync(userId, fullWorthSpaceId, visible, request, ct);

        var byId = (await store.CategoryParentsAsync(fullWorthSpaceId, ct))
            .ToDictionary(category => category.Id);

        string RootName(Guid? id)
        {
            if (!id.HasValue || !byId.TryGetValue(id.Value, out var category)) return "Uncategorized";
            // Der Waechter faengt einen Kreis ab, statt ewig zu laufen.
            var guard = new HashSet<Guid>();
            while (category.ParentId.HasValue && byId.TryGetValue(category.ParentId.Value, out var parent)
                   && guard.Add(category.Id))
                category = parent;
            return category.Name;
        }

        var income = loaded.Items.Where(item => item.BaseAmount > 0).Sum(item => item.BaseAmount);
        var expenses = loaded.Items.Where(item => item.BaseAmount < 0)
            .GroupBy(item => RootName(item.CategoryId))
            .Select(group => new { name = group.Key, value = -group.Sum(item => item.BaseAmount) })
            .Where(row => row.value > 0)
            .OrderByDescending(row => row.value)
            .ToArray();

        var remaining = Math.Max(0, income - expenses.Sum(row => row.value));
        var nodes = new List<object> { new { id = "income", name = "Income" }, new { id = "available", name = "Available income" } };
        nodes.AddRange(expenses.Select((row, index) => (object)new { id = $"cat-{index}", name = row.name }));
        if (remaining > 0) nodes.Add(new { id = "remaining", name = "Remaining" });

        var links = new List<object> { new { source = "income", target = "available", value = income } };
        for (var index = 0; index < expenses.Length; index++)
            links.Add(new { source = "available", target = $"cat-{index}", value = expenses[index].value });
        if (remaining > 0) links.Add(new { source = "available", target = "remaining", value = remaining });

        return Results.Ok(new { currency = loaded.BaseCurrency, incomplete = loaded.Incomplete, nodes, links });
    }

    private static async Task<IResult> ListSaved(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        SavedAnalysisStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = await store.ListAsync(userId, fullWorthSpaceId, ct);
        return Results.Ok(rows.Select(row => new
        {
            id = row.Id,
            name = row.Name,
            schemaVersion = row.SchemaVersion,
            config = JsonSerializer.Deserialize<JsonElement>(row.ConfigJson),
            createdAt = row.CreatedAt,
            updatedAt = row.UpdatedAt
        }));
    }

    private static async Task<IResult> CreateSaved(
        Guid fullWorthSpaceId, SavedAnalysisWrite request, CurrentUserContext currentUser, SpaceAccess space,
        SavedAnalysisStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Name) || ValidateQuery(request.Query) is not null)
            return Results.BadRequest(new { error = "Invalid saved analysis." });

        return Results.Ok(new { id = await store.CreateAsync(userId, fullWorthSpaceId, request, ct) });
    }

    private static async Task<IResult> UpdateSaved(
        Guid id, Guid fullWorthSpaceId, SavedAnalysisWrite request, CurrentUserContext currentUser,
        SavedAnalysisStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || ValidateQuery(request.Query) is not null)
            return Results.BadRequest();

        return await store.UpdateAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteSaved(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        SavedAnalysisStore store, CancellationToken ct) =>
        await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();

    private static IEnumerable<string> DimensionKeys(
        StatisticalContribution contribution, string dimension,
        IReadOnlyDictionary<Guid, string> categories, IReadOnlyDictionary<Guid, string> accounts,
        IReadOnlyDictionary<Guid, string> tags, IReadOnlyDictionary<Guid, string> contracts)
    {
        var date = contribution.Date;
        switch (dimension.ToLowerInvariant())
        {
            case "day":
                yield return date.ToString("yyyy-MM-dd");
                yield break;
            case "week":
                // Montag als Wochenanfang; DayOfWeek zaehlt ab Sonntag, daher der Versatz.
                yield return date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");
                yield break;
            case "quarter":
                yield return $"{date.Year}-Q{((date.Month - 1) / 3) + 1}";
                yield break;
            case "year":
                yield return date.Year.ToString();
                yield break;
            case "category":
                yield return contribution.CategoryId.HasValue
                             && categories.TryGetValue(contribution.CategoryId.Value, out var name)
                    ? name
                    : "Uncategorized";
                yield break;
            case "merchant":
                yield return contribution.Merchant;
                yield break;
            case "account":
                yield return accounts.GetValueOrDefault(contribution.AccountId, "Unknown account");
                yield break;
            case "tag":
                // Ein Betrag mit drei Etiketten zaehlt unter allen dreien - die Summe der Reihen ist
                // darum groesser als das Gesamtergebnis, und das ist gewollt.
                if (contribution.TagIds.Count == 0) { yield return "Untagged"; yield break; }
                foreach (var id in contribution.TagIds) yield return tags.GetValueOrDefault(id, "Unknown tag");
                yield break;
            case "contract":
                if (contribution.ContractIds.Count == 0) { yield return "No contract"; yield break; }
                foreach (var id in contribution.ContractIds) yield return contracts.GetValueOrDefault(id, "Unknown contract");
                yield break;
            default:
                yield return $"{date.Year}-{date.Month:00}";
                yield break;
        }
    }

    private static decimal Measure(IReadOnlyList<decimal> values, string measure)
    {
        if (values.Count == 0) return 0;
        return measure.ToLowerInvariant() switch
        {
            "income" => Math.Round(values.Where(value => value > 0).Sum(), 2),
            "net" => Math.Round(values.Sum(), 2),
            "count" => values.Count,
            "average" => Math.Round(values.Select(Math.Abs).Average(), 2),
            "median" => Median(values.Select(Math.Abs)),
            // "spend" und alles Unbekannte: Ausgaben als positive Zahl.
            _ => Math.Round(values.Where(value => value < 0).Sum(value => -value), 2)
        };
    }

    private static decimal Median(IEnumerable<decimal> source)
    {
        var sorted = source.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : Math.Round((sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2m, 2);
    }

    private static string? ValidateQuery(AnalysisQueryWrite query)
    {
        if (!Measures.Contains(query.Measure, StringComparer.OrdinalIgnoreCase)) return "Unsupported measure.";
        if (!Dimensions.Contains(query.Dimension, StringComparer.OrdinalIgnoreCase)) return "Unsupported dimension.";
        if (query.From.HasValue && query.To.HasValue && query.From > query.To) return "Invalid date range.";
        return null;
    }
}
