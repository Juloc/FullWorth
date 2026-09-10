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

// Outbox depth answers "is the link working at all" better than any status flag: consent and
// entitlement can look fine while every submission sits queued because the Cloud is unreachable. The
// oldest waiting item's age is what tells an operator this has been stuck, not merely mid-retry.
function formatAge(value) {
  if (!value) return '—';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.valueOf())) return '—';
  const ms = Date.now() - parsed.valueOf();
  if (ms < 60000) return '< 1 Min.';
  const minutes = Math.floor(ms / 60000);
  if (minutes < 60) return `${minutes} Min.`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} Std.`;
  const days = Math.floor(hours / 24);
  return `${days} Tag${days === 1 ? '' : 'e'}`;
}

function selectedChoice() {
  if ($('cloud-choice-enabled')?.checked) return 'enabled';
  if ($('cloud-choice-local')?.checked) return 'disabled';
  return null;
}

// The transport reports a machine code. Showing it raw ("cloud_entitlement_denied") tells the
// operator nothing and offers no way out, so each known code gets a sentence and a next step; an
// unknown code is still shown, because a token is more use than silence.
const CLOUD_ERRORS = {
  cloud_unreachable: 'Die Cloud ist nicht erreichbar. Prüfe die Internetverbindung und FullWorthCloud:BaseUrl.',
  cloud_timeout: 'Die Cloud hat nicht rechtzeitig geantwortet. Der Versuch wird automatisch wiederholt.',
  cloud_unauthorized: 'Die Zugangsdaten dieser Instanz wurden abgelehnt. Aktiviere Cloud Intelligence erneut, um sie neu zu registrieren.',
  cloud_entitlement_denied: 'Diese Instanz ist für diesen Cloud-Dienst nicht freigeschaltet.',
  cloud_rate_limited: 'Die Cloud hat zu viele Anfragen gemeldet. Der nächste Versuch erfolgt später automatisch.',
  cloud_server_error: 'Die Cloud meldet einen Serverfehler. Das liegt nicht an dieser Instanz; es wird erneut versucht.',
  cloud_batch_too_large: 'Ein Beitrag war zu groß für die Cloud. Er wird beim nächsten Versuch kleiner geschnitten.',
  cloud_enrollment_refused: 'Die Cloud hat die Anmeldung dieser Instanz abgelehnt. Prüfe den Enrollment-Token.',
  registration_lost: 'Der Vorgang ist abgelaufen. Bitte starte die Einrichtung erneut.'
};

function cloudErrorText(code) {
  return CLOUD_ERRORS[code] || `Unbekannter Cloud-Fehler (${code}).`;
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

  $('cloud-endpoint').textContent = state.cloudEndpoint || '—';

  $('cloud-policy').textContent = state.currentPolicyVersion || '—';
  $('cloud-entitlement').textContent = state.entitlementStatus || (enabled ? 'noch nicht geprüft' : '—');
  $('cloud-registration').textContent = formatDate(state.lastRegistrationAt);
  $('cloud-submission').textContent = formatDate(state.lastSubmissionAt);
  $('cloud-outbox-waiting').textContent = String(state.outbox?.waitingCount ?? 0);
  $('cloud-outbox-dead-letter').textContent = String(state.outbox?.deadLetterCount ?? 0);
  $('cloud-outbox-oldest').textContent = formatAge(state.outbox?.oldestWaitingCreatedAt);
  $('cloud-ops').hidden = !state.setupDecisionAt;

  if (state.lastErrorCode && enabled) setResult(`Cloud-Status: ${cloudErrorText(state.lastErrorCode)}`, 'bad');
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
    // Enabling stores the decision locally and then registers with the Cloud, and registration is
    // deliberately best-effort - a temporarily unreachable Cloud must not make setup fail. But the
    // green success line was printed either way, so a refused or unreachable Cloud read as "aktiviert"
    // and the reason sat unnoticed in the state as lastErrorCode.
    if (choice === 'enabled' && state?.lastErrorCode) {
      setResult(`Die Entscheidung ist gespeichert, aber die Anmeldung bei der Cloud ist fehlgeschlagen: ${cloudErrorText(state.lastErrorCode)} Es wird automatisch erneut versucht.`, 'bad');
    } else {
      setResult(choice === 'enabled'
        ? 'FullWorth Cloud Intelligence ist aktiviert. Empfang und geeignete minimierte Beiträge sind gemeinsam aktiv.'
        : 'Diese Instanz bleibt lokal. Es werden keine FullWorth-Cloud-Beiträge gesendet und keine erweiterten Cloud-Mappings bezogen.', 'ok');
    }
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
