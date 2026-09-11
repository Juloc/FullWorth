// Altersvorsorge — the Dokumente tab (step 2 of docs/PENSION.md): upload → review → commit.
//
// Mounted by features/pension.js as its fourth tab, so this module never registers a view or a route
// of its own. The shared pension copy (field labels, routes, statuses, cost kinds …) is handed in as
// `t` / `label` instead of being imported: pension.js already imports this file, and importing back
// would make the pair circular. Only the strings that belong to the document flow live here, in DOC_T.
//
// Three rules decide how this screen looks, and all three are the point of the step:
//
//   1. Nothing is stored until the user commits. The pipeline only ever produces a draft, so the
//      screen says so in words, once, and the commit is the only button that writes anything.
//   2. Every value says where it came from. `provenance` carries the page and the confidence per
//      dotted field path; a value that was OCR'd is *erkannt*, not *gelesen*, and that state sits on
//      the field itself. A field listed in `unresolved` is rendered EMPTY and asks — never pre-filled
//      with a guess, because a guessed number that looks extracted is worse than a gap.
//   3. A guarantee is not a projection. The guaranteed and the projected figures get separate blocks,
//      the projection is labelled as an assumption and carries its return, and no projected number is
//      ever rendered next to the balance. The database refuses the same mix with a check constraint;
//      undoing that on screen would defeat it.
//
// It is a PAGE, not a dialog: docs/UI_AUDIT.md measured the existing dialogs as the app's main
// usability problem, and a review form of this size in a dialog would be the worst of them. The only
// dialog here is the one-sentence commit confirmation.
import { sectionCard, esc } from '../ui/ux-kit.js';

// --- injected by pension.js ---
let ctx = null;
let host = null;
let contracts = [];
let sharedT = null;
let sharedLabel = null;
let reloadArea = null;

// --- module state ---
// 'list' shows upload + the documents; 'review' shows one document's draft; 'failed' explains why a
// document produced nothing; 'done' is the read-only view of an already committed document.
let screen = { mode: 'list' };
let documents = [];
let listError = null;
let loading = false;
// Upload feedback lives outside `screen` so it survives a repaint of the list.
let upload = null;
// The last commit result, shown above the list until dismissed: what it wrote AND what it skipped.
let lastResult = null;

const DOC_KINDS = ['annual_statement', 'certificate', 'offer', 'correspondence', 'other'];
const ROUTES = ['direct_insurance', 'pension_fund', 'pension_scheme', 'provident_fund', 'direct_commitment', 'other'];
const STATUSES = ['active', 'paid_up', 'in_payout', 'transferred', 'terminated'];
const CYCLES = ['monthly', 'quarterly', 'semiannual', 'yearly', 'one_off'];
const PROJECTION_BASES = ['document_forecast', 'document_guaranteed', 'simulation'];
const COST_KINDS = [
  'acquisition', 'administration_on_contribution', 'administration_on_capital', 'administration_fixed',
  'fund', 'guarantee', 'risk_premium', 'payout', 'other'
];
const COST_BASES = ['fixed_amount', 'percent_of_contribution', 'percent_of_capital', 'percent_of_sum', 'percent_of_annuity'];
const COST_TIMINGS = ['ongoing', 'incurred', 'future'];
const ASSET_CLASSES = ['equity', 'bond', 'mixed', 'money_market', 'real_estate', 'commodity', 'guarantee_assets', 'other'];

const MAX_BYTES = 12 * 1024 * 1024;
const ACCEPT = 'application/pdf,image/png,image/jpeg,image/webp,.pdf,.png,.jpg,.jpeg,.webp';

