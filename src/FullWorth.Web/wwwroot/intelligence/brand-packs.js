const apiBase = '/bff/backend/api/intelligence/admin/brand-packs/custom';
const $ = id => document.getElementById(id);

async function api(path = '', init = {}) {
  const response = await fetch(apiBase + path, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init.headers || {})
    }
  });
  if (response.status === 401) {
    location.href = `/auth/login?returnUrl=${encodeURIComponent(location.pathname)}`;
    throw new Error('unauthorized');
  }
  if (response.status === 403) throw new Error('Nur Instanz-Administratoren können Brand-Packs verwalten.');
  if (!response.ok) {
    let detail = null;
    try { detail = await response.json(); } catch { }
    throw new Error(detail?.error || detail?.message || `HTTP ${response.status}`);
  }
  if (response.status === 204) return null;
  return response.json();
}

function esc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}

function status(message, bad = false) {
  const node = $('brand-pack-result');
  if (!node) return;
  node.textContent = message || '';
  node.className = `intel-result${bad ? ' bad' : message ? ' ok' : ''}`;
}

function render(packs) {
  const list = $('brand-pack-list');
  if (!list) return;
  if (!packs.length) {
    list.innerHTML = '<div class="state-empty"><div class="row-sub">Keine eigenen Brand-Packs installiert.</div></div>';
    return;
  }
  list.innerHTML = packs.map(pack => `
    <div class="row brand-pack-row" data-pack-id="${esc(pack.id)}">
      <div class="row-main">
        <div class="row-title">${esc(pack.name)} <span class="pill">v${esc(pack.version)}</span></div>
        <div class="row-sub">${pack.assetCount} Logos · ${pack.aliasCount} Aliase · Priorität ${pack.priority}</div>
      </div>
      <label class="fw-toggle brand-pack-toggle" title="Pack aktiv">
        <input type="checkbox" data-pack-enabled ${pack.enabled ? 'checked' : ''}>
        <span class="fw-toggle-track"></span>
      </label>
      <button type="button" class="ghost" data-pack-delete>Löschen</button>
    </div>`).join('');

  list.querySelectorAll('[data-pack-id]').forEach(row => {
    const id = row.dataset.packId;
    row.querySelector('[data-pack-enabled]')?.addEventListener('change', async e => {
      e.target.disabled = true;
      try {
        await api(`/${encodeURIComponent(id)}/enabled`, {
          method: 'PUT',
          body: JSON.stringify({ enabled: e.target.checked })
        });
        status(e.target.checked ? 'Brand-Pack aktiviert.' : 'Brand-Pack deaktiviert.');
      } catch (err) {
        e.target.checked = !e.target.checked;
        status(err.message || 'Änderung fehlgeschlagen.', true);
      } finally {
        e.target.disabled = false;
      }
    });

    row.querySelector('[data-pack-delete]')?.addEventListener('click', async () => {
      if (!confirm('Dieses eigene Brand-Pack löschen? Das offizielle FullWorth-Pack bleibt unverändert.')) return;
      try {
        await api(`/${encodeURIComponent(id)}`, { method: 'DELETE' });
        status('Brand-Pack gelöscht.');
        await load();
      } catch (err) {
        status(err.message || 'Löschen fehlgeschlagen.', true);
      }
    });
  });
}

async function load() {
  try {
    render(await api());
  } catch (err) {
    status(err.message || 'Brand-Packs konnten nicht geladen werden.', true);
  }
}

async function importPack() {
  const input = $('brand-pack-file');
  const file = input?.files?.[0];
  if (!file) {
    status('Bitte zuerst eine .json-Datei auswählen.', true);
    return;
  }
  if (file.size > 20 * 1024 * 1024) {
    status('Brand-Pack ist größer als 20 MB.', true);
    return;
  }

  let payload;
  try {
    payload = JSON.parse(await file.text());
  } catch {
    status('Die Datei enthält kein gültiges JSON.', true);
    return;
  }

  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
    status('Ungültiges Brand-Pack-Format.', true);
    return;
  }

  const button = $('brand-pack-import');
  button.disabled = true;
  status('Brand-Pack wird geprüft und installiert…');
  try {
    const imported = await api('', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
    input.value = '';
    status(`${imported.name} v${imported.version} installiert.`);
    await load();
  } catch (err) {
    status(err.message || 'Brand-Pack konnte nicht importiert werden.', true);
  } finally {
    button.disabled = false;
  }
}

$('brand-pack-import')?.addEventListener('click', importPack);
$('brand-pack-refresh')?.addEventListener('click', load);
load();
