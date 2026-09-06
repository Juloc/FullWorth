// Bulk receipt archive importer. It deliberately stays separate from the multi-photo scan-set UI:
// one bulk-selected physical file is one receipt, while the normal scan flow may combine several
// photos into one logical receipt.

let dialog = null;
let pollTimer = 0;
let paperlessConnected = false;
let paperlessOptions = { tags: [], documentTypes: [], correspondents: [], storagePaths: [], customFields: [] };

install();

function install() {
  const run = () => {
    ensureCss();
    const scan = document.getElementById('scan-receipt');
    if (!scan || document.getElementById('receipt-imports-launch')) return;
    const button = document.createElement('button');
    button.id = 'receipt-imports-launch';
    button.type = 'button';
    button.className = 'ghost';
    button.textContent = t('Belege importieren', 'Import receipts');
    button.addEventListener('click', openDialog);
    scan.insertAdjacentElement('afterend', button);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', run, { once: true });
  else run();
}

function ensureCss() {
  if (document.getElementById('receipt-imports-css')) return;
  const link = document.createElement('link');
  link.id = 'receipt-imports-css';
  link.rel = 'stylesheet';
  link.href = '/features/receipt-imports.css';
  document.head.appendChild(link);
}

async function openDialog() {
  if (dialog?.isConnected) { dialog.showModal(); return; }
  dialog = document.createElement('dialog');
  dialog.className = 'receipt-import-dialog';
  dialog.innerHTML = `<div class="receipt-import-shell">
    <div class="panel-head receipt-import-head">
      <div><h2>${esc(t('Belege importieren', 'Import receipts'))}</h2><div class="row-sub">${esc(t('Große Belegarchive über Dateien, Paperless-ngx oder einen Importordner verarbeiten.', 'Process large receipt archives from files, Paperless-ngx or an import folder.'))}</div></div>
      <button type="button" class="icon-button" data-close aria-label="${esc(t('Schließen', 'Close'))}">×</button>
    </div>
    <div class="receipt-import-tabs" role="tablist">
      <button type="button" class="active" data-tab="files">${esc(t('Dateien', 'Files'))}</button>
      <button type="button" data-tab="paperless">Paperless-ngx</button>
      <button type="button" data-tab="folder">${esc(t('Importordner', 'Import folder'))}</button>
    </div>
    <div class="receipt-import-body">
      <section data-pane="files">${filesPane()}</section>
      <section data-pane="paperless" hidden>${paperlessPane()}</section>
      <section data-pane="folder" hidden>${folderPane()}</section>
      <section class="receipt-import-batches">
        <div class="panel-head"><h3>${esc(t('Letzte Importe', 'Recent imports'))}</h3><button type="button" class="ghost" data-refresh>${esc(t('Aktualisieren', 'Refresh'))}</button></div>
        <div data-batches class="rows"><div class="row-sub">${esc(t('Lade …', 'Loading …'))}</div></div>
      </section>
    </div>
  </div>`;
  document.body.appendChild(dialog);
  dialog.addEventListener('close', () => { clearInterval(pollTimer); pollTimer = 0; });
  dialog.querySelector('[data-close]').onclick = () => dialog.close();
  dialog.querySelectorAll('[data-tab]').forEach(button => button.addEventListener('click', () => selectTab(button.dataset.tab)));
  dialog.querySelector('[data-refresh]').onclick = refreshBatches;
  bindFiles();
  bindPaperless();
  bindFolder();
  dialog.showModal();
  await Promise.allSettled([refreshBatches(), refreshPaperlessConnection(), refreshFolderStatus()]);
  pollTimer = setInterval(() => { if (dialog?.open) refreshBatches().catch(() => {}); }, 2500);
}

function filesPane() {
  return `<div class="receipt-import-card">
    <h3>${esc(t('Viele Dateien auf einmal', 'Many files at once'))}</h3>
    <p class="row-sub">${esc(t('Jede JPG/PNG/WebP/HEIC/PDF-Datei wird standardmäßig als eigener Beleg verarbeitet. Mehrseitige PDFs bleiben ein Beleg.', 'Each JPG/PNG/WebP/HEIC/PDF file is processed as its own receipt. Multi-page PDFs remain one receipt.'))}</p>
    <label class="receipt-import-drop" data-drop>
      <input data-files type="file" multiple accept="image/jpeg,image/png,image/webp,image/heic,application/pdf,.jpg,.jpeg,.png,.webp,.heic,.pdf">
      <strong>${esc(t('Dateien auswählen oder hier ablegen', 'Choose files or drop them here'))}</strong>
      <span data-file-summary>${esc(t('Noch keine Dateien ausgewählt.', 'No files selected.'))}</span>
    </label>
    <div class="receipt-import-actions"><label class="check inline"><input data-upload-auto type="checkbox" checked> ${esc(t('Direkt analysieren', 'Analyze immediately'))}</label><button type="button" data-upload>${esc(t('Import starten', 'Start import'))}</button></div>
    <div data-upload-result></div>
  </div>`;
}

function paperlessPane() {
  return `<div class="receipt-import-card receipt-import-step">
    <div class="panel-head">
      <div>
        <div class="receipt-import-step-title"><span>1</span><h3>${esc(t('Paperless-ngx verbinden', 'Connect Paperless-ngx'))}</h3></div>
        <div class="row-sub" data-paperless-state>${esc(t('Verbindung wird geprüft …', 'Checking connection …'))}</div>
      </div>
      <button type="button" class="ghost" data-paperless-test hidden>${esc(t('Testen', 'Test'))}</button>
    </div>
    <div data-paperless-connection-form>
      <div class="receipt-import-grid">
        <label><span>URL</span><input data-paperless-url type="url" placeholder="https://paperless.example.local/"></label>
        <label><span>API Token</span><input data-paperless-token type="password" autocomplete="new-password" placeholder="••••••••"></label>
      </div>
      <div data-paperless-connection-result></div>
    </div>
    <div class="receipt-import-actions">
      <button type="button" class="ghost" data-paperless-edit hidden>${esc(t('Verbindung ändern', 'Edit connection'))}</button>
      <button type="button" data-paperless-save>${esc(t('Verbinden', 'Connect'))}</button>
      <button type="button" class="danger ghost" data-paperless-delete hidden>${esc(t('Trennen', 'Disconnect'))}</button>
    </div>
  </div>
  <div class="receipt-import-card receipt-import-step" data-paperless-selection hidden>
    <div class="receipt-import-step-title"><span>2</span><h3>${esc(t('Dokumente auswählen', 'Select documents'))}</h3></div>
    <p class="row-sub">${esc(t('Filter aus Paperless auswählen und mit UND, ODER, NICHT sowie Klammern kombinieren.', 'Choose Paperless filters and combine them with AND, OR, NOT and parentheses.'))}</p>
    <div class="paperless-filter-builder">
      <div data-paperless-rules></div>
      <div class="paperless-filter-toolbar">
        <button type="button" class="ghost" data-paperless-add-filter>+ ${esc(t('Filter', 'Filter'))}</button>
        <span class="row-sub" data-paperless-options-state></span>
      </div>
      <details class="paperless-advanced-query">
        <summary>${esc(t('Erweiterte Paperless-Suche', 'Advanced Paperless query'))}</summary>
        <label><span>${esc(t('Zusätzliche Suchabfrage', 'Additional query'))}</span><textarea data-paperless-raw rows="2" placeholder='z. B. invoice AND NOT draft'></textarea></label>
      </details>
      <div class="paperless-query-preview"><span>${esc(t('Abfrage', 'Query'))}</span><code data-paperless-query-preview>—</code></div>
    </div>
    <div class="receipt-import-actions">
      <button type="button" class="ghost" data-paperless-preview>${esc(t('Vorschau', 'Preview'))}</button>
      <label class="check inline"><input data-paperless-auto type="checkbox" checked> ${esc(t('Direkt analysieren', 'Analyze immediately'))}</label>
      <button type="button" data-paperless-import>${esc(t('Auswahl importieren', 'Import selection'))}</button>
    </div>
    <div data-paperless-preview-result class="receipt-import-preview"></div>
  </div>`;
}

function folderPane() {
  return `<div class="receipt-import-card">
    <div class="panel-head"><div><h3>${esc(t('Server-/NAS-Importordner', 'Server/NAS import folder'))}</h3><div class="row-sub" data-folder-state>${esc(t('Status wird geprüft …', 'Checking status …'))}</div></div><button type="button" class="ghost" data-folder-preview>${esc(t('Vorschau', 'Preview'))}</button></div>
    <p class="row-sub">${esc(t('Der Pfad wird ausschließlich serverseitig konfiguriert. FullWorth kann keinen beliebigen Serverpfad aus dem Browser öffnen.', 'The path is configured only on the server. FullWorth cannot open arbitrary server paths from the browser.'))}</p>
    <div class="receipt-import-actions"><label class="check inline"><input data-folder-auto type="checkbox" checked> ${esc(t('Direkt analysieren', 'Analyze immediately'))}</label><button type="button" data-folder-import>${esc(t('Ordner importieren', 'Import folder'))}</button></div>
    <div data-folder-preview-result class="receipt-import-preview"></div>
  </div>`;
}

function bindFiles() {
  const input = dialog.querySelector('[data-files]');
  const drop = dialog.querySelector('[data-drop]');
  const update = () => {
    const files = [...(input.files || [])];
    const bytes = files.reduce((sum, file) => sum + file.size, 0);
    dialog.querySelector('[data-file-summary]').textContent = files.length
      ? `${files.length} ${t('Dateien', 'files')} · ${formatBytes(bytes)}`
      : t('Noch keine Dateien ausgewählt.', 'No files selected.');
  };
  input.addEventListener('change', update);
  for (const eventName of ['dragenter', 'dragover']) drop.addEventListener(eventName, event => { event.preventDefault(); drop.classList.add('dragging'); });
  for (const eventName of ['dragleave', 'drop']) drop.addEventListener(eventName, event => { event.preventDefault(); drop.classList.remove('dragging'); });
  drop.addEventListener('drop', event => { if (event.dataTransfer?.files?.length) { input.files = event.dataTransfer.files; update(); } });
  dialog.querySelector('[data-upload]').onclick = async () => {
    const files = [...(input.files || [])];
    if (!files.length) return setBox('[data-upload-result]', t('Bitte Dateien auswählen.', 'Choose files first.'), 'error');
    const form = new FormData();
    files.forEach(file => form.append('receipts', file));
    form.append('currency', currentCurrency());
    form.append('autoStart', String(dialog.querySelector('[data-upload-auto]').checked));
    form.append('clientBatchId', crypto.randomUUID());
    setBusy('[data-upload]', true);
    try {
      const batch = await api('api/purchases/receipt-imports/upload', { method: 'POST', body: form });
      input.value = ''; update(); renderBatchResult('[data-upload-result]', batch); await refreshBatches();
    } catch (error) { setBox('[data-upload-result]', error.message, 'error'); }
    finally { setBusy('[data-upload]', false); }
  };
}

function bindPaperless() {
  dialog.querySelector('[data-paperless-save]').onclick = savePaperlessConnection;
  dialog.querySelector('[data-paperless-delete]').onclick = deletePaperlessConnection;
  dialog.querySelector('[data-paperless-test]').onclick = testPaperless;
  dialog.querySelector('[data-paperless-edit]').onclick = () => setPaperlessConnectionUi(true, true);
  dialog.querySelector('[data-paperless-preview]').onclick = previewPaperless;
  dialog.querySelector('[data-paperless-import]').onclick = importPaperless;
  dialog.querySelector('[data-paperless-add-filter]').onclick = () => addPaperlessRule();
  dialog.querySelector('[data-paperless-raw]').addEventListener('input', updatePaperlessQueryPreview);
}

async function refreshPaperlessConnection() {
  const state = dialog.querySelector('[data-paperless-state]');
  try {
    const connection = await api('api/purchases/receipt-imports/paperless/connection');
    if (!connection?.configured) {
      paperlessConnected = false;
      state.textContent = t('Nicht verbunden', 'Not connected');
      setPaperlessConnectionUi(false);
      return;
    }

    paperlessConnected = true;
    dialog.querySelector('[data-paperless-url]').value = connection.baseUrl || '';
    dialog.dataset.paperlessDefaultQuery = connection.defaultQuery || '';
    const raw = dialog.querySelector('[data-paperless-raw]');
    if (raw && !raw.value.trim() && connection.defaultQuery) raw.value = connection.defaultQuery;
    state.textContent = `${t('Verbunden', 'Connected')} · ${connection.baseUrl}`;
    setPaperlessConnectionUi(true);
    await loadPaperlessOptions();
  } catch (error) {
    state.textContent = error.message;
    setPaperlessConnectionUi(false);
  }
}

function setPaperlessConnectionUi(connected, editing = false) {
  paperlessConnected = connected;
  const form = dialog.querySelector('[data-paperless-connection-form]');
  const selection = dialog.querySelector('[data-paperless-selection]');
  const save = dialog.querySelector('[data-paperless-save]');
  const edit = dialog.querySelector('[data-paperless-edit]');
  const remove = dialog.querySelector('[data-paperless-delete]');
  const test = dialog.querySelector('[data-paperless-test]');
  if (form) form.hidden = connected && !editing;
  if (selection) selection.hidden = !connected;
  if (save) {
    save.hidden = connected && !editing;
    save.textContent = connected ? t('Änderung speichern', 'Save changes') : t('Verbinden', 'Connect');
  }
  if (edit) edit.hidden = !connected || editing;
  if (remove) remove.hidden = !connected;
  if (test) test.hidden = !connected;
}

async function savePaperlessConnection() {
  const url = dialog.querySelector('[data-paperless-url]').value.trim();
  const token = dialog.querySelector('[data-paperless-token]').value.trim();
  const defaultQuery = dialog.dataset.paperlessDefaultQuery || null;
  if (!url || !token) return setBox('[data-paperless-connection-result]', t('URL und API-Token sind erforderlich.', 'URL and API token are required.'), 'error');
  setBusy('[data-paperless-save]', true);
  try {
    const result = await api('api/purchases/receipt-imports/paperless/connection', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ baseUrl: url, apiToken: token, defaultQuery, isEnabled: true })
    });
    dialog.querySelector('[data-paperless-token]').value = '';
    dialog.querySelector('[data-paperless-state]').textContent = `${t('Verbunden', 'Connected')}${result.serverVersion ? ` · ${result.serverVersion}` : ''}`;
    setBox('[data-paperless-connection-result]', t('Verbindung gespeichert.', 'Connection saved.'), 'ok');
    await refreshPaperlessConnection();
  } catch (error) {
    setBox('[data-paperless-connection-result]', error.message, 'error');
  } finally {
    setBusy('[data-paperless-save]', false);
  }
}

