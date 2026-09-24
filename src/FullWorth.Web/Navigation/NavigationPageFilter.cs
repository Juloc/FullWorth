using FullWorth.Web.Modules.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FullWorth.Web.Navigation;

/// <summary>
/// Setzt fuer jede Razor-Seite, ob diese Sitzung Adminrechte hat (#154).
///
/// Die Navigation braucht es, und sie braucht es VOR dem ersten Zeichnen: die alte Huelle rendert den
/// Admin-Eintrag mit <c>hidden</c> und blendet ihn nach dem Laden ueber <c>/auth/capabilities</c>
/// ein - das ist ein Sprung in der Seitenleiste, genau das, was Frontend-Regel 1 verbietet.
///
/// Als Filter und nicht als Basisklasse: dann muss keine Seite daran denken, und keine kann es
/// vergessen. Als Filter und nicht per <c>@inject</c> in der Ansicht: eine Ansicht, die selbst die
/// Datenbank fragt, ist der Anfang derselben Vermischung, die im Backend LayerSeparationTests
/// verhindert.
/// </summary>
public sealed class NavigationPageFilter(InstanceAdminService admins) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    /// <summary>Der Schluessel, unter dem die Navigation die Antwort erwartet.</summary>
    public const string IsAdminKey = "FullWorth.IsAdmin";

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        var isAdmin = user.Identity?.IsAuthenticated == true
            && await admins.GetCurrentAdminAsync(user, context.HttpContext.RequestAborted) is not null;
        context.HttpContext.Items[IsAdminKey] = isAdmin;

        // Und dieselbe Antwort schuetzt die Seite auch, statt sie nur aus dem Menue zu nehmen.
        //
        // /admin war bis #154 eine eigene Route, die die Huelle auslieferte UND die Rechte pruefte.
        // Als die Verwaltung eine Razor-Seite wurde, blieb die Route stehen, verdeckte die Seite und
        // schickte eine Datei, die es nicht mehr gibt - die Seite antwortete mit 500. Die Pruefung
        // gehoert nicht an eine Adresse, sondern an die Seiten, die sie brauchen.
        //
        // Welche das sind, sagt der Katalog: ein Eintrag mit AdminOnly. Kommt eine zweite
        // Verwaltungsseite dazu, ist sie damit geschuetzt, ohne dass jemand daran denken muss.
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var adminOnly = NavigationCatalog.Entries.Any(entry =>
            entry.AdminOnly && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

        if (adminOnly && !isAdmin)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        await next();
    }
}
