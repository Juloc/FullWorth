const exportSelector = '#export-data';
let exporting = false;

function spaceId() { return localStorage.getItem('finance.space') || ''; }
function lang() { return document.documentElement.lang?.startsWith('en') ? 'en' : 'de'; }
function t(de, en) { return lang() === 'en' ? en : de; }
function toast(message) {
  const el = document.querySelector('#toast');
  if (!el) return;
  el.textContent = message;
  el.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.remove('show'), 3200);
}

async function downloadBackup(button) {
  const space = spaceId();
  if (!space || exporting) return;
  exporting = true;
  const oldDisabled = button?.disabled;
  if (button) button.disabled = true;
  try {
    const response = await fetch(`/bff/backend/api/export/wealth-backup?fullWorthSpaceId=${encodeURIComponent(space)}`,
      { headers: { Accept: 'application/zip' }, cache: 'no-store' });
    if (!response.ok) {
      let detail = `${response.status}`;
      try { const body = await response.json(); detail = body.error || body.title || detail; } catch {}
      throw new Error(detail);
    }
    const blob = await response.blob();
    const disposition = response.headers.get('content-disposition') || '';
    const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^";]+)/i);
    const filename = match ? decodeURIComponent(match[1].replace(/^"|"$/g, '')) : `fullworth-backup-${space}-${new Date().toISOString().slice(0,10)}.zip`;
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url; link.download = filename; link.hidden = true;
    document.body.appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast(t('Vollständiges FullWorth-Backup erstellt.', 'Complete FullWorth backup created.'));
  } catch (error) {
    toast(`${t('Backup fehlgeschlagen', 'Backup failed')}: ${error.message || error}`);
  } finally {
    exporting = false;
    if (button) button.disabled = Boolean(oldDisabled);
  }
}

// app.js owns the legacy JSON export click handler. Capture the explicit Export button before that
// bubble handler so the visible action now produces the portable ZIP, while /api/export/snapshot stays
// unchanged for compatibility clients.
document.addEventListener('click', event => {
  const button = event.target.closest(exportSelector);
  if (!button) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  void downloadBackup(button);
}, true);
