import { api as sharedApi } from '../core/services.js';
const $ = id => document.getElementById(id);

let cloudState = null;
let saving = false;

async function api(path, init = {}) {
  try {
    return await sharedApi(`api/intelligence/admin${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...(init.headers || {})
      }
    });
  } catch (error) {
    if (error?.status === 401) {
      location.href = `/auth/login?returnUrl=${encodeURIComponent(location.pathname)}`;
      throw new Error('unauthorized');
    }
    if (error?.status === 403) throw Object.assign(new Error('forbidden'), { status: 403, detail: error.detail });
    throw error;
  }
}

function formatDate(value) {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? '—' : parsed.toLocaleString();
}

function selectedChoice() {
  if ($('cloud-choice-enabled')?.checked) return 'enabled';
  if ($('cloud-choice-local')?.checked) return 'disabled';
  return null;
}

function setResult(message, kind = '') {
  const node = $('cloud-result');
  if (!node) return;
  node.textContent = message || '';
  node.className = `intel-result ${kind}`.trim();
}

function renderCloudState(state) {
  cloudState = state;
  const enabled = state.mode === 'enabled';
  const decided = !state.requiresSetupDecision;
  const needsReconsent = state.requiresSetupDecision && enabled && Boolean(state.setupDecisionAt);

  $('cloud-mode').textContent = state.requiresSetupDecision
    ? (needsReconsent ? 'Neue Zustimmung nötig' : 'Nicht entschieden')
    : (enabled ? 'Cloud aktiv' : 'Nur lokal');
  $('cloud-mode').className = `status-pill ${enabled && decided ? 'ok' : state.requiresSetupDecision ? 'bad' : ''}`.trim();

  $('cloud-required').hidden = !state.requiresSetupDecision;
  if (state.requiresSetupDecision) {
    $('cloud-required').textContent = needsReconsent
      ? 'Die Cloud-Richtlinie wurde wesentlich geändert. Vor weiteren Cloud-Uploads ist eine neue ausdrückliche Zustimmung erforderlich.'
      : 'Diese Entscheidung gehört zum Intelligence-Setup und muss einmal bewusst getroffen werden.';
  }

  if (state.requiresSetupDecision && !needsReconsent) {
    $('cloud-choice-enabled').checked = false;
    $('cloud-choice-local').checked = false;
  } else {
    $('cloud-choice-enabled').checked = enabled;
    $('cloud-choice-local').checked = !enabled;
  }

  $('cloud-consent').checked = false;
  updateConsentVisibility();

  $('cloud-policy').textContent = state.currentPolicyVersion || '—';
  $('cloud-entitlement').textContent = state.entitlementStatus || (enabled ? 'noch nicht geprüft' : '—');
  $('cloud-registration').textContent = formatDate(state.lastRegistrationAt);
  $('cloud-submission').textContent = formatDate(state.lastSubmissionAt);
  $('cloud-ops').hidden = !state.setupDecisionAt;

  if (state.lastErrorCode && enabled) setResult(`Cloud-Status: ${state.lastErrorCode}`, 'bad');
  refreshSaveState();
}

function updateConsentVisibility() {
  const wantsCloud = selectedChoice() === 'enabled';
  $('cloud-consent-wrap').hidden = !wantsCloud;
  if (!wantsCloud) $('cloud-consent').checked = false;
  refreshSaveState();
}

function refreshSaveState() {
  const choice = selectedChoice();
  const consentOk = choice !== 'enabled' || $('cloud-consent').checked;
  $('cloud-save').disabled = saving || !choice || !consentOk;
}

async function loadCloud() {
  try {
    const state = await api('/cloud');
    renderCloudState(state);
  } catch (error) {
    if (error.status === 403) return;
    setResult(error.message || 'Cloud-Status konnte nicht geladen werden.', 'bad');
  }
}

async function saveDecision() {
  const choice = selectedChoice();
  if (!choice) {
    setResult('Bitte wähle Cloud Intelligence oder nur lokal.', 'bad');
    return;
  }
  if (choice === 'enabled' && !$('cloud-consent').checked) {
    setResult('Cloud Intelligence kann nur nach ausdrücklicher Zustimmung aktiviert werden.', 'bad');
    return;
  }

  saving = true;
  refreshSaveState();
  setResult('Speichert…');

  try {
    const state = choice === 'enabled'
      ? await api('/cloud/enable', {
          method: 'POST',
          body: JSON.stringify({
            policyVersion: cloudState.currentPolicyVersion,
            locale: navigator.language || 'de-DE',
            clientVersion: 'fullworth-web'
          })
        })
      : await api('/cloud/disable', { method: 'POST', body: '{}' });

    renderCloudState(state);
    setResult(choice === 'enabled'
      ? 'FullWorth Cloud Intelligence ist aktiviert. Empfang und geeignete minimierte Beiträge sind gemeinsam aktiv.'
      : 'Diese Instanz bleibt lokal. Es werden keine FullWorth-Cloud-Beiträge gesendet und keine erweiterten Cloud-Mappings bezogen.', 'ok');
  } catch (error) {
    if (error.status === 409 && error.detail?.error === 'cloud_policy_stale') {
      setResult('Die Cloud-Richtlinie hat sich geändert. Der aktuelle Stand wird neu geladen; bitte bestätige erneut.', 'bad');
      await loadCloud();
    } else {
      setResult(error.message || 'Cloud-Entscheidung konnte nicht gespeichert werden.', 'bad');
    }
  } finally {
    saving = false;
    refreshSaveState();
  }
}

for (const id of ['cloud-choice-enabled', 'cloud-choice-local']) {
  $(id)?.addEventListener('change', updateConsentVisibility);
}
$('cloud-consent')?.addEventListener('change', refreshSaveState);
$('cloud-save')?.addEventListener('click', saveDecision);

loadCloud();
