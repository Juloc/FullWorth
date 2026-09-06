import { initializePasskeyLogin } from '../passkeys/passkeys.js';

const preferences = {
  language: localStorage.getItem('finance.language') || ((navigator.language || 'de').startsWith('de') ? 'de' : 'en'),
  theme: localStorage.getItem('finance.theme') || 'system'
};

const endpoints = Object.freeze({
  providers: '/auth/providers',
  login: '/auth/login',
  register: '/auth/register',
  passwordResetRequest: '/auth/password-reset/request',
  passwordResetComplete: '/auth/password-reset/complete',
  recoveryCodeRedeem: '/auth/recovery-code/redeem',
  claim: '/auth/claim'
});

const media = matchMedia('(prefers-color-scheme: dark)');
const supportedViews = new Set(['login', 'two-factor', 'register', 'forgot-password', 'reset-password', 'recovery-code', 'recovery-codes', 'claim']);
const state = {
  messages: {},
  view: resolveView(),
  generatedRecoveryCodes: [],
  pendingLogin: null,
  capabilities: {
    registrationEnabled: false,
    google: false,
    apple: false
  }
};

const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];

function resolveView() {
  const queryView = new URLSearchParams(location.search).get('view');
  if (queryView && supportedViews.has(queryView)) return queryView;

  const path = location.pathname.replace(/\/+$/, '');
  const last = path.split('/').at(-1);
  if (supportedViews.has(last)) return last;

  return 'login';
}

async function boot() {
  updateLanguageButton();
  updateThemeButton();
  applyTheme();
  await loadMessages();
  await loadCapabilities();
  bind();
  showView(state.view);
  applyCapabilities();
  showSessionStatus();
  prepareResetView();
}

function get(path) {
  return path.split('.').reduce((value, key) => value?.[key], state.messages) || path;
}

async function loadMessages() {
  const response = await fetch(`/locales/${preferences.language}.json`, { cache: 'no-store' });
  if (!response.ok) throw new Error(`Locale request failed: ${response.status}`);
  state.messages = await response.json();
  document.documentElement.lang = preferences.language;
  renderTranslations();
  updateDocumentTitle();
}

async function loadCapabilities() {
  try {
    const response = await fetch(endpoints.providers, {
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' }
    });
    if (!response.ok) return;
    const payload = await response.json();
    state.capabilities = {
      registrationEnabled: payload?.registrationEnabled === true,
      google: payload?.google === true,
      apple: payload?.apple === true
    };
  } catch {
    // Email/password and passkeys remain available if provider discovery fails.
  }
}

function applyCapabilities() {
  $$('[data-registration-link]').forEach(link => {
    link.hidden = !state.capabilities.registrationEnabled;
  });

  const registrationForm = $('#register-form');
  const registrationDisabled = $('#registration-disabled-state');
  if (registrationForm && state.view === 'register') {
    registrationForm.hidden = !state.capabilities.registrationEnabled;
    if (state.capabilities.registrationEnabled) hideMessage(registrationDisabled);
    else showMessage(registrationDisabled);
  }

  for (const context of ['login', 'register']) {
    const container = document.querySelector(`[data-auth-social="${context}"]`);
    const divider = document.querySelector(`[data-auth-social-divider="${context}"]`);
    if (!container) continue;

    let visible = false;
    container.querySelectorAll('[data-external-provider]').forEach(button => {
      const provider = button.dataset.externalProvider;
      const enabled = Boolean(state.capabilities[provider])
        && (context !== 'register' || state.capabilities.registrationEnabled);
      button.hidden = !enabled;
      visible ||= enabled;
    });

    container.hidden = !visible;
    if (divider) divider.hidden = !visible;
  }
}

function renderTranslations() {
  $$('[data-i18n]').forEach(element => {
    element.textContent = get(element.dataset.i18n);
  });
  $$('[data-i18n-placeholder]').forEach(element => {
    element.placeholder = get(element.dataset.i18nPlaceholder);
  });
  $$('[data-i18n-aria-label]').forEach(element => {
    element.setAttribute('aria-label', get(element.dataset.i18nAriaLabel));
  });
  $$('[data-i18n-title]').forEach(element => {
    element.title = get(element.dataset.i18nTitle);
  });
  renderRemainingCount();
}