const DOC_T = {
  de: {
    uploadTitle: 'Dokument einlesen',
    uploadHint: 'Standmitteilung, Versicherungsschein oder ein Foto davon. PDF oder Bild, bis 12 MB. Die Datei wird sofort gelesen – gespeichert wird erst, wenn du die Werte geprüft und übernommen hast.',
    uploadKind: 'Art des Dokuments',
    drop: 'Datei auswählen oder hier ablegen',
    uploading: 'Datei wird übertragen und gelesen …',
    tooLarge: 'Die Datei ist größer als 12 MB. Bitte ein kleineres PDF oder Bild wählen.',
    duplicate: 'Diese Datei ist hier schon eingelesen. Nichts wurde doppelt angelegt.',
    toExisting: 'zum vorhandenen Dokument',
    listTitle: 'Eingelesene Dokumente',
    listEmpty: 'Noch kein Dokument eingelesen. Lies eine Standmitteilung ein – die Werte landen zuerst in einer Prüfung und nicht in deinen Verträgen.',
    pages: '{count} Seiten',
    pagesOne: '1 Seite',
    quality: 'Erkennungsgüte {percent} %',
    qualityHint: 'Die Güte sagt, wie sicher die Erkennung war – nicht, dass die Werte stimmen.',
    read: 'gelesen',
    recognised: 'erkannt',
    ocrBadge: 'Texterkennung',
    aiBadge: 'KI-Vorschlag',
    ocrNotice: 'Dieses Dokument hatte keine Textschicht. Alle Werte sind erkannt, nicht gelesen – Zahlen bitte besonders prüfen.',
    review: 'Prüfen',
    back: 'Zurück zur Liste',
    open: 'Original öffnen',
    del: 'Dokument löschen',
    delConfirm: 'Dieses Dokument und seine gelesenen Werte löschen? Übernommene Verträge und Stände bleiben.',
    deleted: 'Dokument gelöscht',
    nothingSaved: 'Noch ist nichts gespeichert.',
    nothingSavedHint: 'Diese Werte stehen nur hier. Vertrag, Stand, Beiträge und Kosten entstehen erst, wenn du unten „Werte übernehmen“ drückst. „Eingaben zwischenspeichern“ merkt sich nur deine Korrekturen an diesem Dokument.',
    saveDraft: 'Eingaben zwischenspeichern',
    draftSaved: 'Eingaben gemerkt – noch nichts übernommen',
    commit: 'Werte übernehmen',
    commitConfirm: 'Vertrag, Stand, Beiträge und Kosten jetzt mit diesen Werten anlegen?',
    commitConfirmLabel: 'Übernehmen',
    committed: 'Werte übernommen',
    warnings: 'Bitte prüfen',
    origins: 'Herkunft der Werte',
    page: 'Seite {page}',
    noPage: 'ohne Seitenangabe',
    confidence: 'Sicherheit {percent} %',
    unresolvedOrigin: 'Nicht gefunden – bitte selbst eintragen.',
    noOrigin: 'Ohne Quelle im Dokument.',
    unresolvedCount: '{count} Feld konnte nicht gelesen werden und ist leer geblieben.',
    unresolvedCountPlural: '{count} Felder konnten nicht gelesen werden und sind leer geblieben.',
    blockContract: 'Vertrag',
    blockSnapshot: 'Stand zum Stichtag',
    blockSnapshotHint: 'Das ist Geld, das heute da ist. Kein prognostizierter Wert gehört in diesen Block.',
    blockGuarantee: 'Garantie zum Rentenbeginn',
    blockGuaranteeHint: 'Zugesagte Werte. Sie stehen absichtlich getrennt von der Prognose.',
    blockProjection: 'Prognose zum Rentenbeginn – eine Annahme',
    blockProjectionHint: 'Eine Prognose ist keine Garantie und kein heutiges Vermögen. Sie wird nur mit ihrer angenommenen Rendite gespeichert; ohne Rendite lehnt der Server sie ab.',
    blockContribution: 'Beitrag',
    blockContributionHint: 'Eigenanteil, AG-Zuschuss und arbeitgeberfinanzierter Anteil bleiben drei Zahlen. Nur der Eigenanteil verlässt dein Netto.',
    blockAllocations: 'Fonds & ETFs',
    blockCosts: 'Kosten',
    addContribution: 'Beitrag aus dem Dokument ergänzen',
    partsTotal: 'Summe der Anteile',
    statedMismatch: 'Die Summe der drei Anteile ({parts}) weicht vom Gesamtbeitrag laut Dokument ({stated}) ab. Beides bleibt so stehen und wird nicht korrigiert.',
    take: 'Übernehmen',
    rowDropped: 'Diese Zeile wird nicht übernommen.',
    policyFound: 'Im Dokument stand eine Versicherungsnummer. Sie wird nie angezeigt und bleibt hinterlegt, wenn das Feld leer bleibt.',
    policyHint: 'Nur ausfüllen, wenn du die Nummer ergänzen oder ersetzen willst.',
    matchTitle: 'Zuordnung',
    matched: 'Passender Vertrag gefunden: {contract}.',
    matchedOn: 'Erkannt über {rule}.',
    matchConfirm: 'Eine Zuordnung gilt erst, wenn du sie hier bestätigst – automatisch passiert nichts.',
    assignMatched: 'diesem Vertrag zuordnen',
    assignOther: 'einem anderen Vertrag zuordnen',
    assignExisting: 'einem vorhandenen Vertrag zuordnen',
    createContract: 'neuen Vertrag anlegen',
    unmatched: 'Kein passender Vertrag gefunden.',
    unmatchedHint: 'Lege einen neuen Vertrag an – oder ordne das Dokument selbst einem vorhandenen zu.',
    pickTarget: 'Bitte zuerst eine Zuordnung wählen.',
    chooseContract: 'Vertrag',
    includeInNetWorth: 'In Gesamtvermögen einbeziehen',
    resultTitle: 'Das ist übernommen worden',
    applied: 'Angelegt',
    skipped: 'Nicht angelegt',
    nothingApplied: 'Nichts angelegt.',
    nothingSkipped: 'Nichts übersprungen.',
    skippedHint: 'Übersprungen heißt nicht fehlgeschlagen: ein Stand, den es für dieses Datum schon gibt, wird ergänzt und nie überschrieben.',
    dismiss: 'Schließen',
    failedTitle: 'Aus diesem Dokument ließ sich nichts lesen',
    failedHint: 'Du kannst die Werte weiterhin von Hand erfassen – dafür braucht die App keine Texterkennung.',
    waiting: 'Das Dokument wird noch gelesen. Lade die Liste in einem Moment neu.',
    refresh: 'Liste neu laden',
    committedTitle: 'Bereits übernommen',
    committedHint: 'Die Werte dieses Dokuments stehen in deinen Verträgen. Die gelesene Zwischenfassung wird danach gelöscht, damit dieselben Zahlen nicht an zwei Stellen liegen.',
    kinds: {
      annual_statement: 'Standmitteilung', certificate: 'Versicherungsschein',
      offer: 'Angebot', correspondence: 'Schreiben', other: 'Sonstiges'
    },
    states: {
      pending: 'Wird gelesen', parsed: 'Bereit zur Prüfung', reviewed: 'Geprüft, nicht übernommen',
      committed: 'Übernommen', failed: 'Nicht lesbar'
    },
    errors: {
      no_text: 'Im Dokument war kein Text zu finden, und auch die Texterkennung hat nichts geliefert.',
      tool_missing: 'Auf diesem Server fehlt das Werkzeug für die Texterkennung.',
      unsupported: 'Dieses Dateiformat kann nicht gelesen werden.',
      too_large: 'Die Datei war zu groß, um gelesen zu werden.'
    },
    rules: {
      policy_number: 'die Versicherungsnummer',
      provider_tariff_employer: 'Anbieter, Tarif und Arbeitgeber',
      provider_retirement_date: 'Anbieter und Rentenbeginn'
    },
    results: {
      contract_created: 'Vertrag angelegt', contract_matched: 'Vertrag zugeordnet',
      contract: 'Vertrag', snapshot: 'Stand', contribution: 'Beitrag',
      allocations: 'Fonds', costs: 'Kosten', document: 'Dokument',
      snapshot_exists: 'Stand war für dieses Datum bereits vorhanden',
      contribution_exists: 'Beitrag war für dieses Datum bereits vorhanden',
      // Nothing was written because a date was missing - said plainly, because the user is the one
      // who can supply it.
      snapshot_no_figure: 'Kein Wert im Dokument gefunden – nichts gespeichert',
      snapshot_no_date: 'Ohne Stichtag kein Stand – Datum nachtragen',
      contribution_no_date: 'Ohne Datum kein Beitrag – „gültig ab" nachtragen',
      allocations_no_date: 'Ohne Stichtag keine Fonds – Datum nachtragen',
      costs_no_date: 'Ohne Stichtag keine Kosten – Datum nachtragen'
    }
  },
  en: {
    uploadTitle: 'Read a document',
    uploadHint: 'An annual statement, a policy certificate or a photo of one. PDF or image, up to 12 MB. The file is read straight away — nothing is stored until you have checked the values and committed them.',
    uploadKind: 'Kind of document',
    drop: 'Choose a file or drop it here',
    uploading: 'Uploading and reading the file …',
    tooLarge: 'The file is larger than 12 MB. Please pick a smaller PDF or image.',
    duplicate: 'This file has already been read here. Nothing was created twice.',
    toExisting: 'go to the existing document',
    listTitle: 'Documents read',
    listEmpty: 'No document yet. Read an annual statement — the values land in a review first, not in your contracts.',
    pages: '{count} pages',
    pagesOne: '1 page',
    quality: 'Extraction quality {percent} %',
    qualityHint: 'Quality says how sure the extraction was — not that the values are right.',
    read: 'read',
    recognised: 'recognised',
    ocrBadge: 'OCR',
    aiBadge: 'AI suggestion',
    ocrNotice: 'This document had no text layer. Every value was recognised rather than read — check the numbers carefully.',
    review: 'Review',
    back: 'Back to the list',
    open: 'Open the original',
    del: 'Delete the document',
    delConfirm: 'Delete this document and its extracted values? Committed contracts and values stay.',
    deleted: 'Document deleted',
    nothingSaved: 'Nothing is saved yet.',
    nothingSavedHint: 'These values only exist here. Contract, value, contributions and costs are created when you press “Commit the values” below. “Keep my edits” only remembers your corrections to this document.',
    saveDraft: 'Keep my edits',
    draftSaved: 'Edits kept — nothing committed yet',
    commit: 'Commit the values',
    commitConfirm: 'Create contract, value, contributions and costs with these values now?',
    commitConfirmLabel: 'Commit',
    committed: 'Values committed',
    warnings: 'Worth checking',
    origins: 'Where the values come from',
    page: 'page {page}',
    noPage: 'no page given',
    confidence: 'confidence {percent} %',
    unresolvedOrigin: 'Not found — please fill this in yourself.',
    noOrigin: 'No source in the document.',
    unresolvedCount: '{count} field could not be read and was left empty.',
    unresolvedCountPlural: '{count} fields could not be read and were left empty.',
    blockContract: 'Contract',
    blockSnapshot: 'Value at its date',
    blockSnapshotHint: 'This is money that exists today. No projected figure belongs in this block.',
    blockGuarantee: 'Guaranteed at retirement',
    blockGuaranteeHint: 'Promised figures. They are kept apart from the projection on purpose.',
    blockProjection: 'Projected at retirement — an assumption',
    blockProjectionHint: 'A projection is neither a guarantee nor money you have today. It is only stored together with its assumed return; without one the server refuses it.',
    blockContribution: 'Contribution',
    blockContributionHint: 'Own share, employer subsidy and employer-financed share stay three numbers. Only your own share leaves your net pay.',
    blockAllocations: 'Funds & ETFs',
    blockCosts: 'Costs',
    addContribution: 'Add the contribution from the document',
    partsTotal: 'Sum of the shares',
    statedMismatch: 'The three shares add up to {parts}, while the document states {stated}. Both are kept as they are and neither is corrected.',
    take: 'Commit',
    rowDropped: 'This row will not be committed.',
    policyFound: 'The document stated a policy number. It is never displayed and is kept when this field stays empty.',
    policyHint: 'Only fill this in to add or replace the number.',
    matchTitle: 'Assignment',
    matched: 'Found a matching contract: {contract}.',
    matchedOn: 'Matched on {rule}.',
    matchConfirm: 'An assignment only applies once you confirm it here — nothing happens automatically.',
    assignMatched: 'assign to this contract',
    assignOther: 'assign to a different contract',
    assignExisting: 'assign to an existing contract',
    createContract: 'create a new contract',
    unmatched: 'No matching contract found.',
    unmatchedHint: 'Create a new contract — or assign the document to an existing one yourself.',
    pickTarget: 'Please choose an assignment first.',
    chooseContract: 'Contract',
    includeInNetWorth: 'Count in total wealth',
    resultTitle: 'What was committed',
    applied: 'Created',
    skipped: 'Not created',
    nothingApplied: 'Nothing created.',
    nothingSkipped: 'Nothing skipped.',
    skippedHint: 'Skipped is not failed: a value that already exists for that date is added to, never overwritten.',
    dismiss: 'Close',
    failedTitle: 'Nothing could be read from this document',
    failedHint: 'You can still enter the values by hand — that path needs no text recognition at all.',
    waiting: 'The document is still being read. Reload the list in a moment.',
    refresh: 'Reload the list',
    committedTitle: 'Already committed',
    committedHint: 'This document’s values are in your contracts. The extracted draft is deleted afterwards so the same numbers do not sit in two places.',
    kinds: {
      annual_statement: 'Annual statement', certificate: 'Policy certificate',
      offer: 'Offer', correspondence: 'Letter', other: 'Other'
    },
    states: {
      pending: 'Being read', parsed: 'Ready to review', reviewed: 'Reviewed, not committed',
      committed: 'Committed', failed: 'Unreadable'
    },
    errors: {
      no_text: 'There was no text in the document, and text recognition returned nothing either.',
      tool_missing: 'This server does not have the text-recognition tool installed.',
      unsupported: 'This file format cannot be read.',
      too_large: 'The file was too large to read.'
    },
    rules: {
      policy_number: 'the policy number',
      provider_tariff_employer: 'provider, tariff and employer',
      provider_retirement_date: 'provider and retirement date'
    },
    results: {
      contract_created: 'Contract created', contract_matched: 'Contract assigned',
      contract: 'Contract', snapshot: 'Value', contribution: 'Contribution',
      allocations: 'Funds', costs: 'Costs', document: 'Document',
      snapshot_exists: 'A value already existed for that date',
      contribution_exists: 'A contribution already existed for that date',
      snapshot_no_figure: 'No value found in the document – nothing stored',
      snapshot_no_date: 'No effective date, so no value was stored – please add one',
      contribution_no_date: 'No valid-from date, so no contribution was stored',
      allocations_no_date: 'No effective date, so the funds were not stored',
      costs_no_date: 'No effective date, so the costs were not stored'
    }
  }
};

