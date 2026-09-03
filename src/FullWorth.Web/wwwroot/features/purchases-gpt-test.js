// Explicit experimental UI for Codex/ChatGPT receipt scanning.
// It is intentionally verbose: every phase, raw model event and bridge log is visible for debugging.
let ctx = null;
let onApplied = null;
let currentFile = null;
let currentResult = null;
let authPoll = null;
let uiLog = [];

export function bindGptReceiptTest(context, appliedCallback) {
  ctx = context;
  onApplied = appliedCallback;
  ensureStylesheet();
  const normalScan = ctx.$('#scan-receipt');
  if (!normalScan || document.querySelector('#gpt-receipt-test')) return;
  const button = document.createElement('button');
  button.id = 'gpt-receipt-test';
  button.type = 'button';
  button.className = 'ghost gpt-test-launch';
  button.textContent = 'GPT Scan · Test';
  button.title = 'Experimenteller ChatGPT/Codex-Bonscan mit vollständigem Debug-Output';
  normalScan.insertAdjacentElement('afterend', button);
  button.addEventListener('click', openTestDialog);
}

// The production CSP permits same-origin stylesheets but not JS-created inline <style> blocks.
function ensureStylesheet() {
  if (document.querySelector('#gpt-receipt-test-stylesheet')) return;
  const link = document.createElement('link');
  link.id = 'gpt-receipt-test-stylesheet';
  link.rel = 'stylesheet';
  link.href = '/features/purchases-gpt-test.css';
  document.head.appendChild(link);
}

function pushUi(stage, message, level = 'info') {
  const entry = { timestamp: new Date().toISOString(), scope: 'frontend', stage, stream: level, message };
  uiLog.push(entry);
  renderTimeline();
}