async function deletePaperlessConnection() {
  try {
    await api('api/purchases/receipt-imports/paperless/connection', { method: 'DELETE' });
    dialog.querySelector('[data-paperless-url]').value = '';
    dialog.querySelector('[data-paperless-token]').value = '';
    dialog.querySelector('[data-paperless-state]').textContent = t('Nicht verbunden', 'Not connected');
    dialog.querySelector('[data-paperless-rules]').innerHTML = '';
    dialog.querySelector('[data-paperless-preview-result]').innerHTML = '';
    paperlessOptions = { tags: [], documentTypes: [], correspondents: [], storagePaths: [], customFields: [] };
    setPaperlessConnectionUi(false);
  } catch (error) {
    setBox('[data-paperless-connection-result]', error.message, 'error');
  }
}

async function testPaperless() {
  setBusy('[data-paperless-test]', true);
  try {
    const result = await api('api/purchases/receipt-imports/paperless/test', { method: 'POST' });
    dialog.querySelector('[data-paperless-state]').textContent = `${t('Verbindung OK', 'Connection OK')}${result.serverVersion ? ` · ${result.serverVersion}` : ''}`;
  } catch (error) {
    dialog.querySelector('[data-paperless-state]').textContent = error.message;
  } finally {
    setBusy('[data-paperless-test]', false);
  }
}

