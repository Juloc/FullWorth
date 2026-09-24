// Sammelbearbeitung der ausgewählten Buchungen (#177).
//
// Der Endpunkt dafür war fertig gebaut und hatte keinen Aufrufer: 452 Zeilen, die niemand erreichen
// konnte. Es gab sogar zwei Maschinen dafür — die ältere ist mit #177 gelöscht, diese ist geblieben,
// weil sie mehr kann und vorsichtiger ist.
//
// Vorsichtiger heißt konkret: der Server bekommt gesagt, wie viele Buchungen der Aufrufer erwartet,
// und lehnt ab, wenn es inzwischen andere sind (409). Das ist kein Formalismus — zwischen dem Öffnen
// des Dialogs und dem Klick auf „Ändern" kann ein Import gelaufen sein. Lieber eine Absage als eine
// Änderung an etwas, das der Benutzer nie gesehen hat.
//
// Zwei Eingaben verlangen eine zweite, ausdrückliche Bestätigung, weil sie nicht umkehrbar sind:
// eine Notiz zu ersetzen (die alte ist weg) und die Auswahl überhaupt anzuwenden.

import { openFormDialog, FieldKind } from '../../components/form-dialog.js';

const de = () => (localStorage.getItem('finance.language')
  || (navigator.language || 'de')).startsWith('de');
const t = (german, english) => (de() ? german : english);

/** Die Aktionen, die der Server kennt. Leer heißt: an den Verträgen nichts ändern. */
const CONTRACT_ACTIONS = ['', 'link', 'unlink'];

/**
 * @param {object} ctx        Der Seitenkontext.
 * @param {string[]} ids      Die ausgewählten Buchungen.
 * @param {() => void} onDone Was nach einer Änderung passiert - die Liste neu zeichnen.
 */
export async function openBulkEdit(ctx, ids, onDone) {
  if (!ids.length) return;

  const [categories, contracts] = await Promise.all([
    ctx.api('api/categories').catch(() => []),
    ctx.api('api/contracts').catch(() => [])
  ]);

  const categoryChoices = [{ value: '', label: t('— unverändert —', '— unchanged —') }]
    .concat(categoryPaths(categories).map(entry => ({ value: entry.id, label: entry.path })));

  const contractChoices = [{ value: '', label: t('— keiner —', '— none —') }]
    .concat((contracts || [])
      .filter(contract => contract.isActive !== false)
      .map(contract => ({ value: contract.id, label: contract.name })));

  const tristate = [
    { value: '', label: t('— unverändert —', '— unchanged —') },
    { value: 'yes', label: t('Ja', 'Yes') },
    { value: 'no', label: t('Nein', 'No') }
  ];

  openFormDialog({
    title: t('Sammelbearbeitung', 'Bulk edit'),
    subtitle: t(`${ids.length} Buchungen`, `${ids.length} transactions`),
    closeLabel: ctx.get('common.close'),
    fallbackError: ctx.get('common.error'),
    create: html => ctx.dialog(html),
    // Die Kategorie ist derselbe lange Baum wie überall sonst (#157).
    comboboxCtx: ctx,
    fields: [
      {
        name: 'categoryId', kind: FieldKind.Select, searchable: true,
        label: ctx.get('transactions.category'), options: categoryChoices
      },
      { name: 'isIgnored', kind: FieldKind.Select, label: t('Ignorieren', 'Ignore'), options: tristate },
      { name: 'isReviewed', kind: FieldKind.Select, label: t('Geprüft', 'Reviewed'), options: tristate },
      {
        name: 'contractAction', kind: FieldKind.Select, advanced: true,
        label: t('Vertrag', 'Contract'),
        options: [
          { value: '', label: t('— unverändert —', '— unchanged —') },
          { value: 'link', label: t('Verknüpfen', 'Link') },
          { value: 'unlink', label: t('Verknüpfung lösen', 'Unlink') }
        ]
      },
      {
        name: 'contractId', kind: FieldKind.Select, advanced: true, searchable: true,
        label: t('Welcher Vertrag', 'Which contract'), options: contractChoices
      },
      {
        name: 'note', kind: FieldKind.Textarea, advanced: true, maxLength: 2000,
        label: t('Notiz ersetzen', 'Replace note'),
        hint: t('Ersetzt die vorhandene Notiz jeder ausgewählten Buchung.',
          'Replaces the existing note on every selected transaction.')
      },
      {
        name: 'replaceNotes', kind: FieldKind.Check, advanced: true,
        label: t('Notiz wirklich ersetzen', 'Really replace the note')
      }
    ],
    advancedLabel: t('Mehr', 'More'),
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'apply', label: t('Ändern', 'Apply'), role: 'primary', submit: true }
    ],
    async onSubmit({ values, setFormError, close }) {
      const body = buildRequest(values, ids);
      if (!hasChange(body)) {
        setFormError(t('Nichts ausgewählt, was geändert werden soll.', 'Nothing to change.'));
        return;
      }

      const beschriftung = summary(body, categoryChoices, contractChoices);
      const bestaetigt = await ctx.confirm(
        t(`${ids.length} Buchungen werden geändert:\n${beschriftung}`,
          `${ids.length} transactions will change:\n${beschriftung}`),
        { title: t('Sammelbearbeitung', 'Bulk edit'), confirmLabel: t('Ändern', 'Apply') });
      if (!bestaetigt) return;

      try {
        await ctx.api('api/transaction-bulk/apply', ctx.jsonBody(body));
      } catch (error) {
        // Der Fehler gehoert neben den Knopf, der ihn ausgeloest hat - nicht in eine Meldung, die
        // den Dialog ueberlebt und dann ohne Zusammenhang dasteht.
        setFormError(message(error));
        return;
      }

      close();
      ctx.toast(t(`${ids.length} Buchungen geändert`, `${ids.length} transactions changed`));
      onDone();
    }
  });
}