async function openTestDialog() {
  currentFile = null;
  currentResult = null;
  uiLog = [];
  const dlg = ctx.dialog(`<div class="gpt-test-shell">
    <div class="gpt-test-head"><div><h2>GPT Scan · Test</h2><p>ChatGPT/Codex Vision · keine Speicherung bis „Übernehmen“ · vollständige Debugansicht</p></div><button type="button" data-close aria-label="Schließen">×</button></div>
    <div class="gpt-test-body">
      <div class="gpt-test-banner"><strong>TESTMODUS.</strong> Der normale OCR-Scan bleibt unverändert. Codex-Auth ist pro Benutzer und FullWorth Space isoliert. Debugausgaben werden redaktiert.</div>
      <div class="gpt-test-status">
        <div class="gpt-test-card"><span class="gpt-test-k">Bridge</span><span class="gpt-test-v" data-bridge>Prüfe …</span></div>
        <div class="gpt-test-card"><span class="gpt-test-k">ChatGPT / Codex</span><span class="gpt-test-v" data-auth>Prüfe …</span></div>
        <div class="gpt-test-card"><span class="gpt-test-k">Codex-Version</span><span class="gpt-test-v" data-version>—</span></div>
      </div>

      <div class="gpt-test-card" data-login-card>
        <div class="panel-head"><div><strong>1. ChatGPT anmelden</strong><div class="row-sub">Verwendet <code>codex login --device-auth</code>.</div></div><div class="gpt-test-actions"><button type="button" data-login>Mit ChatGPT anmelden</button><button type="button" class="ghost" data-logout>Trennen</button></div></div>
        <div data-login-flow></div>
      </div>

      <div class="gpt-test-card">
        <div class="panel-head"><div><strong>2. Modell & Bon</strong><div class="row-sub">Auto nutzt die aktuelle Codex-Standardauswahl.</div></div><button type="button" class="ghost" data-models>Modelle neu laden</button></div>
        <div class="gpt-test-actions">
          <label>Modell <select data-model><option value="auto">Auto</option></select></label>
          <label>Bon <input type="file" data-file accept="image/jpeg,image/png,image/webp,application/pdf,.jpg,.jpeg,.png,.webp,.pdf" capture="environment"></label>
          <button type="button" data-scan disabled>Mit GPT analysieren</button>
        </div>
      </div>

      <div class="gpt-test-grid">
        <div class="gpt-test-card"><strong>3. Original</strong><div class="gpt-test-preview" data-preview><span class="row-sub">Noch kein Bon ausgewählt.</span></div><div class="row-sub" data-file-meta></div></div>
        <div class="gpt-test-card"><strong>4. Strukturiertes Ergebnis</strong><div data-result class="row-sub gpt-test-mt10">Noch kein Scan ausgeführt.</div></div>
      </div>

      <details class="gpt-test-section" open><summary>5. Ablauf / Timeline</summary><div class="gpt-test-section-body"><div class="gpt-test-timeline" data-timeline></div></div></details>
      <details class="gpt-test-section"><summary>6. Prompt an Codex</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-prompt>—</pre></div></details>
      <details class="gpt-test-section"><summary>7. JSON Schema</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-schema>—</pre></div></details>
      <details class="gpt-test-section" open><summary>8. Finales Raw-Output</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-raw-output>—</pre></div></details>
      <details class="gpt-test-section"><summary>9. Codex JSONL Events</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-events>—</pre></div></details>
      <details class="gpt-test-section"><summary>10. stderr</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-stderr>—</pre></div></details>
      <details class="gpt-test-section" open><summary>11. Vollständiges Run-Log</summary><div class="gpt-test-section-body"><div class="gpt-test-actions gpt-test-mb8"><button type="button" class="ghost" data-refresh-logs>Bridge-Log aktualisieren</button><button type="button" class="ghost" data-copy-debug>Debug kopieren</button></div><pre class="gpt-test-pre" data-full-log>—</pre></div></details>
      <details class="gpt-test-section"><summary>12. Kompletter Response-Payload</summary><div class="gpt-test-section-body"><pre class="gpt-test-pre" data-response>—</pre></div></details>

      <div class="gpt-test-foot"><button type="button" class="ghost" data-close-bottom>Schließen</button><div class="gpt-test-actions"><span class="row-sub" data-apply-state>Es wird noch nichts gespeichert.</span><button type="button" data-apply disabled>Übernehmen</button></div></div>
    </div>
  </div>`);
  dlg.classList.add('gpt-test-dialog');
  dlg.dataset.connected = '0';

  const cleanup = () => {
    if (authPoll) clearInterval(authPoll);
    authPoll = null;
  };
  const close = () => dlg.close();
  dlg.addEventListener('close', cleanup, { once: true });
  dlg.querySelector('[data-close]').onclick = close;
  dlg.querySelector('[data-close-bottom]').onclick = close;
  dlg.querySelector('[data-login]').onclick = () => startLogin(dlg);
  dlg.querySelector('[data-logout]').onclick = () => logout(dlg);
  dlg.querySelector('[data-models]').onclick = () => loadModels(dlg);
  dlg.querySelector('[data-file]').onchange = e => chooseFile(dlg, e.target.files?.[0]);
  dlg.querySelector('[data-scan]').onclick = () => runScan(dlg);
  dlg.querySelector('[data-refresh-logs]').onclick = () => refreshBridgeLogs(dlg);
  dlg.querySelector('[data-copy-debug]').onclick = () => copyDebug(dlg);
  dlg.querySelector('[data-apply]').onclick = () => applyResult(dlg);
  dlg.showModal();
  pushUi('open', 'GPT receipt test console opened.');
  const status = await refreshStatus(dlg);
  if (status?.connected) await loadModels(dlg, true);
}

async function refreshStatus(dlg) {
  pushUi('status', 'Checking Codex bridge status.');
  try {
    const status = await ctx.api('api/purchases/gpt-test/status');
    dlg.querySelector('[data-response]').textContent = pretty(status);
    if (status.enabled === false) {
      dlg.dataset.connected = '0';
      dlg.querySelector('[data-bridge]').textContent = 'Deaktiviert';
      dlg.querySelector('[data-auth]').textContent = '—';
      dlg.querySelector('[data-version]').textContent = '—';
      pushUi('status', 'GPT receipt test mode is disabled.', 'warn');
      setScanState(dlg, false);
      return status;
    }
    dlg.querySelector('[data-bridge]').textContent = 'Online';
    dlg.querySelector('[data-auth]').textContent = status.connected ? 'Verbunden' : 'Nicht verbunden';
    dlg.querySelector('[data-version]').textContent = status.codexVersion || '—';
    pushUi('status', status.connected ? 'Codex login is active.' : 'Codex is not logged in.');
    dlg.dataset.connected = status.connected ? '1' : '0';
    setScanState(dlg);
    return status;
  } catch (error) {
    dlg.dataset.connected = '0';
    dlg.querySelector('[data-bridge]').textContent = 'Nicht erreichbar';
    dlg.querySelector('[data-auth]').textContent = '—';
    dlg.querySelector('[data-version]').textContent = '—';
    pushUi('status', error.message || String(error), 'error');
    setScanState(dlg, false);
    return null;
  }
}

