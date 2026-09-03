// FullWorth durable multi-source receipt scan experience.
//
// One UI draft = one ReceiptScanJob = one Purchase. The browser uploads every selected photo/PDF into
// that draft first, then the user may add/remove/replace/reorder sources and explicitly start analysis.
// Once a source upload has committed, closing/reloading the app is safe because the server owns the job.

let singleton = null;
const POLL_MS = 1200;
const TERMINAL_VISIBLE_MS = 20 * 60 * 1000;

export function runReceiptScanExperience(ctx, file) {
  singleton ??= createExperience(ctx);
  singleton.update(ctx);
  return singleton.enqueueForeground(file);
}

function createExperience(initialCtx) {
  let ctx = initialCtx;
  const jobs = [];
  let dialog = null;
  let chip = null;
  let raf = 0;
  let pollTimer = 0;
  let captureInstalled = false;
  let hydrateRunning = false;
  let draggedSourceId = null;

  ensureCss();
  setInputMultiple();

  function update(nextCtx) {
    if (nextCtx) ctx = nextCtx;
    setInputMultiple();
    scheduleHydrate(0);
  }

  function enqueueForeground(file) {
    installCapture();
    const input = document.getElementById('receipt-file');
    const selected = [...(input?.files || [])];
    const files = selected.length ? selected : [file].filter(Boolean);
    if (file && !files.some(candidate => sameFile(candidate, file))) files.unshift(file);
    if (input) input.value = '';
    return createDraft(files, true);
  }

  function installCapture() {
    setInputMultiple();
    if (captureInstalled) return;
    captureInstalled = true;
    document.addEventListener('change', event => {
      const target = event.target;
      if (!(target instanceof HTMLInputElement) || target.id !== 'receipt-file') return;
      // Installed from inside the first purchases.js change handler, so it only owns later selections.
      event.preventDefault();
      event.stopImmediatePropagation();
      const files = [...(target.files || [])];
      target.value = '';
      if (!files.length) return;
      const current = currentDialogJob();
      if (current && (current.state === 'draft' || current.state === 'uploading')) {
        addFilesToDraft(current, files);
        showDialog(current);
      } else {
        createDraft(files, false);
      }
    }, true);
  }

  function createDraft(files, legacy) {
    if (!files?.length) return Promise.resolve(null);
    const roots = files.map(file => ({ id: crypto.randomUUID(), file }));
    const job = {
      localId: crypto.randomUUID(), id: null, legacy,
      state: 'uploading', stage: 'preparing', engine: null, error: null, warnings: [],
      purchaseId: null, purchase: null, fileName: files[0]?.name || t('Kassenbon', 'Receipt'),
      contentType: files[0]?.type || contentTypeFromName(files[0]?.name), sourceCount: 0,
      sources: [], localFiles: new Map(roots.map(root => [root.id, root.file])),
      createdAt: new Date().toISOString(), completedAt: null, backgrounded: false,
      previewUrl: null, boxes: [], resolve: null, reject: null,
      terminalHandled: false, terminalNotified: false, hydrated: false, readyPromise: null
    };
    const promise = legacy
      ? new Promise((resolve, reject) => { job.resolve = resolve; job.reject = reject; })
      : Promise.resolve(null);
    jobs.push(job);
    preparePreview(job).catch(() => {});
    showDialog(job);
    renderAll();
    job.readyPromise = submitDraft(job, roots);
    return promise;
  }

  async function submitDraft(job, roots) {
    const form = new FormData();
    roots.forEach(root => { form.append('receipt', root.file); form.append('sourceId', root.id); });
    form.append('currency', 'EUR');
    form.append('clientJobId', job.localId);
    try {
      setStage(job, 'preparing');
      const row = await ctx.api('api/purchases/receipt-scan/jobs', { method: 'POST', body: form });
      mergeServerRow(job, row);
      await hydrateSources(job);
      job.state = 'draft';
      job.stage = 'draft';
      renderAll();
      return job;
    } catch (error) {
      // The POST may have committed even if its response was lost. The stable clientJobId recovers the
      // complete set without uploading any file a second time.
      try {
        const row = await ctx.api(`api/purchases/receipt-scan/jobs/${job.localId}`);
        mergeServerRow(job, row);
        await hydrateSources(job);
        renderAll();
        return job;
      } catch { /* durable draft truly was not recoverable */ }
      job.state = 'error'; job.stage = 'error'; job.error = error?.message || String(error);
      job.completedAt = new Date().toISOString(); renderAll();
      if (job.legacy) { job.reject?.(error); job.resolve = null; job.reject = null; }
      else ctx.toast(t('Belegentwurf konnte nicht sicher gespeichert werden.', 'Receipt draft could not be stored safely.'));
      throw error;
    }
  }

  async function addFilesToDraft(job, files) {
    if (!files?.length) return;
    try {
      if (job.readyPromise) await job.readyPromise.catch(() => null);
      if (!job.id || job.state !== 'draft') throw new Error(t('Der Beleg wird bereits analysiert.', 'This receipt is already being analyzed.'));
      const roots = files.map(file => ({ id: crypto.randomUUID(), file }));
      const form = new FormData();
      roots.forEach(root => { form.append('receipt', root.file); form.append('sourceId', root.id); });
      setStage(job, 'preparing');
      try {
        const rows = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/sources`, { method: 'POST', body: form });
        roots.forEach(root => job.localFiles.set(root.id, root.file));
        job.sources = rows || [];
      } catch (error) {
        // An add-source response can also be lost after commit. Reload and accept success if all stable
        // root IDs are now present; this is whole-set idempotency without a second physical upload.
        await hydrateSources(job);
        const ids = new Set(job.sources.map(source => source.id));
        if (!roots.every(root => ids.has(root.id))) throw error;
        roots.forEach(root => job.localFiles.set(root.id, root.file));
      }
      job.sourceCount = job.sources.length; job.state = 'draft'; job.stage = 'draft'; job.error = null;
      job.previewUrl = null; job.boxes = []; await preparePreview(job).catch(() => {}); renderAll();
    } catch (error) {
      job.error = error?.message || String(error); job.stage = 'draft'; renderAll();
      ctx.toast(job.error);
    }
  }

  async function hydrateSources(job) {
    if (!job?.id) return [];
    const rows = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/sources`);
    job.sources = rows || [];
    job.sourceCount = job.sources.length;
    return job.sources;
  }

  async function startDraft(job) {
    if (!job?.id || job.state !== 'draft') return;
    try {
      const row = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/start`, { method: 'POST' });
      mergeServerRow(job, row); job.error = null; startPolling(); renderAll();
    } catch (error) { job.error = error?.message || String(error); renderAll(); }
  }

  async function retryJob(job) {
    if (!job?.id || job.state !== 'error') return;
    try {
      const row = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/retry`, { method: 'POST' });
      job.terminalHandled = false; job.terminalNotified = false;
      mergeServerRow(job, row); startPolling(); renderAll();
    } catch (error) { job.error = error?.message || String(error); renderAll(); }
  }

  async function removeSource(job, sourceId) {
    if (job.state !== 'draft') return;
    try {
      job.sources = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/sources/${sourceId}`, { method: 'DELETE' }) || [];
      job.sourceCount = job.sources.length; job.localFiles.delete(sourceId); job.previewUrl = null; job.boxes = [];
      await preparePreview(job).catch(() => {}); renderAll();
    } catch (error) { job.error = error?.message || String(error); renderAll(); }
  }

  async function replaceSource(job, sourceId) {
    if (job.state !== 'draft') return;
    const picker = document.createElement('input');
    picker.type = 'file'; picker.accept = 'image/jpeg,image/png,image/webp,image/heic,application/pdf,.jpg,.jpeg,.png,.webp,.heic,.pdf';
    picker.addEventListener('change', async () => {
      const file = picker.files?.[0]; if (!file) return;
      const form = new FormData(); form.append('receipt', file);
      try {
        job.sources = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/sources/${sourceId}`, { method: 'PUT', body: form }) || [];
        job.sourceCount = job.sources.length; job.localFiles.set(sourceId, file); job.previewUrl = null; job.boxes = [];
        await preparePreview(job).catch(() => {}); renderAll();
      } catch (error) { job.error = error?.message || String(error); renderAll(); }
    }, { once: true });
    picker.click();
  }

  async function reorderSources(job, orderedIds) {
    if (job.state !== 'draft') return;
    try {
      job.sources = await ctx.api(`api/purchases/receipt-scan/jobs/${job.id}/sources/order`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceIds: orderedIds })
      }) || [];
      job.sourceCount = job.sources.length; job.previewUrl = null; job.boxes = [];
      await preparePreview(job).catch(() => {}); renderAll();
    } catch (error) { job.error = error?.message || String(error); renderAll(); }
  }

  function moveSource(job, sourceId, direction) {
    const ids = job.sources.map(source => source.id);
    const index = ids.indexOf(sourceId); const target = index + direction;
    if (index < 0 || target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    reorderSources(job, ids);
  }

  function mergeServerRow(job, row) {
    if (!row) return;
    const wasTerminal = isTerminal(job.state);
    job.id = row.id || job.id; job.purchaseId = row.purchaseId || job.purchaseId;
    job.fileName = row.fileName || job.fileName; job.contentType = row.contentType || job.contentType;
    job.engine = row.engine || null; job.error = row.error || null; job.sourceCount = Number(row.sourceCount ?? job.sourceCount ?? 0);
    job.createdAt = row.createdAt || job.createdAt; job.completedAt = row.completedAt || null;
    job.stage = row.stage || serverState(row.status).stage; job.state = serverState(row.status).state;
    job.warnings = parseWarnings(row.warningsJson);
    job.purchase = job.purchaseId ? {
      id: job.purchaseId, merchant: row.merchant || '', totalAmount: row.totalAmount,
      currency: row.currency || 'EUR', status: job.state === 'done' ? 'review' : 'captured'
    } : null;
    if (wasTerminal && !isTerminal(job.state)) { job.terminalHandled = false; job.terminalNotified = false; }
    ensureRemotePreview(job);
    if (!wasTerminal && isTerminal(job.state)) handleTerminal(job);
  }

  async function handleTerminal(job) {
    if (job.terminalHandled) return;
    job.terminalHandled = true;
    await hydrateSources(job).catch(() => {});
    refreshPurchases(); renderAll();
    if (job.state === 'done' && job.legacy && !job.backgrounded && job.resolve) {
      await sleep(reducedMotion() ? 40 : 420);
      if (currentDialogJob() === job) closeDialog();
      job.resolve(job.purchase || { id: job.purchaseId, status: 'review' });
      job.resolve = null; job.reject = null; return;
    }
    if (!job.terminalNotified) {
      job.terminalNotified = true;
      ctx.toast(job.state === 'done'
        ? t('Beleg vollständig analysiert.', 'Receipt fully analyzed.')
        : t('Beleg gespeichert; Analyse benötigt Prüfung oder Wiederholung.', 'Receipt saved; analysis needs review or retry.'));
    }
  }

  function scheduleHydrate(delay = 250) {
    clearTimeout(pollTimer);
    pollTimer = window.setTimeout(() => hydrate(), delay);
  }

  async function hydrate() {
    if (hydrateRunning) return;
    hydrateRunning = true;
    try {
      const rows = (await ctx.api('api/purchases/receipt-scan/jobs?includeCompleted=true&limit=40')) || [];
      const now = Date.now();
      for (const row of rows) {
        const terminal = row.status === 'done' || row.status === 'error';
        const completed = row.completedAt ? new Date(row.completedAt).getTime() : 0;
        if (terminal && completed && now - completed > TERMINAL_VISIBLE_MS) continue;
        let job = jobs.find(x => x.id === row.id || x.localId === row.id);
        if (!job) {
          job = {
            localId: row.id || crypto.randomUUID(), id: row.id, legacy: false,
            state: serverState(row.status).state, stage: row.stage || serverState(row.status).stage,
            engine: row.engine || null, error: row.error || null, warnings: parseWarnings(row.warningsJson),
            purchaseId: row.purchaseId || null, purchase: null, fileName: row.fileName || t('Kassenbon', 'Receipt'),
            contentType: row.contentType || '', sourceCount: Number(row.sourceCount || 0), sources: [], localFiles: new Map(),
            createdAt: row.createdAt || new Date().toISOString(), completedAt: row.completedAt || null,
            backgrounded: true, previewUrl: null, boxes: [], resolve: null, reject: null,
            terminalHandled: terminal, terminalNotified: terminal, hydrated: true, readyPromise: null
          };
          jobs.push(job);
        }
        mergeServerRow(job, row);
        if (!job.sources.length || job.sources.length !== job.sourceCount) await hydrateSources(job).catch(() => {});
      }
      pruneOldJobs(); renderAll();
    } catch { /* FullWorth Space may still be booting; server jobs continue independently. */ }
    finally {
      hydrateRunning = false;
      if (hasOpenJobs()) startPolling();
    }
  }

  function startPolling() {
    clearTimeout(pollTimer); pollTimer = 0;
    if (!hasOpenJobs()) return;
    pollTimer = window.setTimeout(() => { pollTimer = 0; hydrate(); }, POLL_MS);
  }

  function hasOpenJobs() { return jobs.some(x => ['uploading', 'queued', 'running'].includes(x.state)); }
  function firstOpenJob() { return jobs.find(x => x.state === 'running') || jobs.find(x => x.state === 'uploading') || jobs.find(x => x.state === 'queued') || jobs.find(x => x.state === 'draft'); }
  function setStage(job, stage) { job.stage = stage; if (dialog?.open && currentDialogJob() === job) renderDialog(job); renderChip(); }
  function currentDialogJob() { const key = dialog?.dataset.jobKey; return jobs.find(x => jobKey(x) === key) || firstOpenJob() || jobs.at(-1) || null; }

  function ensureDialog() {
    if (dialog?.isConnected) return dialog;
    dialog = document.createElement('dialog'); dialog.className = 'receipt-ai-dialog';
    dialog.addEventListener('cancel', event => { event.preventDefault(); minimizeCurrent(); });
    document.body.appendChild(dialog); return dialog;
  }
  function showDialog(job) { if (!job) return; ensureDialog(); dialog.dataset.jobKey = jobKey(job); renderDialog(job); if (!dialog.open) dialog.showModal(); startAnimation(); }
  function closeDialog() { stopAnimation(); if (dialog?.open) dialog.close(); renderChip(); }
  function minimizeCurrent() { const job = currentDialogJob(); if (job && !isTerminal(job.state)) job.backgrounded = true; closeDialog(); }

  function addMore() {
    const job = currentDialogJob();
    if (!job || (job.state !== 'draft' && job.state !== 'uploading')) return;
    installCapture(); const input = document.getElementById('receipt-file');
    if (input) { input.multiple = true; input.click(); }
  }

  function reviewJob(job) {
    if (!job?.purchaseId) return;
    if (job.legacy && job.resolve) {
      closeDialog(); job.resolve(job.purchase || { id: job.purchaseId, status: job.state === 'done' ? 'review' : 'captured' });
      job.resolve = null; job.reject = null; return;
    }
    closeDialog();
    document.querySelector('.sidebar button[data-view="purchases"], #bottom-nav button[data-view="purchases"]')?.click();
    refreshPurchases(); ctx.toast(t('Der Kauf ist unter „Käufe“ bereit.', 'The purchase is ready under Purchases.'));
  }

  function renderDialog(job) {
    if (!dialog || !job) return;
    const busy = jobs.filter(x => ['uploading', 'queued', 'running'].includes(x.state));
    const uploadingCount = busy.filter(x => x.state === 'uploading').length;
    const queueSafety = uploadingCount
      ? t('Upload wird noch gespeichert – App offen lassen', 'Upload is still being stored — keep the app open')
      : t('App darf geschlossen werden', 'safe to close the app');
    const sourceCount = job.sources.length || job.sourceCount || 0;
    const preview = job.previewUrl
      ? `<img class="receipt-ai-image" data-preview-image src="${escapeAttr(job.previewUrl)}" alt="${escapeAttr(t('Beleg-Vorschau', 'Receipt preview'))}">`
      : isPdfJob(job)
        ? `<div class="receipt-ai-pdf"><strong>PDF</strong><span>${escapeHtml(job.fileName)}</span><small>${t(`Alle ${sourceCount || '?'} PDF-Seiten werden vollständig in Reihenfolge verarbeitet.`, `All ${sourceCount || '?'} PDF pages are processed completely in order.`)}</small></div>`
        : `<div class="receipt-ai-placeholder">${t('Vorschau wird vorbereitet …', 'Preparing preview …')}</div>`;

    const sourceRows = job.sources.map((source, index) => {
      const editable = job.state === 'draft';
      const page = source.pageNumber ? ` · ${t('Seite', 'page')} ${source.pageNumber}` : '';
      const controls = editable ? `<div class="receipt-ai-source-actions">
        <button type="button" class="ghost" data-up="${source.id}" ${index === 0 ? 'disabled' : ''}>↑</button>
        <button type="button" class="ghost" data-down="${source.id}" ${index === job.sources.length - 1 ? 'disabled' : ''}>↓</button>
        <button type="button" class="ghost" data-replace="${source.id}">${t('Ersetzen', 'Replace')}</button>
        <button type="button" class="ghost" data-remove="${source.id}">${t('Entfernen', 'Remove')}</button>
      </div>` : '';
      return `<div class="receipt-ai-source" data-source-id="${source.id}" draggable="${editable ? 'true' : 'false'}">
        <span class="receipt-ai-source-index">${index + 1}</span><div><strong>${escapeHtml(source.originalFileName || job.fileName)}</strong><span>${escapeHtml(source.sourceType === 'pdf_page' ? 'PDF' : t('Foto', 'Photo'))}${escapeHtml(page)}</span></div>${controls}</div>`;
    }).join('') || `<div class="receipt-ai-empty">${t('Noch keine Seite gespeichert.', 'No page stored yet.')}</div>`;

    const recent = jobs.slice().sort((a, b) => String(a.createdAt).localeCompare(String(b.createdAt))).slice(-8);
    const queueRows = recent.map((x, index) => `<div class="receipt-ai-queue-row ${jobKey(x) === jobKey(job) ? 'is-current' : ''}"><span class="receipt-ai-queue-index">${index + 1}</span><div><strong>${escapeHtml(x.purchase?.merchant || x.fileName)}</strong><span>${escapeHtml(stageLabel(x.stage))} · ${Number(x.sourceCount || x.sources?.length || 0)} ${t('Quelle(n)', 'source(s)')}</span></div>${isTerminal(x.state) && x.purchaseId ? `<button type="button" class="ghost receipt-ai-review" data-review="${escapeAttr(jobKey(x))}">${t('Prüfen', 'Review')}</button>` : ''}</div>`).join('');
    const warnings = job.warnings?.length ? `<div class="receipt-ai-warnings">${job.warnings.map(w => `<div>${escapeHtml(w)}</div>`).join('')}</div>` : '';
    const error = job.error ? `<div class="receipt-ai-error">${escapeHtml(job.error)}</div>` : '';

    let actions = '';
    if (job.state === 'draft' || job.state === 'uploading') {
      // Old baseline copy retained for compatibility: "+ Weitere Bons". The actual action now adds
      // more pages/images to the SAME logical receipt instead of creating more receipt jobs.
      actions = `<button type="button" class="ghost" data-add>${t('+ Weitere Seiten/Bilder', '+ Add pages/images')}</button><button type="button" class="ghost" data-background>${t('Im Hintergrund weiter', 'Continue in background')}</button><button type="button" data-start ${job.state === 'uploading' || !sourceCount ? 'disabled' : ''}>${t('Analyse starten', 'Start analysis')}</button>`;
    } else if (job.state === 'error') {
      actions = `<button type="button" class="ghost" data-review-current>${t('Manuell prüfen', 'Review manually')}</button><button type="button" data-retry>${t('Gesamten Beleg erneut analysieren', 'Retry complete receipt')}</button>`;
    } else if (job.state === 'done') {
      actions = `<button type="button" data-review-current>${t('Ergebnis prüfen', 'Review result')}</button>`;
    } else {
      actions = `<button type="button" data-background>${t('Im Hintergrund weiter', 'Continue in background')}</button>`;
    }

    dialog.innerHTML = `<div class="receipt-ai-card">
      <header class="receipt-ai-head"><div><span class="receipt-ai-eyebrow">FullWorth AI Scan</span><h2>${escapeHtml(job.state === 'draft' ? t('Beleg zusammenstellen', 'Build receipt') : job.purchase?.merchant || t('Beleg wird analysiert', 'Analyzing receipt'))}</h2></div><span class="receipt-ai-count">${sourceCount} ${t('Seiten/Bilder', 'pages/images')}</span></header>
      <div class="receipt-ai-body">
        <section class="receipt-ai-visual" data-visual>${preview}<canvas class="receipt-ai-canvas" data-particles aria-hidden="true"></canvas><div class="receipt-ai-scanline" aria-hidden="true"></div></section>
        <section class="receipt-ai-side">
          <section class="receipt-ai-progress"><div class="receipt-ai-orb" aria-hidden="true"></div><div><strong data-stage>${escapeHtml(stageLabel(job.stage))}</strong><span>${escapeHtml(stageHint(job.stage, job.engine))}</span></div></section>
          ${error}${warnings}
          <section class="receipt-ai-sources"><div class="receipt-ai-queue-head"><strong>${t('Dieser Beleg', 'This receipt')}</strong><span>${escapeHtml(queueSafety)}</span></div>${sourceRows}</section>
          <section class="receipt-ai-queue"><div class="receipt-ai-queue-head"><strong>${t('Server-Warteschlange', 'Server queue')}</strong><span>${busy.length} ${t('in Verarbeitung', 'processing')}</span></div>${queueRows}</section>
        </section>
      </div>
      <footer class="receipt-ai-actions">${actions}</footer>
    </div>`;

    dialog.querySelector('[data-background]')?.addEventListener('click', minimizeCurrent);
    dialog.querySelector('[data-add]')?.addEventListener('click', addMore);
    dialog.querySelector('[data-start]')?.addEventListener('click', () => startDraft(job));
    dialog.querySelector('[data-retry]')?.addEventListener('click', () => retryJob(job));
    dialog.querySelector('[data-review-current]')?.addEventListener('click', () => reviewJob(job));
    dialog.querySelectorAll('[data-review]').forEach(button => button.addEventListener('click', () => reviewJob(jobs.find(x => jobKey(x) === button.dataset.review))));
    dialog.querySelectorAll('[data-up]').forEach(button => button.addEventListener('click', () => moveSource(job, button.dataset.up, -1)));
    dialog.querySelectorAll('[data-down]').forEach(button => button.addEventListener('click', () => moveSource(job, button.dataset.down, 1)));
    dialog.querySelectorAll('[data-remove]').forEach(button => button.addEventListener('click', () => removeSource(job, button.dataset.remove)));
    dialog.querySelectorAll('[data-replace]').forEach(button => button.addEventListener('click', () => replaceSource(job, button.dataset.replace)));
    dialog.querySelectorAll('[data-source-id]').forEach(row => {
      row.addEventListener('dragstart', () => { draggedSourceId = row.dataset.sourceId; });
      row.addEventListener('dragover', event => { if (job.state === 'draft') event.preventDefault(); });
      row.addEventListener('drop', event => {
        event.preventDefault(); const target = row.dataset.sourceId;
        if (!draggedSourceId || !target || draggedSourceId === target) return;
        const ids = job.sources.map(source => source.id); const from = ids.indexOf(draggedSourceId); const to = ids.indexOf(target);
        if (from < 0 || to < 0) return; const [moved] = ids.splice(from, 1); ids.splice(to, 0, moved); draggedSourceId = null; reorderSources(job, ids);
      });
    });
    requestAnimationFrame(startAnimation);
  }

  function ensureChip() {
    if (chip?.isConnected) return chip;
    chip = document.createElement('button'); chip.type = 'button'; chip.className = 'receipt-ai-chip';
    chip.addEventListener('click', () => showDialog(firstOpenJob() || newestTerminalJob() || jobs.at(-1)));
    document.body.appendChild(chip); return chip;
  }

  function renderChip() {
    const visible = jobs.filter(x => ['uploading', 'draft', 'queued', 'running'].includes(x.state) || (isTerminal(x.state) && isRecentTerminal(x)));
    if (!visible.length) { if (chip) chip.hidden = true; return; }
    ensureChip(); chip.hidden = !!dialog?.open;
    const draftCount = visible.filter(x => x.state === 'draft').length;
    const running = visible.filter(x => ['uploading', 'queued', 'running'].includes(x.state)).length;
    chip.innerHTML = `<span class="receipt-ai-chip-dot"></span><strong>${running ? `${running} ${t('aktiv', 'active')}` : draftCount ? `${draftCount} ${t('Entwurf', 'draft')}` : `${visible.length} ${t('fertig', 'done')}`}</strong><span>${running ? t('Server-Analyse läuft', 'Server analysis running') : draftCount ? t('Beleg vervollständigen', 'Complete receipt') : t('Ergebnisse ansehen', 'View results')}</span>`;
  }

  function renderAll() { renderChip(); if (dialog?.open) { const selected = currentDialogJob(); if (selected) renderDialog(selected); } }

  async function preparePreview(job) {
    if (!job) return;
    const first = job.sources?.[0];
    const local = first ? job.localFiles.get(first.id) : job.localFiles.values().next().value;
    if (local && !/\.pdf$/i.test(local.name) && (local.type?.startsWith('image/') || /\.(jpe?g|png|webp)$/i.test(local.name))) {
      const url = await readDataUrl(local); job.previewUrl = url;
      try { job.boxes = await detectTextBands(url); } catch { job.boxes = syntheticBoxes(16); }
      return;
    }
    ensureRemotePreview(job);
    if (!job.boxes.length) job.boxes = syntheticBoxes(16);
  }

  function ensureRemotePreview(job) {
    if (job.previewUrl || !job.purchaseId || isPdfJob(job)) return;
    if (!String(job.contentType || '').startsWith('image/')) return;
    job.previewUrl = backendUrl(`api/purchases/${job.purchaseId}/receipt`);
  }

  function startAnimation() {
    stopAnimation(); if (!dialog?.open) return;
    const job = currentDialogJob(); const canvas = dialog.querySelector('[data-particles]'); const visual = dialog.querySelector('[data-visual]');
    if (!job || !(canvas instanceof HTMLCanvasElement) || !visual) return;
    const context = canvas.getContext('2d'); if (!context) return;
    const particles = Array.from({ length: reducedMotion() ? 0 : 42 }, () => ({ x: Math.random(), y: Math.random(), phase: Math.random() * Math.PI * 2 }));
    const frame = now => {
      if (!dialog?.open || currentDialogJob() !== job) return;
      const rect = visual.getBoundingClientRect(); const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const width = Math.max(1, Math.round(rect.width)), height = Math.max(1, Math.round(rect.height));
      if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) { canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr); canvas.style.width = `${width}px`; canvas.style.height = `${height}px`; }
      context.setTransform(dpr, 0, 0, dpr, 0, 0); context.clearRect(0, 0, width, height);
      const boxes = (job.boxes.length ? job.boxes : syntheticBoxes(14)).map(box => ({ x: box.x * width, y: box.y * height, width: box.width * width, height: box.height * height }));
      context.lineWidth = 1; boxes.forEach((box, index) => { context.strokeStyle = `hsla(${200 + (index * 17) % 150},75%,62%,.18)`; context.strokeRect(box.x, box.y, box.width, box.height); });
      particles.forEach((particle, index) => { const target = boxes[index % boxes.length]; const x = target.x + target.width * ((Math.sin(now / 900 + particle.phase) + 1) / 2); const y = target.y + target.height * .5; context.beginPath(); context.fillStyle = `hsla(${(index * 31 + now * .02) % 360},88%,66%,.7)`; context.arc(x, y, 1.4, 0, Math.PI * 2); context.fill(); });
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
  }

  function stopAnimation() { if (raf) cancelAnimationFrame(raf); raf = 0; }
  function pruneOldJobs() { for (let i = jobs.length - 1; i >= 0; i--) if (isTerminal(jobs[i].state) && !isRecentTerminal(jobs[i]) && !jobs[i].resolve) jobs.splice(i, 1); }
  function newestTerminalJob() { return jobs.slice().reverse().find(x => isTerminal(x.state) && isRecentTerminal(x)); }
  function isRecentTerminal(job) { const time = job.completedAt ? new Date(job.completedAt).getTime() : Date.now(); return Date.now() - time <= TERMINAL_VISIBLE_MS; }
  function jobKey(job) { return String(job.id || job.localId); }
  function backendUrl(path) { return typeof ctx.bffUrl === 'function' ? ctx.bffUrl(path) : standaloneBffUrl(path); }

  return { update, enqueueForeground, hydrate };
}