function lang() { return (document.documentElement.lang || '').startsWith('en') ? 'en' : 'de'; }
function d() { return DOC_T[lang()]; }
function docLabel(group, key) { return d()[group]?.[key] || key || '—'; }
function fill(template, values) {
  return Object.entries(values).reduce((text, [key, value]) => text.replaceAll(`{${key}}`, String(value)), template);
}
function bytes(value) {
  const size = Number(value || 0);
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}
// Confidence arrives as 0..1 and is shown as whole percent. Deliberately integer arithmetic and not a
// second number formatter: money and dates come from ctx, and nothing else here needs locale rules.
function pct(value) { return value == null ? null : Math.round(Number(value) * 100); }

// ---- mounting ----

/** pension.js calls this when the Dokumente tab becomes active; the tab bar stays its business. */
export function renderPensionDocuments(hostElement, options) {
  ctx = options.ctx;
  host = hostElement;
  contracts = options.contracts || [];
  sharedT = options.t;
  sharedLabel = options.label;
  reloadArea = options.reload;
  if (!host) return Promise.resolve();
  // Paint first so the tab is never blank, but say "loading" rather than "no documents yet" — an
  // empty-state that turns into a list one tick later reads like data appearing out of nowhere.
  if (!documents.length && !listError) loading = true;
  paint();
  return refresh();
}

/** Switching tabs leaves the review screen, so returning to Dokumente always starts at the list. */
export function resetPensionDocuments() {
  screen = { mode: 'list' };
  upload = null;
  // Leaving the tab means the commit result has been seen; it does not follow the user around.
  lastResult = null;
}

async function refresh() {
  loading = true;
  listError = null;
  try {
    documents = (await ctx.api('api/pension/documents')) || [];
  } catch (error) {
    documents = [];
    listError = error;
  } finally {
    loading = false;
  }
  paint();
}

function paint() {
  if (!host) return;
  host.innerHTML = screen.mode === 'list' ? listHtml() : documentScreenHtml();
  bind();
}

// ---- the list ----

function listHtml() {
  return `${uploadHtml()}${resultHtml()}${sectionCard(d().listTitle, listBodyHtml(), { className: 'pension-doc-list-card' })}`;
}

function listBodyHtml() {
  if (listError) return `<div class="row-sub">${esc(listError.message || ctx.get('common.error'))}</div>`;
  if (loading && !documents.length) return `<div class="row-sub">${esc(ctx.get('common.loading') || '…')}</div>`;
  if (!documents.length) return `<div class="row-sub">${esc(d().listEmpty)}</div>`;
  return `<div class="rows pension-list">${documents.map(documentRow).join('')}</div>`;
}

function documentRow(document_) {
  const badges = [`<span class="pension-badge">${esc(docLabel('kinds', document_.kind))}</span>`,
    `<span class="pension-badge pension-badge-quiet">${esc(docLabel('states', document_.extractionStatus))}</span>`];
  if (document_.textLayerUsed === false)
    badges.push(`<span class="pension-badge pension-badge-warn">${esc(d().ocrBadge)}</span>`);
  if (document_.extractionSource === 'codex')
    badges.push(`<span class="pension-badge pension-badge-quiet">${esc(d().aiBadge)}</span>`);

  const facts = [
    document_.pageCount ? (document_.pageCount === 1 ? d().pagesOne : fill(d().pages, { count: document_.pageCount })) : null,
    bytes(document_.byteSize),
    document_.extractionConfidence == null ? null : fill(d().quality, { percent: pct(document_.extractionConfidence) })
  ].filter(Boolean).join(' · ');

  return `<button type="button" class="row pension-row" data-doc-open="${esc(document_.id)}">
      <div class="row-main">
        <div class="row-title">${esc(document_.originalFileName || docLabel('kinds', document_.kind))}</div>
        <div class="row-sub">${esc(facts)}</div>
        <div class="pension-badges">${badges.join('')}</div>
      </div>
      <div class="pension-row-value">
        <div class="row-sub">${esc(ctx.date(String(document_.createdAt).slice(0, 10)))}</div>
      </div>
      <span class="pension-row-chevron" aria-hidden="true">›</span>
    </button>`;
}

