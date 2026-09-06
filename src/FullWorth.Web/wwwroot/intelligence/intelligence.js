import { api as sharedApi } from '../core/services.js';
const $ = id => document.getElementById(id);

let overview = null;
let credentials = [];
let providers = [];

async function api(path, init = {}) {
  try {
    return await sharedApi(`api/intelligence/admin${path}`, {
      ...init,
      headers: { Accept: 'application/json', ...(init.body ? { 'Content-Type': 'application/json' } : {}), ...(init.headers || {}) }
    });
  } catch (error) {
    if (error?.status === 401) {
      location.href = `/auth/login?returnUrl=${encodeURIComponent(location.pathname)}`;
      throw new Error('unauthorized');
    }
    if (error?.status === 403) throw Object.assign(new Error('forbidden'), { status: 403, detail: error.detail });
    throw error;
  }
}

function setStatus(text, kind = '') {
  const node = $('intel-status');
  node.textContent = text;
  node.className = `status-pill ${kind}`.trim();
}

function toast(message) {
  const node = $('toast');
  node.textContent = message;
  node.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => node.classList.remove('show'), 2600);
}

function valueOrNull(id) {
  const value = $(id).value.trim();
  return value === '' ? null : Number(value);
}

function safeJson(value) {
  if (!value) return {};
  if (typeof value === 'object') return value;
  try { return JSON.parse(value); } catch { return {}; }
}

function confidenceLabel(value) {
  const percent = Math.round(Number(value || 0) * 100);
  return `${percent}%`;
}

function renderProviderOptions() {
  for (const id of ['ai-provider', 'credential-provider']) {
    const select = $(id);
    const previous = select.value;
    select.replaceChildren();
    for (const descriptor of providers) {
      const option = document.createElement('option');
      option.value = descriptor.provider;
      option.textContent = descriptor.provider;
      select.append(option);
    }
    if ([...select.options].some(x => x.value === previous)) select.value = previous;
  }
}

function renderCredentialOptions(selectedId) {
  const select = $('ai-credential');
  select.replaceChildren(new Option('Keins', ''));
  for (const credential of credentials) {
    const option = new Option(`${credential.name} · ${credential.secretFingerprint}`, credential.id);
    select.append(option);
  }
  select.value = selectedId || '';
}

function renderCredentials() {
  const list = $('credential-list');
  list.replaceChildren();
  if (!credentials.length) {
    const empty = document.createElement('p');
    empty.className = 'row-sub';
    empty.textContent = 'Keine Credentials gespeichert.';
    list.append(empty);
    return;
  }
  for (const credential of credentials) {
    const row = document.createElement('div');
    row.className = 'row';
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = credential.name;
    const meta = document.createElement('div');
    meta.className = 'intel-row-meta';
    meta.textContent = `${credential.provider} · ${credential.secretFingerprint} · Test: ${credential.lastTestSucceeded === true ? 'OK' : credential.lastTestSucceeded === false ? 'Fehlgeschlagen' : 'nicht getestet'}`;
    main.append(title, meta);

    const actions = document.createElement('div');
    actions.className = 'intel-row-actions';
    const test = document.createElement('button');
    test.type = 'button';
    test.className = 'ghost';
    test.textContent = 'Testen';
    test.addEventListener('click', () => testCredential(credential.id));
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'ghost';
    remove.textContent = 'Löschen';
    remove.addEventListener('click', () => deleteCredential(credential.id));
    actions.append(test, remove);
    row.append(main, actions);
    list.append(row);
  }
}

function renderSettings(settings) {
  $('ai-enabled').checked = settings.enabled;
  $('ai-provider').value = settings.provider;
  $('ai-text-model').value = settings.defaultTextModel || '';
  $('ai-vision-model').value = settings.defaultVisionModel || '';
  $('ai-daily-budget').value = settings.dailyBudgetEur ?? '';
  $('ai-monthly-budget').value = settings.monthlyBudgetEur ?? '';
  $('ai-allow-user-credentials').checked = settings.allowUserCredentials;
  $('ai-receipt').checked = settings.receiptAiEnabled;
  $('ai-merchant').checked = settings.merchantAiEnabled;
  $('ai-category').checked = settings.categoryAiEnabled;
  $('ai-contract').checked = settings.contractAiEnabled;
  $('ai-product').checked = settings.productAiEnabled;
  $('ai-logo').checked = settings.logoResearchEnabled;
  $('ai-internet').checked = settings.internetResearchEnabled;
  $('ai-daily').checked = settings.dailyScanEnabled;
  $('ai-weekly').checked = settings.weeklyDeepScanEnabled;
  $('ai-monthly').checked = settings.monthlyReviewEnabled;
  renderCredentialOptions(settings.credentialId);
}

