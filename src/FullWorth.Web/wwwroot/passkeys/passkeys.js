import '../security/browser-fetch.js';
import { decodeBase64Url, encodeBase64Url } from './base64url.js';

const endpoints = {
  loginBegin: '/auth/passkeys/login/begin',
  loginComplete: '/auth/passkeys/login/complete',
  registerBegin: '/auth/passkeys/register/begin',
  registerComplete: '/auth/passkeys/register/complete',
  credentials: '/auth/passkeys'
};

export function isPasskeySupported() {
  return Boolean(window.PublicKeyCredential && navigator.credentials && window.isSecureContext);
}

function convertCredentialDescriptors(items = []) {
  return items.map(credential => ({ ...credential, id: decodeBase64Url(credential.id) }));
}

function convertCreationOptions(payload) {
  const source = payload.publicKey ?? payload;
  return {
    ...source,
    challenge: decodeBase64Url(source.challenge),
    user: { ...source.user, id: decodeBase64Url(source.user.id) },
    excludeCredentials: convertCredentialDescriptors(source.excludeCredentials)
  };
}

function convertRequestOptions(payload) {
  const source = payload.publicKey ?? payload;
  return {
    ...source,
    challenge: decodeBase64Url(source.challenge),
    allowCredentials: convertCredentialDescriptors(source.allowCredentials)
  };
}

function serializeRegistration(credential) {
  return {
    id: credential.id,
    rawId: encodeBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: encodeBase64Url(credential.response.clientDataJSON),
      attestationObject: encodeBase64Url(credential.response.attestationObject),
      transports: credential.response.getTransports?.() ?? []
    },
    clientExtensionResults: credential.getClientExtensionResults?.() ?? {}
  };
}

function serializeAssertion(credential) {
  return {
    id: credential.id,
    rawId: encodeBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: encodeBase64Url(credential.response.clientDataJSON),
      authenticatorData: encodeBase64Url(credential.response.authenticatorData),
      signature: encodeBase64Url(credential.response.signature),
      userHandle: credential.response.userHandle ? encodeBase64Url(credential.response.userHandle) : null
    },
    clientExtensionResults: credential.getClientExtensionResults?.() ?? {}
  };
}

