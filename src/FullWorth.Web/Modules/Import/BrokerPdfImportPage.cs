namespace FullWorth.Web.Modules.Import;

public static class BrokerPdfImportPageEndpoints
{
    private const string Html = """
<!doctype html>
<html lang="de" data-theme="light">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="color-scheme" content="light dark">
  <title>Broker-PDF importieren · FullWorth</title>
  <script src="/theme-init.js"></script>
  <link rel="stylesheet" href="/app.css">
  <link rel="stylesheet" href="/features/import-center-page.css">
</head>
<body>
  <main id="main" class="import-center" tabindex="-1">
    <header class="topbar import-center-topbar">
      <div class="topbar-title"><h1 id="pdf-title">Broker-PDF importieren</h1><p id="pdf-subtitle"></p></div>
      <div class="topbar-actions"><a class="ghost import-back" href="/settings/import" id="pdf-back">Zurück</a></div>
    </header>
    <section class="view active import-center-view">
      <div class="import-context panel"><div class="row"><div class="row-main"><div class="row-title" id="pdf-space-label">FullWorth Space</div><div class="row-sub" id="pdf-space">—</div></div></div></div>
      <article class="panel import-workflow">
        <div class="panel-head"><div><h2 id="pdf-heading">Abrechnung erkennen</h2><p class="row-sub" id="pdf-hint"></p></div></div>
        <div class="import-form-grid">
          <label class="field"><span id="pdf-portfolio-label">Zieldepot</span><select id="pdf-portfolio"></select></label>
          <label class="field span-2"><span id="pdf-file-label">Broker-Abrechnung als PDF</span><input id="pdf-file" type="file" accept=".pdf,application/pdf"></label>
        </div>
        <div class="workflow-actions"><button id="pdf-detect" class="primary-action" type="button">PDF analysieren</button></div>
        <div id="pdf-status" class="row-sub import-status" role="status" aria-live="polite"></div>
        <section id="pdf-preview-section" hidden>
          <div id="pdf-detection" class="rows"></div>
          <div class="preview-wrap"><table><thead id="pdf-preview-head"></thead><tbody id="pdf-preview-body"></tbody></table></div>
          <div class="workflow-actions"><button id="pdf-stage" class="primary-action" type="button">Import prüfen</button></div>
        </section>
        <section id="pdf-review-section" hidden>
          <h3 id="pdf-security-title">Wertpapier prüfen</h3>
          <div id="pdf-security-summary" class="rows"></div>
          <label class="check import-option"><input id="pdf-create-securities" type="checkbox" checked><span id="pdf-create-securities-label">Fehlendes Wertpapier automatisch anlegen</span></label>
          <div id="pdf-review-summary" class="metric-grid import-metrics"></div>
          <div class="workflow-actions"><button id="pdf-commit" class="primary-action" type="button">Ins Depot importieren</button></div>
        </section>
        <div id="pdf-result" class="rows" hidden></div>
      </article>
    </section>
  </main>
  <script src="/security/browser-fetch.js"></script>
  <script type="module" src="/features/broker-pdf-import-page.js"></script>
</body>
</html>
""";

    public static IEndpointRouteBuilder MapBrokerPdfImportPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings/import/broker-pdf", () => Results.Content(Html, "text/html; charset=utf-8"))
            .RequireAuthorization();
        return app;
    }
}
