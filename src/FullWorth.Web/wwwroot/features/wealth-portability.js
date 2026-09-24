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

/**
 * Eine Sicherung prüfen (#177).
 *
 * `POST /api/import/wealth-backup/validate` stand fertig im Baum und hatte keinen Aufrufer — und
 * davor sogar zweimal, Zeile für Zeile gleich, einmal unter `/api/export` und einmal unter
 * `/api/import`. Übrig ist der eine Weg, und jetzt führt auch einer hin.
 *
 * Geprüft, nicht eingespielt: einen Wiederherstellungs-Endpunkt gibt es in diesem Stand NICHT. Das
 * steht deshalb im Dialog und nicht nur hier — ein Knopf namens „Sicherung“ in einer Anwendung, die
 * nicht zurückspielen kann, ist genau die Art Versprechen, das man erst im Ernstfall prüft.
 *
 * Was der Server nachsieht: Format und Schema-Version des Manifests, ob die Sicherung zu DIESEM
 * Bereich gehört, und für jedes Dokument, ob es im Archiv liegt und sein SHA-256 stimmt. Die Zahl
 * der geprüften Dokumente gehört deshalb ins Ergebnis: „gültig" über null geprüften Dokumenten
 * bedeutet etwas anderes als über zweihundert.
 */
export function openBackupCheckDialog(ctx, { openFormDialog, FieldKind }) {
  const handles = openFormDialog({
    title: t('Sicherung prüfen', 'Check backup'),
    closeLabel: ctx.get('common.close'),
    fallbackError: ctx.get('common.error'),
    fields: [{ name: 'file', kind: FieldKind.Text, label: t('Sicherungsdatei (ZIP)', 'Backup file (ZIP)') }],
    values: { file: '' },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close('cancel') },
      { name: 'check', label: t('Prüfen', 'Check'), role: 'primary', submit: true }
    ],
    onSubmit: ({ setFormError }) => check(setFormError)
  });

  const form = handles.form;
  // form-dialog kennt keine Dateiauswahl. Das Feld hier gegen ein echtes <input type="file"> zu
  // tauschen ist die kleinere Änderung als eine sechste Feldart für einen einzigen Dialog.
  const text = form.elements.namedItem('file');
  const file = document.createElement('input');
  file.type = 'file';
  file.name = 'file';
  file.accept = '.zip,application/zip';
  file.required = true;
  text.replaceWith(file);

  const hint = document.createElement('p');
  hint.className = 'row-sub';
  hint.textContent = t(
    'Geprüft wird Format, Bereich und jede Datei im Archiv. Einspielen kann FullWorth eine Sicherung noch nicht.',
    'Checks format, space and every file in the archive. FullWorth cannot restore a backup yet.');
  file.closest('label')?.after(hint);

  const result = document.createElement('div');
  result.className = 'rows backup-check-result';
  hint.after(result);

  async function check(setFormError) {
    const chosen = file.files?.[0];
    if (!chosen) return setFormError(t('Bitte eine ZIP-Datei wählen.', 'Please choose a ZIP file.'));

    const space = state.space?.id || localStorage.getItem('finance.space') || '';
    result.replaceChildren(line(t('Wird geprüft …', 'Checking …')));
    try {
      const response = await apiClient.backendResponse(
        `api/import/wealth-backup/validate?fullWorthSpaceId=${encodeURIComponent(space)}`,
        { method: 'POST', body: chosen, headers: { 'Content-Type': 'application/zip', Accept: 'application/json' } });
      paint(await response.json());
    } catch (error) {
      result.replaceChildren();
      setFormError(error.message || ctx.get('common.error'));
    }
  }

  function line(title, sub, tone) {
    const row = document.createElement('div');
    row.className = `row${tone ? ` backup-check-${tone}` : ''}`;
    const main = document.createElement('div');
    main.className = 'row-main';
    const head = document.createElement('div');
    head.className = 'row-title';
    head.textContent = title;
    main.append(head);
    if (sub) {
      const detail = document.createElement('div');
      detail.className = 'row-sub';
      detail.textContent = sub;
      main.append(detail);
    }
    row.append(main);
    return row;
  }

  function paint(verdict) {
    const rows = [];
    rows.push(verdict.valid
      ? line(t('Die Sicherung ist in Ordnung.', 'The backup is sound.'),
          t(`${verdict.documentsChecked} Dokument(e) geprüft, Schema ${verdict.schemaVersion ?? '—'}.`,
            `${verdict.documentsChecked} document(s) checked, schema ${verdict.schemaVersion ?? '—'}.`), 'ok')
      : line(t('Die Sicherung ist nicht vollständig.', 'The backup is not complete.'),
          t('Sie lässt sich so nicht als Sicherung verwenden.', 'It cannot be relied on as a backup.'), 'bad'));

    // Fehler und Warnungen kommen vom Server als Text. Sie gehen über textContent, nie über Markup.
    for (const error of verdict.errors || []) rows.push(line(error, null, 'bad'));
    for (const warning of verdict.warnings || []) rows.push(line(warning, null, 'warn'));
    result.replaceChildren(...rows);
  }

  return handles;
}