async function requestJson(url, options = {}) {
  const response = await fetch(url, {
    credentials: 'same-origin',
    cache: 'no-store',
    headers: { Accept: 'application/json', ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    const error = new Error('Passkey request failed.');
    error.status = response.status;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

function errorKey(error, prefix) {
  if (error?.status === 429) return prefix === 'auth' ? 'auth.passkeyRetryLater' : 'passkeys.retryLater';
  switch (error?.name) {
    case 'NotAllowedError':
    case 'AbortError': return prefix === 'auth' ? 'auth.passkeyCancelled' : 'passkeys.cancelled';
    case 'InvalidStateError': return prefix === 'auth' ? 'auth.passkeyInvalidState' : 'passkeys.invalidState';
    case 'SecurityError': return prefix === 'auth' ? 'auth.passkeySecurityError' : 'passkeys.securityError';
    default: return prefix === 'auth' ? 'auth.passkeyCouldNotUse' : 'passkeys.error';
  }
}

// Shared WebAuthn assertion (get): used by both login and the inactivity-lock unlock. The only
// difference between them is the begin/complete URLs, so the convert/serialize/base64url path lives
// here once instead of being duplicated per caller.
export async function assertPasskey({ beginUrl, completeUrl, extraBody = {} }) {
  const begin = await requestJson(beginUrl, { method: 'POST' });
  const publicKey = convertRequestOptions(begin);
  const credential = await navigator.credentials.get({ publicKey });
  if (!credential) throw new DOMException('No credential returned.', 'NotAllowedError');
  return requestJson(completeUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ challengeId: begin.challengeId, credential: serializeAssertion(credential), ...extraBody })
  });
}

// Map a passkey/WebAuthn failure to an i18n key; exported so the lock screen reuses the same mapping.
export function passkeyErrorKey(error, prefix) {
  return errorKey(error, prefix);
}

export function initializePasskeyLogin({ button, statusElement: status, message, onSuccess }) {
  if (!button) return;
  if (!isPasskeySupported()) {
    button.disabled = true;
    if (status) {
      status.hidden = false;
      status.textContent = message('auth.passkeyUnsupported');
    }
    return;
  }

  button.addEventListener('click', async () => {
    button.disabled = true;
    if (status) {
      status.hidden = false;
      status.textContent = message('auth.passkeySigningIn');
    }
    try {
      const payload = await assertPasskey({ beginUrl: endpoints.loginBegin, completeUrl: endpoints.loginComplete });
      onSuccess?.(payload);
    } catch (error) {
      if (status) status.textContent = message(errorKey(error, 'auth'));
    } finally {
      button.disabled = false;
    }
  });
}

export function initializePasskeyManagement({ root, message, locale = 'de' }) {
  if (!root) return;
  const add = root.querySelector('[data-passkey-action="add"]');
  const name = root.querySelector('#passkey-name');
  const list = root.querySelector('#passkey-list');
  const status = root.querySelector('#passkey-management-status');

  const show = text => {
    if (!status) return;
    status.hidden = false;
    status.textContent = text;
  };

  const formatDate = value => value
    ? new Intl.DateTimeFormat(locale === 'de' ? 'de-DE' : 'en-US').format(new Date(value))
    : message('passkeys.neverUsed');

  async function load() {
    try {
      const credentials = await requestJson(endpoints.credentials);
      list.textContent = '';
      if (!credentials?.length) {
        const empty = document.createElement('div');
        empty.className = 'passkey-empty';
        empty.textContent = message('passkeys.empty');
        list.appendChild(empty);
        return;
      }
      for (const credential of credentials) {
        const row = document.createElement('article');
        row.className = 'passkey-item';
        const details = document.createElement('div');
        const title = document.createElement('strong');
        title.textContent = credential.displayName || credential.name || message('passkeys.unnamed');
        const meta = document.createElement('span');
        meta.textContent = `${message('passkeys.created')}: ${formatDate(credential.createdAt)} · ${message('passkeys.lastUsed')}: ${formatDate(credential.lastUsedAt)}`;
        details.append(title, meta);
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.dataset.passkeyAction = 'remove';
        remove.dataset.managementId = credential.id;
        remove.textContent = message('passkeys.remove');
        remove.addEventListener('click', async event => {
          const button = event.currentTarget;
          const managementId = button.dataset.managementId;
          if (!managementId || !window.confirm(message('passkeys.removeConfirm'))) return;
          button.disabled = true;
          try {
            await requestJson(`${endpoints.credentials}/${encodeURIComponent(managementId)}`, { method: 'DELETE' });
            show(message('passkeys.removed'));
            await load();
          } catch (error) {
            show(message(errorKey(error, 'passkeys')));
            button.disabled = false;
          }
        });
        row.append(details, remove);
        list.appendChild(row);
      }
    } catch (error) {
      if (error.status === 401) show(message('passkeys.signInRequired'));
      else show(message(errorKey(error, 'passkeys')));
    }
  }

  if (!isPasskeySupported()) {
    if (add) add.disabled = true;
    show(message('passkeys.unsupported'));
  } else if (add) {
    add.addEventListener('click', async () => {
      add.disabled = true;
      show(message('passkeys.adding'));
      try {
        const displayName = String(name?.value || '').trim().slice(0, 80) || message('passkeys.unnamed');
        const begin = await requestJson(endpoints.registerBegin, { method: 'POST' });
        const publicKey = convertCreationOptions(begin);
        const credential = await navigator.credentials.create({ publicKey });
        if (!credential) throw new DOMException('No credential returned.', 'NotAllowedError');
        await requestJson(endpoints.registerComplete, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ challengeId: begin.challengeId, displayName, credential: serializeRegistration(credential) })
        });
        if (name) name.value = '';
        show(message('passkeys.added'));
        await load();
      } catch (error) {
        show(message(errorKey(error, 'passkeys')));
      } finally {
        add.disabled = false;
      }
    });
  }

  void load();
}