/** Aus den Feldern die Anfrage, die der Server erwartet. */
function buildRequest(values, ids) {
  const flag = value => (value === 'yes' ? true : value === 'no' ? false : null);
  const action = CONTRACT_ACTIONS.includes(values.contractAction) ? values.contractAction : '';
  const note = String(values.note ?? '');
  const replaceNotes = Boolean(values.replaceNotes) && note.trim() !== '';

  return {
    transactionIds: ids,
    // Die Zusicherung, auf die der Server besteht: so viele erwarte ich, sonst brich ab.
    expectedCount: ids.length,
    confirmSelection: true,
    updateCategory: Boolean(values.categoryId),
    categoryId: values.categoryId || null,
    isIgnored: flag(values.isIgnored),
    isReviewed: flag(values.isReviewed),
    contractAction: action || null,
    contractId: action === 'link' ? (values.contractId || null) : null,
    replaceNotes,
    note: replaceNotes ? note : null,
    // Das Kästchen IST die zweite Bestätigung - der Server verlangt sie getrennt vom Text.
    confirmReplaceNotes: replaceNotes
  };
}

function hasChange(body) {
  return body.updateCategory || body.isIgnored !== null || body.isReviewed !== null
    || Boolean(body.contractAction) || body.replaceNotes;
}

/** Was gleich passiert, in Worten - eine Zeile je Änderung. */
function summary(body, categoryChoices, contractChoices) {
  const label = (choices, value) => choices.find(choice => choice.value === value)?.label ?? value;
  const lines = [];
  if (body.updateCategory) lines.push(`· ${t('Kategorie', 'Category')}: ${label(categoryChoices, body.categoryId)}`);
  if (body.isIgnored !== null) lines.push(`· ${t('Ignorieren', 'Ignore')}: ${body.isIgnored ? t('Ja', 'Yes') : t('Nein', 'No')}`);
  if (body.isReviewed !== null) lines.push(`· ${t('Geprüft', 'Reviewed')}: ${body.isReviewed ? t('Ja', 'Yes') : t('Nein', 'No')}`);
  if (body.contractAction === 'link') lines.push(`· ${t('Vertrag verknüpfen', 'Link contract')}: ${label(contractChoices, body.contractId)}`);
  if (body.contractAction === 'unlink') lines.push(`· ${t('Vertragsverknüpfung lösen', 'Unlink contract')}`);
  if (body.replaceNotes) lines.push(`· ${t('Notiz ersetzen', 'Replace note')}`);
  return lines.join('\n');
}

/**
 * Die Absagen des Servers in Worten des Benutzers.
 *
 * 409 ist die wichtigste: zwischen Öffnen und Bestätigen hat sich die Auswahl geändert. Ein „das hat
 * nicht funktioniert" würde hier genau das Falsche sagen - es hat funktioniert, es wurde bewusst
 * abgelehnt, und der richtige nächste Schritt ist ein Blick auf die Liste.
 */
function message(error) {
  const status = Number(error?.status ?? 0);
  if (status === 409) {
    return t('Die Auswahl hat sich inzwischen geändert. Bitte die Liste neu ansehen.',
      'The selection changed in the meantime. Review the list again.');
  }
  if (status === 403) return t('Dafür fehlt die Berechtigung.', 'You are not allowed to do that.');
  return error?.body?.error || error?.message || t('Das hat nicht funktioniert.', 'That did not work.');
}

/** Der volle Pfad je Kategorie, damit „Strom" unter „Wohnen" nicht wie ein zweites „Strom" aussieht. */
function categoryPaths(categories) {
  const flat = [];
  (function walk(list, prefix) {
    for (const entry of list || []) {
      if (!entry?.id) continue;
      const path = prefix ? `${prefix} › ${entry.name}` : entry.name;
      flat.push({ id: entry.id, path });
      if (entry.children) walk(entry.children, path);
    }
  })(categories, '');
  return flat.sort((left, right) => left.path.localeCompare(right.path));
}
