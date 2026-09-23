using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Die erste Seite, die die alte Huelle verlassen hat (#154 Phase B).
///
/// Ein Umzug ist erst dann einer, wenn die Seite auf BEIDEN Seiten stimmt: es gibt sie als eigenes
/// Dokument, und die Huelle hat sie hergegeben. Faellt das zweite aus, antworten zwei Wege auf
/// dieselbe Adresse, und welcher gewinnt, entscheidet die Reihenfolge im Startcode - das ist genau
/// die Sorte Fehler, die man erst sieht, wenn man sie schon eine Weile hat.
/// </summary>
public sealed class TransactionsRazorPageTests(FullWorthWebFactory factory) : IClassFixture<FullWorthWebFactory>
{
    [Fact]
    public void There_is_a_razor_page_for_the_address()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var page = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => string.Equals(endpoint.RoutePattern.RawText?.Trim('/'), "transactions", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null);

        Assert.NotNull(page);
    }

    [Fact]
    public async Task The_address_is_behind_the_login_like_every_app_page()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/transactions");

        Assert.StartsWith("/auth/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_shell_gave_the_page_up()
    {
        var shell = ReadWwwRoot("index.html");

        Assert.DoesNotContain("view-transactions", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("/pages/transactions/page.css", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("/pages/transactions/page.js", shell, StringComparison.Ordinal);

        var app = ReadWwwRoot("app.js");
        Assert.DoesNotContain("pages/transactions/page.js", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_brings_its_own_assets_and_no_others()
    {
        var page = ReadWeb("Pages", "Transactions", "Index.cshtml");
        Assert.Contains("/pages/transactions/page.css", page, StringComparison.Ordinal);
        Assert.Contains("/pages/transactions/entry.js", page, StringComparison.Ordinal);

        var foreign = System.Text.RegularExpressions.Regex
            .Matches(page, @"/pages/([a-z0-9-]+)/")
            .Select(match => match.Groups[1].Value)
            .Where(folder => !string.Equals(folder, "transactions", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(foreign.Length == 0,
            "Die Buchungsseite bindet Dateien anderer Seiten ein: " + string.Join(", ", foreign));
    }

    [Fact]
    public void The_shell_no_longer_claims_the_view_so_its_link_navigates_for_real()
    {
        var routes = ReadWwwRoot("app", "routes.js");
        Assert.Contains("'transactions'", routes, StringComparison.Ordinal);

        // Ohne diesen Filter faenge die alte Huelle den Klick weiter ab und zeigte eine Ansicht, die
        // es in ihrem Dokument gar nicht mehr gibt - eine leere Seite ohne Fehlermeldung.
        var app = ReadWwwRoot("app.js");
        Assert.Contains("filter(view=>!MIGRATED.has(view))", app, StringComparison.Ordinal);
        Assert.Contains("if(MIGRATED.has(view))", app, StringComparison.Ordinal);
    }

    private static string ReadWwwRoot(params string[] parts) =>
        ReadWeb(new[] { "wwwroot" }.Concat(parts).ToArray());

    private static string ReadWeb(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            new[] { dir!.FullName, "src", "FullWorth.Web" }.Concat(parts).ToArray()));
    }
}