async function loadPaperlessOptions() {
  const state = dialog.querySelector('[data-paperless-options-state]');
  if (!paperlessConnected) return;
  state.textContent = t('Filter werden geladen …', 'Loading filters …');
  try {
    const result = await api('api/purchases/receipt-imports/paperless/options');
    paperlessOptions = {
      tags: result?.tags || [],
      documentTypes: result?.documentTypes || [],
      correspondents: result?.correspondents || [],
      storagePaths: result?.storagePaths || [],
      customFields: result?.customFields || []
    };
    const count = Object.values(paperlessOptions).reduce((sum, values) => sum + values.length, 0);
    state.textContent = `${count} ${t('Filterwerte geladen', 'filter values loaded')}`;
  } catch (error) {
    state.textContent = t('Filterwerte konnten nicht geladen werden; Texteingabe bleibt möglich.', 'Could not load filter values; text input remains available.');
  }
  if (!dialog.querySelector('[data-paperless-rule]')) addPaperlessRule();
  else dialog.querySelectorAll('[data-paperless-rule]').forEach(renderPaperlessRuleValue);
  updatePaperlessQueryPreview();
}

function addPaperlessRule(initial = {}) {
  const host = dialog.querySelector('[data-paperless-rules]');
  const row = document.createElement('div');
  row.className = 'paperless-filter-rule';
  row.dataset.paperlessRule = '';
  row.innerHTML = `
    <select data-rule-join aria-label="${esc(t('Verknüpfung', 'Join'))}">
      <option value="AND">UND</option>
      <option value="OR">ODER</option>
    </select>
    <label class="paperless-rule-not"><input type="checkbox" data-rule-not> NICHT</label>
    <select data-rule-open aria-label="${esc(t('Öffnende Klammern', 'Opening parentheses'))}">
      <option value="0">(</option><option value="1">( ×1</option><option value="2">( ×2</option><option value="3">( ×3</option>
    </select>
    <select data-rule-field aria-label="${esc(t('Filtertyp', 'Filter field'))}">
      <option value="text">${esc(t('Text / Inhalt', 'Text / content'))}</option>
      <option value="title">${esc(t('Titel', 'Title'))}</option>
      <option value="tag">Tag</option>
      <option value="type">${esc(t('Dokumenttyp', 'Document type'))}</option>
      <option value="correspondent">${esc(t('Korrespondent', 'Correspondent'))}</option>
      <option value="storage_path">${esc(t('Speicherpfad', 'Storage path'))}</option>
      <option value="created">${esc(t('Erstellt am', 'Created'))}</option>
      <option value="added">${esc(t('Hinzugefügt am', 'Added'))}</option>
      <option value="modified">${esc(t('Geändert am', 'Modified'))}</option>
      <option value="custom_fields.name">${esc(t('Benutzerdefiniertes Feld', 'Custom field'))}</option>
      <option value="custom_fields.value">${esc(t('Wert eines benutzerdefinierten Felds', 'Custom field value'))}</option>
      <option value="notes.user">${esc(t('Notiz-Autor', 'Note author'))}</option>
      <option value="notes.note">${esc(t('Notiztext', 'Note text'))}</option>
    </select>
    <div class="paperless-rule-value" data-rule-value-host></div>
    <select data-rule-close aria-label="${esc(t('Schließende Klammern', 'Closing parentheses'))}">
      <option value="0">)</option><option value="1">) ×1</option><option value="2">) ×2</option><option value="3">) ×3</option>
    </select>
    <button type="button" class="icon-button paperless-rule-remove" data-rule-remove aria-label="${esc(t('Filter entfernen', 'Remove filter'))}">×</button>`;

  host.appendChild(row);
  row.querySelector('[data-rule-join]').value = initial.join || 'AND';
  row.querySelector('[data-rule-not]').checked = Boolean(initial.not);
  row.querySelector('[data-rule-open]').value = String(initial.open || 0);
  row.querySelector('[data-rule-field]').value = initial.field || 'text';
  row.querySelector('[data-rule-close]').value = String(initial.close || 0);
  row.dataset.initialValue = initial.value || '';

  row.querySelector('[data-rule-field]').addEventListener('change', () => {
    row.dataset.initialValue = '';
    renderPaperlessRuleValue(row);
    updatePaperlessQueryPreview();
  });
  row.querySelector('[data-rule-join]').addEventListener('change', updatePaperlessQueryPreview);
  row.querySelector('[data-rule-not]').addEventListener('change', updatePaperlessQueryPreview);
  row.querySelector('[data-rule-open]').addEventListener('change', updatePaperlessQueryPreview);
  row.querySelector('[data-rule-close]').addEventListener('change', updatePaperlessQueryPreview);
  row.querySelector('[data-rule-remove]').onclick = () => {
    row.remove();
    if (!host.querySelector('[data-paperless-rule]')) addPaperlessRule();
    updatePaperlessRuleJoins();
    updatePaperlessQueryPreview();
  };

  renderPaperlessRuleValue(row);
  updatePaperlessRuleJoins();
  updatePaperlessQueryPreview();
}

