namespace FullWorth.Web.Modules.Import;

public static class ImportCenterPageEndpoints
{
    private const string Html = """
<!doctype html>
<html lang="de" data-theme="light">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="color-scheme" content="light dark">
  <title>Daten importieren · FullWorth</title>
  <script src="/theme-init.js"></script>
  <link rel="stylesheet" href="/app.css">
  <link rel="stylesheet" href="/features/import-center-page.css">
</head>
<body>
  <main id="main" class="import-center" tabindex="-1">
    <header class="topbar import-center-topbar">
      <div class="topbar-title"><h1 id="page-title">Daten importieren</h1><p id="page-subtitle"></p></div>
      <div class="topbar-actions"><a class="ghost import-back" href="/settings" id="back-link">Zurück</a></div>
    </header>

    <section class="view active import-center-view">
      <div class="import-context panel">
        <div class="row"><div class="row-main"><div class="row-title" id="space-label">FullWorth Space</div><div class="row-sub" id="space-name">—</div></div></div>
      </div>

      <div class="import-provider-grid" aria-label="Import sources">
        <button type="button" class="panel import-provider active" data-import-mode="transactions">
          <span class="provider-icon" aria-hidden="true">↕</span><strong id="source-transactions-title">CSV / XLSX</strong><span id="source-transactions-hint">Buchungen aus Banken und Finanz-Apps</span>
        </button>
        <a class="panel import-provider" href="/settings/import/finanzguru/xlsx">
          <span class="provider-icon" aria-hidden="true">FG</span><strong>Finanzguru</strong><span id="source-finanzguru-hint">Alle Buchungen inklusive Splits und Kategorien</span>
        </a>
        <button type="button" class="panel import-provider" data-import-mode="investments">
          <span class="provider-icon" aria-hidden="true">↗</span><strong id="source-investments-title">Depot / Parqet</strong><span id="source-investments-hint">Käufe, Verkäufe, Dividenden und Gebühren</span>
        </button>
        <a class="panel import-provider" href="/settings/import/broker-pdf">
          <span class="provider-icon" aria-hidden="true">PDF</span><strong id="source-pdf-title">Broker-PDF</strong><span id="source-pdf-hint">Text- und Scan-PDFs lokal erkennen, prüfen und importieren</span>
        </a>
      </div>

      <article id="transaction-import" class="panel import-workflow">
        <div class="panel-head"><div><h2 id="tx-title">Buchungen importieren</h2><p class="row-sub" id="tx-hint"></p></div></div>
        <div class="import-form-grid">
          <label class="field"><span id="tx-preset-label">Quelle</span><select id="tx-preset"><option value="generic">CSV / XLSX</option><option value="finanzfluss">Finanzfluss Copilot</option><option value="outbank">Outbank</option></select></label>
          <label class="field span-2"><span id="tx-file-label">Datei</span><input id="tx-file" type="file" accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"></label>
        </div>
        <div class="workflow-actions"><button id="tx-detect" class="primary-action" type="button">Datei analysieren</button></div>
        <div id="tx-status" class="row-sub import-status" role="status" aria-live="polite"></div>

        <section id="tx-mapping-section" hidden>
          <h3 id="tx-mapping-title">Spalten zuordnen</h3>
          <div id="tx-mapping" class="mapping-grid"></div>
          <div class="preview-wrap"><table><thead id="tx-preview-head"></thead><tbody id="tx-preview-body"></tbody></table></div>
          <div class="workflow-actions"><button id="tx-stage" class="primary-action" type="button">Import prüfen</button></div>
        </section>

        <section id="tx-review-section" hidden>
          <h3 id="tx-target-title">Konten zuordnen</h3>
          <p class="row-sub" id="tx-target-hint"></p>
          <div id="tx-account-mapping" class="rows"></div>
          <div class="import-options">
            <label class="check"><input id="tx-create-categories" type="checkbox" checked><span id="tx-create-categories-label">Kategorien aus Datei übernehmen</span></label>
            <label class="check"><input id="tx-run-rules" type="checkbox" checked><span id="tx-run-rules-label">FullWorth-Regeln für nicht kategorisierte Buchungen anwenden</span></label>
          </div>
          <div id="tx-review-summary" class="metric-grid import-metrics"></div>
          <div class="workflow-actions"><button id="tx-commit" class="primary-action" type="button">Importieren</button></div>
        </section>
        <div id="tx-result" class="rows" hidden></div>
      </article>

      <article id="investment-import" class="panel import-workflow" hidden>
        <div class="panel-head"><div><h2 id="inv-title">Depot importieren</h2><p class="row-sub" id="inv-hint"></p></div></div>
        <div class="import-form-grid">
          <label class="field"><span id="inv-preset-label">Quelle</span><select id="inv-preset"><option value="generic">CSV / XLSX</option><option value="traderepublic">Trade Republic</option><option value="parqet">Parqet</option><option value="finanzfluss">Finanzfluss Copilot</option></select></label>
          <label class="field"><span id="inv-portfolio-label">Zieldepot</span><select id="inv-portfolio"></select></label>
          <div id="inv-new-portfolio-fields" class="import-form-grid span-2" hidden>
            <label class="field"><span id="inv-new-portfolio-name-label">Depotname</span><input id="inv-new-portfolio-name" type="text" maxlength="120" value="Importiertes Depot"></label>
            <label class="field"><span id="inv-new-portfolio-currency-label">Währung</span><input id="inv-new-portfolio-currency" type="text" maxlength="3" value="EUR"></label>
          </div>
          <label class="field span-2"><span id="inv-file-label">Datei</span><input id="inv-file" type="file" accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"></label>
        </div>
        <div class="workflow-actions"><button id="inv-detect" class="primary-action" type="button">Datei analysieren</button></div>
        <div id="inv-status" class="row-sub import-status" role="status" aria-live="polite"></div>

        <section id="inv-mapping-section" hidden>
          <h3 id="inv-mapping-title">Spalten zuordnen</h3>
          <div id="inv-mapping" class="mapping-grid"></div>
          <div class="preview-wrap"><table><thead id="inv-preview-head"></thead><tbody id="inv-preview-body"></tbody></table></div>
          <div class="workflow-actions"><button id="inv-stage" class="primary-action" type="button">Import prüfen</button></div>
        </section>

        <section id="inv-review-section" hidden>
          <h3 id="inv-security-title">Wertpapiere prüfen</h3>
          <div id="inv-security-summary" class="rows"></div>
          <label class="check import-option"><input id="inv-create-securities" type="checkbox" checked><span id="inv-create-securities-label">Fehlende Wertpapiere automatisch anlegen</span></label>
          <div id="inv-review-summary" class="metric-grid import-metrics"></div>
          <div class="workflow-actions"><button id="inv-commit" class="primary-action" type="button">Depot importieren</button></div>
        </section>
        <div id="inv-result" class="rows" hidden></div>
      </article>
    </section>
  </main>
  <script src="/security/browser-fetch.js"></script>
  <script type="module" src="/features/import-center-page.js"></script>
</body>
</html>
""";

    public static IEndpointRouteBuilder MapImportCenterPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings/import", () => Results.Content(Html, "text/html; charset=utf-8"))
            .RequireAuthorization();
        // Keep the already-shipped settings entry working while it is renamed in the main shell:
        // the old Finanzguru link now opens the import center, not a provider-specific dead end.
        app.MapGet("/settings/import/finanzguru", () => Results.Content(Html, "text/html; charset=utf-8"))
            .RequireAuthorization();
        return app;
    }
}