function uploadHtml() {
  const busy = upload?.phase === 'uploading';
  const notice = upload?.phase === 'duplicate'
    ? `<div class="pension-notice pension-notice-info pension-doc-duplicate">
         <span>${esc(d().duplicate)}</span>
         <button type="button" class="btn btn-secondary" data-doc-open="${esc(upload.existingDocumentId)}">${esc(d().toExisting)}</button>
       </div>`
    : upload?.phase === 'error'
      ? `<div class="pension-notice pension-notice-warn">${esc(upload.text)}</div>`
      : '';

  const body = `
    <p class="row-sub">${esc(d().uploadHint)}</p>
    <label class="field pension-field pension-doc-kind">
      <span>${esc(d().uploadKind)}</span>
      <select data-doc-kind>${DOC_KINDS.map(key =>
        `<option value="${esc(key)}"${key === 'annual_statement' ? ' selected' : ''}>${esc(docLabel('kinds', key))}</option>`).join('')}</select>
    </label>
    <label class="pension-doc-drop" data-doc-drop>
      <input type="file" accept="${ACCEPT}" data-doc-file${busy ? ' disabled' : ''}>
      <strong>${esc(d().drop)}</strong>
    </label>
    ${busy ? `<div class="pension-doc-progress" role="status">
        <progress class="pension-doc-bar" aria-label="${esc(d().uploading)}"></progress>
        <span class="row-sub">${esc(upload.text || d().uploading)}</span>
      </div>` : ''}
    ${notice}`;
  return sectionCard(d().uploadTitle, body, { className: 'pension-doc-upload-card' });
}

// The commit answer, both halves of it. "Stand war bereits vorhanden" is information, not a failure,
// so it is rendered next to what was written rather than as an error.
function resultHtml() {
  if (!lastResult) return '';
  const list = (entries, empty) => entries?.length
    ? `<ul class="pension-doc-result-list">${entries.map(entry => `<li>${esc(resultText(entry))}</li>`).join('')}</ul>`
    : `<p class="row-sub">${esc(empty)}</p>`;
  return sectionCard(d().resultTitle, `
      <div class="pension-doc-result">
        <div><div class="pension-value-head">${esc(d().applied)}</div>${list(lastResult.applied, d().nothingApplied)}</div>
        <div><div class="pension-value-head">${esc(d().skipped)}</div>${list(lastResult.skipped, d().nothingSkipped)}</div>
      </div>
      <p class="row-sub">${esc(d().skippedHint)}</p>
      <div class="pension-doc-actions"><button type="button" class="btn btn-secondary" data-doc-dismiss>${esc(d().dismiss)}</button></div>`,
    { className: 'pension-doc-result-card' });
}

// `applied`/`skipped` are server tokens. A known one gets German words, an unknown one is shown as it
// came: inventing a label for a token this module does not know would hide what actually happened.
function resultText(entry) {
  const raw = String(entry ?? '');
  const [key, ...rest] = raw.split(':');
  const detail = rest.join(':');
  const known = d().results[key];
  if (!known) return raw;
  if (!detail) return known;
  return /^\d{4}-\d{2}-\d{2}$/.test(detail) ? `${known} (${ctx.date(detail)})` : `${known} (${detail})`;
}

// ---- one document ----

function documentScreenHtml() {
  const detail = screen.detail;
  const document_ = detail?.document;
  if (!document_) return listHtml();

  const head = `<div class="pension-doc-head">
      <button type="button" class="btn btn-secondary" data-doc-back>${esc(d().back)}</button>
      <div class="pension-doc-head-main">
        <h3>${esc(document_.originalFileName || docLabel('kinds', document_.kind))}</h3>
        <div class="row-sub">${esc(documentFacts(document_))}</div>
      </div>
      <a class="btn btn-secondary" href="${esc(ctx.bffUrl(`api/pension/documents/${document_.id}/content`))}" target="_blank" rel="noopener">${esc(d().open)}</a>
    </div>`;

  if (document_.extractionStatus === 'failed' || (!detail.draft && document_.extractionStatus !== 'committed'))
    return `<div class="pension-doc-screen">${head}${infoPanelHtml(document_)}</div>`;
  if (!detail.draft)
    return `<div class="pension-doc-screen">${head}${sectionCard(d().committedTitle,
      `<p class="row-sub">${esc(d().committedHint)}</p>${dangerActionsHtml()}`)}</div>`;

  return `<div class="pension-doc-screen">${head}${reviewHtml(detail)}</div>`;
}

function documentFacts(document_) {
  const draft = screen.detail?.draft;
  const pageCount = document_.pageCount ?? draft?.pageCount;
  // "gelesen"/"erkannt" describes where values came from, so it is left out entirely when nothing was
  // extracted — saying "erkannt" about a failed read would claim something that did not happen.
  const extracted = document_.extractionStatus !== 'failed' && document_.extractionStatus !== 'pending';
  return [
    docLabel('kinds', document_.kind),
    pageCount ? (pageCount === 1 ? d().pagesOne : fill(d().pages, { count: pageCount })) : null,
    bytes(document_.byteSize),
    !extracted ? null : document_.textLayerUsed === false ? `${d().recognised} (${d().ocrBadge})` : d().read,
    document_.extractionConfidence == null ? null : fill(d().quality, { percent: pct(document_.extractionConfidence) })
  ].filter(Boolean).join(' · ');
}

function infoPanelHtml(document_) {
  const waiting = document_.extractionStatus === 'pending';
  const body = `
    <p class="pension-doc-notice">${esc(waiting ? d().waiting : docLabel('errors', document_.extractionError || 'unsupported'))}</p>
    <p class="row-sub">${esc(waiting ? '' : d().failedHint)}</p>
    <div class="pension-doc-actions">
      ${waiting ? `<button type="button" class="btn btn-secondary" data-doc-refresh>${esc(d().refresh)}</button>` : ''}
      <button type="button" class="btn btn-danger" data-doc-delete>${esc(d().del)}</button>
    </div>`;
  return sectionCard(waiting ? docLabel('states', 'pending') : d().failedTitle, body);
}

function dangerActionsHtml() {
  return `<div class="pension-doc-actions"><button type="button" class="btn btn-danger" data-doc-delete>${esc(d().del)}</button></div>`;
}

// ---- the review form ----

