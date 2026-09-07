import { api as sharedApi, jsonBody } from '../core/services.js';
// Web Push subscription helper (Wave K2). Call enablePush() from a user gesture (e.g. a
// "Enable notifications" toggle). No-ops gracefully when push is unsupported or the server has no
// VAPID key configured. The subscription is stored server-side via the BFF.

export async function enablePush() {
  if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
    return { ok: false, reason: 'unsupported' };
  }
  let publicKey;
  try {
    ({ publicKey } = await sharedApi('api/push/vapid-public-key'));
  } catch {
    return { ok: false, reason: 'no-key' };
  }
  if (!publicKey) return { ok: false, reason: 'not-configured' };

  const permission = await Notification.requestPermission();
  if (permission !== 'granted') return { ok: false, reason: 'denied' };

  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(publicKey),
  });
  const keys = subscription.toJSON().keys || {};
  try {
    await sharedApi('api/push/subscriptions', jsonBody({
      endpoint: subscription.endpoint,
      p256dh: keys.p256dh,
      auth: keys.auth,
      deviceLabel: navigator.userAgent.slice(0, 100),
    }));
    return { ok: true };
  } catch {
    return { ok: false };
  }
}

export async function disablePush() {
  if (!('serviceWorker' in navigator)) return { ok: true };
  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.getSubscription();
  if (!subscription) return { ok: true };
  // Delete the server-side row too (matched by endpoint), otherwise it lingers and keeps receiving
  // push sends after the browser has unsubscribed.
  const endpoint = subscription.endpoint;
  await subscription.unsubscribe();
  try {
    const devices = await sharedApi('api/push/subscriptions').catch(() => []);
    const match = (devices || []).find(d => d.endpoint === endpoint);
    if (match) await sharedApi(`api/push/subscriptions/${match.id}`, { method: 'DELETE' });
  } catch { /* browser is already unsubscribed; a stale server row is harmless, so don't fail the toggle */ }
  return { ok: true };
}

function urlBase64ToUint8Array(base64) {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(normalized);
  return Uint8Array.from([...raw].map((c) => c.charCodeAt(0)));
}