function renderOverview() {
  $('metric-provider').textContent = overview.settings.enabled ? overview.settings.provider : 'Deaktiviert';
  $('metric-suggestions').textContent = String(overview.pendingSuggestions ?? 0);
  $('metric-failed').textContent = String(overview.failedJobs ?? 0);
  $('metric-cost').textContent = `${Number(overview.monthlyCostEur ?? 0).toFixed(2)} €`;
  setStatus(overview.settings.enabled ? 'AI aktiv' : 'AI aus', overview.settings.enabled ? 'ok' : '');
}

function renderSuggestions(suggestions) {
  const list = $('suggestion-list');
  list.replaceChildren();
  if (!suggestions.length) {
    const empty = document.createElement('p');
    empty.className = 'row-sub';
    empty.textContent = 'Keine offenen Vorschläge.';
    list.append(empty);
    return;
  }

  for (const suggestion of suggestions) {
    const payload = safeJson(suggestion.proposedPayloadJson);
    const evidence = safeJson(suggestion.evidenceJson);
    const row = document.createElement('div');
    row.className = 'row intel-suggestion';

    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = suggestion.type === 'merchant-category'
      ? `${suggestion.subjectId} → ${payload.categoryKey || '—'}`
      : `${suggestion.type} · ${suggestion.subjectId}`;

    const meta = document.createElement('div');
    meta.className = 'intel-row-meta';
    const parts = [
      `Confidence ${confidenceLabel(suggestion.confidence)}`,
      payload.direction || null,
      `${suggestion.provider} / ${suggestion.model}`,
      evidence.occurrences ? `${evidence.occurrences} Treffer` : null,
      new Date(suggestion.createdAt).toLocaleString()
    ].filter(Boolean);
    meta.textContent = parts.join(' · ');

    const reason = document.createElement('div');
    reason.className = 'intel-suggestion-reason';
    reason.textContent = payload.evidenceSummary || evidence.evidenceSummary || 'Keine zusätzliche Begründung.';
    main.append(title, meta, reason);

    const actions = document.createElement('div');
    actions.className = 'intel-row-actions';
    const accept = document.createElement('button');
    accept.type = 'button';
    accept.className = 'primary-action';
    accept.textContent = 'Annehmen';
    accept.addEventListener('click', () => reviewSuggestion(suggestion.id, 'accept', accept, reject));
    const reject = document.createElement('button');
    reject.type = 'button';
    reject.className = 'ghost';
    reject.textContent = 'Ablehnen';
    reject.addEventListener('click', () => reviewSuggestion(suggestion.id, 'reject', accept, reject));
    actions.append(accept, reject);
    row.append(main, actions);
    list.append(row);
  }
}

function renderRuns(runs) {
  const list = $('run-list');
  list.replaceChildren();
  if (!runs.length) {
    list.textContent = 'Noch keine AI-Runs.';
    return;
  }
  for (const run of runs) {
    const row = document.createElement('div');
    row.className = 'row';
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = `${run.jobType} · ${run.status}`;
    const sub = document.createElement('div');
    sub.className = 'intel-row-meta';
    sub.textContent = `${run.provider} / ${run.model} · ${new Date(run.startedAt).toLocaleString()} · Tokens ${run.inputTokens ?? '—'} / ${run.outputTokens ?? '—'}`;
    main.append(title, sub);
    row.append(main);
    list.append(row);
  }
}

function renderAudit(events) {
  const list = $('audit-list');
  list.replaceChildren();
  if (!events.length) {
    list.textContent = 'Noch keine Intelligence-Audit-Ereignisse.';
    return;
  }
  for (const event of events) {
    const row = document.createElement('div');
    row.className = 'row';
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = `${event.action} · ${event.outcome}`;
    const sub = document.createElement('div');
    sub.className = 'intel-row-meta';
    sub.textContent = `${event.entityType}${event.entityId ? ` · ${event.entityId}` : ''} · ${new Date(event.occurredAt).toLocaleString()}`;
    main.append(title, sub);
    row.append(main);
    list.append(row);
  }
}

async function reload() {
  try {
    overview = await api('/overview');
    providers = overview.providers || [];
    renderProviderOptions();
    credentials = await api('/credentials');
    renderCredentials();
    renderSettings(overview.settings);
    renderOverview();
    const [suggestions, runs, audit] = await Promise.all([
      api('/suggestions/pending?limit=100'),
      api('/runs?limit=20'),
      api('/audit?limit=30')
    ]);
    renderSuggestions(suggestions);
    renderRuns(runs);
    renderAudit(audit);
    $('intel-denied').hidden = true;
    $('intel-content').hidden = false;
  } catch (error) {
    if (error.status === 403) {
      $('intel-content').hidden = true;
      $('intel-denied').hidden = false;
      setStatus('Kein Zugriff', 'bad');
      return;
    }
    setStatus('Fehler', 'bad');
    toast(error.message || 'Laden fehlgeschlagen');
  }
}

