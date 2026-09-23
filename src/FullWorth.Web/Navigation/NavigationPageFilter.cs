using FullWorth.Web.Modules.Admin;
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
        context.HttpContext.Items[IsAdminKey] = user.Identity?.IsAuthenticated == true
            && await admins.GetCurrentAdminAsync(user, context.HttpContext.RequestAborted) is not null;

        await next();
    }
}
