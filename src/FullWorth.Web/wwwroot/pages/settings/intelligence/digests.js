// Die Zusammenfassungen der Intelligence-Läufe (#177).
//
// `GET /api/intelligence/digests` und `/{id}` standen fertig im Baum und hatten keinen Aufrufer —
// und anders als die meisten Funde in diesem Issue stand dahinter nicht nur eine leere Tabelle:
// `IntelligenceDigestService` schreibt sie bei jedem wöchentlichen und monatlichen Lauf. Die Daten
// entstanden also seit Monaten und niemand konnte sie sehen.
//
// Gezeigt wird, was drinsteht, und nichts dazugerechnet: Vorschläge (angelegt/offen/angenommen/
// abgelehnt), Lernereignisse, AI-Läufe mit Kosten, und was unaufgelöst liegen blieb. Die Kosten sind
// der Grund, warum diese Seite sie überhaupt braucht — „was hat die KI diesen Monat gekostet" ist
// sonst nur aus dem Überblick abzulesen, und der zeigt nur den laufenden Monat.

import { apiClient } from '../../../core/services.js';

const $ = id => document.getElementById(id);

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

const PERIOD_LABELS = {
  weekly: () => bilingual('Woche', 'Week'),
  monthly: () => bilingual('Monat', 'Month')
};

function money(value) {
  const number = Number(value) || 0;
  return new Intl.NumberFormat(
    (document.documentElement.lang || '').startsWith('en') ? 'en-US' : 'de-DE',
    { style: 'currency', currency: 'EUR' }).format(number);
}

/** Alles, was in EINER Zeile Platz hat - der Rest steht im Aufklappbereich. */
function headline(summary) {
  const suggestions = summary?.suggestions || {};
  const ai = summary?.ai || {};
  const parts = [];
  if (Number(suggestions.created) > 0)
    parts.push(bilingual(`${suggestions.created} Vorschläge`, `${suggestions.created} suggestions`));
  if (Number(ai.runs) > 0)
    parts.push(bilingual(`${ai.runs} AI-Läufe`, `${ai.runs} AI runs`));
  const cost = Number(ai.estimatedOrActualCostEur) || 0;
  if (cost > 0) parts.push(money(cost));
  return parts.length ? parts.join(' · ') : bilingual('Nichts passiert.', 'Nothing happened.');
}

function detailRows(summary) {
  const suggestions = summary?.suggestions || {};
  const learning = summary?.learning || {};
  const ai = summary?.ai || {};
  const unresolved = summary?.unresolved || {};
  return [
    [bilingual('Vorschläge offen', 'Suggestions pending'), suggestions.pending],
    [bilingual('Vorschläge angenommen', 'Suggestions accepted'), suggestions.accepted],
    [bilingual('Vorschläge abgelehnt', 'Suggestions rejected'), suggestions.rejected],
    [bilingual('Lernereignisse', 'Learning events'), learning.feedbackEvents],
    [bilingual('davon für die Cloud', 'of those cloud-eligible'), learning.cloudEligible],
    [bilingual('AI-Läufe erfolgreich', 'AI runs succeeded'), ai.succeeded],
    [bilingual('AI-Läufe fehlgeschlagen', 'AI runs failed'), ai.failed],
    [bilingual('Offene Artikel', 'Unresolved items'), unresolved.purchaseItems],
    [bilingual('Offene Belege', 'Unresolved receipts'), unresolved.receipts]
  ].filter(([, value]) => value != null);
}

export async function renderIntelligenceDigests() {
  const host = $('digest-list');
  if (!host) return;

  let rows;
  try {
    rows = await apiClient.backend('api/intelligence/digests?limit=12', { headers: { Accept: 'application/json' } });
  } catch {
    host.replaceChildren(text(bilingual('Konnte nicht geladen werden.', 'Could not be loaded.')));
    return;
  }

  if (!Array.isArray(rows) || !rows.length) {
    // Kein Fehler: die Zusammenfassungen entstehen in den geplanten Läufen, und vor dem ersten gibt
    // es eben keine. Das gehört gesagt, sonst liest sich die leere Liste wie ein Defekt.
    host.replaceChildren(text(bilingual(
      'Noch keine Zusammenfassung. Sie entsteht mit dem ersten wöchentlichen oder monatlichen Lauf.',
      'No digest yet. The first weekly or monthly run creates one.')));
    return;
  }

  host.replaceChildren(...rows.map(row => {
    const summary = row.summary || {};
    const details = document.createElement('details');
    details.className = 'row intel-digest';

    const label = (PERIOD_LABELS[row.periodType] || (() => row.periodType))();
    const head = document.createElement('summary');
    head.innerHTML = '';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = `${label} ${row.periodKey}`;
    const sub = document.createElement('div');
    sub.className = 'intel-row-meta';
    sub.textContent = headline(summary);
    head.append(title, sub);
    details.append(head);

    const list = document.createElement('div');
    list.className = 'intel-digest-detail';
    for (const [name, value] of detailRows(summary)) {
      const line = document.createElement('div');
      line.className = 'intel-row-meta';
      line.textContent = `${name}: ${value}`;
      list.append(line);
    }
    details.append(list);
    return details;
  }));
}

function text(message) {
  const element = document.createElement('p');
  element.className = 'row-sub';
  element.textContent = message;
  return element;
}
