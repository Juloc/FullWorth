/**
 * Settings that describe the installation, as one generic form.
 *
 * Every field — its kind, its range, its choices, whether it may be edited at all — comes from the
 * server's catalogue. Adding a setting is one entry in InstanceSettingCatalogue and nothing in this
 * file changes. That is the difference between "settings live in the UI" being a property of the
 * system and being a promise somebody has to keep by hand for every new value.
 *
 * The `source` each setting reports is the part that matters. A value the environment pins is shown
 * read-only and says so — without that an administrator edits a field, an environment variable
 * overrides it silently, and the application looks broken.
 */

const ENDPOINT = '/auth/admin/instance-settings';

const SECTION_LABELS = {
  enableBanking: 'Enable Banking',
  banking: 'FinTS',
  sync: 'Bank-Synchronisierung',
  registration: 'Registrierung',
  logging: 'Protokollierung'
};

const SOURCE_NOTES = {
  environment: 'durch die Umgebung gesetzt',
  stored: 'hier gespeichert',
  default: 'Standardwert'
};

// Said out loud rather than left to be discovered: at Debug and below, EF Core writes raw SQL and its
// parameters to the container log — account numbers and amounts in clear text, under whatever log
// rotation the host happens to have.
const VERBOSE_LEVELS = new Set(['Trace', 'Debug']);

export function createInstanceSettingsPanel({ request, esc, toast }) {
  const body = () => document.querySelector('#instance-settings-body');

  function control(setting) {
    const id = 'setting-' + setting.key.replace(/[^a-zA-Z0-9]/g, '-');
    const disabled = setting.readOnly ? ' disabled' : '';

    if (setting.kind === 'choice') {
      const options = (setting.choices || [])
        .map(choice => {
          const selected = String(choice) === String(setting.value ?? '') ? ' selected' : '';
          return '<option value="' + esc(choice) + '"' + selected + '>' + esc(choice) + '</option>';
        })
        .join('');
      return '<select id="' + id + '" data-setting="' + esc(setting.key) + '"' + disabled + '>' + options + '</select>';
    }

    if (setting.kind === 'boolean') {
      const checked = String(setting.value ?? '').toLowerCase() === 'true' ? ' checked' : '';
      return '<input id="' + id + '" type="checkbox" data-setting="' + esc(setting.key) + '"' + checked + disabled + '>';
    }

    const type = setting.kind === 'integer' ? 'number' : 'text';
    const min = setting.minimum != null ? ' min="' + esc(setting.minimum) + '"' : '';
    const max = setting.maximum != null ? ' max="' + esc(setting.maximum) + '"' : '';
    return '<input id="' + id + '" type="' + type + '" value="' + esc(setting.value ?? '') +
      '" data-setting="' + esc(setting.key) + '"' + min + max + disabled + '>';
  }

  function warningFor(setting) {
    if (setting.section !== 'logging' || !VERBOSE_LEVELS.has(String(setting.value))) return '';
    return '<span class="setting-warning">Schreibt rohes SQL samt Werten ins Container-Log.</span>';
  }

  function render(settings) {
    const target = body();
    if (!target) return;

    const sections = [...new Set(settings.map(setting => setting.section))];
    target.innerHTML = sections.map(section => {
      const rows = settings
        .filter(setting => setting.section === section)
        .map(setting =>
          '<label class="setting-row">' +
            '<span class="setting-key">' + esc(setting.key) + '</span>' +
            control(setting) +
            '<span class="setting-source">' + esc(SOURCE_NOTES[setting.source] || setting.source) + '</span>' +
            warningFor(setting) +
          '</label>')
        .join('');
      return '<div class="setting-section"><h3>' + esc(SECTION_LABELS[section] || section) + '</h3>' + rows + '</div>';
    }).join('');

    target.querySelectorAll('[data-setting]').forEach(element =>
      element.addEventListener('change', () => save(element)));
  }

  async function save(element) {
    const key = element.dataset.setting;
    const value = element.type === 'checkbox' ? String(element.checked) : element.value;
    element.disabled = true;
    try {
      // The response is the whole list again, so the source column updates too: saving a value that
      // an environment variable overrides has to visibly stay "durch die Umgebung gesetzt".
      render(await request(ENDPOINT, { method: 'PUT', body: JSON.stringify({ key, value }) }));
      toast('Gespeichert.');
    } catch (error) {
      toast(error.message || 'Speichern fehlgeschlagen.');
      element.disabled = false;
      await load().catch(() => {});
    }
  }

  async function load() {
    render(await request(ENDPOINT));
  }

  return { load };
}