function serverState(status) {
  if (status === 'draft') return { state: 'draft', stage: 'draft' };
  if (status === 'processing') return { state: 'running', stage: 'preparing' };
  if (status === 'done') return { state: 'done', stage: 'done' };
  if (status === 'error') return { state: 'error', stage: 'error' };
  return { state: 'queued', stage: 'queued' };
}

function stageLabel(stage) {
  const labels = {
    draft: t('Seiten prüfen und sortieren', 'Review and order pages'), queued: t('Wartet auf Server', 'Waiting on server'),
    preparing: t('Quellen werden vorbereitet', 'Preparing sources'), connecting: t('GPT-Verbindung wird geprüft', 'Checking GPT connection'),
    analyzing: t('GPT analysiert den gesamten Beleg', 'GPT is analyzing the complete receipt'), structuring: t('Artikel und Summen werden strukturiert', 'Structuring items and totals'),
    saving: t('Ergebnis wird gespeichert', 'Saving result'), ocr: t('Lokales OCR verarbeitet alle Seiten', 'Local OCR is processing all pages'),
    done: t('Analyse abgeschlossen', 'Analysis complete'), error: t('Manuelle Prüfung nötig', 'Manual review needed')
  };
  return labels[stage] || labels.preparing;
}
function stageHint(stage, engine) {
  if (stage === 'draft') return t('Füge alle Fotos/PDF-Seiten dieses einen Belegs hinzu. Erst „Analyse starten“ friert die Reihenfolge ein.', 'Add every photo/PDF page for this one receipt. “Start analysis” freezes the order.');
  if (stage === 'ocr') return t('GPT ist nicht verfügbar; FullWorth verarbeitet jede Quelle mit dem lokalen Fallback.', 'GPT is unavailable; FullWorth processes every source with the local fallback.');
  if (stage === 'done') return engine === 'gpt' ? t('Der vollständige GPT-Beleg ist bereit zur Prüfung.', 'The complete GPT receipt is ready for review.') : t('Der Beleg ist bereit zur Prüfung.', 'The receipt is ready for review.');
  if (stage === 'queued') return t('Alle Quellen sind bereits serverseitig gespeichert. Du kannst die App schließen.', 'All sources are already stored on the server. You can close the app.');
  return t('Keine Fake-Prozentanzeige – nur der tatsächliche Verarbeitungsschritt.', 'No fake percentage — only the actual processing step.');
}