function reviewHtml(detail) {
  const draft = detail.draft;
  const cur = draft.snapshot?.currency || draft.contract?.currency || 'EUR';
  const t = sharedT;

  const notices = [];
  // A warning from the extractor is information, not an alarm: calm, neutral, and each one names what
  // to look at. Red in this app is reserved for a real problem.
  (detail.warnings || []).forEach(text => notices.push(`<li>${esc(text)}</li>`));
  if (draft.unresolved?.length) {
    const copy = draft.unresolved.length === 1 ? d().unresolvedCount : d().unresolvedCountPlural;
    notices.push(`<li>${esc(fill(copy, { count: draft.unresolved.length }))}</li>`);
  }
  if (detail.document?.textLayerUsed === false) notices.push(`<li>${esc(d().ocrNotice)}</li>`);

  return `<form class="pension-doc-review" novalidate>
      <p class="pension-doc-unsaved"><strong>${esc(d().nothingSaved)}</strong> ${esc(d().nothingSavedHint)}</p>
      ${notices.length ? `<div class="pension-notice pension-notice-info pension-doc-warnings">
          <div class="pension-value-head">${esc(d().warnings)}</div>
          <ul>${notices.join('')}</ul>
        </div>` : ''}
      <p class="row-sub pension-doc-quality">${esc(d().qualityHint)}</p>

      ${matchHtml(detail)}

      ${block('blockContract', null, contractFieldsHtml())}
      ${block('blockSnapshot', 'blockSnapshotHint', snapshotFieldsHtml(cur), 'pension-doc-now')}
      ${block('blockGuarantee', 'blockGuaranteeHint', guaranteeFieldsHtml(cur), 'pension-doc-guarantee')}
      ${block('blockProjection', 'blockProjectionHint', projectionFieldsHtml(cur), 'pension-doc-projection')}
      ${contributionHtml(draft, cur)}
      ${draft.allocations?.length ? block('blockAllocations', null, allocationsHtml(draft.allocations, cur)) : ''}
      ${draft.costs?.length ? block('blockCosts', null, costsHtml(draft.costs, cur)) : ''}

      <div class="pension-doc-actions pension-doc-actions-main">
        <button type="button" class="btn btn-danger" data-doc-delete>${esc(d().del)}</button>
        <span class="pension-doc-spacer"></span>
        <button type="button" class="btn btn-secondary" data-doc-save>${esc(d().saveDraft)}</button>
        <button type="button" class="btn btn-primary" data-doc-commit>${esc(d().commit)}</button>
      </div>
      <p class="row-sub" data-doc-message aria-live="polite"></p>
    </form>`;

  function block(titleKey, hintKey, body, extra = '') {
    return `<fieldset class="pension-doc-block ${extra}">
        <legend>${esc(d()[titleKey])}</legend>
        ${hintKey ? `<p class="row-sub">${esc(d()[hintKey])}</p>` : ''}
        <div class="pension-doc-grid">${body}</div>
      </fieldset>`;
  }

  function contractFieldsHtml() {
    const policy = prov('contract.policyNumber');
    return `${CONTRACT_SPEC.map(item => specField(item, t)).join('')}
      ${fieldHtml('contract.policyNumber', t.policyNumber,
        `<input name="contract.policyNumber" type="text" maxlength="60" autocomplete="off" value="">`,
        policy ? `${d().policyFound} ${d().policyHint}` : d().policyHint)}`;
  }

  function snapshotFieldsHtml(currency) {
    return SNAPSHOT_SPEC.map(item => specField(item, t, currency)).join('');
  }

  function guaranteeFieldsHtml(currency) {
    return GUARANTEE_SPEC.map(item => specField(item, t, currency)).join('');
  }

  function projectionFieldsHtml(currency) {
    return PROJECTION_SPEC.map(item => specField(item, t, currency)).join('');
  }
}

// The match block. It names the rule that fired and asks for a confirmation; only an unmatched
// document may create a contract. Nothing here is pre-selected, so no assignment can happen by
// accident — a weaker match applied silently is how two contracts become one.
function matchHtml(detail) {
  const match = detail.match;
  const options = [];
  if (match?.matched) {
    const name = match.contract?.providerName || d().chooseContract;
    options.push(radio('matched', `${d().assignMatched} · ${name}`));
    if (contracts.length > 1) options.push(radio('other', d().assignOther));
  } else {
    options.push(radio('create', d().createContract));
    if (contracts.length) options.push(radio('existing', d().assignExisting));
  }

  const intro = match?.matched
    ? `<p>${esc(fill(d().matched, { contract: match.contract?.providerName || '—' }))}
        ${match.matchedOn ? esc(fill(d().matchedOn, { rule: docLabel('rules', match.matchedOn) })) : ''}</p>`
    : `<p>${esc(d().unmatched)} ${esc(d().unmatchedHint)}</p>`;

  const picker = `<label class="field pension-field pension-doc-target" data-doc-contract-picker hidden>
      <span>${esc(d().chooseContract)}</span>
      <select name="targetContractId">${contracts.map(contract =>
        `<option value="${esc(contract.id)}"${contract.id === match?.contractId ? ' selected' : ''}>${esc(contractCaption(contract))}</option>`).join('')}</select>
    </label>`;

  const include = `<label class="check pension-check pension-doc-include" data-doc-include hidden>
      <input name="includeInNetWorth" type="checkbox" checked>
      <span>${esc(d().includeInNetWorth)}</span>
    </label>`;

  return `<fieldset class="pension-doc-block pension-doc-match">
      <legend>${esc(d().matchTitle)}</legend>
      ${intro}
      <p class="row-sub">${esc(d().matchConfirm)}</p>
      <div class="pension-doc-choices">${options.join('')}</div>
      ${picker}
      ${include}
    </fieldset>`;

  function radio(value, text) {
    return `<label class="check pension-check"><input type="radio" name="docTarget" value="${value}"><span>${esc(text)}</span></label>`;
  }
}

function contractCaption(contract) {
  return [contract.providerName, contract.tariffName, contract.employerName].filter(Boolean).join(' · ');
}

function contributionHtml(draft, currency) {
  const t = sharedT;
  const has = !!draft.contribution;
  const fields = `<div class="pension-doc-grid">
      ${fieldHtml('contribution.validFrom', t.validFrom, `<input name="contribution.validFrom" type="date" value="${esc(draft.contribution?.validFrom || '')}">`)}
      ${fieldHtml('contribution.cycle', t.cycle, selectControl('contribution.cycle', CYCLES, draft.contribution?.cycle || 'monthly', key => sharedLabel('cycles', key)))}
      ${CONTRIBUTION_SPEC.map(item => specField(item, t, currency)).join('')}
    </div>
    <div class="pension-doc-total" data-doc-total></div>`;

  // A contribution the extractor did not find sits behind a disclosure rather than adding eight empty
  // inputs to a form this long — the pattern docs/UI_AUDIT.md asks for.
  const body = has
    ? fields
    : `<details class="pension-doc-details"><summary>${esc(d().addContribution)}</summary>${fields}</details>`;

  return `<fieldset class="pension-doc-block pension-doc-contribution">
      <legend>${esc(d().blockContribution)}</legend>
      <p class="row-sub">${esc(d().blockContributionHint)}</p>
      ${body}
    </fieldset>`;
}

function allocationsHtml(rows, currency) {
  const t = sharedT;
  return rows.map((row, index) => `<div class="pension-doc-row">
      ${takeCheckbox(`allocations[${index}]`)}
      ${fieldHtml(`allocations[${index}].fundName`, t.fundName, `<input name="allocations[${index}].fundName" type="text" maxlength="300" value="${esc(row.fundName || '')}">`)}
      ${fieldHtml(`allocations[${index}].isin`, t.isin, `<input name="allocations[${index}].isin" type="text" maxlength="14" autocomplete="off" value="${esc(row.isin || '')}">`)}
      ${fieldHtml(`allocations[${index}].weightPercent`, t.weight, numberControl(`allocations[${index}].weightPercent`, row.weightPercent, { min: 0, max: 100, step: '0.01' }))}
      ${fieldHtml(`allocations[${index}].amount`, `${t.amount} (${currency})`, numberControl(`allocations[${index}].amount`, row.amount, { min: 0, step: '0.01' }))}
      ${fieldHtml(`allocations[${index}].ongoingChargesPercent`, t.ongoingCharges, numberControl(`allocations[${index}].ongoingChargesPercent`, row.ongoingChargesPercent, { min: 0, max: 100, step: '0.001' }))}
      ${fieldHtml(`allocations[${index}].assetClass`, t.assetClass, selectControl(`allocations[${index}].assetClass`, ASSET_CLASSES, row.assetClass || 'equity', key => sharedLabel('assetClasses', key)))}
      <label class="check pension-check"><input name="allocations[${index}].ongoingChargesEstimated" type="checkbox"${row.ongoingChargesEstimated ? ' checked' : ''}><span>${esc(t.chargesEstimated)}</span></label>
    </div>`).join('');
}