async function saveSettings() {
  const result = $('settings-result');
  result.textContent = 'Speichert…';
  const payload = {
    enabled: $('ai-enabled').checked,
    provider: $('ai-provider').value,
    credentialId: $('ai-credential').value || null,
    allowUserCredentials: $('ai-allow-user-credentials').checked,
    defaultTextModel: $('ai-text-model').value.trim(),
    defaultVisionModel: $('ai-vision-model').value.trim(),
    dailyBudgetEur: valueOrNull('ai-daily-budget'),
    monthlyBudgetEur: valueOrNull('ai-monthly-budget'),
    dailyScanEnabled: $('ai-daily').checked,
    weeklyDeepScanEnabled: $('ai-weekly').checked,
    monthlyReviewEnabled: $('ai-monthly').checked,
    receiptAiEnabled: $('ai-receipt').checked,
    merchantAiEnabled: $('ai-merchant').checked,
    categoryAiEnabled: $('ai-category').checked,
    contractAiEnabled: $('ai-contract').checked,
    productAiEnabled: $('ai-product').checked,
    logoResearchEnabled: $('ai-logo').checked,
    internetResearchEnabled: $('ai-internet').checked
  };
  try {
    const saved = await api('/settings', { method: 'PUT', body: JSON.stringify(payload) });
    overview.settings = saved;
    renderOverview();
    result.textContent = 'Gespeichert.';
    toast('AI-Einstellungen gespeichert');
    await refreshAudit();
  } catch (error) {
    result.textContent = error.message || 'Speichern fehlgeschlagen.';
  }
}

async function addCredential(event) {
  event.preventDefault();
  const secret = $('credential-secret').value;
  try {
    await api('/credentials', {
      method: 'POST',
      body: JSON.stringify({ provider: $('credential-provider').value, name: $('credential-name').value.trim(), secret })
    });
    $('credential-secret').value = '';
    $('credential-name').value = '';
    credentials = await api('/credentials');
    renderCredentials();
    renderCredentialOptions(overview.settings.credentialId);
    toast('Credential gespeichert');
    await refreshAudit();
  } catch (error) {
    $('credential-secret').value = '';
    toast(error.message || 'Credential konnte nicht gespeichert werden');
  }
}

async function testCredential(id) {
  try {
    const result = await api(`/credentials/${id}/test`, { method: 'POST', body: '{}' });
    toast(result.success ? 'Credential funktioniert' : `Test fehlgeschlagen: ${result.errorCode || 'unbekannt'}`);
    credentials = await api('/credentials');
    renderCredentials();
    await refreshAudit();
  } catch (error) {
    toast(error.message || 'Credential-Test fehlgeschlagen');
  }
}

async function deleteCredential(id) {
  if (!confirm('Credential wirklich löschen?')) return;
  try {
    await api(`/credentials/${id}`, { method: 'DELETE' });
    credentials = await api('/credentials');
    renderCredentials();
    if (overview.settings.credentialId === id) overview.settings.credentialId = null;
    renderCredentialOptions(overview.settings.credentialId);
    toast('Credential gelöscht');
    await refreshAudit();
  } catch (error) {
    toast(error.message || 'Löschen fehlgeschlagen');
  }
}

async function reviewSuggestion(id, action, ...buttons) {
  buttons.forEach(button => { button.disabled = true; });
  try {
    await api(`/suggestions/${id}/${action}`, { method: 'POST', body: '{}' });
    toast(action === 'accept' ? 'Vorschlag übernommen' : 'Vorschlag abgelehnt');
    await refreshSuggestions();
    overview = await api('/overview');
    renderOverview();
    await refreshAudit();
  } catch (error) {
    toast(error.message || 'Vorschlag konnte nicht verarbeitet werden');
    buttons.forEach(button => { button.disabled = false; });
  }
}

async function refreshSuggestions() {
  renderSuggestions(await api('/suggestions/pending?limit=100'));
}

async function runSmokeTest() {
  const button = $('run-smoke');
  button.disabled = true;
  $('smoke-result').textContent = 'Läuft…';
  try {
    const result = await api('/jobs/provider-smoke-test/run', {
      method: 'POST',
      body: JSON.stringify({ idempotencyKey: crypto.randomUUID() })
    });
    $('smoke-result').textContent = JSON.stringify(result, null, 2);
    toast('Smoke Test erfolgreich');
    const [suggestions, runs, audit] = await Promise.all([
      api('/suggestions/pending?limit=100'),
      api('/runs?limit=20'),
      api('/audit?limit=30')
    ]);
    renderSuggestions(suggestions);
    renderRuns(runs);
    renderAudit(audit);
  } catch (error) {
    $('smoke-result').textContent = JSON.stringify(error.detail || { error: error.message }, null, 2);
    toast('Smoke Test fehlgeschlagen');
  } finally {
    button.disabled = false;
  }
}

async function refreshAudit() {
  renderAudit(await api('/audit?limit=30'));
}

$('save-settings').addEventListener('click', saveSettings);
$('credential-form').addEventListener('submit', addCredential);
$('run-smoke').addEventListener('click', runSmokeTest);
$('refresh-suggestions').addEventListener('click', refreshSuggestions);
reload();