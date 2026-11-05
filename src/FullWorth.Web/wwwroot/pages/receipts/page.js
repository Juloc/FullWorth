// Belege: Warteschlange, Verlauf, Zu pruefen - und live, was der Server gerade liest.
//
// Die Liste fragt in kurzem Takt nach, solange etwas wartet oder laeuft, und selten, wenn nichts zu tun
// ist. Gezeichnet wird nur, was sich geaendert hat: ein Beleg, der gerade gelesen wird, soll seinen
// Balken weiterschieben, nicht die ganze Liste neu aufbauen.

import { emptyRow } from '../../components/empty.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

const TABS = ['queue', 'history', 'review', 'failed'];
const PAGE = 30;
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
let timer = null;

export function bindReceipts(context) {
  ctx = context;
  ctx.$('#view-receipts').addEventListener('click', onClick);
  ctx.$('#receipts-file').addEventListener('change', upload);
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
  const loaded = await load(0, Math.min(100, Math.max(PAGE, rows.length)));
  rows = loaded.rows;
  hasMore = loaded.more;
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

async function onClick(event) {
  const tabButton = event.target.closest('[data-receipts-tab]');
  if (tabButton) return switchTab(tabButton.dataset.receiptsTab);
  const button = event.target.closest('[data-action]');
  if (!button) return;
  button.disabled = true;
  try {
    if (button.dataset.action === 'more') await more();
    else if (button.dataset.action === 'confirm') await confirmPurchase(button.dataset.purchase);
    else if (button.dataset.action === 'retry') await retry(button.dataset.job);
  } catch (error) {
    ctx.toast(error?.message || ctx.get('common.error'));
  } finally {
    button.disabled = false;
  }
}

async function switchTab(next) {
  if (next === tab) return;
  tab = next;
  history.replaceState(null, '', `#${tab}`);
  rows = [];
  lastCountsKey = '';
  drawn = '';
  await refresh(true);
}

async function more() {
  const loaded = await load(rows.length, PAGE);
  rows = rows.concat(loaded.rows);
  hasMore = loaded.more;
  draw();
}

async function confirmPurchase(purchaseId) {
  await ctx.api(`api/purchases/${purchaseId}/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ createSafeAllocations: true, allowUnlinked: true })
  });
  await refresh(true);
}

async function retry(jobId) {
  await ctx.api(`api/purchases/receipt-scan/jobs/${jobId}/retry`, { method: 'POST' });
  await refresh(true);
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
  // Die Minuten "vor 3 Min" laufen weiter, auch wenn sich sonst nichts aendert - sie gehoeren deshalb
  // nicht in den Vergleich, sondern werden bei jedem Zeichnen frisch gerechnet.
  const html = rows.length
    ? rows.map(row => (tab === 'queue' ? queueRow(row) : tab === 'review' ? reviewRow(row) : historyRow(row))).join('')
      + (hasMore ? `<div class="receipts-more"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-action="more">${ctx.esc(t('Mehr anzeigen', 'Show more'))}</button></div>` : '')
    : emptyRow(emptyText());
  if (html === drawn) return;
  drawn = html;
  ctx.$('#receipts-list').innerHTML = html;
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
  return `<span class="receipts-tag receipts-tag--${ctx.esc(job.engine)}">${ctx.esc(readerLabel(job.engine))}</span>${version}${reason ? ` <span class="row-sub">${ctx.esc(reason)}</span>` : ''}`;
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

function historyRow(job) {
  const failed = job.status === 'error';
  return `<div class="receipts-row">
    <span class="row-main"><span class="receipts-name">${ctx.esc(name(job))}${failed ? ` <span class="receipts-tag receipts-tag--failed">${ctx.esc(t('Fehler', 'Failed'))}</span>` : ''}</span>
      <span class="row-sub">${failed ? ctx.esc(job.error || '') : purchaseSub(job)} · ${ctx.esc(ago(job.completedAt))}</span>
      <span class="row-sub">${readerTag(job)}</span></span>
    ${failed
      ? `<button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-action="retry" data-job="${ctx.esc(job.id)}">${ctx.esc(t('Erneut', 'Retry'))}</button>`
      : `<span class="amount">${ctx.money(job.totalAmount, job.currency)}</span>`}
  </div>`;
}

function reviewRow(job) {
  const warnings = parseWarnings(job.warningsJson);
  return `<div class="receipts-row">
    <span class="row-main"><span class="receipts-name">${ctx.esc(name(job))}</span>
      <span class="row-sub">${purchaseSub(job)}</span>
      <span class="row-sub">${readerTag(job)}</span>
      ${warnings.length ? `<span class="row-sub receipts-warning">${ctx.esc(warnings[0])}${warnings.length > 1 ? ` (+${warnings.length - 1})` : ''}</span>` : ''}</span>
    <span class="amount">${ctx.money(job.totalAmount, job.currency)}</span>
    <span class="receipts-actions">
      <a class="${buttonClass(ButtonRole.Secondary)}" href="/purchases#purchase=${ctx.esc(job.purchaseId)}">${ctx.esc(t('Ansehen', 'View'))}</a>
      <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-action="confirm" data-purchase="${ctx.esc(job.purchaseId)}">${ctx.esc(t('Bestätigen', 'Confirm'))}</button>
    </span>
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