function renderPaperlessRuleValue(row) {
  const field = row.querySelector('[data-rule-field]')?.value || 'text';
  const host = row.querySelector('[data-rule-value-host]');
  if (!host) return;

  const lists = {
    tag: paperlessOptions.tags,
    type: paperlessOptions.documentTypes,
    correspondent: paperlessOptions.correspondents,
    storage_path: paperlessOptions.storagePaths,
    'custom_fields.name': paperlessOptions.customFields
  };
  const values = lists[field];
  const current = row.querySelector('[data-rule-value]')?.value ?? row.dataset.initialValue ?? '';

  if (values?.length) {
    host.innerHTML = `<select data-rule-value><option value="">${esc(t('Auswählen …', 'Choose …'))}</option>${values.map(item => `<option value="${esc(item.name)}">${esc(item.name)}</option>`).join('')}</select>`;
  } else {
    const date = ['created', 'added', 'modified'].includes(field);
    host.innerHTML = `<input data-rule-value type="text" value="${esc(current)}" placeholder="${esc(date ? t('z. B. today, 2026-09-01 oder [2026-01-01 TO 2026-12-31]', 'e.g. today, 2026-09-01 or [2026-01-01 TO 2026-12-31]') : t('Wert', 'Value'))}">`;
  }

  const input = host.querySelector('[data-rule-value]');
  if (input && current) input.value = current;
  if (input) {
    input.addEventListener('input', updatePaperlessQueryPreview);
    input.addEventListener('change', updatePaperlessQueryPreview);
  }
  delete row.dataset.initialValue;
}