async function detectTextBands(dataUrl) {
  const image = await loadImage(dataUrl); const maxWidth = 360; const scale = Math.min(1, maxWidth / Math.max(1, image.naturalWidth));
  const width = Math.max(80, Math.round(image.naturalWidth * scale)), height = Math.max(120, Math.round(image.naturalHeight * scale));
  const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
  const context = canvas.getContext('2d', { willReadFrequently: true }); if (!context) return syntheticBoxes(16);
  context.drawImage(image, 0, 0, width, height); const pixels = context.getImageData(0, 0, width, height).data;
  const energy = new Float32Array(height); let total = 0;
  for (let y = 0; y < height; y++) { let row = 0; for (let x = 2; x < width; x += 2) { const a = (y * width + x) * 4, b = (y * width + x - 2) * 4; const ga = pixels[a] * .299 + pixels[a + 1] * .587 + pixels[a + 2] * .114; const gb = pixels[b] * .299 + pixels[b + 1] * .587 + pixels[b + 2] * .114; row += Math.abs(ga - gb); } energy[y] = row / Math.max(1, width / 2); total += energy[y]; }
  const threshold = Math.max(7, (total / height) * 1.22); const bands = []; let start = -1;
  for (let y = 1; y < height - 1; y++) { const active = (energy[y - 1] + energy[y] + energy[y + 1]) / 3 > threshold; if (active && start < 0) start = y; if ((!active || y === height - 2) && start >= 0) { const end = y; if (end - start >= 2 && end - start <= Math.max(18, height * .045)) bands.push([start, end]); start = -1; } }
  const boxes = bands.slice(0, 30).map(([y0, y1]) => ({ x: .16, y: y0 / height, width: .68, height: Math.max(5, y1 - y0 + 4) / height }));
  return boxes.length >= 5 ? boxes : syntheticBoxes(16);
}

