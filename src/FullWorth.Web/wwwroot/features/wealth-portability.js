import { apiClient } from '../core/services.js';
import { state } from '../core/state.js';

let exporting = false;

function lang() {
  return document.documentElement.lang?.startsWith('en') ? 'en' : 'de';
}

function t(de, en) {
  return lang() === 'en' ? en : de;
}

export async function downloadWealthBackup(ctx, button) {
  const space = state.space?.id || localStorage.getItem('finance.space') || '';
  if (!space || exporting) return;

  exporting = true;
  const oldDisabled = button?.disabled;
  if (button) button.disabled = true;

  try {
    const response = await apiClient.backendResponse(
      `api/export/wealth-backup?fullWorthSpaceId=${encodeURIComponent(space)}`,
      { headers: { Accept: 'application/zip' }, cache: 'no-store' });

    const blob = await response.blob();
    const disposition = response.headers.get('content-disposition') || '';
    const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^";]+)/i);
    const filename = match
      ? decodeURIComponent(match[1].replace(/^"|"$/g, ''))
      : `fullworth-backup-${space}-${new Date().toISOString().slice(0,10)}.zip`;

    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);

    ctx.toast(t('Vollständiges FullWorth-Backup erstellt.', 'Complete FullWorth backup created.'));
  } catch (error) {
    ctx.toast(`${t('Backup fehlgeschlagen', 'Backup failed')}: ${error.message || error}`);
  } finally {
    exporting = false;
    if (button) button.disabled = Boolean(oldDisabled);
  }
}