function updatePaperlessRuleJoins() {
  const rows = [...dialog.querySelectorAll('[data-paperless-rule]')];
  rows.forEach((row, index) => {
    const join = row.querySelector('[data-rule-join]');
    if (join) {
      join.disabled = index === 0;
      join.classList.toggle('is-first', index === 0);
    }
  });
}

function buildPaperlessQuery() {
  const rows = [...dialog.querySelectorAll('[data-paperless-rule]')];
  const parts = [];
  let balance = 0;

  rows.forEach((row, index) => {
    const field = row.querySelector('[data-rule-field]')?.value || 'text';
    const rawValue = row.querySelector('[data-rule-value]')?.value?.trim() || '';
    if (!rawValue) return;

    const open = Number(row.querySelector('[data-rule-open]')?.value || 0);
    const close = Number(row.querySelector('[data-rule-close]')?.value || 0);
    const join = row.querySelector('[data-rule-join]')?.value === 'OR' ? 'OR' : 'AND';
    const negated = Boolean(row.querySelector('[data-rule-not]')?.checked);
    const term = paperlessQueryTerm(field, rawValue);

    if (parts.length) parts.push(join);
    if (open) parts.push('('.repeat(open));
    balance += open;
    if (negated) parts.push('NOT');
    parts.push(term);
    if (close) {
      balance -= close;
      if (balance < 0) throw new Error(t('Die Klammern im Paperless-Filter sind nicht gültig.', 'The parentheses in the Paperless filter are invalid.'));
      parts.push(')'.repeat(close));
    }
  });

  if (balance !== 0) throw new Error(t('Die Klammern im Paperless-Filter sind nicht ausgeglichen.', 'The parentheses in the Paperless filter are not balanced.'));

  const built = parts.join(' ').replace(/\(\s+/g, '(').replace(/\s+\)/g, ')').trim();
  const raw = dialog.querySelector('[data-paperless-raw]')?.value?.trim() || '';
  if (built && raw) return `(${built}) AND (${raw})`;
  return built || raw || null;
}

