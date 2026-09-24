// Der Kontext, den jedes Seitenmodul bekommt — und der Start einer einzelnen Seite (#154).
//
// Bisher baute app.js dieses Objekt einmal zusammen und reichte es an jeden Seitenrenderer weiter.
// Das ging nur, weil alle Seiten in einem Dokument lagen. Eine echte Seite muss ihn selbst erzeugen,
// und genau das ist die Arbeit, die alle weiteren Seiten danach billig macht: ein Seitenmodul ändert
// sich dafür nicht, es bekommt denselben Kontext wie vorher.
//
// Drei Felder heißen gleich und bedeuten etwas anderes:
//
//   reload     war „zeichne die aktive Ansicht neu" — jetzt „zeichne DIESE Seite neu".
//   showView   war ein Ansichtswechsel im selben Dokument — jetzt eine echte Navigation.
//   navScope   dasselbe mit Abfrageteil.
//
// Das ist kein zweiter Router: es gibt genau einen Weg zu einer anderen Seite, und der ist die
// Adresse. `location.assign` und ein Klick auf einen Link tun dasselbe.

import { money, setMoneyLocale } from '../components/money.js';
import { isPrivate } from '../components/privacy.js';
import { confirmDialog } from '../components/confirm.js';
import { createDialog } from '../components/dialog.js';
import { createToast } from '../components/toast.js';
import { emptyRow } from '../components/empty.js';
import { apiClient, api, bankApi, i18n, jsonBody } from '../core/services.js';
import { state } from '../core/state.js';
import { pathForView } from './routes.js';

const $ = selector => document.querySelector(selector);
const get = path => i18n.get(path);

function esc(value) {
  return String(value ?? '').replace(/[&<>'"]/g, character =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));
}

function date(value) {
  if (!value) return '—';
  return new Intl.DateTimeFormat(state.lang === 'de' ? 'de-DE' : 'en-US')
    .format(new Date(`${String(value).slice(0, 10)}T12:00:00`));
}

function dateTime(value) {
  if (!value) return '—';
  const raw = String(value);
  if (!/[T ]\d{2}:\d{2}/.test(raw)) return date(value);
  const parsed = new Date(raw);
  if (Number.isNaN(parsed.getTime())) return date(value);
  return new Intl.DateTimeFormat(state.lang === 'de' ? 'de-DE' : 'en-US',
    { dateStyle: 'medium', timeStyle: 'medium' }).format(parsed);
}

function skeleton(element, rows = 4) {
  element.innerHTML = Array.from({ length: rows },
    () => '<div class="row skel"><div class="skel-bar shimmer"></div><div class="skel-bar short shimmer"></div></div>').join('');
}

async function categoryOptions(selected) {
  const categories = await api('api/categories');
  const byId = new Map(categories.map(category => [category.id, category]));
  const path = category => {
    const chain = [];
    let current = category;
    while (current) {
      chain.unshift(current.name);
      current = current.parentId ? byId.get(current.parentId) : null;
    }
    return chain.join(' › ');
  };
  return categories
    .map(category => `<option value="${category.id}"${category.id === selected ? ' selected' : ''}>${esc(path(category))}</option>`)
    .join('');
}

/**
 * Der Raum, in dem gearbeitet wird. Jede fachliche Abfrage hängt daran, deshalb lädt ihn jede Seite,
 * bevor sie zeichnet — und merkt sich die Wahl, damit ein Seitenwechsel sie nicht vergisst.
 */
export async function loadSpaces() {
  const spaces = await api('api/fullworth-spaces');
  state.spaces = spaces || [];
  const saved = localStorage.getItem('finance.space');
  state.space = state.spaces.find(space => space.id === saved) || state.spaces[0] || null;
  if (state.space) localStorage.setItem('finance.space', state.space.id);
}

async function loadCapabilities() {
  try {
    const response = await fetch('/auth/capabilities', { cache: 'no-store' });
    if (response.ok) state.capabilities = await response.json();
  } catch { /* Eine Seite ohne bekannte Rechte zeigt weniger, sie bricht nicht ab. */ }
}

export function createPageContext({ reload = () => location.reload() } = {}) {
  const toastController = createToast($('#toast'));
  const toast = (text, duration) => toastController.show(text, duration);

  const context = {
    $,
    api,
    bankApi,
    get,
    esc,
    date,
    dateTime,
    toast,
    money,
    isPrivate,
    jsonBody,
    categoryOptions,
    skeleton,
    reload,
    dialog: (html, options = {}) => createDialog(html, { closeLabel: get('common.close'), ...options }),
    empty: (element, message) => { element.innerHTML = emptyRow(message || get('common.empty')); },
    confirm: (message, options) => confirmDialog(context, message, options),
    bffUrl: path => apiClient.backendUrl(path),
    // api() parst jede Antwort als JSON. Ein Endpunkt, der bewusst ein Dokument liefert (das
    // Kündigungsschreiben ist text/plain), scheiterte daran - dieselbe Anfrage über denselben
    // Client, nur ohne JSON.parse.
    apiText: path => apiClient.backendResponse(path).then(response => response.text()),
    // Mit Mitgabe: auf einer eigenen Seite ist das eine echte Navigation, und ein Ereignis, das
    // danach abgeschickt wird, trifft niemanden mehr - das Dokument, das zuhoeren wuerde, wird
    // gerade abgebaut. Was die Zielseite wissen muss, steht deshalb in der Adresse.
    showView: (view, options = {}) => {
      const query = options.query ? '?' + String(options.query).replace(/^\?/, '') : '';
      location.assign(pathForView(view) + query);
    },
    navScope: (view, query) => {
      const target = pathForView(view);
      location.assign(query ? `${target}?${String(query).replace(/^\?/, '')}` : target);
    }
  };
  return context;
}

/**
 * Was jede Seite vor dem Zeichnen braucht: Zahlenformat, Rechte und Raum.
 *
 * Nicht die Sprache und nicht den Rahmen — das macht die Shell, und zwar vor dem Zeichnen. Hier
 * steht nur, was eine fachliche Abfrage voraussetzt.
 */
export async function loadSession(onError) {
  setMoneyLocale(state.lang);
  await loadCapabilities();
  try {
    await loadSpaces();
  } catch (error) {
    console.error(error);
    onError?.(get('common.error'));
  }
}
