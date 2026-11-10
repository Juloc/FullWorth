import { api as sharedApi, jsonBody } from '../core/services.js';
// Web Push for this device. The browser holds a subscription bound to the server's public VAPID key,
// the server holds a row for it - "on" means both exist, agree, and the browser still allows it.
// Notification.permission alone proves none of that: it stays "granted" after a subscription is gone,
// replaced or rejected by the server, and a script can never take it back.

// serviceWorker.ready never settles when no worker is registered, which would hang the page.
const READY_TIMEOUT_MS = 4000;

const isSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;

function readyRegistration() {
  return Promise.race([
    navigator.serviceWorker.ready,
    new Promise(resolve => setTimeout(() => resolve(null), READY_TIMEOUT_MS)),
  ]);
}

function keyBytes(base64) {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, c => c.charCodeAt(0));
}

// A subscription made for another key can neither be reused nor coexist with a new one.
function boundTo(subscription, publicKey) {
  const bound = subscription.options.applicationServerKey;
  if (!bound || !publicKey) return false;
  const mine = new Uint8Array(bound);
  const theirs = keyBytes(publicKey);
  return mine.length === theirs.length && mine.every((byte, i) => byte === theirs[i]);
}

/**
 * The true state of this device: 'unsupported' | 'denied' | 'no-worker' | 'off' | 'on' |
 * 'stale' (a subscription exists but cannot receive: other key, unknown to the server, permission gone) |
 * 'unverified' (a subscription exists but the server cannot be asked).
 */
export async function pushStatus() {
  if (!isSupported()) return 'unsupported';
  const registration = await readyRegistration();
  const subscription = registration && await registration.pushManager.getSubscription();
  if (!subscription) return Notification.permission === 'denied' ? 'denied' : registration ? 'off' : 'no-worker';
  if (Notification.permission !== 'granted') return 'stale';

  let publicKey;
  let devices;
  try {
    [{ publicKey }, devices] = await Promise.all([sharedApi('api/push/vapid-public-key'), sharedApi('api/push/subscriptions')]);
  } catch {
    return 'unverified';
  }
  return boundTo(subscription, publicKey) && devices.some(d => d.endpoint === subscription.endpoint) ? 'on' : 'stale';
}

// Takes this device off the server (its row is matched by endpoint, which is gone once the browser
// has unsubscribed) and out of the browser. The browser step happens even when the server cannot be
// reached: a device that keeps receiving is worse than a row that lingers until the push service
// answers 410. The caller is told, so nothing is swallowed.
async function release(subscription) {
  let serverRemoved = true;
  try {
    const devices = await sharedApi('api/push/subscriptions');
    const row = devices.find(d => d.endpoint === subscription.endpoint);
    if (row) await sharedApi(`api/push/subscriptions/${row.id}`, { method: 'DELETE' });
  } catch {
    serverRemoved = false;
  }
  return { unsubscribed: await subscription.unsubscribe(), serverRemoved };
}

// Call from a user gesture. Resolves to { ok: true } or { ok: false, reason }.
export async function enablePush() {
  if (!isSupported()) return { ok: false, reason: 'unsupported' };
  let publicKey;
  try {
    ({ publicKey } = await sharedApi('api/push/vapid-public-key'));
  } catch {
    return { ok: false, reason: 'no-key' };
  }
  if (!publicKey) return { ok: false, reason: 'not-configured' };

  if (await Notification.requestPermission() !== 'granted') return { ok: false, reason: 'denied' };

  const registration = await readyRegistration();
  if (!registration) return { ok: false, reason: 'no-worker' };

  const existing = await registration.pushManager.getSubscription();
  if (existing && !boundTo(existing, publicKey) && !(await release(existing)).unsubscribed) {
    return { ok: false, reason: 'subscribe' };
  }

  let subscription;
  try {
    subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
  } catch {
    return { ok: false, reason: 'subscribe' };
  }
  const keys = subscription.toJSON().keys || {};
  try {
    await sharedApi('api/push/subscriptions', jsonBody({
      endpoint: subscription.endpoint,
      p256dh: keys.p256dh,
      auth: keys.auth,
      deviceLabel: navigator.userAgent.slice(0, 100),
    }));
  } catch {
    return { ok: false, reason: 'save' };
  }
  return { ok: true };
}

// Works in every state. Resolves to { ok, serverRemoved }: ok is false only when the browser itself
// refused to unsubscribe.
export async function disablePush() {
  if (!isSupported()) return { ok: true, serverRemoved: true };
  const registration = await readyRegistration();
  const subscription = registration && await registration.pushManager.getSubscription();
  if (!subscription) return { ok: true, serverRemoved: true };
  const { unsubscribed, serverRemoved } = await release(subscription);
  return { ok: unsubscribed, serverRemoved };
}
