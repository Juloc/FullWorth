// Belege: Warteschlange, Verlauf, Zu pruefen - und live, was der Server gerade liest.
//
// Die Liste fragt in kurzem Takt nach, solange etwas wartet oder laeuft, und selten, wenn nichts zu tun
// ist. Gezeichnet wird nur, was sich geaendert hat: ein Beleg, der gerade gelesen wird, soll seinen
// Balken weiterschieben, nicht die ganze Liste neu aufbauen.

import { emptyRow } from '../../components/empty.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmMessage } from '../../components/confirm.js';
import { spriteHref } from '../../components/sprite.js';
import { createSelectionList } from '../../components/selection-list.js';
import { initializeReceiptIntake } from './intake.js';
import { addReceiptScanCameraShots } from './receipt-scan-set.js';

const TABS = ['queue', 'history', 'review', 'failed'];
const PAGE = 40;
const POLL_BUSY_MS = 2000;
const POLL_IDLE_MS = 15000;

// Wie weit der Balken einer Stufe steht. Der Server meldet nur die Stufe, keinen Prozentwert - die
// Stufen sind aber geordnet, und mehr als "vorbei an Stufe n von m" gibt es ehrlich nicht zu sagen.
const STAGE_PROGRESS = { preparing: 12, connecting: 25, analyzing: 50, ocr: 50, structuring: 78, saving: 92 };

let ctx = null;
let tab = TABS.includes(location.hash.slice(1)) ? location.hash.slice(1) : 'queue';
let rows = [];
let counts = null;
let lastCountsKey = '';
let drawn = '';
let hasMore = false;
let loading = false;
let timer = null;
// Was der Benutzer angehakt hat - die Schluessel sind die Auftrags-Ids und ueberleben jedes Neuzeichnen.
let selected = new Set();
// Haken, Gesamt-Haken und Zaehlung kommen vom gemeinsamen Baustein; die Seite haelt nur die Ids, damit
// die Auswahl ein Neuzeichnen der Tabelle ueberlebt.
const selection = createSelectionList({
  onChange: ({ selectedIds }) => {
    selected = new Set(selectedIds);
    for (const row of document.querySelectorAll('#receipts-list .dt-row'))
      row.classList.toggle('is-selected', selected.has(row.querySelector('[data-select-item]')?.value));
    showBar();
  }
});

export function bindReceipts(context) {
  ctx = context;
  ctx.$('#view-receipts').addEventListener('click', onClick);
  document.getElementById('action-bar-selection').addEventListener('click', onBar);
  initializeReceiptIntake(ctx);
  // Mit Kamera: das Livebild; Fotos und Dateien waehlt die Hauptaktion. Beides endet im selben Scan-Set.
  const camera = document.createElement('button');
  camera.type = 'button';
  camera.className = buttonClass(ButtonRole.Secondary);
  camera.innerHTML = `<svg viewBox="0 0 24 24"><use href="${spriteHref('ui-scan')}"></use></svg><span class="action-label">${ctx.esc(ctx.get('purchases.camera'))}</span>`;
  camera.addEventListener('click', () => scanWithCamera().catch(error => ctx.toast(error?.message || ctx.get('common.error'))));
  document.getElementById('action-bar-extra').append(camera);
  // Ein Beleg ist angelegt oder fertig (Scan-Dialog, Import) - oder der Import schickt einen zur Pruefung.
  document.addEventListener('fullworth:receipts-changed', () => refresh(true).catch(() => {}));
  document.addEventListener('fullworth:receipts-show-review', () => switchTab('review'));
  // Endloses Nachladen: der Wachposten am Ende der Liste fragt die naechste Seite an, sobald er in die
  // Naehe des Bildschirms kommt. Eine Seitenzahl gibt es nicht - in fast allen Faellen liest man von oben.
  new IntersectionObserver(entries => { if (entries[0].isIntersecting) more().catch(() => {}); }, { rootMargin: '480px' })
    .observe(ctx.$('#receipts-sentinel'));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
}

