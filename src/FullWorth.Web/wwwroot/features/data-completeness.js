function esc(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  }[char]));
}

function parseDate(value) {
  const raw = String(value || '').slice(0, 10);
  return /^\d{4}-\d{2}-\d{2}$/.test(raw) ? raw : null;
}

export async function loadFinanzguruCompleteness(api) {
  try {
    const data = await api('api/import/finanzguru/accounts');
    const imports = data?.importAccounts || [];
    const targets = data?.targetAccounts || [];
    const attached = data?.attachedHistory || [];
    const targetsById = new Map(targets.map(item => [String(item.id), item]));

    const unresolvedImports = imports.filter(item => Number(item.transactionCount) > 0);
    const attachedHistory = attached.filter(item => Number(item.transactionCount) > 0);
    const missingBalance = [];

    for (const item of unresolvedImports) {
      const targetId = item.linkedTargetAccountId || item.suggestedTargetAccountId;
      const target = targetId ? targetsById.get(String(targetId)) : null;
      if (target && !target.hasCurrentBalance) missingBalance.push({ type: 'import', item, target });
    }
    for (const item of attachedHistory) {
      if (!item.hasCurrentBalance) missingBalance.push({ type: 'attached', item, target: item });
    }

    const all = [
      ...unresolvedImports.map(item => ({
        count: Number(item.transactionCount) || 0,
        first: parseDate(item.firstBookingDate),
        last: parseDate(item.lastBookingDate)
      })),
      ...attachedHistory.map(item => ({
        count: Number(item.transactionCount) || 0,
        first: parseDate(item.firstBookingDate),
        last: parseDate(item.lastBookingDate)
      }))
    ];
    const dates = all.flatMap(item => [item.first, item.last]).filter(Boolean).sort();

    return {
      hasIssues: unresolvedImports.length > 0 || attachedHistory.length > 0,
      needsBalance: missingBalance.length > 0,
      needsLink: unresolvedImports.length > 0,
      needsConfirmation: attachedHistory.length > 0,
      transactionCount: all.reduce((sum, item) => sum + item.count, 0),
      firstDate: dates[0] || null,
      lastDate: dates.at(-1) || null,
      importAccountCount: unresolvedImports.length,
      attachedAccountCount: attachedHistory.length
    };
  } catch {
    return { hasIssues: false };
  }
}

function formatDate(value, lang) {
  if (!value) return '';
  const date = new Date(value + 'T12:00:00');
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(lang === 'de' ? 'de-DE' : 'en-US').format(date);
}

export function finanzguruCompletenessNotice(state, options = {}) {
  if (!state?.hasIssues) return '';
  const lang = options.lang === 'en' ? 'en' : 'de';
  const scope = options.scope || 'wealth';
  const count = Number(state.transactionCount) || 0;
  const period = state.firstDate
    ? (state.lastDate && state.lastDate !== state.firstDate
      ? `${formatDate(state.firstDate, lang)} – ${formatDate(state.lastDate, lang)}`
      : formatDate(state.firstDate, lang))
    : '';

  let title, body, action;
  if (lang === 'de') {
    if (state.needsBalance) {
      title = 'Vermögenshistorie unvollständig';
      body = `${count} importierte Buchungen sind vorhanden${period ? ` (${period})` : ''}. Für mindestens ein zugeordnetes Konto fehlt ein aktueller Kontostand. ` +
        (scope === 'analytics'
          ? 'Ausgaben und Einnahmen bleiben auswertbar, der Vermögens- und Kontoverlauf ist aber unvollständig.'
          : 'Trage den Kontostand ein, damit FullWorth den historischen Vermögensverlauf berechnen kann.');
      action = 'Kontostand ergänzen';
    } else if (state.needsLink) {
      title = 'Importierte Historie noch nicht verbunden';
      body = `${count} importierte Buchungen sind vorhanden${period ? ` (${period})` : ''}. Sie sind als Buchungen sichtbar, werden aber noch nicht als historischer Vermögensstand verwendet. Verbinde das Importkonto mit dem richtigen Konto.`;
      action = 'Importkonto verbinden';
    } else {
      title = 'Importierte Historie noch nicht bestätigt';
      body = `${count} importierte Buchungen sind bereits einem Konto zugeordnet${period ? ` (${period})` : ''}. Bestätige die Historie, damit sie für den Vermögensverlauf verwendet wird.`;
      action = 'Historie bestätigen';
    }
  } else {
    if (state.needsBalance) {
      title = 'Wealth history incomplete';
      body = `${count} imported transactions are available${period ? ` (${period})` : ''}. At least one linked account has no current balance. ` +
        (scope === 'analytics'
          ? 'Income and spending remain usable, but account and wealth history are incomplete.'
          : 'Enter the current balance so FullWorth can reconstruct historical wealth.');
      action = 'Add current balance';
    } else if (state.needsLink) {
      title = 'Imported history not linked yet';
      body = `${count} imported transactions are available${period ? ` (${period})` : ''}. They remain visible as transactions but are not yet used as historical wealth. Link the import to the correct account.`;
      action = 'Link imported account';
    } else {
      title = 'Imported history not confirmed yet';
      body = `${count} imported transactions are already attached to an account${period ? ` (${period})` : ''}. Confirm the history to use it for the wealth timeline.`;
      action = 'Confirm history';
    }
  }

  return `<div class="data-completeness-warning" role="status">
    <div class="data-completeness-copy"><strong>${esc(title)}</strong><span>${esc(body)}</span></div>
    <a class="secondary" href="/settings/import/finanzguru/xlsx#import-link-heading">${esc(action)}</a>
  </div>`;
}
