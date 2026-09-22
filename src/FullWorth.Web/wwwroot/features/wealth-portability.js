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
 * Eine Datei vom Server holen und im Browser speichern. Fuenf Ziele unterscheiden sich nur in Pfad,
 * Dateityp und Meldung - alles andere (Sperre gegen Doppelklick, Dateiname aus content-disposition,
 * Blob-Link, Aufraeumen) ist bei allen dasselbe und steht deshalb genau einmal hier.
 *
 * `exporting` ist bewusst eine Sperre ueber ALLE Ziele: zwei gleichzeitig laufende Exporte desselben
 * Bestands waeren nur doppelte Serverarbeit.
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

/**
 * Alle fünf Exporte an einem Ort (#135).
 *
 * Vorher standen drei Zeilen nebeneinander in den Einstellungen, und zwei weitere Routen —
 * `export/wealth-full` und `export/snapshot` — hatten gar keinen Knopf. Fünf Zeilen für im Kern
 * dieselben Daten wären die falsche Antwort gewesen; es sind nicht fünf Exporte, sondern zwei
 * Fragen: **was** und **in welcher Form**.
 *
 * Wiederherstellbar ist nur die Sicherung. Das steht als Hinweis unter der Auswahl und nicht im
 * Kleingedruckten: wer eine Tabelle zieht und glaubt, ein Backup zu haben, merkt es erst, wenn er
 * es braucht.
 */
const EXPORT_TARGETS = {
  'backup:zip': {
    path: 'api/export/wealth-backup', accept: 'application/zip', extension: 'zip',
    done: () => t('Vollständiges FullWorth-Backup erstellt.', 'Complete FullWorth backup created.'),
    failed: () => t('Backup fehlgeschlagen', 'Backup failed')
  },
  'backup:json': {
    path: 'api/export/wealth-full', accept: 'application/json', extension: 'json',
    done: () => t('Sicherung als JSON erstellt.', 'Backup created as JSON.'),
    failed: () => t('Export fehlgeschlagen', 'Export failed')
  },
  'tables:csv': {
    path: 'api/export/csv-zip', accept: 'application/zip', extension: 'zip',
    done: () => t('CSV-Export erstellt.', 'CSV export created.'),
    failed: () => t('CSV-Export fehlgeschlagen', 'CSV export failed')
  },
  'tables:xlsx': {
    path: 'api/export/xlsx', accept: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    extension: 'xlsx',
    done: () => t('Excel-Export erstellt.', 'Excel export created.'),
    failed: () => t('Excel-Export fehlgeschlagen', 'Excel export failed')
  },
  'tables:json': {
    path: 'api/export/snapshot', accept: 'application/json', extension: 'json',
    done: () => t('Export als JSON erstellt.', 'Export created as JSON.'),
    failed: () => t('Export fehlgeschlagen', 'Export failed')
  }
};

const EXPORT_FORMATS = {
  backup: [['zip', 'ZIP'], ['json', 'JSON']],
  tables: [['csv', 'CSV'], ['xlsx', 'Excel'], ['json', 'JSON']]
};

export function openExportDialog(ctx, { openFormDialog, FieldKind }) {
  const whatOptions = [
    { value: 'backup', label: t('Alles – vollständige Sicherung', 'Everything – complete backup') },
    { value: 'tables', label: t('Tabellen – Buchungen, Konten, Käufe', 'Tables – transactions, accounts, purchases') }
  ];
  const formatOptions = what => EXPORT_FORMATS[what].map(([value, label]) => ({ value, label }));

  const handles = openFormDialog({
    title: t('Daten exportieren', 'Export data'),
    closeLabel: ctx.get('common.close'),
    fields: [
      { name: 'what', kind: FieldKind.Select, label: t('Was?', 'What?'), options: whatOptions },
      { name: 'format', kind: FieldKind.Select, label: t('Format', 'Format'), options: formatOptions('backup') }
    ],
    values: { what: 'backup', format: 'zip' },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close('cancel') },
      { name: 'download', label: t('Herunterladen', 'Download'), role: 'primary', submit: true }
    ],
    onSubmit: ({ values, close }) => {
      close('download');
      const target = EXPORT_TARGETS[`${values.what}:${values.format}`];
      if (!target) return;
      void downloadExport(ctx, null, {
        ...target,
        fallbackName: (space, today) =>
          `fullworth-${values.what === 'backup' ? 'backup' : 'export'}-${space}-${today}.${target.extension}`
      });
    }
  });

  // Die Formate hängen an der Auswahl: eine Sicherung gibt es nicht als Excel, eine Tabelle nicht
  // als wiederherstellbares ZIP. Statt fünf Zeilen mit gleichem Gewicht führt die erste Frage die
  // zweite.
  const form = handles.form;
  const what = form.elements.namedItem('what');
  const format = form.elements.namedItem('format');
  const hint = document.createElement('p');
  hint.className = 'row-sub';
  const paint = () => {
    const options = formatOptions(what.value);
    format.replaceChildren(...options.map(option => new Option(option.label, option.value)));
    hint.textContent = what.value === 'backup'
      ? t('Nur die Sicherung lässt sich wieder einspielen.', 'Only the backup can be restored.')
      : t('Zum Weiterverarbeiten – nicht wiederherstellbar.', 'For further processing – not restorable.');
  };
  what.addEventListener('change', paint);
  paint();
  format.closest('label')?.after(hint);
  return handles;
}