// Der Knopf "Beleg hochladen" sitzt in der Topbar; die Seite besitzt nur das Dateifeld dahinter.
export function chooseFiles(context) {
  context.$('#receipt-file').click();
}

export async function renderReceipts(context) {
  ctx = context;
  await refresh(true);
  schedule();
}

function schedule() {
  clearTimeout(timer);
  const busy = counts && counts.queued + counts.processing > 0;
  timer = setTimeout(tick, busy ? POLL_BUSY_MS : POLL_IDLE_MS);
}

async function tick() {
  clearTimeout(timer);
  if (!document.hidden) await refresh(false).catch(() => {});
  schedule();
}

async function refresh(force) {
  counts = await ctx.api('api/purchases/receipt-scan/counts');
  showCounts();
  // Der Verlauf und "Zu pruefen" aendern sich nur, wenn sich die Zaehler aendern; die Warteschlange
  // aendert sich mit jeder Stufe und wird deshalb jedes Mal gelesen.
  const key = JSON.stringify(counts);
  if (!force && tab !== 'queue' && key === lastCountsKey) return;
  lastCountsKey = key;
  if (!rows.length) { loading = true; draw(); }
  try {
    const loaded = await load(0, Math.min(100, Math.max(PAGE, rows.length)));
    rows = loaded.rows;
    hasMore = loaded.more;
  } finally {
    loading = false;
  }
  for (const id of selected) if (!rows.some(row => row.id === id)) selected.delete(id);
  draw();
}

async function load(offset, limit) {
  const list = (await ctx.api(`api/purchases/receipt-scan/jobs?scope=${tab}&limit=${limit}&offset=${offset}`)) || [];
  return { rows: list, more: tab !== 'queue' && list.length === limit };
}

// Die Zahlen stehen an den Reitern. Der Verlauf hat keine: er waechst nur, und eine Zahl, die nie
// weniger wird, sagt nichts.
function showCounts() {
  const shown = { queue: counts.queued + counts.processing, needsReview: counts.needsReview, failed: counts.failed };
  for (const el of ctx.$('#view-receipts').querySelectorAll('[data-count]')) {
    el.textContent = shown[el.dataset.count] ?? 0;
    el.classList.remove('is-pending');
  }
}

function onClick(event) {
  const tabButton = event.target.closest('[data-receipts-tab]');
  if (tabButton) switchTab(tabButton.dataset.receiptsTab);
}

// Die Auswahl-Aktionen wohnen in der globalen Aktionsleiste (layout: #action-bar-selection) und ersetzen
// dort die Hauptaktion, solange etwas angehakt ist. Neu gezeichnet wird nur, wenn sich Zahl oder Reiter
// aendern - ein Knopf, den man gerade drueckt, soll seinen Fokus behalten.
let barKey = '';
function showBar() {
  const open = selected.size > 0 && (tab === 'review' || tab === 'failed');
  document.getElementById('action-bar').classList.toggle('has-selection', open);
  const key = open ? `${tab}:${selected.size}` : '';
  if (key === barKey) return;
  barKey = key;
  const glyph = id => `<svg viewBox="0 0 24 24"><use href="${spriteHref(id)}"></use></svg>`;
  const review = tab === 'review';
  document.getElementById('action-bar-selection').innerHTML = open
    ? `<span class="action-bar-count">${ctx.esc(t(`${selected.size} ausgewählt`, `${selected.size} selected`))}</span>
       <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-bar="${review ? 'confirm' : 'retry'}">${glyph(review ? 'ui-check' : 'ui-refresh')}<span>${ctx.esc(review ? ctx.get('purchases.confirm') : ctx.get('receipts.retry'))}</span></button>`
    : '';
}

async function onBar(event) {
  const button = event.target.closest('[data-bar]');
  if (!button) return;
  button.disabled = true;
  try { await (button.dataset.bar === 'confirm' ? confirmSelected() : retrySelected()); }
  catch (error) { ctx.toast(error?.message || ctx.get('common.error')); }
  finally { button.disabled = false; }
}