function costsHtml(rows, currency) {
  const t = sharedT;
  return rows.map((row, index) => `<div class="pension-doc-row">
      ${takeCheckbox(`costs[${index}]`)}
      ${fieldHtml(`costs[${index}].kind`, t.costKind, selectControl(`costs[${index}].kind`, COST_KINDS, row.kind || 'other', key => sharedLabel('costKinds', key)))}
      ${fieldHtml(`costs[${index}].basis`, t.costBasis, selectControl(`costs[${index}].basis`, COST_BASES, row.basis || 'percent_of_capital', key => sharedLabel('costBases', key)))}
      ${fieldHtml(`costs[${index}].amount`, `${t.amount} (${currency})`, numberControl(`costs[${index}].amount`, row.amount, { min: 0, step: '0.01' }))}
      ${fieldHtml(`costs[${index}].percent`, t.percent, numberControl(`costs[${index}].percent`, row.percent, { min: 0, max: 100, step: '0.001' }))}
      ${fieldHtml(`costs[${index}].timing`, t.costTiming, selectControl(`costs[${index}].timing`, COST_TIMINGS, row.timing || 'ongoing', key => sharedLabel('costTimings', key)))}
      <label class="check pension-check"><input name="costs[${index}].isEstimated" type="checkbox"${row.isEstimated ? ' checked' : ''}><span>${esc(t.isEstimated)}</span></label>
      ${fieldHtml(`costs[${index}].estimateBasis`, t.estimateBasis, `<input name="costs[${index}].estimateBasis" type="text" maxlength="300" value="${esc(row.estimateBasis || '')}">`, t.estimateBasisHint)}
      <label class="check pension-check"><input name="costs[${index}].continuesWhenPaidUp" type="checkbox"${row.continuesWhenPaidUp === false ? '' : ' checked'}><span>${esc(t.continuesWhenPaidUp)}</span></label>
    </div>`).join('');
}

function takeCheckbox(prefix) {
  return `<label class="check pension-check pension-doc-take">
      <input name="${prefix}.take" type="checkbox" checked>
      <span>${esc(d().take)}</span>
    </label>`;
}

// ---- field specs: one source of truth for rendering AND for reading back ----

const CONTRACT_SPEC = [
  { path: 'contract.providerName', label: 'provider', kind: 'text', max: 200 },
  { path: 'contract.tariffName', label: 'tariff', kind: 'text', max: 200 },
  { path: 'contract.implementationRoute', label: 'route', kind: 'select', options: ROUTES, group: 'routes', fallback: 'direct_insurance' },
  { path: 'contract.status', label: 'status', kind: 'select', options: STATUSES, group: 'statuses', fallback: 'active' },
  { path: 'contract.employerName', label: 'employer', kind: 'text', max: 200 },
  { path: 'contract.policyHolderName', label: 'policyHolder', kind: 'text', max: 200 },
  { path: 'contract.insuredPersonName', label: 'insuredPerson', kind: 'text', max: 200 },
  { path: 'contract.startDate', label: 'startDate', kind: 'date' },
  { path: 'contract.retirementDate', label: 'retirementDate', kind: 'date' },
  { path: 'contract.currency', label: 'currency', kind: 'currency' },
  { path: 'contract.guaranteeQuotaPercent', label: 'guaranteeQuota', kind: 'number', min: 0, max: 100, step: '0.01' },
  { path: 'contract.guaranteedAnnuityFactor', label: 'annuityFactor', kind: 'number', min: 0, step: '0.01' }
];

// Money that exists at the snapshot's date. No projected figure is allowed in this list.
const SNAPSHOT_SPEC = [
  { path: 'snapshot.effectiveDate', label: 'effectiveDate', kind: 'date' },
  { path: 'snapshot.currency', label: 'currency', kind: 'currency' },
  { path: 'snapshot.balance', label: 'balance', kind: 'money' },
  { path: 'snapshot.guaranteedBalance', label: 'guaranteedBalance', kind: 'money' },
  { path: 'snapshot.surrenderValue', label: 'surrenderValue', kind: 'money' },
  { path: 'snapshot.securityAssetsAmount', label: 'securityAssets', kind: 'money' },
  { path: 'snapshot.fundAssetsAmount', label: 'fundAssets', kind: 'money' }
];

const GUARANTEE_SPEC = [
  { path: 'snapshot.guaranteedCapitalAtRetirement', label: 'guaranteedCapital', kind: 'money' },
  { path: 'snapshot.guaranteedMonthlyAnnuity', label: 'guaranteedMonthly', kind: 'money' }
];

const PROJECTION_SPEC = [
  { path: 'snapshot.projectedCapitalAtRetirement', label: 'projectedCapital', kind: 'money' },
  { path: 'snapshot.projectedMonthlyAnnuity', label: 'projectedMonthly', kind: 'money' },
  { path: 'snapshot.projectionReturnPercent', label: 'projectionReturn', kind: 'number', min: -100, max: 100, step: '0.01' },
  { path: 'snapshot.projectionBasis', label: 'projectionBasis', kind: 'select', options: PROJECTION_BASES, group: 'bases', fallback: 'document_forecast' }
];

const CONTRIBUTION_SPEC = [
  { path: 'contribution.employeeAmount', label: 'employeeAmount', kind: 'money' },
  { path: 'contribution.employerSubsidyAmount', label: 'employerSubsidy', kind: 'money' },
  { path: 'contribution.employerAmount', label: 'employerAmount', kind: 'money' },
  { path: 'contribution.statedTotalAmount', label: 'statedTotal', kind: 'money', hint: 'statedTotalHint' }
];

const ALL_SPECS = [CONTRACT_SPEC, SNAPSHOT_SPEC, GUARANTEE_SPEC, PROJECTION_SPEC, CONTRIBUTION_SPEC];

function specField(item, t, currency) {
  const value = draftValue(item.path);
  const labelText = item.kind === 'money' && currency ? `${t[item.label]} (${currency})` : t[item.label];
  const hint = item.hint ? t[item.hint] : null;
  let control;
  if (item.kind === 'select') {
    control = selectControl(item.path, item.options, value || item.fallback, key => sharedLabel(item.group, key));
  } else if (item.kind === 'money' || item.kind === 'number') {
    control = numberControl(item.path, value, item);
  } else if (item.kind === 'date') {
    control = `<input name="${item.path}" type="date" value="${esc(value || '')}">`;
  } else if (item.kind === 'currency') {
    control = `<input name="${item.path}" type="text" maxlength="3" minlength="3" pattern="[A-Za-z]{3}" value="${esc(value || '')}">`;
  } else {
    control = `<input name="${item.path}" type="text" maxlength="${item.max || 200}" value="${esc(value ?? '')}">`;
  }
  return fieldHtml(item.path, labelText, control, hint);
}

