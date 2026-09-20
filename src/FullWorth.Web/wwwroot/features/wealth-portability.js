// Der Datenexport. Vermögen und Einstellungen benutzen ihn beide, also gehört er keiner der
// zwei Seiten - eine Seite greift nie in eine andere.
import { apiClient } from '../core/services.js';
import { state } from '../core/state.js';

let exporting = false;

function lang() {
  return document.documentElement.lang?.startsWith('en') ? 'en' : 'de';
}

function t(de, en) {
  return lang() === 'en' ? en : de;
}

/**
 * Eine Datei vom Server holen und im Browser speichern. Das war bis #135 die Sicherung und sonst
 * nichts; mit dem CSV- und dem Excel-Export sind es drei Ziele, die sich nur in Pfad, Dateityp und
 * Meldung unterscheiden - alles andere (Sperre gegen Doppelklick, Dateiname aus content-disposition,
 * Blob-Link, Aufraeumen) ist bei allen dasselbe und steht deshalb genau einmal hier.
 *
 * `exporting` ist bewusst eine Sperre ueber ALLE Ziele: die drei Knoepfe stehen nebeneinander, und
 * zwei gleichzeitig laufende Exporte desselben Bestands waeren nur doppelte Serverarbeit.
 */
async function downloadExport(ctx, button, { path, accept, fallbackName, done, failed }) {
  const space = state.space?.id || localStorage.getItem('finance.space') || '';
  if (!space || exporting) return;

  exporting = true;
  const oldDisabled = button?.disabled;
  if (button) button.disabled = true;

  try {
    const response = await apiClient.backendResponse(
      `${path}?fullWorthSpaceId=${encodeURIComponent(space)}`,
      { headers: { Accept: accept }, cache: 'no-store' });

    const blob = await response.blob();
    // Der Server benennt die Datei selbst; der Rueckfall greift nur, wenn er es einmal nicht tut.
    const disposition = response.headers.get('content-disposition') || '';
    const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^";]+)/i);
    const filename = match
      ? decodeURIComponent(match[1].replace(/^"|"$/g, ''))
      : fallbackName(space, new Date().toISOString().slice(0, 10));

    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);

    ctx.toast(done());
  } catch (error) {
    ctx.toast(`${failed()}: ${error.message || error}`);
  } finally {
    exporting = false;
    if (button) button.disabled = Boolean(oldDisabled);
  }
}

export function downloadWealthBackup(ctx, button) {
  return downloadExport(ctx, button, {
    path: 'api/export/wealth-backup',
    accept: 'application/zip',
    fallbackName: (space, today) => `fullworth-backup-${space}-${today}.zip`,
    done: () => t('Vollständiges FullWorth-Backup erstellt.', 'Complete FullWorth backup created.'),
    failed: () => t('Backup fehlgeschlagen', 'Backup failed')
  });
}

/**
 * #135: der CSV-Export war fertig - am 2026-09-15 sogar noch von zwei N+1-Schleifen befreit - und
 * hatte keinen Knopf. Eine ZIP mit einer Tabelle je Bereich.
 */
export function downloadCsvExport(ctx, button) {
  return downloadExport(ctx, button, {
    path: 'api/export/csv-zip',
    accept: 'application/zip',
    fallbackName: (space, today) => `fullworth-export-${space}-${today}.zip`,
    done: () => t('CSV-Export erstellt.', 'CSV export created.'),
    failed: () => t('CSV-Export fehlgeschlagen', 'CSV export failed')
  });
}

/** #135: dieselben Daten wie der CSV-Export, als eine Arbeitsmappe mit einem Blatt je Bereich. */
export function downloadXlsxExport(ctx, button) {
  return downloadExport(ctx, button, {
    path: 'api/export/xlsx',
    accept: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    fallbackName: (space, today) => `fullworth-export-${space}-${today}.xlsx`,
    done: () => t('Excel-Export erstellt.', 'Excel export created.'),
    failed: () => t('Excel-Export fehlgeschlagen', 'Excel export failed')
  });
}