// Eine Sammelbestaetigung wiegt schwer, also fragt sie einmal nach - und bestaetigt dann Beleg fuer
// Beleg ueber dieselbe Summenpruefung wie die Einzelbestaetigung. Einer, dem sie nicht genuegt, bleibt
// angehakt und auf "Zu pruefen"; die anderen sind trotzdem durch.
async function confirmSelected() {
  const jobs = rows.filter(row => selected.has(row.id));
  if (!await confirmMessage({
    title: t('Belege bestätigen', 'Confirm receipts'),
    message: t(`${jobs.length} Beleg(e) bestätigen? Jeder wird einzeln geprüft; was nicht aufgeht, bleibt in der Liste.`,
               `Confirm ${jobs.length} receipt(s)? Each is checked on its own; anything that does not add up stays in the list.`),
    confirmLabel: t('Bestätigen', 'Confirm'),
    cancelLabel: t('Abbrechen', 'Cancel')
  })) return;
  let ok = 0;
  for (const job of jobs) {
    try {
      await ctx.api(`api/purchases/${job.purchaseId}/confirm`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ createSafeAllocations: true, allowUnlinked: true })
      });
      selected.delete(job.id);
      ok++;
    } catch { /* bleibt angehakt */ }
  }
  const left = jobs.length - ok;
  ctx.toast(left
    ? t(`${ok} bestätigt, ${left} stimmen nicht und bleiben zu prüfen.`, `${ok} confirmed, ${left} do not add up and stay in review.`)
    : t(`${ok} bestätigt.`, `${ok} confirmed.`));
  await refresh(true);
}

