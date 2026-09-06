export function createAccessSetup(ctx, openBankingWizard) {
  const { api, bankApi, get, esc, toast, dialog, confirm, jsonBody } = ctx;
  let activeAiPoll = null;

  const putJson = data => ({
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data)
  });

  const modeLabel = mode =>
    get('aiAccess.mode_' + String(mode || 'none').replaceAll('-', '_'));

  async function renderAiAccessSettings() {
    const row = document.querySelector('#ai-access-settings');
    const sub = document.querySelector('#ai-access-status');
    if (!row || !sub) return;

    sub.textContent = get('aiAccess.loading');
    try {
      const status = await api('api/intelligence/access');
      sub.textContent = status.configured
        ? get('aiAccess.ready').replace('{mode}', modeLabel(status.mode))
        : get('aiAccess.notConfigured');
      row.onclick = () => openAiAccessWizard(status);
    } catch (error) {
      sub.textContent = error.message || get('common.error');
      row.onclick = () => openAiAccessWizard(null);
    }
  }

  function openAiAccessWizard(initialStatus, options = {}) {
    let status = initialStatus;
    let closed = false;

    if (activeAiPoll) {
      clearTimeout(activeAiPoll);
      activeAiPoll = null;
    }

    const dlg = dialog(
      '<div class="dialog-card banking-setup ai-access-setup">' +
      '<div class="panel-head"><h2></h2><button type="button" data-close aria-label="Close">×</button></div>' +
      '<div data-step></div></div>');
    const step = dlg.querySelector('[data-step]');
    dlg.querySelector('h2').textContent = get('aiAccess.title');
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    dlg.addEventListener('close', () => {
      closed = true;
      if (activeAiPoll) {
        clearTimeout(activeAiPoll);
        activeAiPoll = null;
      }
      options.onClose?.();
    }, { once: true });

    const refresh = async () => {
      status = await api('api/intelligence/access');
      await renderAiAccessSettings();
      return status;
    };

    const showChoices = () => {
      step.innerHTML =
        '<p class="row-sub">' + esc(get('aiAccess.chooseHint')) + '</p>' +
        '<div class="setup-choice-grid">' +
          '<button type="button" class="setup-choice" data-mode="codex"><strong>' + esc(get('aiAccess.codexTitle')) + '</strong><span>' + esc(get('aiAccess.codexHint')) + '</span></button>' +
          '<button type="button" class="setup-choice" data-mode="api"><strong>' + esc(get('aiAccess.apiKeyTitle')) + '</strong><span>' + esc(get('aiAccess.apiKeyHint')) + '</span></button>' +
          '<button type="button" class="setup-choice" data-mode="custom"><strong>' + esc(get('aiAccess.customTitle')) + '</strong><span>' + esc(get('aiAccess.customHint')) + '</span></button>' +
        '</div>' +
        '<div class="dialog-actions"><button type="button" data-cancel>' + esc(get('common.cancel')) + '</button></div>';

      step.querySelector('[data-cancel]').onclick = () => status?.configured ? showCurrent() : dlg.close();
      step.querySelector('[data-mode="codex"]').onclick = showCodex;
      step.querySelector('[data-mode="api"]').onclick = showApiKey;
      step.querySelector('[data-mode="custom"]').onclick = showCustom;
    };

    const showCurrent = () => {
      if (!status?.configured) {
        showChoices();
        return;
      }

      let detail = get('aiAccess.readyShort');
      if (status.mode === 'custom' && status.custom?.baseUrl) {
        detail = status.custom.baseUrl;
      } else if (status.mode === 'codex') {
        detail = status.codexConnected
          ? get('aiAccess.codexConnected')
          : get('aiAccess.codexDisconnected');
      } else if (status.credential?.secretFingerprint) {
        detail = get('aiAccess.fingerprint').replace('{value}', status.credential.secretFingerprint);
      }

      step.innerHTML =
        '<div class="row"><div class="row-main"><div class="row-title">' + esc(modeLabel(status.mode)) + '</div>' +
          '<div class="row-sub">' + esc(detail) + '</div></div></div>' +
        '<p class="row-sub">' + esc(get('aiAccess.secretHidden')) + '</p>' +
        '<div class="dialog-actions">' +
          '<button type="button" class="ghost danger" data-remove>' + esc(get('aiAccess.remove')) + '</button>' +
          '<button type="button" data-test>' + esc(get('aiAccess.test')) + '</button>' +
          '<button type="button" data-change>' + esc(get('aiAccess.change')) + '</button>' +
          '<button type="button" data-done>' + esc(get('common.close')) + '</button>' +
        '</div>';

      step.querySelector('[data-done]').onclick = () => dlg.close();
      step.querySelector('[data-change]').onclick = showChoices;
      step.querySelector('[data-test]').onclick = async event => {
        const button = event.currentTarget;
        button.disabled = true;
        try {
          await api('api/intelligence/access/test', jsonBody({}));
          toast(get('aiAccess.testSuccess'));
        } catch (error) {
          toast(error.message || get('aiAccess.testFailed'));
        } finally {
          button.disabled = false;
        }
      };
      step.querySelector('[data-remove]').onclick = async event => {
        if (!await confirm(get('aiAccess.removeConfirm'), {
          destructive: true,
          confirmLabel: get('aiAccess.remove')
        })) return;

        const button = event.currentTarget;
        button.disabled = true;
        try {
          await api('api/intelligence/access', { method: 'DELETE' });
          await refresh();
          showChoices();
          toast(get('aiAccess.removed'));
        } catch (error) {
          toast(error.message || get('common.error'));
          button.disabled = false;
        }
      };
    };

    const showApiKey = () => {
      step.innerHTML =
        '<form data-ai-form><p class="row-sub">' + esc(get('aiAccess.apiKeyExplain')) + '</p>' +
        '<label>' + esc(get('aiAccess.apiKey')) + '<input name="apiKey" type="password" autocomplete="off" required maxlength="8192"></label>' +
        '<label>' + esc(get('aiAccess.textModel')) + '<input name="textModel" value="' + esc(status?.textModel || '') + '" placeholder="gpt-5.6"></label>' +
        '<label>' + esc(get('aiAccess.visionModel')) + '<input name="visionModel" value="' + esc(status?.visionModel || '') + '" placeholder="gpt-5.6"></label>' +
        '<div class="dialog-actions"><button type="button" data-back>' + esc(get('common.cancel')) + '</button><button type="submit">' + esc(get('aiAccess.saveAndTest')) + '</button></div></form>';

      const form = step.querySelector('form');
      step.querySelector('[data-back]').onclick = showChoices;
      form.onsubmit = async event => {
        event.preventDefault();
        const values = new FormData(form);
        const button = form.querySelector('[type="submit"]');
        button.disabled = true;
        try {
          await api('api/intelligence/access/api-key', putJson({
            apiKey: String(values.get('apiKey') || ''),
            textModel: String(values.get('textModel') || '') || null,
            visionModel: String(values.get('visionModel') || '') || null
          }));
          await refresh();
          showCurrent();
          toast(get('aiAccess.saved'));
        } catch (error) {
          toast(error.message || get('aiAccess.testFailed'));
          button.disabled = false;
        }
      };
    };

    const showCustom = () => {
      const custom = status?.mode === 'custom' ? status.custom : null;
      step.innerHTML =
        '<form data-ai-form><p class="row-sub">' + esc(get('aiAccess.customExplain')) + '</p>' +
        '<label>' + esc(get('aiAccess.baseUrl')) + '<input name="baseUrl" type="url" value="' + esc(custom?.baseUrl || '') + '" placeholder="https://example.com/v1" required maxlength="2048"></label>' +
        '<label>' + esc(get('aiAccess.authType')) + '<select name="authType"><option value="bearer">Bearer Token</option><option value="basic">Basic Auth</option><option value="none">' + esc(get('aiAccess.noAuth')) + '</option></select></label>' +
        '<label data-user>' + esc(get('aiAccess.username')) + '<input name="username" autocomplete="username" maxlength="256"></label>' +
        '<label data-secret>' + esc(get('aiAccess.secret')) + '<input name="secret" type="password" autocomplete="off" maxlength="8192"></label>' +
        '<label>' + esc(get('aiAccess.textModel')) + '<input name="textModel" value="' + esc(status?.textModel || '') + '" placeholder="gpt-5.6"></label>' +
        '<label>' + esc(get('aiAccess.visionModel')) + '<input name="visionModel" value="' + esc(status?.visionModel || '') + '" placeholder="gpt-5.6"></label>' +
        '<div class="dialog-actions"><button type="button" data-back>' + esc(get('common.cancel')) + '</button><button type="submit">' + esc(get('aiAccess.saveAndTest')) + '</button></div></form>';

      const form = step.querySelector('form');
      const auth = form.elements.authType;
      auth.value = custom?.authType || 'bearer';
      if (custom?.username) form.elements.username.value = custom.username;

      const syncAuth = () => {
        const mode = auth.value;
        form.querySelector('[data-user]').hidden = mode !== 'basic';
        form.querySelector('[data-secret]').hidden = mode === 'none';
        form.elements.username.required = mode === 'basic';
        form.elements.secret.required = mode !== 'none';
      };
      auth.onchange = syncAuth;
      syncAuth();

      step.querySelector('[data-back]').onclick = showChoices;
      form.onsubmit = async event => {
        event.preventDefault();
        const values = new FormData(form);
        const button = form.querySelector('[type="submit"]');
        button.disabled = true;
        try {
          await api('api/intelligence/access/custom', putJson({
            baseUrl: String(values.get('baseUrl') || ''),
            authType: String(values.get('authType') || 'bearer'),
            username: String(values.get('username') || '') || null,
            secret: String(values.get('secret') || '') || null,
            textModel: String(values.get('textModel') || '') || null,
            visionModel: String(values.get('visionModel') || '') || null
          }));
          await refresh();
          showCurrent();
          toast(get('aiAccess.saved'));
        } catch (error) {
          toast(error.message || get('aiAccess.testFailed'));
          button.disabled = false;
        }
      };
    };

    const showCodex = async () => {
      step.innerHTML =
        '<p class="row-sub">' + esc(get('aiAccess.codexExplain')) + '</p>' +
        '<div data-codex-status class="row-sub">' + esc(get('aiAccess.startingLogin')) + '</div>' +
        '<div data-codex-action class="codex-login-action"></div>' +
        '<div class="dialog-actions"><button type="button" data-back>' + esc(get('common.cancel')) + '</button></div>';

      step.querySelector('[data-back]').onclick = () => {
        if (activeAiPoll) {
          clearTimeout(activeAiPoll);
          activeAiPoll = null;
        }
        showChoices();
      };

      let session;
      try {
        session = await api('api/intelligence/access/codex/login', jsonBody({}));
      } catch (error) {
        step.querySelector('[data-codex-status]').textContent =
          error.message || get('aiAccess.codexUnavailable');
        return;
      }
      if (!session?.id) {
        step.querySelector('[data-codex-status]').textContent = get('aiAccess.codexUnavailable');
        return;
      }

      const statusElement = step.querySelector('[data-codex-status]');
      const action = step.querySelector('[data-codex-action]');

      const draw = sessionState => {
        const key = 'aiAccess.codexStatus_' + (sessionState.status || 'waiting');
        statusElement.textContent = get(key);
        action.innerHTML = '';

        if (sessionState.verificationUrl) {
          const link = document.createElement('a');
          link.href = sessionState.verificationUrl;
          link.target = '_blank';
          link.rel = 'noopener';
          link.textContent = get('aiAccess.openLogin') + ' ↗';
          action.appendChild(link);
        }
        if (sessionState.userCode) {
          const code = document.createElement('div');
          code.className = 'setup-code';
          code.textContent = sessionState.userCode;
          action.appendChild(code);
        }
      };

      const poll = async () => {
        if (closed) return;
        try {
          const next = await api(
            'api/intelligence/access/codex/login/' + encodeURIComponent(session.id));
          draw(next);
          if (next.status === 'connected') {
            activeAiPoll = null;
            await refresh();
            showCurrent();
            toast(get('aiAccess.saved'));
            return;
          }
          if (next.status === 'error') {
            activeAiPoll = null;
            return;
          }
        } catch (error) {
          statusElement.textContent = error.message || get('aiAccess.codexUnavailable');
          activeAiPoll = null;
          return;
        }
        activeAiPoll = setTimeout(poll, 1500);
      };

      draw(session);
      activeAiPoll = setTimeout(poll, 500);
    };

    const start = async () => {
      if (!status) {
        try { status = await api('api/intelligence/access'); }
        catch { status = null; }
      }
      if (status?.configured) showCurrent();
      else showChoices();
    };

    dlg.showModal();
    start();
  }

  async function renderCloudSettings() {
    const panel = document.querySelector('#cloud-intelligence-panel');
    const row = document.querySelector('#cloud-intelligence-settings');
    const sub = document.querySelector('#cloud-intelligence-status');
    if (!panel || !row || !sub) return;

    try {
      const status = await api('api/intelligence/admin/cloud');
      panel.hidden = false;
      sub.textContent = status.requiresSetupDecision
        ? get('cloudIntelligence.consentUpdateRequired')
        : status.mode === 'enabled'
          ? get('cloudIntelligence.enabled')
          : get('cloudIntelligence.disabled');
      if (status.lastErrorCode)
        sub.textContent += ' · ' + status.lastErrorCode;
      row.onclick = () => openCloudWizard(status);
    } catch {
      panel.hidden = true;
    }
  }

  function openCloudWizard(initialStatus, options = {}) {
    let status = initialStatus;
    const dlg = dialog(
      '<div class="dialog-card banking-setup cloud-intelligence-setup">' +
      '<div class="panel-head"><h2></h2><button type="button" data-close aria-label="Close">×</button></div>' +
      '<div data-step></div></div>');
    const step = dlg.querySelector('[data-step]');
    dlg.querySelector('h2').textContent = get('cloudIntelligence.title');
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    dlg.addEventListener('close', () => options.onClose?.(), { once: true });

    const draw = () => {
      const enabledByDefault = status?.requiresSetupDecision
        ? true
        : status?.mode === 'enabled';
      step.innerHTML =
        '<p>' + esc(get('cloudIntelligence.explain')) + '</p>' +
        '<p class="row-sub">' + esc(get('cloudIntelligence.shared')) + '</p>' +
        '<label class="check"><input type="checkbox" data-enabled ' + (enabledByDefault ? 'checked' : '') + '> ' +
          esc(get('cloudIntelligence.useCloud')) + '</label>' +
        '<p class="row-sub">' + esc(get('cloudIntelligence.localWins')) + '</p>' +
        '<div class="dialog-actions"><button type="button" data-cancel>' + esc(get('common.cancel')) + '</button>' +
        '<button type="button" data-save>' + esc(get('common.save')) + '</button></div>';

      step.querySelector('[data-cancel]').onclick = () => dlg.close();
      step.querySelector('[data-save]').onclick = async event => {
        const button = event.currentTarget;
        button.disabled = true;
        const enabled = step.querySelector('[data-enabled]').checked;
        try {
          if (enabled) {
            status = await api('api/intelligence/admin/cloud/enable', jsonBody({
              policyVersion: status.currentPolicyVersion,
              locale: document.documentElement.lang || navigator.language || 'und',
              clientVersion: 'web'
            }));
          } else {
            status = await api('api/intelligence/admin/cloud/disable', jsonBody({}));
          }
          await renderCloudSettings();
          dlg.close();
          toast(get(enabled ? 'cloudIntelligence.savedEnabled' : 'cloudIntelligence.savedDisabled'));
        } catch (error) {
          toast(error.message || get('common.error'));
          button.disabled = false;
        }
      };
    };

    dlg.showModal();
    draw();
  }

  async function maybeOpenRegistrationOnboarding() {
    let onboarding;
    try { onboarding = await api('api/onboarding/status'); }
    catch { return; }
    if (onboarding?.completed) return;

    const dlg = dialog(
      '<div class="dialog-card onboarding-dialog">' +
      '<div class="panel-head"><h2></h2><button type="button" data-close aria-label="Close">×</button></div>' +
      '<div data-step></div></div>');
    const step = dlg.querySelector('[data-step]');
    dlg.querySelector('h2').textContent = get('onboarding.title');
    dlg.querySelector('[data-close]').onclick = () => dlg.close();

    const welcome = () => {
      step.innerHTML =
        '<h3>' + esc(get('onboarding.welcome')) + '</h3>' +
        '<p>' + esc(get('onboarding.intro')) + '</p>' +
        '<p class="row-sub">' + esc(get('onboarding.optional')) + '</p>' +
        '<div class="dialog-actions"><button type="button" data-start>' + esc(get('onboarding.start')) + '</button></div>';
      step.querySelector('[data-start]').onclick = aiStep;
    };

    const aiStep = async () => {
      let ai = null;
      try { ai = await api('api/intelligence/access'); } catch {}

      step.innerHTML =
        '<div class="setup-progress">1 / 3</div>' +
        '<h3>' + esc(get('onboarding.aiTitle')) + '</h3>' +
        '<p>' + esc(get('onboarding.aiText')) + '</p>' +
        '<div class="row-sub">' +
          esc(ai?.configured
            ? get('onboarding.configured').replace('{value}', modeLabel(ai.mode))
            : get('onboarding.notConfigured')) +
        '</div>' +
        '<div class="dialog-actions">' +
          '<button type="button" class="ghost" data-setup>' +
            esc(ai?.configured ? get('aiAccess.change') : get('onboarding.configure')) +
          '</button>' +
          '<button type="button" data-next>' + esc(get('onboarding.continueOrSkip')) + '</button>' +
        '</div>';

      step.querySelector('[data-setup]').onclick =
        () => openAiAccessWizard(ai, { onClose: aiStep });
      step.querySelector('[data-next]').onclick = bankStep;
    };

    const bankStep = async () => {
      let bank = null;
      try { bank = await bankApi('api/banking/status'); } catch {}
      const configured = Boolean(bank?.profile);

      step.innerHTML =
        '<div class="setup-progress">2 / 3</div>' +
        '<h3>' + esc(get('onboarding.bankTitle')) + '</h3>' +
        '<p>' + esc(get('onboarding.bankText')) + '</p>' +
        '<div class="row-sub">' +
          esc(configured
            ? get('onboarding.configured').replace(
                '{value}', bank.profile.applicationName || bank.profile.applicationId)
            : get('onboarding.notConfigured')) +
        '</div>' +
        '<div class="dialog-actions">' +
          '<button type="button" data-back>' + esc(get('onboarding.back')) + '</button>' +
          '<button type="button" class="ghost" data-setup>' +
            esc(configured ? get('bankingSetup.manage') : get('onboarding.configure')) +
          '</button>' +
          '<button type="button" data-finish>' + esc(get('onboarding.finish')) + '</button>' +
        '</div>';

      step.querySelector('[data-back]').onclick = aiStep;
      step.querySelector('[data-setup]').onclick =
        () => openBankingWizard(bank, { onClose: bankStep });
      step.querySelector('[data-finish]').textContent = get('onboarding.continueOrSkip');
      step.querySelector('[data-finish]').onclick = cloudStep;
    };

    const cloudStep = async () => {
      let cloud = null;
      try { cloud = await api('api/intelligence/admin/cloud'); }
      catch {
        await finish();
        return;
      }

      const checked = cloud.requiresSetupDecision ? true : cloud.mode === 'enabled';
      step.innerHTML =
        '<div class="setup-progress">3 / 3</div>' +
        '<h3>' + esc(get('onboarding.cloudTitle')) + '</h3>' +
        '<p>' + esc(get('onboarding.cloudText')) + '</p>' +
        '<label class="check"><input type="checkbox" data-cloud ' + (checked ? 'checked' : '') + '> ' +
          esc(get('cloudIntelligence.useCloud')) + '</label>' +
        '<p class="row-sub">' + esc(get('cloudIntelligence.shared')) + '</p>' +
        '<div class="dialog-actions">' +
          '<button type="button" data-back>' + esc(get('onboarding.back')) + '</button>' +
          '<button type="button" data-finish>' + esc(get('onboarding.finish')) + '</button>' +
        '</div>';

      step.querySelector('[data-back]').onclick = bankStep;
      step.querySelector('[data-finish]').onclick = async event => {
        const button = event.currentTarget;
        button.disabled = true;
        try {
          if (step.querySelector('[data-cloud]').checked) {
            await api('api/intelligence/admin/cloud/enable', jsonBody({
              policyVersion: cloud.currentPolicyVersion,
              locale: document.documentElement.lang || navigator.language || 'und',
              clientVersion: 'web'
            }));
          } else {
            await api('api/intelligence/admin/cloud/disable', jsonBody({}));
          }
          await finish();
        } catch (error) {
          toast(error.message || get('common.error'));
          button.disabled = false;
        }
      };
    };

    const finish = async () => {
      try {
        await api('api/onboarding/complete', jsonBody({}));
        dlg.close();
        toast(get('onboarding.done'));
      } catch (error) {
        toast(error.message || get('common.error'));
      }
    };

    welcome();
    dlg.showModal();
  }

  return {
    renderAiAccessSettings,
    openAiAccessWizard,
    renderCloudSettings,
    openCloudWizard,
    maybeOpenRegistrationOnboarding
  };
}