function syntheticBoxes(count) { return Array.from({ length: count }, (_, i) => ({ x: .18 + (i % 4 === 0 ? .06 : 0), y: .12 + i * (.72 / Math.max(1, count - 1)), width: .56 + ((i * 17) % 20) / 100, height: .012 + (i % 3) * .004 })); }
function refreshPurchases() { document.getElementById('purchase-source')?.dispatchEvent(new Event('change')); }
function ensureCss() { if (document.querySelector('link[data-receipt-ai-css]')) return; const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = '/features/receipt-scan-ai.css?v=3'; link.dataset.receiptAiCss = '1'; document.head.appendChild(link); }
function setInputMultiple() { const input = document.getElementById('receipt-file'); if (input) input.multiple = true; }
function isTerminal(state) { return state === 'done' || state === 'error'; }
function isPdfJob(job) { return job?.contentType === 'application/pdf' || /\.pdf$/i.test(job?.fileName || '') || job?.sources?.[0]?.sourceType === 'pdf_page'; }
function sameFile(a, b) { return a === b || (!!a && !!b && a.name === b.name && a.size === b.size && a.lastModified === b.lastModified); }
function contentTypeFromName(name) { if (/\.pdf$/i.test(name || '')) return 'application/pdf'; if (/\.png$/i.test(name || '')) return 'image/png'; if (/\.webp$/i.test(name || '')) return 'image/webp'; if (/\.heic$/i.test(name || '')) return 'image/heic'; return 'image/jpeg'; }
function reducedMotion() { return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches; }
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }
function t(de, en) { return (document.documentElement.lang || localStorage.getItem('finance.language') || 'de').toLowerCase().startsWith('de') ? de : en; }
function readDataUrl(file) { return new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(String(reader.result || '')); reader.onerror = () => reject(reader.error || new Error('Preview failed.')); reader.readAsDataURL(file); }); }
function loadImage(src) { return new Promise((resolve, reject) => { const img = new Image(); img.onload = () => resolve(img); img.onerror = reject; img.src = src; }); }
function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
function escapeAttr(value) { return escapeHtml(value); }
function parseWarnings(value) { if (!value) return []; if (Array.isArray(value)) return value; try { const parsed = JSON.parse(value); return Array.isArray(parsed) ? parsed : []; } catch { return []; } }

