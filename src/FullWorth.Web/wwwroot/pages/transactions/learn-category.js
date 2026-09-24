// „Immer so kategorisieren" (#177).
//
// `POST /api/category-intelligence/learn` stand fertig im Baum und hatte keinen Aufrufer — und das
// war der teuerste der Funde in diesem Issue, weil er nicht nur eine Oberfläche fehlte, sondern
// einen ganzen Weg: eine bestätigte Händler-zu-Kategorie-Zuordnung.
//
// Der Endpunkt kann drei Dinge, und der Unterschied ist keiner der Bequemlichkeit:
//
//   one       Nur diese Buchung. Dasselbe wie die Auswahl im Detail, also hier nicht angeboten.
//   existing  Alle bisherigen desselben Händlers in derselben Richtung.
//   future    Dazu eine Regel, damit künftige gleich richtig landen — und NUR in diesem Fall meldet
//             der Server die Zuordnung an die Cloud weiter. „REWE ist Lebensmittel" gilt für jeden;
//             „diese eine Buchung gehört zu Urlaub" gilt nur hier. Das ist die Grenze, an der die
//             Auswahl hängt, und sie steht deshalb auch im Dialog.
//
// Richtung heisst Vorzeichen: Ausgaben und Einnahmen desselben Händlers sind zwei Fälle (Gehalt vs.
// Rückzahlung), und der Server trennt sie. Der Dialog sagt das nicht extra — er nennt den Händler,
// und die Buchung, von der aus man ihn öffnet, IST die Richtung.

import { openFormDialog, FieldKind } from '../../components/form-dialog.js';

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

/**
 * @param {object} ctx           Seitenkontext.
 * @param {object} transaction   Die Buchung, von der gelernt wird.
 * @param {string} categoryId    Die Kategorie, die gelten soll.
 * @param {string} categoryName  Ihr Name, nur zum Anzeigen.
 * @param {Function} onDone      Nach einem erfolgreichen Lauf.
 */
export function openLearnCategory(ctx, transaction, categoryId, categoryName, onDone) {
  const merchant = (transaction.counterparty || transaction.normalizedCounterparty || '').trim();

  openFormDialog({
    title: ctx.get('transactions.learnTitle'),
    closeLabel: ctx.get('common.close'),
    fallbackError: ctx.get('common.error'),
    create: html => ctx.dialog(html),
    fields: [{
      name: 'scope',
      kind: FieldKind.Select,
      label: ctx.get('transactions.learnScope'),
      options: [
        { value: 'future', label: ctx.get('transactions.learnFuture') },
        { value: 'existing', label: ctx.get('transactions.learnExisting') }
      ],
      hint: bilingual(
        `Gilt für „${merchant}" in dieser Richtung, mit der Kategorie „${categoryName}".`,
        `Applies to "${merchant}" in this direction, with the category "${categoryName}".`)
    }],
    values: { scope: 'future' },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'apply', label: ctx.get('common.apply'), role: 'primary', submit: true }
    ],
    onSubmit: async ({ values, setFormError, close }) => {
      try {
        const result = await ctx.api('api/category-intelligence/learn', ctx.jsonBody({
          transactionId: transaction.id,
          categoryId,
          scope: values.scope
        }));
        close();
        // Die Zahl gehört dazu: "künftig so" allein sagt nicht, dass gerade 34 Buchungen
        // umkategorisiert wurden - und genau das ist passiert.
        ctx.toast(bilingual(
          `${result?.affected ?? 0} Buchung(en) angepasst.`,
          `${result?.affected ?? 0} transaction(s) updated.`));
        await onDone?.();
      } catch (error) {
        setFormError(error.message || ctx.get('common.error'));
      }
    }
  });
}

/** Ohne Händler kann der Server nichts lernen - dann gibt es den Knopf gar nicht erst. */
export function canLearnFrom(transaction) {
  return Boolean((transaction?.counterparty || transaction?.normalizedCounterparty || '').trim());
}