function updateDocumentTitle() {
  const page = state.messages.auth?.pages?.[state.view];
  document.title = page?.title ? `${page.title} · FullWorth` : 'FullWorth';
}

function applyTheme() {
  const actual = preferences.theme === 'system'
    ? (media.matches ? 'dark' : 'light')
    : preferences.theme;
  document.documentElement.dataset.theme = actual;
}

function updateLanguageButton() {
  const label = document.getElementById('auth-language-label');
  if (label) label.textContent = preferences.language.toUpperCase();
}

function updateThemeButton() {
  const button = document.getElementById('auth-theme');
  if (button) button.dataset.themePref = preferences.theme;
}

function bind() {
  $('#auth-language').addEventListener('click', async () => {
    const order = ['de', 'en'];
    preferences.language = order[(order.indexOf(preferences.language) + 1) % order.length] ?? 'de';
    localStorage.setItem('finance.language', preferences.language);
    updateLanguageButton();
    await loadMessages();
  });

  $('#auth-theme').addEventListener('click', () => {
    const order = ['system', 'light', 'dark'];
    preferences.theme = order[(order.indexOf(preferences.theme) + 1) % order.length] ?? 'system';
    localStorage.setItem('finance.theme', preferences.theme);
    applyTheme();
    updateThemeButton();
  });

  media.addEventListener('change', () => {
    if (preferences.theme === 'system') applyTheme();
  });

  $$('#login-form, #two-factor-form, #register-form, #forgot-form, #reset-form, #recovery-code-form, #claim-form').forEach(form => {
    form.addEventListener('submit', handleSubmit);
  });

  $$('[data-external-provider]').forEach(button => {
    button.addEventListener('click', () => {
      const provider = button.dataset.externalProvider;
      const mode = button.dataset.externalMode === 'register' ? 'register' : 'login';
      if (!provider) return;

      const parameters = new URLSearchParams({ mode });
      const returnUrl = new URLSearchParams(location.search).get('returnUrl');
      if (returnUrl) parameters.set('returnUrl', resolveSafeReturnPath(returnUrl));
      location.assign(`/auth/external/${encodeURIComponent(provider)}?${parameters}`);
    });
  });

  $$('[data-auth-action="toggle-password"]').forEach(button => {
    button.addEventListener('click', togglePassword);
  });

  loadAppVersion();

  initializePasskeyLogin({
    button: $('[data-auth-action="passkey-login"]'),
    statusElement: $('#passkey-status'),
    emailInput: $('#login-email'),
    message: get,
    onSuccess: payload => location.assign(resolveSafeReturnPath(payload?.returnUrl))
  });

  $('#copy-recovery-codes').addEventListener('click', copyRecoveryCodes);

  window.addEventListener('finance:recovery-codes', event => {
    renderRecoveryCodes(event.detail?.codes, event.detail?.remainingCount);
  });

  window.addEventListener('beforeunload', () => {
    state.generatedRecoveryCodes = [];
  });
}

async function handleSubmit(event) {
  event.preventDefault();
  switch (event.currentTarget.id) {
    case 'login-form':
      await submitLogin(event.currentTarget);
      break;
    case 'two-factor-form':
      await submitTwoFactor(event.currentTarget);
      break;
    case 'register-form':
      await submitRegister(event.currentTarget);
      break;
    case 'forgot-form':
      await submitForgotPassword(event.currentTarget);
      break;
    case 'reset-form':
      await submitResetPassword(event.currentTarget);
      break;
    case 'recovery-code-form':
      await submitRecoveryCode(event.currentTarget);
      break;
    case 'claim-form':
      await submitClaim(event.currentTarget);
      break;
  }
}