// An unresolved field is rendered empty: the extractor said it could not read it, so a pre-filled
// guess there would be indistinguishable from a value that really was on the page.
function draftValue(path) {
  if (isUnresolved(path)) return null;
  const draft = screen.detail?.draft;
  if (!draft) return null;
  const [head, tail] = path.split('.');
  const value = draft[head]?.[tail];
  return value == null ? null : value;
}

function numberControl(name, value, opts = {}) {
  const attrs = [
    opts.min != null ? `min="${opts.min}"` : '',
    opts.max != null ? `max="${opts.max}"` : '',
    `step="${opts.step || '0.01'}"`
  ].filter(Boolean).join(' ');
  return `<input name="${name}" type="number" inputmode="decimal" ${attrs} value="${value == null ? '' : esc(String(value))}">`;
}

function selectControl(name, keys, selected, labelOf) {
  return `<select name="${name}"><option value="">—</option>${keys.map(key =>
    `<option value="${esc(key)}"${key === selected ? ' selected' : ''}>${esc(labelOf(key))}</option>`).join('')}</select>`;
}

function fieldHtml(path, labelText, control, hint) {
  const classes = ['field', 'pension-field', 'pension-doc-field'];
  const entry = prov(path);
  const unresolved = isUnresolved(path);
  if (unresolved) classes.push('pension-doc-field-unresolved');
  else if (entry && screen.detail?.document?.textLayerUsed === false) classes.push('pension-doc-field-ocr');
  else if (entry?.source === 'codex') classes.push('pension-doc-field-ai');
  return `<label class="${classes.join(' ')}" data-doc-field="${esc(path)}">
      <span>${esc(labelText)}</span>
      ${control}
      <small class="pension-doc-origin">${originHtml(path)}</small>
      ${hint ? `<small class="row-sub">${esc(hint)}</small>` : ''}
    </label>`;
}

// Page and confidence per field, plus the one distinction that matters: a value off a text layer was
// READ, a value out of OCR was only RECOGNISED, and an AI-filled value says that too.
function originHtml(path) {
  if (isUnresolved(path)) return `<span class="pension-doc-flag pension-doc-flag-missing">${esc(d().unresolvedOrigin)}</span>`;
  const entry = prov(path);
  if (!entry) return esc(d().noOrigin);
  const ocr = screen.detail?.document?.textLayerUsed === false;
  const parts = [
    entry.page == null ? d().noPage : fill(d().page, { page: entry.page }),
    entry.confidence == null ? null : fill(d().confidence, { percent: pct(entry.confidence) }),
    entry.matchedLabel ? `„${entry.matchedLabel}“` : null
  ].filter(Boolean).map(part => esc(part));
  const flags = [];
  // "gelesen" is a claim about the source, so it is only made when the value really came off a text
  // layer AND out of the deterministic parser. OCR makes it *erkannt*, the AI pass makes it a
  // suggestion, and a value can be both.
  if (ocr) flags.push(`<span class="pension-doc-flag pension-doc-flag-ocr">${esc(`${d().recognised} · ${d().ocrBadge}`)}</span>`);
  else if (entry.source !== 'codex') flags.push(`<span class="pension-doc-flag">${esc(d().read)}</span>`);
  if (entry.source === 'codex') flags.push(`<span class="pension-doc-flag pension-doc-flag-ai">${esc(d().aiBadge)}</span>`);
  return `${flags.join(' ')} ${parts.join(' · ')}`;
}

function prov(path) {
  return (screen.detail?.draft?.provenance || []).find(entry => entry.field === path) || null;
}

function isUnresolved(path) {
  return (screen.detail?.draft?.unresolved || []).includes(path);
}

// ---- reading the form back into a draft ----

function draftFromForm(form) {
  const base = screen.detail.draft;
  const draft = {
    ...base,
    contract: { ...(base.contract || {}) },
    snapshot: { ...(base.snapshot || {}) },
    contribution: base.contribution ? { ...base.contribution } : {},
    allocations: [],
    costs: [],
    // A person has touched these values, so the draft says so: `manual` is exactly what
    // BavExtractionSources reserves for it.
    source: 'manual'
  };

  ALL_SPECS.forEach(spec => spec.forEach(item => {
    const [head, tail] = item.path.split('.');
    const target = head === 'contribution' ? draft.contribution : draft[head];
    if (target) target[tail] = readControl(form, item.path, item.kind);
  }));

  draft.contract.policyNumber = readControl(form, 'contract.policyNumber', 'text');
  draft.contribution.validFrom = readControl(form, 'contribution.validFrom', 'date');
  draft.contribution.cycle = readControl(form, 'contribution.cycle', 'text');
  draft.contribution.currency = draft.snapshot.currency || draft.contract.currency || null;

  // No amount means no contribution row at all, rather than a row of zeros that looks like a stated
  // "you pay nothing".
  const amounts = [draft.contribution.employeeAmount, draft.contribution.employerSubsidyAmount,
    draft.contribution.employerAmount, draft.contribution.statedTotalAmount];
  if (amounts.every(value => value == null)) draft.contribution = null;

  (base.allocations || []).forEach((row, index) => {
    if (!checked(form, `allocations[${index}].take`)) return;
    draft.allocations.push({
      ...row,
      fundName: readControl(form, `allocations[${index}].fundName`, 'text') || row.fundName,
      isin: readControl(form, `allocations[${index}].isin`, 'text'),
      weightPercent: readControl(form, `allocations[${index}].weightPercent`, 'number'),
      amount: readControl(form, `allocations[${index}].amount`, 'number'),
      currency: row.currency || draft.snapshot.currency || null,
      ongoingChargesPercent: readControl(form, `allocations[${index}].ongoingChargesPercent`, 'number'),
      ongoingChargesEstimated: checked(form, `allocations[${index}].ongoingChargesEstimated`),
      assetClass: readControl(form, `allocations[${index}].assetClass`, 'text')
    });
  });

  (base.costs || []).forEach((row, index) => {
    if (!checked(form, `costs[${index}].take`)) return;
    const basis = readControl(form, `costs[${index}].basis`, 'text') || row.basis;
    draft.costs.push({
      ...row,
      kind: readControl(form, `costs[${index}].kind`, 'text') || row.kind,
      basis,
      // A fixed cost carries an amount and a percentage cost a percentage — never both.
      amount: basis === 'fixed_amount' ? readControl(form, `costs[${index}].amount`, 'number') : null,
      percent: basis === 'fixed_amount' ? null : readControl(form, `costs[${index}].percent`, 'number'),
      timing: readControl(form, `costs[${index}].timing`, 'text'),
      isEstimated: checked(form, `costs[${index}].isEstimated`),
      estimateBasis: checked(form, `costs[${index}].isEstimated`)
        ? readControl(form, `costs[${index}].estimateBasis`, 'text')
        : null,
      continuesWhenPaidUp: checked(form, `costs[${index}].continuesWhenPaidUp`)
    });
  });

  return draft;
}

function readControl(form, name, kind) {
  const element = form.elements[name];
  if (!element) return null;
  const raw = String(element.value ?? '').trim();
  if (raw === '') return null;
  if (kind === 'money' || kind === 'number') {
    // A number input reports a machine-readable value, so there is no locale parsing anywhere here.
    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : null;
  }
  if (kind === 'currency') return raw.toUpperCase();
  return raw;
}

function checked(form, name) {
  return !!form.elements[name]?.checked;
}

// ---- events ----

