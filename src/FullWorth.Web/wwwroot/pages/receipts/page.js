// Belege: Warteschlange, Verlauf, Zu pruefen - und live, was der Server gerade liest.
//
// Die Liste fragt in kurzem Takt nach, solange etwas wartet oder laeuft, und selten, wenn nichts zu tun
// ist. Gezeichnet wird nur, was sich geaendert hat: ein Beleg, der gerade gelesen wird, soll seinen
// Balken weiterschieben, nicht die ganze Liste neu aufbauen.

import { emptyRow } from '../../components/empty.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmMessage } from '../../components/confirm.js';
import { spriteHref } from '../../components/sprite.js';

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
const selected = new Set();

export function bindReceipts(context) {
  ctx = context;
  ctx.$('#view-receipts').addEventListener('click', onClick);
  ctx.$('#view-receipts').addEventListener('change', onSelect);
  document.getElementById('action-bar-selection').addEventListener('click', onBar);
  ctx.$('#receipts-file').addEventListener('change', upload);
  // Endloses Nachladen: der Wachposten am Ende der Liste fragt die naechste Seite an, sobald er in die
  // Naehe des Bildschirms kommt. Eine Seitenzahl gibt es nicht - in fast allen Faellen liest man von oben.
  new IntersectionObserver(entries => { if (entries[0].isIntersecting) more().catch(() => {}); }, { rootMargin: '480px' })
    .observe(ctx.$('#receipts-sentinel'));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
}