async function startLogin(dlg) {
  pushUi('auth', 'Starting device-code login.');
  try {
    const session = await ctx.api('api/purchases/gpt-test/login', { method: 'POST' });
    renderLoginSession(dlg, session);
    if (authPoll) clearInterval(authPoll);
    let polling = false;
    authPoll = setInterval(async () => {
      if (polling || !dlg.isConnected) return;
      polling = true;
      try {
        const next = await ctx.api(`api/purchases/gpt-test/login/${session.id}`);
        renderLoginSession(dlg, next);
        if (next.status === 'connected' || next.status === 'error') {
          clearInterval(authPoll);
          authPoll = null;
          const status = await refreshStatus(dlg);
          if (next.status === 'connected' && status?.connected) await loadModels(dlg, true);
        }
      } catch (error) {
        pushUi('auth-poll', error.message || String(error), 'error');
      } finally {
        polling = false;
      }
    }, 1000);
  } catch (error) {
    pushUi('auth', error.message || String(error), 'error');
    renderTimeline();
  }
}

function renderLoginSession(dlg, session) {
  const box = dlg.querySelector('[data-login-flow]');
  const rows = (session.output || []).map(x => `[${x.timestamp || ''}] ${x.stream || ''}: ${x.message || ''}`).join('\n');
  box.innerHTML = `<div class="gpt-test-meta gpt-test-mt12">
    <div><span class="gpt-test-k">Status</span><span class="gpt-test-v">${ctx.esc(session.status || '—')}</span></div>
    <div><span class="gpt-test-k">Code</span><span class="gpt-test-v gpt-test-login-code">${ctx.esc(session.userCode || 'wartet …')}</span></div>
    <div><span class="gpt-test-k">URL</span><span class="gpt-test-v">${session.verificationUrl ? `<a href="${ctx.esc(session.verificationUrl)}" target="_blank" rel="noopener">${ctx.esc(session.verificationUrl)}</a>` : 'wartet …'}</span></div>
  </div><pre class="gpt-test-pre gpt-test-mt10">${ctx.esc(rows || 'Warte auf Codex-Ausgabe …')}</pre>`;
  pushUi('auth', `Device login status: ${session.status || 'unknown'}.`);
}

async function logout(dlg) {
  if (authPoll) clearInterval(authPoll);
  authPoll = null;
  pushUi('auth', 'Logging out Codex account.');
  try {
    await ctx.api('api/purchases/gpt-test/logout', { method: 'POST' });
    pushUi('auth', 'Codex account disconnected.');
  } catch (error) {
    pushUi('auth', error.message || String(error), 'error');
  }
  await refreshStatus(dlg);
}

async function loadModels(dlg, silent = false) {
  if (!silent) pushUi('models', 'Loading Codex model catalog.');
  try {
    const payload = await ctx.api('api/purchases/gpt-test/models');
    const names = extractModelNames(payload.models).sort();
    const select = dlg.querySelector('[data-model]');
    const previous = select.value;
    select.innerHTML = '<option value="auto">Auto</option>' + names.map(x => `<option value="${ctx.esc(x)}">${ctx.esc(x)}</option>`).join('');
    if ([...select.options].some(x => x.value === previous)) select.value = previous;
    pushUi('models', `Model catalog loaded (${names.length} selectable IDs).`);
  } catch (error) {
    if (!silent) pushUi('models', error.message || String(error), 'error');
  }
}

function extractModelNames(root) {
  const result = new Set();
  const seen = new Set();
  const walk = (value, key = '') => {
    if (value == null) return;
    if (typeof value === 'string') {
      if (/^(id|slug|model|model_slug)$/i.test(key) && /^[a-z0-9][a-z0-9._-]{2,}$/i.test(value)) result.add(value);
      return;
    }
    if (typeof value !== 'object' || seen.has(value)) return;
    seen.add(value);
    if (Array.isArray(value)) value.forEach(x => walk(x));
    else Object.entries(value).forEach(([k, v]) => walk(v, k));
  };
  walk(root);
  return [...result];
}

