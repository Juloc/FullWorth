namespace FullWorth.Web.Modules.Import;

public static class FinanzguruImportPageEndpoints
{
    private const string Html = """
<!doctype html>
<html lang="de" data-theme="light">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="color-scheme" content="light dark">
  <title>Finanzguru Import</title>
  <script src="/theme-init.js"></script>
  <link rel="stylesheet" href="/app.css">
  <link rel="stylesheet" href="/features/finanzguru-import-page.css">
</head>
<body>
  <main id="main" class="import-page" tabindex="-1">
    <header class="topbar import-topbar">
      <div class="topbar-title"><h1 id="import-title">Finanzguru Import</h1><p id="import-subtitle"></p></div>
      <div class="topbar-actions"><a class="ghost import-back" href="/settings/import" id="import-back">Zurück</a></div>
    </header>
    <section class="view active">
      <article class="panel import-card">
        <div class="panel-head"><h2 id="import-heading">Alle Buchungen importieren</h2></div>
        <p class="row-sub panel-intro" id="import-hint"></p>
        <div class="rows import-info">
          <div class="row"><div class="row-main"><div class="row-title" id="import-space-label">FullWorth Space</div><div class="row-sub" id="import-space">—</div></div></div>
          <div class="row"><div class="row-main"><div class="row-title" id="import-safety-title"></div><div class="row-sub" id="import-safety"></div></div></div>
        </div>
        <form id="finanzguru-form" class="import-form">
          <label class="field"><span id="import-file-label">Finanzguru .xlsx Export</span><input id="finanzguru-file" name="file" type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" required></label>
          <button id="finanzguru-submit" class="primary-action" type="submit">Importieren</button>
        </form>
        <div id="import-status" class="row-sub import-status" role="status" aria-live="polite"></div>
        <div id="import-result" class="rows" hidden></div>
      </article>

      <article class="panel import-card import-link-card">
        <div class="panel-head"><h2 id="import-link-heading"></h2></div>
        <p class="row-sub panel-intro" id="import-link-hint"></p>
        <div id="import-link-status" class="row-sub import-status" role="status" aria-live="polite"></div>
        <div id="import-link-list" class="import-link-list"></div>
        <p class="row-sub import-link-footer"><a href="/accounts" id="import-link-accounts"></a></p>
      </article>
    </section>
  </main>
  <script src="/security/browser-fetch.js"></script>
  <script type="module" src="/features/finanzguru-import-page.js"></script>
</body>
</html>
""";

    // Compatibility entry point used by FullWorth.Web/Program.cs. New import pages are registered by
    // ImportPageEndpoints so provider-specific modules do not need to know about each other.
    public static IEndpointRouteBuilder MapFinanzguruImportPageEndpoints(this IEndpointRouteBuilder app) =>
        ImportPageEndpoints.MapImportPageEndpoints(app);

    internal static IEndpointRouteBuilder MapFinanzguruProviderPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings/import/finanzguru/xlsx", () => Results.Content(Html, "text/html; charset=utf-8"))
            .RequireAuthorization();
        return app;
    }
}
