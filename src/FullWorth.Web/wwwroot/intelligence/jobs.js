const apiBase = '/bff/backend/api/intelligence/admin';
const list = document.getElementById('job-list');
const refresh = document.getElementById('refresh-jobs');

function formatDate(value) {
  return value ? new Date(value).toLocaleString() : '—';
}

function render(jobs) {
  list.replaceChildren();
  if (!jobs.length) {
    const empty = document.createElement('p');
    empty.className = 'row-sub';
    empty.textContent = 'Noch keine Intelligence-Jobs.';
    list.append(empty);
    return;
  }

  for (const job of jobs) {
    const row = document.createElement('div');
    row.className = 'row';
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = `${job.type} · ${job.status}`;
    const meta = document.createElement('div');
    meta.className = 'intel-row-meta';
    const bits = [
      `geplant ${formatDate(job.scheduledFor)}`,
      job.startedAt ? `gestartet ${formatDate(job.startedAt)}` : null,
      job.completedAt ? `fertig ${formatDate(job.completedAt)}` : null,
      job.nextRetryAt ? `Retry ${formatDate(job.nextRetryAt)}` : null,
      job.retryCount ? `Versuch ${job.retryCount + 1}` : null,
      job.errorCode ? `Grund: ${job.errorCode}` : null
    ].filter(Boolean);
    meta.textContent = bits.join(' · ');
    main.append(title, meta);
    row.append(main);
    list.append(row);
  }
}

async function loadJobs() {
  refresh.disabled = true;
  try {
    const response = await fetch(`${apiBase}/jobs?limit=50`, { headers: { Accept: 'application/json' } });
    if (response.status === 401) return;
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    render(await response.json());
  } catch {
    list.textContent = 'Jobstatus konnte nicht geladen werden.';
  } finally {
    refresh.disabled = false;
  }
}

async function enqueue(type, button) {
  button.disabled = true;
  try {
    const response = await fetch(`${apiBase}/jobs/${encodeURIComponent(type)}/enqueue`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({ idempotencyKey: crypto.randomUUID() })
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    await loadJobs();
  } catch {
    list.textContent = 'Job konnte nicht eingereiht werden.';
  } finally {
    button.disabled = false;
  }
}

function installManualControls() {
  const host = refresh.parentElement;
  if (!host || host.querySelector('[data-intel-enqueue]')) return;
  const controls = document.createElement('div');
  controls.className = 'intel-row-actions';
  const definitions = [
    ['daily-incremental', 'Daily jetzt'],
    ['weekly-deep', 'Weekly jetzt'],
    ['monthly-review', 'Monthly jetzt']
  ];
  for (const [type, label] of definitions) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'ghost';
    button.dataset.intelEnqueue = type;
    button.textContent = label;
    button.addEventListener('click', () => enqueue(type, button));
    controls.append(button);
  }
  host.insertBefore(controls, refresh);
}

installManualControls();
refresh.addEventListener('click', loadJobs);
loadJobs();