async function chooseFile(dlg, file) {
  currentFile = file || null;
  currentResult = null;
  dlg.querySelector('[data-apply]').disabled = true;
  dlg.querySelector('[data-apply-state]').textContent = 'Es wird noch nichts gespeichert.';
  const preview = dlg.querySelector('[data-preview]');
  if (!file) {
    preview.innerHTML = '<span class="row-sub">Noch kein Bon ausgewählt.</span>';
    dlg.querySelector('[data-file-meta]').textContent = '';
    setScanState(dlg);
    return;
  }

  dlg.querySelector('[data-file-meta]').textContent = `${file.name} · ${file.type || 'unbekannter Typ'} · ${formatBytes(file.size)}`;
  if (file.size > 20 * 1024 * 1024) {
    preview.innerHTML = '<span class="gpt-test-error">Datei ist größer als 20 MB.</span>';
    pushUi('input', `Rejected ${file.name}: ${file.size} bytes exceeds 20 MB.`, 'error');
    currentFile = null;
    setScanState(dlg, false);
    return;
  }

  if (file.type === 'application/pdf' || /\.pdf$/i.test(file.name)) {
    // Production CSP deliberately blocks embedded PDFs/object content. The bridge converts page 1
    // server-side before sending it to Codex, so a local PDF preview is not required for the test.
    preview.innerHTML = '<span class="row-sub">PDF ausgewählt. Inline-PDF-Vorschau ist aus Sicherheitsgründen deaktiviert; Seite 1 wird serverseitig für Codex gerendert.</span>';
  } else {
    try {
      const dataUrl = await readDataUrl(file);
      if (file === currentFile) preview.innerHTML = `<img src="${ctx.esc(dataUrl)}" alt="Ausgewählter Bon">`;
    } catch (error) {
      preview.innerHTML = `<span class="gpt-test-error">${ctx.esc(error.message || String(error))}</span>`;
      pushUi('preview', error.message || String(error), 'error');
    }
  }
  pushUi('input', `Selected ${file.name} (${file.type || 'unknown'}, ${file.size} bytes).`);
  setScanState(dlg);
}

function readDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result || ''));
    reader.onerror = () => reject(reader.error || new Error('Dateivorschau konnte nicht gelesen werden.'));
    reader.readAsDataURL(file);
  });
}

function setScanState(dlg, force) {
  const enabled = force ?? (dlg.dataset.connected === '1' && !!currentFile);
  dlg.querySelector('[data-scan]').disabled = !enabled;
}

async function runScan(dlg) {
  if (!currentFile) return;
  const scan = dlg.querySelector('[data-scan]');
  scan.disabled = true;
  currentResult = null;
  dlg.querySelector('[data-apply]').disabled = true;
  dlg.querySelector('[data-result]').innerHTML = '<div class="row-sub">Codex analysiert den Bon …</div>';
  clearOutputs(dlg);
  pushUi('scan', 'Uploading original receipt to internal Codex bridge.');
  const form = new FormData();
  form.append('receipt', currentFile);
  form.append('model', dlg.querySelector('[data-model]').value || 'auto');
  try {
    const response = await ctx.api('api/purchases/gpt-test/scan', { method: 'POST', body: form });
    currentResult = response;
    pushUi('scan', response.success ? `Scan completed in ${response.durationMs} ms.` : `Scan failed: ${response.error || response.parseError || 'unknown error'}`, response.success ? 'info' : 'error');
    renderScanResponse(dlg, response);
    dlg.querySelector('[data-apply]').disabled = !response.success || !response.result;
    dlg.querySelector('[data-apply-state]').textContent = response.success ? 'Noch nicht gespeichert.' : 'Fehler – nichts gespeichert.';
  } catch (error) {
    pushUi('scan', error.message || String(error), 'error');
    dlg.querySelector('[data-result]').innerHTML = `<div class="gpt-test-error">${ctx.esc(error.message || String(error))}</div>`;
  } finally {
    setScanState(dlg);
  }
}

function clearOutputs(dlg) {
  for (const sel of ['[data-prompt]','[data-schema]','[data-raw-output]','[data-events]','[data-stderr]','[data-full-log]','[data-response]'])
    dlg.querySelector(sel).textContent = '—';
}