function bind() {
  host.querySelectorAll('[data-doc-open]').forEach(button =>
    button.addEventListener('click', () => openDocument(button.dataset.docOpen)));
  host.querySelector('[data-doc-back]')?.addEventListener('click', () => { screen = { mode: 'list' }; paint(); });
  host.querySelector('[data-doc-dismiss]')?.addEventListener('click', () => { lastResult = null; paint(); });
  host.querySelector('[data-doc-refresh]')?.addEventListener('click', () => openDocument(screen.detail.document.id));
  host.querySelector('[data-doc-delete]')?.addEventListener('click', deleteDocument);
  bindUpload();
  bindReview();
}

function bindUpload() {
  const input = host.querySelector('[data-doc-file]');
  const drop = host.querySelector('[data-doc-drop]');
  if (!input || !drop) return;
  input.addEventListener('change', () => { if (input.files?.length) uploadFile(input.files[0]); });
  for (const name of ['dragenter', 'dragover'])
    drop.addEventListener(name, event => { event.preventDefault(); drop.classList.add('dragging'); });
  for (const name of ['dragleave', 'drop'])
    drop.addEventListener(name, event => { event.preventDefault(); drop.classList.remove('dragging'); });
  drop.addEventListener('drop', event => {
    const file = event.dataTransfer?.files?.[0];
    if (file) uploadFile(file);
  });
}

function bindReview() {
  const form = host.querySelector('form.pension-doc-review');
  if (!form) return;

  const picker = form.querySelector('[data-doc-contract-picker]');
  const include = form.querySelector('[data-doc-include]');
  const commit = form.querySelector('[data-doc-commit]');
  commit.disabled = true;

  form.querySelectorAll('input[name="docTarget"]').forEach(radio => radio.addEventListener('change', () => {
    const value = radio.value;
    if (picker) picker.hidden = !(value === 'other' || value === 'existing');
    if (include) include.hidden = value !== 'create';
    commit.disabled = false;
  }));

  // The printed total is compared, never applied: three shares that disagree with it stay three shares.
  const totalBox = form.querySelector('[data-doc-total]');
  const updateTotal = () => {
    if (!totalBox) return;
    const currency = readControl(form, 'snapshot.currency', 'currency')
      || readControl(form, 'contract.currency', 'currency') || 'EUR';
    const parts = ['contribution.employeeAmount', 'contribution.employerSubsidyAmount', 'contribution.employerAmount']
      .map(path => readControl(form, path, 'money'))
      .filter(value => value != null);
    if (!parts.length) { totalBox.innerHTML = ''; return; }
    const sum = parts.reduce((total, value) => total + value, 0);
    const stated = readControl(form, 'contribution.statedTotalAmount', 'money');
    const mismatch = stated != null && Math.abs(stated - sum) > 0.005;
    totalBox.innerHTML = `<div class="row-sub">${esc(d().partsTotal)}: ${esc(String(ctx.money(sum, currency)))}</div>
      ${mismatch ? `<div class="pension-notice pension-notice-info">${esc(fill(d().statedMismatch, {
        parts: ctx.money(sum, currency), stated: ctx.money(stated, currency)
      }))}</div>` : ''}`;
  };
  ['contribution.employeeAmount', 'contribution.employerSubsidyAmount', 'contribution.employerAmount',
    'contribution.statedTotalAmount'].forEach(path =>
      form.elements[path]?.addEventListener('input', updateTotal));
  updateTotal();

  form.querySelectorAll('[name$=".take"]').forEach(box => box.addEventListener('change', () => {
    box.closest('.pension-doc-row')?.classList.toggle('pension-doc-row-dropped', !box.checked);
  }));

  form.querySelector('[data-doc-save]').addEventListener('click', () => saveDraft(form));
  commit.addEventListener('click', () => commitDraft(form));
}

async function openDocument(id) {
  upload = null;
  try {
    const detail = await ctx.api(`api/pension/documents/${id}`);
    screen = { mode: 'review', detail };
  } catch (error) {
    ctx.toast(errorText(error));
    return;
  }
  paint();
  host.scrollIntoView({ block: 'start' });
}

async function uploadFile(file) {
  if (file.size > MAX_BYTES) {
    upload = { phase: 'error', text: d().tooLarge };
    paint();
    return;
  }
  const kind = host.querySelector('[data-doc-kind]')?.value || 'annual_statement';
  upload = { phase: 'uploading', text: d().uploading };
  paint();

  const body = new FormData();
  body.append('document', file, file.name);
  body.append('kind', kind);
  try {
    const detail = await ctx.api('api/pension/documents', { method: 'POST', body });
    upload = null;
    screen = { mode: 'review', detail };
    await refreshQuietly();
    paint();
  } catch (error) {
    // A duplicate is the model doing its job: the same file is already here. Point at it instead of
    // showing a status code and a dead end.
    if (error?.status === 409 && error.detail?.existingDocumentId) {
      upload = { phase: 'duplicate', existingDocumentId: error.detail.existingDocumentId };
    } else {
      upload = { phase: 'error', text: errorText(error) };
    }
    paint();
  }
}

async function refreshQuietly() {
  try { documents = (await ctx.api('api/pension/documents')) || []; } catch { /* the list keeps what it had */ }
}

async function saveDraft(form) {
  const button = form.querySelector('[data-doc-save]');
  button.disabled = true;
  try {
    await ctx.api(`api/pension/documents/${screen.detail.document.id}/review`,
      ctx.jsonBody({ draft: draftFromForm(form), kind: screen.detail.document.kind }, 'PUT'));
    message(form, d().draftSaved);
    await refreshQuietly();
  } catch (error) {
    message(form, errorText(error));
  } finally {
    button.disabled = false;
  }
}

async function commitDraft(form) {
  const target = form.querySelector('input[name="docTarget"]:checked')?.value;
  if (!target) { message(form, d().pickTarget); return; }
  if (!await ctx.confirm(d().commitConfirm, { confirmLabel: d().commitConfirmLabel })) return;

  const createContract = target === 'create';
  const contractId = createContract
    ? null
    : (target === 'matched' ? screen.detail.match?.contractId : form.elements.targetContractId?.value || null);

  const button = form.querySelector('[data-doc-commit]');
  button.disabled = true;
  try {
    const result = await ctx.api(`api/pension/documents/${screen.detail.document.id}/commit`, ctx.jsonBody({
      draft: draftFromForm(form),
      contractId,
      createContract,
      kind: screen.detail.document.kind,
      includeInNetWorth: createContract ? checked(form, 'includeInNetWorth') : true
    }));
    lastResult = { applied: result?.applied || [], skipped: result?.skipped || [] };
    screen = { mode: 'list' };
    ctx.toast(d().committed);
    // The contracts, the Übersicht totals and the net worth all moved, so the whole area reloads.
    if (reloadArea) await reloadArea();
    else { await refresh(); }
  } catch (error) {
    message(form, errorText(error));
    button.disabled = false;
  }
}

async function deleteDocument() {
  const id = screen.detail?.document?.id;
  if (!id) return;
  if (!await ctx.confirm(d().delConfirm, { destructive: true, confirmLabel: d().del })) return;
  try {
    await ctx.api(`api/pension/documents/${id}`, { method: 'DELETE' });
    screen = { mode: 'list' };
    ctx.toast(d().deleted);
    await refresh();
  } catch (error) {
    ctx.toast(errorText(error));
  }
}

function message(form, text) {
  const box = form.querySelector('[data-doc-message]');
  if (box) box.textContent = text;
}

function errorText(error) {
  if (error?.status === 403) return sharedT.ownerOnly;
  if (error?.status === 413) return d().tooLarge;
  return error?.message || ctx.get('common.error');
}