async function submitClaim(form) {
  hideMessage($('#claim-invalid'));
  hideMessage($('#claim-error'));
  hideMessage($('#claim-existing'));
  hideMessage($('#claim-password-mismatch'));

  if (!form.reportValidity()) return;

  const newPassword = $('#claim-new-password').value;
  const confirmPassword = $('#claim-confirm-password').value;
  if (newPassword !== confirmPassword) {
    $('#claim-confirm-password').setAttribute('aria-invalid', 'true');
    showMessage($('#claim-password-mismatch'));
    $('#claim-confirm-password').focus();
    return;
  }
  $('#claim-confirm-password').removeAttribute('aria-invalid');

  const token = new URLSearchParams(location.search).get('token');
  if (!token) {
    showMessage($('#claim-invalid'));
    form.hidden = true;
    return;
  }

  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const response = await postJson(endpoints.claim, { token, newPassword });

    if (response.ok) {
      const payload = await readJson(response);
      // The invitee already had a login (shared into a further space): access is granted, but we can't
      // sign them in — send them to the normal login. Otherwise the server signed them in already.
      if (payload?.existingLogin) {
        form.hidden = true;
        showMessage($('#claim-existing'));
        return;
      }
      location.assign('/');
      return;
    }

    if (response.status === 400) {
      showMessage($('#claim-invalid'));
    } else {
      showMessage($('#claim-error'));
    }
  } catch {
    showMessage($('#claim-error'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitLogin(form) {
  hideMessage($('#login-error'));
  hideMessage($('#login-unavailable'));

  if (!form.reportValidity()) return;
  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const body = new FormData(form);
    const response = await postJson(endpoints.login, {
      email: String(body.get('email') || ''),
      password: String(body.get('password') || '')
    });

    const payload = await readJson(response);
    if (response.ok) {
      state.pendingLogin = null;
      location.assign(resolveSafeReturnPath(payload?.returnUrl));
      return;
    }

    if (payload?.requiresTwoFactor) {
      state.pendingLogin = {
        email: String(body.get('email') || ''),
        password: String(body.get('password') || '')
      };
      showView('two-factor');
      $('#two-factor-code')?.focus();
      return;
    }

    if (response.status === 401 || response.status === 400) {
      showMessage($('#login-error'));
    } else {
      showMessage($('#login-unavailable'));
    }
  } catch {
    showMessage($('#login-unavailable'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitTwoFactor(form) {
  hideMessage($('#two-factor-error'));
  if (!form.reportValidity() || !state.pendingLogin) {
    showView('login');
    return;
  }

  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const body = new FormData(form);
    const response = await postJson(endpoints.login, {
      email: state.pendingLogin.email,
      password: state.pendingLogin.password,
      code: String(body.get('code') || '')
    });
    const payload = await readJson(response);

    if (response.ok) {
      state.pendingLogin = null;
      location.assign(resolveSafeReturnPath(payload?.returnUrl));
      return;
    }

    showMessage($('#two-factor-error'));
    $('#two-factor-code')?.select();
  } catch {
    showMessage($('#two-factor-error'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitRegister(form) {
  hideMessage($('#register-error'));
  hideMessage($('#register-disabled'));
  hideMessage($('#register-password-mismatch'));

  if (!form.reportValidity()) return;

  const password = $('#register-password').value;
  const confirmPassword = $('#register-confirm-password').value;
  if (password !== confirmPassword) {
    $('#register-confirm-password').setAttribute('aria-invalid', 'true');
    showMessage($('#register-password-mismatch'));
    $('#register-confirm-password').focus();
    return;
  }

  $('#register-confirm-password').removeAttribute('aria-invalid');
  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const body = new FormData(form);
    const response = await postJson(endpoints.register, {
      displayName: String(body.get('displayName') || ''),
      email: String(body.get('email') || ''),
      password,
      acceptTerms: body.get('acceptTerms') === 'on',
      confirmAdult: body.get('confirmAdult') === 'on'
    });

    if (response.ok) {
      const payload = await readJson(response);
      location.assign(resolveSafeReturnPath(payload?.returnUrl));
      return;
    }

    if (response.status === 403) showMessage($('#register-disabled'));
    else showMessage($('#register-error'));
  } catch {
    showMessage($('#register-error'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitForgotPassword(form) {
  hideMessage($('#forgot-error'));
  if (!form.reportValidity()) return;

  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const body = new FormData(form);
    await postJson(endpoints.passwordResetRequest, {
      email: String(body.get('email') || '')
    });

    form.hidden = true;
    showMessage($('#forgot-confirmation'));
  } catch {
    showMessage($('#forgot-error'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitResetPassword(form) {
  hideMessage($('#reset-error'));
  hideMessage($('#reset-invalid'));
  hideMessage($('#password-mismatch'));

  if (!form.reportValidity()) return;

  const newPassword = $('#new-password').value;
  const confirmPassword = $('#confirm-password').value;
  if (newPassword !== confirmPassword) {
    $('#confirm-password').setAttribute('aria-invalid', 'true');
    showMessage($('#password-mismatch'));
    $('#confirm-password').focus();
    return;
  }

  $('#confirm-password').removeAttribute('aria-invalid');
  const parameters = new URLSearchParams(location.search);
  const token = parameters.get('token');
  const email = parameters.get('email');
  if (!token || !email) {
    showInvalidReset();
    return;
  }

  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const response = await postJson(endpoints.passwordResetComplete, { email, token, newPassword });

    if (response.ok) {
      form.hidden = true;
      form.parentElement.querySelector('.auth-links')?.setAttribute('hidden', '');
      showMessage($('#reset-success'));
    } else if ([400, 404, 410].includes(response.status)) {
      showInvalidReset();
    } else {
      showMessage($('#reset-error'));
    }
  } catch {
    showMessage($('#reset-error'));
  } finally {
    setSubmitting(button, false);
  }
}

async function submitRecoveryCode(form) {
  hideMessage($('#recovery-code-error'));
  if (!form.reportValidity()) return;

  const button = form.querySelector('button[type="submit"]');
  setSubmitting(button, true);

  try {
    const body = new FormData(form);
    const response = await postJson(endpoints.recoveryCodeRedeem, {
      email: String(body.get('email') || ''),
      recoveryCode: String(body.get('recoveryCode') || '')
    });

    if (response.ok) {
      const payload = await readJson(response);
      location.assign(resolveSafeReturnPath(payload?.returnUrl));
      return;
    }

    showMessage($('#recovery-code-error'));
  } catch {
    showMessage($('#recovery-code-error'));
  } finally {
    setSubmitting(button, false);
  }
}

function prepareResetView() {
  if (state.view !== 'reset-password') return;
  const parameters = new URLSearchParams(location.search);
  if (!parameters.get('token') || !parameters.get('email')) showInvalidReset();
}

function showInvalidReset() {
  $('#reset-form').hidden = true;
  showMessage($('#reset-invalid'));
}

async function loadAppVersion() {
  const el = document.getElementById('app-version');
  if (!el) return;
  try {
    const response = await fetch('/api/app-version', { headers: { Accept: 'application/json' } });
    if (!response.ok) return;
    const data = await response.json();
    const version = (data && data.version ? String(data.version) : '').trim();
    if (!version) return;
    el.textContent = version.startsWith('v') ? version : `v${version}`;
    el.hidden = false;
  } catch {
    /* Version is decorative; on failure the footer simply shows the product name. */
  }
}

function togglePassword(event) {
  const button = event.currentTarget;
  const input = document.getElementById(button.dataset.target);
  if (!input) return;

  const show = input.type === 'password';
  input.type = show ? 'text' : 'password';
  button.setAttribute('aria-pressed', String(show));
  const key = show ? 'auth.hidePassword' : 'auth.showPassword';
  button.setAttribute('aria-label', get(key));
  button.title = get(key);
  input.focus({ preventScroll: true });
}

function showView(view) {
  state.view = supportedViews.has(view) ? view : 'login';
  $$('[data-auth-view]').forEach(section => {
    section.hidden = section.dataset.authView !== state.view;
  });
  updateDocumentTitle();
}

function showSessionStatus() {
  const status = new URLSearchParams(location.search).get('status');
  const target = $('#auth-session-status');
  if (status === 'session-expired') {
    target.textContent = get('auth.sessionExpired');
    showMessage(target);
  } else if (status === 'signed-out') {
    target.textContent = get('auth.signedOut');
    showMessage(target);
  } else if (status === 'registration-disabled') {
    target.textContent = get('auth.registrationDisabled');
    showMessage(target);
  } else if (status === 'external-account-not-found') {
    target.textContent = get('auth.externalAccountNotFound');
    showMessage(target);
  } else if (status === 'external-registration-failed') {
    target.textContent = get('auth.externalRegistrationFailed');
    showMessage(target);
  } else if (status === 'external-failed') {
    target.textContent = get('auth.externalFailed');
    showMessage(target);
  } else if (status === 'external-two-factor-required') {
    target.textContent = get('auth.externalTwoFactorRequired');
    showMessage(target);
  }
}

function setSubmitting(button, submitting) {
  button.disabled = submitting;
  button.classList.toggle('is-loading', submitting);
  button.setAttribute('aria-busy', String(submitting));
  button.textContent = get(submitting ? button.dataset.loadingI18n : button.dataset.i18n);
}

function showMessage(element) {
  if (element) element.hidden = false;
}

function hideMessage(element) {
  if (element) element.hidden = true;
}

async function postJson(url, payload) {
  const tokenResponse = await fetch('/auth/antiforgery', {
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' }
  });
  if (!tokenResponse.ok) throw new Error('Antiforgery token unavailable.');

  const tokenPayload = await readJson(tokenResponse);
  const token = tokenPayload?.token;
  if (!token) throw new Error('Antiforgery token missing.');

  return fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': token
    },
    credentials: 'same-origin',
    body: JSON.stringify(payload)
  });
}

async function readJson(response) {
  const type = response.headers.get('Content-Type') || '';
  if (!type.includes('application/json')) return null;
  try {
    return await response.json();
  } catch {
    return null;
  }
}

function resolveSafeReturnPath(serverValue) {
  const queryValue = new URLSearchParams(location.search).get('returnUrl');
  for (const value of [serverValue, queryValue, '/']) {
    if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) continue;
    try {
      const candidate = new URL(value, location.origin);
      if (candidate.origin === location.origin) return `${candidate.pathname}${candidate.search}${candidate.hash}`;
    } catch {
    }
  }
  return '/';
}

function renderRecoveryCodes(codes, remainingCount) {
  const validCodes = Array.isArray(codes)
    ? codes.filter(code => typeof code === 'string' && code.length > 0)
    : [];

  state.generatedRecoveryCodes = [...validCodes];
  const list = $('#recovery-codes-list');
  list.replaceChildren();

  for (const code of validCodes) {
    const item = document.createElement('li');
    const value = document.createElement('code');
    value.textContent = code;
    item.appendChild(value);
    list.appendChild(item);
  }

  $('#copy-recovery-codes').disabled = validCodes.length === 0;
  $('#recovery-codes-remaining').dataset.remainingCount =
    Number.isInteger(remainingCount) ? String(remainingCount) : '';
  renderRemainingCount();
}

function renderRemainingCount() {
  const element = $('#recovery-codes-remaining');
  if (!element) return;
  const raw = element.dataset.remainingCount;
  if (raw === '') {
    element.hidden = true;
    return;
  }
  element.textContent = get('auth.remainingCodes').replace('{count}', raw);
  element.hidden = false;
}

async function copyRecoveryCodes() {
  if (state.generatedRecoveryCodes.length === 0) return;
  try {
    await navigator.clipboard.writeText(state.generatedRecoveryCodes.join('\n'));
    showMessage($('#copy-recovery-status'));
  } catch {
    hideMessage($('#copy-recovery-status'));
  }
}

boot().catch(() => {
  document.documentElement.dataset.theme = 'light';
});