function renderScanResponse(dlg, response) {
  dlg.querySelector('[data-prompt]').textContent = response.prompt || '—';
  dlg.querySelector('[data-schema]').textContent = pretty(response.schema);
  dlg.querySelector('[data-raw-output]').textContent = response.rawOutput || '—';
  dlg.querySelector('[data-events]').textContent = pretty(response.rawEvents);
  dlg.querySelector('[data-stderr]').textContent = response.stderr || '—';
  dlg.querySelector('[data-full-log]').textContent = logText([...(response.logs || []), ...uiLog]);
  dlg.querySelector('[data-response]').textContent = pretty(response);
  const target = dlg.querySelector('[data-result]');
  if (!response.success || !response.result) {
    target.innerHTML = `<div class="gpt-test-error"><strong>Scan fehlgeschlagen</strong><div>${ctx.esc(response.error || response.parseError || `Codex exit ${response.exitCode ?? '—'}`)}</div></div>`;
    return;
  }

  const r = response.result;
  const items = r.items || [];
  const rows = items.map(item => `<tr><td>${ctx.esc(item.rawName || '—')}</td><td><strong>${ctx.esc(item.name || '—')}</strong>${item.brand ? `<div class="row-sub">${ctx.esc(item.brand)}</div>` : ''}</td><td>${ctx.esc(item.categorySuggestion || '—')}</td><td class="num">${item.quantity ?? '—'}</td><td class="num">${moneyRaw(item.unitPrice, r.receipt?.currency)}</td><td class="num">${moneyRaw(item.totalPrice, r.receipt?.currency)}</td><td class="num gpt-test-confidence">${pct(item.confidence)}</td></tr>`).join('');
  target.innerHTML = `<div class="gpt-test-ok gpt-test-result-head"><div><strong>${ctx.esc(r.merchant?.name || 'Unbekannter Händler')}</strong><div>${ctx.esc(r.receipt?.date || '—')} ${ctx.esc(r.receipt?.time || '')}</div></div><div><strong>${moneyRaw(r.totals?.total, r.receipt?.currency)}</strong><div class="gpt-test-confidence">Gesamt: ${pct(r.confidence)}</div></div></div>
    <div class="gpt-test-meta gpt-test-my12"><div><span class="gpt-test-k">Zwischensumme</span><span class="gpt-test-v">${moneyRaw(r.totals?.subtotal,r.receipt?.currency)}</span></div><div><span class="gpt-test-k">Rabatte</span><span class="gpt-test-v">${moneyRaw(r.totals?.discounts,r.receipt?.currency)}</span></div><div><span class="gpt-test-k">Pfand</span><span class="gpt-test-v">${moneyRaw(r.totals?.deposits,r.receipt?.currency)}</span></div><div><span class="gpt-test-k">Steuer</span><span class="gpt-test-v">${moneyRaw(r.totals?.tax,r.receipt?.currency)}</span></div><div><span class="gpt-test-k">Zahlart</span><span class="gpt-test-v">${ctx.esc(r.payment?.method || '—')}</span></div><div><span class="gpt-test-k">Bon-Nr.</span><span class="gpt-test-v">${ctx.esc(r.receipt?.receiptNumber || '—')}</span></div></div>
    <div class="gpt-test-scroll-x"><table class="gpt-test-items"><thead><tr><th>Raw</th><th>Artikel</th><th>Kategorie</th><th>Menge</th><th>Einzel</th><th>Gesamt</th><th>Conf.</th></tr></thead><tbody>${rows || '<tr><td colspan="7">Keine Artikel erkannt.</td></tr>'}</tbody></table></div>
    ${(r.warnings || []).length ? `<div class="gpt-test-error gpt-test-mt10"><strong>Warnings</strong><ul>${r.warnings.map(x => `<li>${ctx.esc(x)}</li>`).join('')}</ul></div>` : ''}`;
}

async function refreshBridgeLogs(dlg) {
  pushUi('logs', 'Loading recent bridge log.');
  try {
    const payload = await ctx.api('api/purchases/gpt-test/logs?limit=1000');
    dlg.querySelector('[data-full-log]').textContent = logText([...(payload.logs || []), ...uiLog]);
  } catch (error) {
    pushUi('logs', error.message || String(error), 'error');
  }
}

async function copyDebug(dlg) {
  const data = {
    frontend: uiLog,
    response: currentResult,
    visibleBridgeLog: dlg.querySelector('[data-full-log]').textContent
  };
  try {
    await navigator.clipboard.writeText(pretty(data));
    pushUi('copy', 'Debug payload copied to clipboard.');
  } catch (error) {
    pushUi('copy', error.message || String(error), 'error');
  }
}