function paperlessQueryTerm(field, value) {
  if (field === 'text') return quotePaperlessValue(value);
  if (['created', 'added', 'modified'].includes(field)) {
    const normalized = value.trim();
    if (/^\[.*\]$/.test(normalized)) return `${field}:${normalized}`;
    return `${field}:${quotePaperlessValue(normalized)}`;
  }
  return `${field}:${quotePaperlessValue(value)}`;
}

function quotePaperlessValue(value) {
  const trimmed = value.trim();
  if (!trimmed) return '""';
  if (/^".*"$/.test(trimmed) || /^\[.*\]$/.test(trimmed)) return trimmed;
  if (/^[\p{L}\p{N}_.*?-]+$/u.test(trimmed)) return trimmed;
  return `"${trimmed.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

function updatePaperlessQueryPreview() {
  const target = dialog?.querySelector('[data-paperless-query-preview]');
  if (!target) return;
  try {
    target.textContent = buildPaperlessQuery() || t('Alle Dokumente', 'All documents');
    target.classList.remove('error');
  } catch (error) {
    target.textContent = error.message;
    target.classList.add('error');
  }
}

async function previewPaperless() {
  setBusy('[data-paperless-preview]', true);
  try {
    const preview = await api('api/purchases/receipt-imports/paperless/preview', json({ query: buildPaperlessQuery(), limit: 500 }));
    renderPaperlessPreview(preview);
  } catch (error) {
    setBox('[data-paperless-preview-result]', error.message, 'error');
  } finally {
    setBusy('[data-paperless-preview]', false);
  }
}

function renderPaperlessPreview(preview) {
  const el = dialog.querySelector('[data-paperless-preview-result]');
  const docs = preview?.documents || [];
  if (!docs.length) {
    el.innerHTML = `<div class="row-sub">${esc(t('Keine Dokumente gefunden.', 'No documents found.'))}</div>`;
    return;
  }
  el.innerHTML = `<div class="receipt-import-preview-head"><label class="check inline"><input type="checkbox" data-paperless-all checked> ${esc(t('Alle auswählen', 'Select all'))}</label><span>${preview.count} ${esc(t('gefunden', 'found'))}${preview.truncated ? ` · ${esc(t('Vorschau begrenzt', 'preview limited'))}` : ''}</span></div>
    <div class="receipt-import-docs">${docs.map(doc => `<label class="receipt-import-doc"><input type="checkbox" data-paperless-doc value="${doc.id}" checked><span><strong>${esc(doc.title || `#${doc.id}`)}</strong><small>${esc(paperlessDocumentMeta(doc))}</small></span></label>`).join('')}</div>`;
  el.querySelector('[data-paperless-all]').onchange = event => el.querySelectorAll('[data-paperless-doc]').forEach(box => { box.checked = event.target.checked; });
}

function paperlessDocumentMeta(doc) {
  const bits = [];
  if (doc.created) bits.push(doc.created);
  const type = paperlessOptions.documentTypes.find(x => x.id === doc.documentType);
  if (type?.name) bits.push(type.name);
  const tags = (doc.tags || []).map(id => paperlessOptions.tags.find(x => x.id === id)?.name).filter(Boolean);
  if (tags.length) bits.push(tags.join(', '));
  return bits.join(' · ');
}

async function importPaperless() {
  const selected = [...dialog.querySelectorAll('[data-paperless-doc]:checked')].map(x => Number(x.value)).filter(Number.isFinite);
  if (!selected.length) return setBox('[data-paperless-preview-result]', t('Zuerst Dokumente über die Vorschau auswählen.', 'Preview and select documents first.'), 'error');
  setBusy('[data-paperless-import]', true);
  try {
    const batch = await api('api/purchases/receipt-imports/paperless/import', json({
      filter: { query: buildPaperlessQuery(), limit: 500 },
      documentIds: selected,
      currency: currentCurrency(),
      autoStart: dialog.querySelector('[data-paperless-auto]').checked
    }));
    renderBatchResult('[data-paperless-preview-result]', batch);
    await refreshBatches();
  } catch (error) {
    setBox('[data-paperless-preview-result]', error.message, 'error');
  } finally {
    setBusy('[data-paperless-import]', false);
  }
}

function bindFolder() {
  dialog.querySelector('[data-folder-preview]').onclick = previewFolder;
  dialog.querySelector('[data-folder-import]').onclick = importFolder;
}

async function refreshFolderStatus() {
  const state = dialog.querySelector('[data-folder-state]');
  try {
    const status = await api('api/purchases/receipt-imports/folder/status');
    state.textContent = status.configured
      ? `${t('Konfiguriert', 'Configured')} · ${status.count} ${t('Dateien bereit', 'files ready')} · ${formatBytes(status.totalBytes || 0)}`
      : t('Nicht serverseitig konfiguriert', 'Not configured on server');
    dialog.querySelector('[data-folder-import]').disabled = !status.configured;
    dialog.querySelector('[data-folder-preview]').disabled = !status.configured;
  } catch (error) { state.textContent = error.message; }
}

async function previewFolder() {
  setBusy('[data-folder-preview]', true);
  try {
    const preview = await api('api/purchases/receipt-imports/folder/preview', { method: 'POST' });
    const el = dialog.querySelector('[data-folder-preview-result]');
    el.innerHTML = preview.files?.length
      ? `<div class="receipt-import-preview-head"><span>${preview.count} ${esc(t('Dateien', 'files'))} · ${formatBytes(preview.totalBytes || 0)}</span></div><div class="receipt-import-docs">${preview.files.slice(0, 100).map(file => `<div class="receipt-import-doc"><span>${esc(file)}</span></div>`).join('')}</div>`
      : `<div class="row-sub">${esc(t('Keine stabilen Belegdateien gefunden.', 'No stable receipt files found.'))}</div>`;
  } catch (error) { setBox('[data-folder-preview-result]', error.message, 'error'); }
  finally { setBusy('[data-folder-preview]', false); }
}

async function importFolder() {
  setBusy('[data-folder-import]', true);
  try {
    const batch = await api('api/purchases/receipt-imports/folder/import', json({ currency: currentCurrency(), autoStart: dialog.querySelector('[data-folder-auto]').checked }));
    renderBatchResult('[data-folder-preview-result]', batch); await refreshBatches(); await refreshFolderStatus();
  } catch (error) { setBox('[data-folder-preview-result]', error.message, 'error'); }
  finally { setBusy('[data-folder-import]', false); }
}

async function refreshBatches() {
  if (!dialog?.isConnected) return;
  const el = dialog.querySelector('[data-batches]');
  try {
    const batches = await api('api/purchases/receipt-imports/batches?limit=10');
    if (!batches?.length) { el.innerHTML = `<div class="row-sub">${esc(t('Noch keine Bulk-Importe.', 'No bulk imports yet.'))}</div>`; return; }
    el.innerHTML = batches.map(renderBatch).join('');
    el.querySelectorAll('[data-start-batch]').forEach(button => button.onclick = () => batchAction(button.dataset.startBatch, 'start-pending'));
    el.querySelectorAll('[data-retry-batch]').forEach(button => button.onclick = () => batchAction(button.dataset.retryBatch, 'retry-failed'));
  } catch (error) { el.innerHTML = `<div class="row-sub">${esc(error.message)}</div>`; }
}

function renderBatch(batch) {
  const b = batch.batch || {};
  const source = b.sourceType === 'paperless' ? 'Paperless-ngx' : b.sourceType === 'folder' ? t('Importordner', 'Import folder') : t('Dateien', 'Files');
  return `<div class="receipt-import-batch">
    <div class="receipt-import-batch-main"><strong>${esc(source)}</strong><span>${formatDate(b.createdAt)}</span></div>
    <div class="receipt-import-stats"><span>${batch.total || 0} ${esc(t('gesamt', 'total'))}</span><span>${batch.processing || 0} ${esc(t('läuft', 'processing'))}</span><span>${batch.completed || 0} ${esc(t('fertig', 'done'))}</span><span>${batch.needsReview || 0} ${esc(t('prüfen', 'review'))}</span><span>${batch.skippedDuplicates || 0} ${esc(t('Duplikate', 'duplicates'))}</span><span>${batch.failed || 0} ${esc(t('Fehler', 'failed'))}</span></div>
    <div class="receipt-import-actions compact">${batch.queued ? `<button type="button" class="ghost" data-start-batch="${b.id}">${esc(t('Ausstehende starten', 'Start pending'))}</button>` : ''}${batch.failed ? `<button type="button" class="ghost" data-retry-batch="${b.id}">${esc(t('Fehler erneut', 'Retry failed'))}</button>` : ''}</div>
  </div>`;
}

async function batchAction(id, action) {
  try { await api(`api/purchases/receipt-imports/batches/${id}/${action}`, { method: 'POST' }); await refreshBatches(); }
  catch (error) { alert(error.message); }
}

function renderBatchResult(selector, batch) {
  const text = `${batch.total || 0} ${t('Belege', 'receipts')} · ${batch.queued || 0} ${t('wartend', 'queued')} · ${batch.processing || 0} ${t('läuft', 'processing')} · ${batch.needsReview || 0} ${t('prüfen', 'review')} · ${batch.skippedDuplicates || 0} ${t('Duplikate', 'duplicates')} · ${batch.failed || 0} ${t('Fehler', 'failed')}`;
  setBox(selector, text, batch.failed ? 'error' : 'ok');
}

function selectTab(name) {
  dialog.querySelectorAll('[data-tab]').forEach(button => button.classList.toggle('active', button.dataset.tab === name));
  dialog.querySelectorAll('[data-pane]').forEach(pane => { pane.hidden = pane.dataset.pane !== name; });
  if (name === 'paperless') refreshPaperlessConnection().catch(() => {});
  if (name === 'folder') refreshFolderStatus().catch(() => {});
}

function currentCurrency() { return document.getElementById('user-space-sub')?.textContent?.trim() || 'EUR'; }
function spaceId() { return localStorage.getItem('finance.space'); }

async function api(path, options = {}) {
  const id = spaceId();
  if (!id) throw new Error(t('Kein FullWorth Space ausgewählt.', 'No FullWorth Space selected.'));
  const [base, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', id);
  const response = await fetch(`/bff/backend/${base.replace(/^\//, '')}?${params}`, options);
  if (!response.ok) {
    let message = `${response.status}`;
    try { const body = await response.json(); message = body.error || body.message || body.title || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

function json(body) { return { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function setBusy(selector, busy) { const button = dialog.querySelector(selector); if (button) button.disabled = busy; }
function setBox(selector, message, kind = '') { const el = dialog.querySelector(selector); if (el) el.innerHTML = `<div class="receipt-import-message ${kind}">${esc(message)}</div>`; }
function formatBytes(value) { const bytes = Number(value || 0); if (bytes < 1024) return `${bytes} B`; if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1024 ** 2).toFixed(1)} MB`; }
function formatDate(value) { if (!value) return ''; try { return new Intl.DateTimeFormat(document.documentElement.lang || 'de', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)); } catch { return value; } }
function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char])); }