function standaloneWithSpace(path) {
  const space = localStorage.getItem('finance.space'); if (!space) throw new Error('FullWorth Space not loaded yet.');
  const [base, query = ''] = String(path).replace(/^\//, '').split('?'); const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', space); return `${base}?${params}`;
}
async function standaloneApi(path, options) {
  const response = await fetch(`/bff/backend/${standaloneWithSpace(path)}`, options);
  if (!response.ok) { let message = String(response.status); try { const body = await response.json(); message = body.error || body.title || body.message || message; } catch { } throw new Error(message); }
  if (response.status === 204) return null; return response.json();
}
function standaloneBffUrl(path) { return `/bff/backend/${standaloneWithSpace(path)}`; }
function standaloneToast(text) { const el = document.getElementById('toast'); if (!el) return; el.textContent = text; el.classList.add('show'); clearTimeout(standaloneToast.timer); standaloneToast.timer = setTimeout(() => el.classList.remove('show'), 3200); }
function standaloneContext() { return { api: standaloneApi, bffUrl: standaloneBffUrl, toast: standaloneToast }; }

// Restore drafts and running jobs even after a full browser restart.
function bootstrapPersistentQueue() {
  const run = () => {
    setInputMultiple();
    if (!localStorage.getItem('finance.space')) { setTimeout(run, 900); return; }
    singleton ??= createExperience(standaloneContext());
    singleton.hydrate();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', run, { once: true });
  else setTimeout(run, 0);
}
bootstrapPersistentQueue();