async function applyResult(dlg) {
  if (!currentFile || !currentResult?.success || !currentResult.result) return;
  const button = dlg.querySelector('[data-apply]');
  button.disabled = true;
  const state = dlg.querySelector('[data-apply-state]');
  state.textContent = 'Speichere Bon …';
  pushUi('apply', 'Persisting receipt through normal receipt capture path.');
  try {
    const r = currentResult.result;
    const currency = /^[A-Z]{3}$/.test(r.receipt?.currency || '') ? r.receipt.currency : 'EUR';
    const upload = new FormData();
    upload.append('receipt', currentFile);
    upload.append('currency', currency);
    const purchase = await ctx.api('api/purchases/receipt-scan', { method: 'POST', body: upload });
    pushUi('apply', `Normal receipt capture created purchase ${purchase.id}.`);

    const categoryIds = await categoryPathMap();
    const items = (r.items || []).filter(x => x.name || x.rawName).map(x => ({
      categoryId: categoryIds.get((x.categorySuggestion || '').toLowerCase()) || null,
      name: x.name || x.rawName,
      brand: x.brand || null,
      sku: null,
      asin: null,
      quantity: Number(x.quantity || 1),
      unitPrice: x.unitPrice == null ? null : Number(x.unitPrice),
      totalPrice: Number(x.totalPrice ?? 0),
      currency,
      notes: x.rawName && x.rawName !== x.name ? `GPT raw: ${x.rawName}` : null
    }));
    const extraction = {
      merchant: r.merchant?.name || 'Unbekannt',
      purchaseDate: validDate(r.receipt?.date),
      totalAmount: Number(r.totals?.total ?? items.reduce((sum, x) => sum + x.totalPrice, 0)),
      currency,
      items,
      sourceReference: `codex-test:${currentResult.requestId}`,
      notes: `GPT/Codex test scan · confidence ${r.confidence ?? 0}`
    };
    await ctx.api(`api/purchases/${purchase.id}/extraction`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(extraction)
    });
    pushUi('apply', `GPT extraction applied to purchase ${purchase.id}.`);
    state.textContent = 'Gespeichert.';
    button.textContent = 'Übernommen';
    await onApplied?.();
  } catch (error) {
    pushUi('apply', error.message || String(error), 'error');
    state.textContent = 'Speichern fehlgeschlagen – Log prüfen.';
    button.disabled = false;
  }
}

async function categoryPathMap() {
  const rows = (await ctx.api('api/categories')) || [];
  const byId = new Map(rows.map(x => [x.id, x]));
  const map = new Map();
  for (const row of rows) {
    const names = [];
    const seen = new Set();
    let current = row;
    while (current && !seen.has(current.id)) {
      seen.add(current.id);
      names.unshift(current.name);
      current = current.parentId ? byId.get(current.parentId) : null;
    }
    map.set(names.join(' > ').toLowerCase(), row.id);
  }
  return map;
}

function renderTimeline() {
  const el = document.querySelector('.gpt-test-dialog [data-timeline]');
  if (!el) return;
  el.innerHTML = uiLog.slice().reverse().map(x => `<div class="gpt-test-step"><span>${ctx.esc(x.timestamp)}</span><strong>${ctx.esc(x.stage)}</strong><span>${ctx.esc(x.message)}</span></div>`).join('') || '<span class="row-sub">—</span>';
}

function logText(rows) {
  return (rows || [])
    .slice()
    .sort((a, b) => String(a.timestamp).localeCompare(String(b.timestamp)))
    .map(x => `[${x.timestamp || ''}] [${x.scope || ''}/${x.stage || ''}] [${x.stream || ''}] ${x.message || ''}`)
    .join('\n') || '—';
}

function pretty(value) { return value == null ? '—' : JSON.stringify(value, null, 2); }
function pct(value) { return value == null ? '—' : `${Math.round(Number(value) * 100)}%`; }
function moneyRaw(value, currency) { return value == null ? '—' : ctx.money(Number(value), currency || 'EUR'); }
function formatBytes(value) { if (value < 1024) return `${value} B`; if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`; return `${(value / 1024 / 1024).toFixed(1)} MB`; }
function validDate(value) { return /^\d{4}-\d{2}-\d{2}$/.test(value || '') ? value : null; }