// Der Knopf "Beleg hochladen" sitzt in der Topbar; die Seite besitzt nur das Dateifeld dahinter.
export function chooseFiles(context) {
  context.$('#receipts-file').click();
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

function onSelect(event) {
  const box = event.target.closest('[data-select]');
  if (!box) return;
  if (box.checked) selected.add(box.dataset.select); else selected.delete(box.dataset.select);
  box.closest('.receipts-row')?.classList.toggle('is-selected', box.checked);
  showBar();
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
  const glyph = id => `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(id)}"></use></svg>`;
  const review = tab === 'review';
  document.getElementById('action-bar-selection').innerHTML = open
    ? `<span class="action-bar-count">${ctx.esc(t(`${selected.size} ausgewählt`, `${selected.size} selected`))}</span>
       <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-bar="all">${ctx.esc(ctx.get('receipts.selectAll'))}</button>
       <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-bar="none">${ctx.esc(ctx.get('receipts.selectNone'))}</button>
       <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-bar="${review ? 'confirm' : 'retry'}">${glyph(review ? 'ui-check' : 'ui-refresh')}<span>${ctx.esc(review ? ctx.get('purchases.confirm') : ctx.get('receipts.retry'))}</span></button>`
    : '';
}

async function onBar(event) {
  const button = event.target.closest('[data-bar]');
  if (!button) return;
  const action = button.dataset.bar;
  if (action === 'all') for (const row of rows) selected.add(row.id);
  else if (action === 'none') selected.clear();
  else {
    button.disabled = true;
    try { await (action === 'confirm' ? confirmSelected() : retrySelected()); }
    catch (error) { ctx.toast(error?.message || ctx.get('common.error')); }
    finally { button.disabled = false; }
    return;
  }
  draw();
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
  selected.clear();
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

// Eine Datei ist ein Beleg. Mehrere Fotos EINES Belegs gehoeren in den Scan-Dialog der Kaeufe-Seite,
// der sie zu einem Beleg zusammenfasst - hier wuerde daraus je Foto ein eigener Kauf.
async function upload() {
  const input = ctx.$('#receipts-file');
  const files = [...(input.files || [])];
  input.value = '';
  if (!files.length) return;
  const currency = document.getElementById('user-space-sub')?.textContent?.trim() || 'EUR';
  try {
    for (const file of files) {
      const form = new FormData();
      form.append('receipt', file, file.name);
      form.append('currency', currency);
      form.append('clientJobId', crypto.randomUUID());
      const job = await ctx.api('api/purchases/receipt-scan/jobs', { method: 'POST', body: form });
      await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/start`, { method: 'POST' });
    }
    ctx.toast(t(`${files.length} Beleg(e) in die Warteschlange gelegt.`, `${files.length} receipt(s) queued.`));
    if (tab !== 'queue') await switchTab('queue'); else await refresh(true);
  } catch (error) {
    ctx.toast(error?.message || ctx.get('common.error'));
  }
}

function draw() {
  for (const button of ctx.$('#view-receipts').querySelectorAll('[data-receipts-tab]')) {
    const active = button.dataset.receiptsTab === tab;
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', String(active));
  }
  // Solange die erste Seite noch aussteht, stehen Platzhalter da: so hoch wie die Zeilen, die kommen.
  const first = loading && !rows.length;
  const html = first
    ? skeleton(4)
    : (rows.length
      ? rows.map(row => (tab === 'queue' ? queueRow(row) : tab === 'review' ? reviewRow(row) : historyRow(row))).join('')
      : emptyRow(emptyText())) + (loading ? skeleton(2) : '');
  if (html !== drawn) {
    drawn = html;
    ctx.$('#receipts-list').innerHTML = html;
  }
  showBar();
}

function skeleton(count) {
  return Array.from({ length: count }, () => '<div class="receipts-row receipts-skeleton" aria-hidden="true"><span class="row-main"><span class="receipts-sk-line shimmer"></span><span class="receipts-sk-line short shimmer"></span></span></div>').join('');
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
  const reason = reasonLabel(job.reason);
  return `<span class="receipts-tag receipts-tag--${ctx.esc(job.engine)}">${ctx.esc(readerLabel(job.engine))}</span>${version}${reason ? ` <span class="receipts-reason">${ctx.esc(reason)}</span>` : ''}`;
}

function queueRow(job) {
  const running = job.status === 'processing';
  const progress = running
    ? `<span class="receipts-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${STAGE_PROGRESS[job.stage] ?? 8}"><span style="width:${STAGE_PROGRESS[job.stage] ?? 8}%"></span></span>`
    : '';
  const state = running
    ? `${ctx.esc(stageLabel(job))} · ${ctx.esc(ago(job.startedAt, true))}`
    : ctx.esc(t(`Position ${job.queuePosition} · wartet seit ${ago(job.createdAt, true)}`, `Position ${job.queuePosition} · waiting ${ago(job.createdAt, true)}`));
  return `<div class="receipts-row${running ? ' is-active' : ''}">
    <span class="row-main"><span class="receipts-name">${ctx.esc(name(job))}</span>
      <span class="row-sub">${state}</span>
      ${running ? `<span class="row-sub">${readerTag(job)}</span>` : ''}${progress}</span>
    <span class="receipts-tag receipts-tag--state">${ctx.esc(running ? t('In Arbeit', 'Working') : t('Wartet', 'Waiting'))}</span>
  </div>`;
}

function purchaseSub(job) {
  const date = job.purchaseDate ? ctx.date(job.purchaseDate) : '';
  const items = `${job.itemCount} ${t('Positionen', 'items')}`;
  return [date, items].filter(Boolean).map(part => ctx.esc(part)).join(' · ');
}

function checkbox(job) {
  return `<label class="fw-check"><input type="checkbox" data-select="${ctx.esc(job.id)}"${selected.has(job.id) ? ' checked' : ''} aria-label="${ctx.esc(t('Auswählen', 'Select'))}"></label>`;
}

function historyRow(job) {
  const failed = job.status === 'error';
  return `<div class="receipts-row${selected.has(job.id) ? ' is-selected' : ''}">
    ${failed ? checkbox(job) : ''}<span class="row-main"><span class="receipts-name">${ctx.esc(name(job))}${failed ? ` <span class="receipts-tag receipts-tag--failed">${ctx.esc(t('Fehler', 'Failed'))}</span>` : ''}</span>
      <span class="row-sub">${failed ? ctx.esc(job.error || '') : purchaseSub(job)} · ${ctx.esc(ago(job.completedAt))}</span>
      <span class="row-sub">${readerTag(job)}</span></span>
    ${failed ? '' : `<span class="amount">${ctx.money(job.totalAmount, job.currency)}</span>`}
  </div>`;
}

function reviewRow(job) {
  const warnings = parseWarnings(job.warningsJson);
  return `<div class="receipts-row${selected.has(job.id) ? ' is-selected' : ''}">
    ${checkbox(job)}<span class="row-main"><span class="receipts-name">${ctx.esc(name(job))}</span>
      <span class="row-sub">${purchaseSub(job)}</span>
      <span class="row-sub">${readerTag(job)}</span>
      ${warnings.length ? `<span class="row-sub receipts-warning">${ctx.esc(warnings[0])}${warnings.length > 1 ? ` (+${warnings.length - 1})` : ''}</span>` : ''}</span>
    <span class="amount">${ctx.money(job.totalAmount, job.currency)}</span>
    <a class="${buttonClass(ButtonRole.Secondary)}" href="/purchases#purchase=${ctx.esc(job.purchaseId)}">${ctx.esc(t('Ansehen', 'View'))}</a>
  </div>`;
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