async function retrySelected() {
  for (const job of rows.filter(row => selected.has(row.id))) {
    await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/retry`, { method: 'POST' });
    selected.delete(job.id);
  }
  await refresh(true);
}

async function switchTab(next) {
  if (next === tab) return;
  tab = next;
  history.replaceState(null, '', `#${tab}`);
  rows = [];
  selected = new Set();
  lastCountsKey = '';
  drawn = '';
  await refresh(true);
}

async function more() {
  if (loading || !hasMore) return;
  loading = true;
  draw();
  try {
    const loaded = await load(rows.length, PAGE);
    rows = rows.concat(loaded.rows);
    hasMore = loaded.more;
  } finally {
    loading = false;
    draw();
  }
}

async function scanWithCamera() {
  if (await addReceiptScanCameraShots(ctx)) await refresh(true);
}

// Welche Spalten ein Reiter hat. "select" ist die Haken-Spalte, deren Kopf alles an- und abwaehlt.
const COLUMNS = {
  queue: ['file', 'state', 'reader'],
  history: ['file', 'date', 'items', 'reader', 'amount'],
  review: ['select', 'file', 'date', 'items', 'reader', 'amount', 'view'],
  failed: ['select', 'file', 'when']
};

function heading(column) {
  return {
    file: t('Beleg', 'Receipt'), state: t('Stand', 'State'), date: t('Datum', 'Date'), items: t('Positionen', 'Items'),
    reader: t('Gelesen von', 'Read by'), amount: t('Betrag', 'Amount'), when: t('Wann', 'When'), view: ''
  }[column] ?? '';
}

function headRow() {
  const cells = COLUMNS[tab].map(column => column === 'select'
    ? `<label class="fw-check"><input type="checkbox" data-select-all></label>`
    : `<span class="${['date', 'items', 'reader', 'state', 'when', 'view'].includes(column) ? 'dt-wide' : column === 'amount' ? 'dt-right' : ''}">${ctx.esc(heading(column))}</span>`);
  return `<div class="dt-head">${cells.join('')}</div>`;
}

function draw() {
  for (const button of ctx.$('#view-receipts').querySelectorAll('[data-receipts-tab]')) {
    const active = button.dataset.receiptsTab === tab;
    button.classList.toggle('active', active);
  }
  // Solange die erste Seite noch aussteht, stehen Platzhalter da: so hoch wie die Zeilen, die kommen.
  const first = loading && !rows.length;
  const list = ctx.$('#receipts-list');
  list.dataset.tab = tab;
  const html = first
    ? skeleton(4)
    : (rows.length
      ? headRow() + rows.map(row => dataRow(row)).join('')
      : emptyRow(emptyText())) + (loading ? skeleton(2) : '');
  if (html !== drawn) {
    drawn = html;
    list.innerHTML = html;
    selection.mount(list);
  }
  showBar();
}

function skeleton(count) {
  return Array.from({ length: count }, () => '<div class="dt-row dt-skeleton"><span class="dt-sk-line shimmer"></span><span class="dt-sk-line short shimmer"></span></div>').join('');
}

function emptyText() {
  if (tab === 'queue') return t('Nichts in der Warteschlange.', 'Nothing in the queue.');
  if (tab === 'review') return t('Alles geprüft.', 'Everything is reviewed.');
  if (tab === 'failed') return t('Nichts fehlgeschlagen.', 'Nothing failed.');
  return t('Noch keine verarbeiteten Belege.', 'No processed receipts yet.');
}

function name(job) {
  const merchant = job.merchant && job.merchant !== 'Unbekannt' ? job.merchant : '';
  return merchant || job.fileName || t('Beleg', 'Receipt');
}

// Wer den Beleg gelesen hat - und das sagt es ausdruecklich in beide Richtungen: keine KI, wo keine
// beteiligt war, und KI, wo eine beteiligt war (#129).
function readerLabel(engine) {
  if (engine === 'text') return t('Digitales PDF · lokal, keine KI', 'Digital PDF · local, no AI');
  if (engine === 'gpt') return t('KI (GPT)', 'AI (GPT)');
  if (engine === 'tesseract') return t('Texterkennung · keine KI', 'Text recognition · no AI');
  return engine || '';
}

function reasonLabel(reason) {
  return {
    digital_pdf: t('Digitales PDF, Summen stimmen', 'Digital PDF, sums agree'),
    digital_unverified: t('Digitales PDF, Summen stimmten nicht → KI', 'Digital PDF, sums disagreed → AI'),
    scan: t('Scan oder Foto → KI', 'Scan or photo → AI'),
    ai_unavailable: t('KI nicht erreichbar → Texterkennung', 'AI unreachable → text recognition')
  }[reason] || '';
}

function stageLabel(job) {
  return {
    preparing: t('Seiten werden vorbereitet', 'Preparing pages'),
    connecting: t('Verbindung zur KI', 'Connecting to AI'),
    analyzing: t('KI liest den Beleg', 'AI is reading the receipt'),
    ocr: t('Texterkennung läuft', 'Text recognition running'),
    structuring: t('Positionen werden zusammengeführt', 'Merging items'),
    saving: t('Ergebnis wird gespeichert', 'Saving result')
  }[job.stage] || t('Wird verarbeitet', 'Processing');
}

function readerTag(job) {
  if (!job.engine) return '';
  const version = job.readerVersion ? ` <span class="receipts-version">${ctx.esc(job.readerVersion)}</span>` : '';
  return `<span class="dt-tag receipts-tag--${ctx.esc(job.engine)}">${ctx.esc(readerLabel(job.engine))}</span>${version}`;
}

// Eine Zeile ist ein Gitter mit den Spalten ihres Reiters. Am Telefon bleiben nur Haken, Beleg und Betrag;
// was die ausgeblendeten Spalten sagen, steht dort in der Zeile unter dem Namen (.dt-meta).
function dataRow(job) {
  const cells = COLUMNS[tab].map(column => cell(column, job)).join('');
  return `<div class="dt-row${selected.has(job.id) ? ' is-selected' : ''}${job.status === 'processing' ? ' is-active' : ''}">${cells}</div>`;
}

function cell(column, job) {
  switch (column) {
    case 'select': return checkbox(job);
    case 'file': return fileCell(job);
    case 'state': return `<span class="dt-cell dt-wide">${stateCell(job)}</span>`;
    case 'date': return `<span class="dt-cell dt-wide">${ctx.esc(job.purchaseDate ? ctx.date(job.purchaseDate) : '–')}</span>`;
    case 'items': return `<span class="dt-cell dt-wide">${job.itemCount}</span>`;
    case 'reader': return `<span class="dt-cell dt-wide">${readerTag(job) || '–'}</span>`;
    case 'amount': return `<span class="dt-cell dt-right amount">${ctx.money(job.totalAmount, job.currency)}</span>`;
    case 'when': return `<span class="dt-cell dt-wide">${ctx.esc(ago(job.completedAt))}</span>`;
    case 'view': return `<a class="dt-cell dt-link dt-wide" href="/purchases#purchase=${ctx.esc(job.purchaseId)}">${ctx.esc(t('Ansehen', 'View'))}</a>`;
    default: return '';
  }
}

function fileCell(job) {
  const warnings = parseWarnings(job.warningsJson);
  const detail = tab === 'failed'
    ? `<span class="dt-sub dt-error">${ctx.esc(job.error || '')}</span>`
    : tab === 'review' && warnings.length
      ? `<span class="dt-sub dt-warning">${ctx.esc(warnings[0])}${warnings.length > 1 ? ` (+${warnings.length - 1})` : ''}</span>`
      : tab === 'history' && reasonLabel(job.reason)
        ? `<span class="dt-sub">${ctx.esc(reasonLabel(job.reason))}</span>`
        : '';
  const progress = job.status === 'processing'
    ? `<span class="receipts-bar"><span style="width:${STAGE_PROGRESS[job.stage] ?? 8}%"></span></span>`
    : '';
  const meta = tab === 'queue'
    ? stateCell(job)
    : [job.purchaseDate ? ctx.esc(ctx.date(job.purchaseDate)) : '', tab === 'failed' ? '' : `${job.itemCount} ${ctx.esc(t('Positionen', 'items'))}`, tab === 'failed' ? ctx.esc(ago(job.completedAt)) : readerTag(job)]
      .filter(Boolean).join(' · ');
  const view = tab === 'review'
    ? ` · <a href="/purchases#purchase=${ctx.esc(job.purchaseId)}">${ctx.esc(t('Ansehen', 'View'))}</a>` : '';
  return `<span class="dt-cell dt-main"><span class="dt-name">${ctx.esc(name(job))}</span>${detail}${progress}<span class="dt-meta">${meta}${view}</span></span>`;
}

function stateCell(job) {
  const running = job.status === 'processing';
  const text = running
    ? `${stageLabel(job)} · ${ago(job.startedAt, true)}`
    : t(`Position ${job.queuePosition} · wartet seit ${ago(job.createdAt, true)}`, `Position ${job.queuePosition} · waiting ${ago(job.createdAt, true)}`);
  return `<span class="dt-tag">${ctx.esc(running ? t('In Arbeit', 'Working') : t('Wartet', 'Waiting'))}</span> <span class="receipts-state-text">${ctx.esc(text)}</span>`;
}

function checkbox(job) {
  return `<label class="fw-check"><input type="checkbox" data-select-item value="${ctx.esc(job.id)}"${selected.has(job.id) ? ' checked' : ''}></label>`;
}

function parseWarnings(json) {
  try { return json ? JSON.parse(json) : []; } catch { return []; }
}

// "vor 3 Min" - nur grob, denn die Seite fragt alle paar Sekunden nach und zeigt keine Uhr.
function ago(iso, bare = false) {
  if (!iso) return '';
  const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  const text = minutes < 1 ? t('gerade eben', 'just now')
    : minutes < 60 ? t(`${minutes} Min`, `${minutes} min`)
    : minutes < 1440 ? t(`${Math.round(minutes / 60)} Std`, `${Math.round(minutes / 60)} h`)
    : t(`${Math.round(minutes / 1440)} Tg`, `${Math.round(minutes / 1440)} d`);
  return bare || minutes < 1 ? text : t(`vor ${text}`, `${text} ago`);
}

function t(de, en) { return (document.documentElement.lang || 'de').toLowerCase().startsWith('en') ? en : de; }
