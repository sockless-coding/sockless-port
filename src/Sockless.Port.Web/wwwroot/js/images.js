// Image jobs: live pull and build progress over SignalR, the pull dialog, and forms that pull
// first (create and recreate). Pulls and builds run on the server; leaving the page doesn't stop them.
(() => {
  'use strict';

  const MAX_LOG_LINES = 5000;
  const MAX_LAYERS = 40;
  // Don't flash the dialog for a pull that finishes at once (the image is already there).
  const DIALOG_DELAY_MS = 300;

  const bytes = (value) => SocklessPort.bytes(value);
  const hub = () => SocklessPort.connection;
  const connected = () => hub().state === signalR.HubConnectionState.Connected;

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  // ---------------------------------------------------------------- rendering

  // The same structure as Pages/Engine/Images/_ImageJob.cshtml, for jobs that start in the browser.
  function jobElement(kind, target) {
    const root = el('div', 'job');
    root.dataset.jobKind = kind;
    const head = el('div', 'flex flex-wrap items-center justify-between gap-2');
    const title = el('p', 'min-w-0 break-all');
    title.append(el('span', 'label-caps mr-2', kind === 'build' ? 'Build' : 'Pull'), el('span', 'font-mono', target));
    const badge = el('span');
    badge.dataset.jobBadge = '';
    head.append(title, badge);
    const progress = el('progress', 'progress mt-2');
    progress.dataset.jobProgress = '';
    progress.setAttribute('aria-label', `${kind === 'build' ? 'Build' : 'Pull'} progress of ${target}`);
    const summary = el('p', 'mt-1 text-xs text-secondary', 'Starting…');
    summary.dataset.jobSummary = '';
    const announce = el('p', 'sr-only');
    announce.setAttribute('role', 'status');
    announce.dataset.jobAnnounce = '';
    root.append(head, progress, summary, announce);
    if (kind === 'build') {
      const log = el('div', 'log-view wrap mt-3 h-80');
      log.setAttribute('role', 'log');
      log.setAttribute('aria-label', 'Build output');
      log.tabIndex = 0;
      log.dataset.jobLog = '';
      root.append(log);
    } else {
      const layers = el('ul', 'job-layers');
      layers.setAttribute('aria-label', 'Layers');
      layers.dataset.jobLayers = '';
      root.append(layers);
    }
    return root;
  }

  // Mirrors ImageDisplay.JobBadge and ImageDisplay.PullProgress on the server.
  function badgeFor(msg) {
    switch (msg.state) {
      case 'running': return [msg.kind === 'build' ? 'Building…' : 'Pulling…', 'badge warning'];
      case 'succeeded': return ['Done', 'badge success'];
      case 'failed': return ['Failed', 'badge danger'];
      default: return ['Cancelled', 'badge-stat'];
    }
  }

  function pullProgress(msg) {
    if (msg.layers.length === 0) return msg.messages.at(-1) ?? 'Asking the registry…';
    const layers = `${msg.layersDone} of ${msg.layers.length} layers`;
    return msg.total > 0 ? `${layers} · ${bytes(msg.downloaded)} of ${bytes(msg.total)}` : layers;
  }

  function summaryFor(msg) {
    if (msg.state !== 'running') return msg.message ?? '';
    return msg.kind === 'build' ? 'Building…' : pullProgress(msg);
  }

  const PHASES = { waiting: 'Waiting', downloading: 'Downloading', downloaded: 'Downloaded', extracting: 'Extracting', done: 'Done' };

  function renderLayers(list, msg) {
    const fragment = document.createDocumentFragment();
    for (const layer of msg.layers.slice(0, MAX_LAYERS)) {
      const item = el('li', 'job-layer');
      item.append(el('span', 'font-mono', layer.id), el('span', 'job-layer-status', PHASES[layer.phase] ?? layer.status));
      const bar = el('progress', 'progress');
      bar.setAttribute('aria-label', `Layer ${layer.id}`);
      if (layer.total > 0) {
        bar.max = layer.total;
        bar.value = layer.current;
      } else if (layer.phase === 'done' || layer.phase === 'downloaded' || layer.phase === 'extracting') {
        bar.max = 1;
        bar.value = 1;
      } else if (layer.phase === 'waiting') {
        bar.max = 1;
        bar.value = 0;
      }
      item.append(bar, el('span', 'job-layer-size font-mono', layer.total > 0 ? `${bytes(layer.current)} / ${bytes(layer.total)}` : ''));
      fragment.append(item);
    }
    if (msg.layers.length > MAX_LAYERS) fragment.append(el('li', 'text-xs text-secondary', `and ${msg.layers.length - MAX_LAYERS} more layers`));
    list.replaceChildren(fragment);
  }

  // state: { lastState, logReset } kept per element between messages.
  function render(root, msg, state) {
    const $ = (selector) => root.querySelector(selector);
    const [label, badgeClass] = badgeFor(msg);
    const badge = $('[data-job-badge]');
    if (badge) badge.replaceChildren(el('span', badgeClass, label));
    const cancel = $('[data-job-cancel]');
    if (cancel) cancel.hidden = msg.state !== 'running';

    const progress = $('[data-job-progress]');
    if (progress) {
      if (msg.state === 'succeeded') {
        progress.max = 1;
        progress.value = 1;
      } else if (msg.state === 'running' && msg.kind === 'pull' && msg.total > 0) {
        progress.max = msg.total;
        progress.value = msg.downloaded;
      } else if (msg.state === 'running') {
        progress.removeAttribute('value'); // indeterminate
      } else {
        progress.max = 1;
        progress.value = 0;
      }
    }

    const summary = $('[data-job-summary]');
    if (summary) {
      summary.textContent = summaryFor(msg);
      summary.classList.toggle('text-danger', msg.state === 'failed');
      summary.classList.toggle('text-secondary', msg.state !== 'failed');
    }
    const announce = $('[data-job-announce]');
    if (announce && state.lastState !== msg.state) {
      announce.textContent = msg.state === 'running' ? `${label} ${msg.target}` : `${label}: ${summaryFor(msg)}`;
    }
    state.lastState = msg.state;

    const layers = $('[data-job-layers]');
    if (layers) renderLayers(layers, msg);

    const log = $('[data-job-log]');
    if (log) {
      // The first message carries the whole log: replace what the server rendered.
      if (!state.logReset) {
        log.replaceChildren();
        state.logReset = true;
      }
      const atEnd = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
      const fragment = document.createDocumentFragment();
      for (const line of msg.log) fragment.append(el('div', 'log-line', line));
      log.append(fragment);
      while (log.childElementCount > MAX_LOG_LINES) log.firstElementChild.remove();
      if (atEnd) log.scrollTop = log.scrollHeight;
    }
  }

  // Streams a job into root until it ends. onEnd(msg) gets the final message.
  function watch(root, jobId, onEnd) {
    const state = { lastState: null, logReset: false };
    let last = null;
    const subscription = hub().stream('WatchJob', jobId).subscribe({
      next: (msg) => {
        last = msg;
        render(root, msg, state);
      },
      complete: () => onEnd?.(last),
      error: (err) => {
        const summary = root.querySelector('[data-job-summary]');
        if (summary) summary.textContent = SocklessPort.message(err);
        onEnd?.(null, err);
      },
    });
    return () => subscription.dispose();
  }

  async function startPull(endpointId, reference, onlyIfMissing) {
    await SocklessPort.ready;
    return hub().invoke('StartPull', Number(endpointId), reference, onlyIfMissing);
  }

  // ---------------------------------------------------------------- the dialog

  // Shows a running job. Resolves with the final message, or null when the user cancels or
  // sends it to the background. background: offer "Continue in background".
  function showJob({ jobId, kind, target, background, closeOnSuccess, delay = 0 }) {
    const dialog = document.getElementById('job-dialog');
    const body = dialog.querySelector('[data-job-dialog-body]');
    const title = dialog.querySelector('#job-dialog-title');
    const backgroundButton = dialog.querySelector('[data-job-dialog-background]');
    const cancelButton = dialog.querySelector('[data-job-dialog-cancel]');
    const closeButton = dialog.querySelector('[data-job-dialog-close]');

    const job = jobElement(kind, target);
    body.replaceChildren(job);
    title.textContent = kind === 'build' ? `Building ${target}` : `Pulling ${target}`;
    backgroundButton.hidden = !background;
    cancelButton.hidden = false;
    closeButton.hidden = true;

    return new Promise((resolve) => {
      let settled = false;
      let stop = () => {};
      const timer = setTimeout(() => { if (!settled) dialog.showModal(); }, delay);

      const finish = (result) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        stop();
        backgroundButton.removeEventListener('click', onBackground);
        cancelButton.removeEventListener('click', onCancel);
        dialog.removeEventListener('cancel', onEscape);
        resolve(result);
      };
      const close = () => { if (dialog.open) dialog.close(); };
      const onBackground = () => {
        finish(null);
        close();
        htmx.trigger(document.body, 'image-jobs-changed');
      };
      const onCancel = () => {
        hub().invoke('CancelJob', jobId).catch(() => {});
        finish(null);
        close();
      };
      // Escape: a pull the page waits for is cancelled; a standalone one carries on in the background.
      const onEscape = (event) => {
        event.preventDefault();
        if (background) onBackground(); else onCancel();
      };
      backgroundButton.addEventListener('click', onBackground);
      cancelButton.addEventListener('click', onCancel);
      dialog.addEventListener('cancel', onEscape);
      closeButton.addEventListener('click', close, { once: true });

      stop = watch(job, jobId, (msg, err) => {
        if (msg?.state === 'succeeded' && closeOnSuccess) {
          finish(msg);
          close();
          return;
        }
        // Show how it ended and let the user close it.
        backgroundButton.hidden = true;
        cancelButton.hidden = true;
        closeButton.hidden = false;
        if (!dialog.open) dialog.showModal();
        closeButton.focus();
        finish(err ? null : msg);
        if (msg?.state === 'succeeded') htmx.trigger(document.body, 'image-jobs-changed');
      });
    });
  }

  // ---------------------------------------------------------------- components

  // A job listed on the page (pulls and builds panel, build page).
  SocklessPort.component('[data-image-job]', (root) => {
    let stop = () => {};
    const cancel = root.querySelector('[data-job-cancel]');
    const onCancel = () => hub().invoke('CancelJob', root.dataset.imageJob).catch((err) => SocklessPort.message(err));
    cancel?.addEventListener('click', onCancel);

    async function start() {
      stop();
      await SocklessPort.ready;
      if (!root.isConnected) return;
      stop = watch(root, root.dataset.imageJob);
    }
    const onReconnect = () => start();
    document.addEventListener('sockless:reconnected', onReconnect);
    start();
    return () => {
      stop();
      cancel?.removeEventListener('click', onCancel);
      document.removeEventListener('sockless:reconnected', onReconnect);
    };
  });

  // "Pull" forms: with scripts, pull as a job with live progress instead of posting and waiting.
  SocklessPort.component('[data-pull-form]', (form) => SocklessPort.beforeSubmit(form, async () => {
    const reference = form.querySelector('[name=reference]')?.value.trim();
    if (!reference || !connected()) return true; // let the server explain or do it
    let jobId;
    try {
      jobId = await startPull(form.dataset.endpoint, reference, false);
    } catch (err) {
      alertDialog('The pull didn’t start', SocklessPort.message(err));
      return false;
    }
    const input = form.querySelector('input[name=reference]:not([type=hidden])');
    if (input) input.value = '';
    showJob({ jobId, kind: 'pull', target: reference, background: true, closeOnSuccess: false });
    return false;
  }));

  // Forms that need the image before they post (create, recreate): pull first with progress,
  // then tell the server it's done (data-pulled) so it doesn't pull again.
  SocklessPort.component('[data-pull-before-submit]', (form) => SocklessPort.beforeSubmit(form, async () => {
    const pulled = form.querySelector('[data-pulled]');
    let reference;
    let onlyIfMissing;
    if (form.dataset.pullImage) {
      reference = document.querySelector(form.dataset.pullImage)?.value.trim();
      onlyIfMissing = document.querySelector(form.dataset.pullMode)?.value !== 'always';
    } else {
      if (!document.querySelector(form.dataset.pullWhen)?.checked) return true;
      reference = form.dataset.pullReference?.trim();
      onlyIfMissing = false;
    }
    // No name to pull (the form reports it), an image ID, or no hub: the server handles it as before.
    if (!reference || reference.startsWith('sha256:') || !connected()) return true;

    let jobId;
    try {
      jobId = await startPull(form.dataset.endpoint, reference, onlyIfMissing);
    } catch (err) {
      // An unparseable name: let the server's validation say so.
      return true;
    }
    const result = await showJob({ jobId, kind: 'pull', target: reference, background: false, closeOnSuccess: true, delay: DIALOG_DELAY_MS });
    if (result?.state !== 'succeeded') return false;
    if (pulled) pulled.value = 'true';
    return true;
  }));

  // Build form: show the fields for the chosen source.
  SocklessPort.component('[data-build-form]', (form) => {
    const sync = () => {
      const source = form.querySelector('[data-build-source]:checked')?.value ?? 'dockerfile';
      for (const panel of form.querySelectorAll('[data-build-panel]')) panel.hidden = panel.dataset.buildPanel !== source;
    };
    form.addEventListener('change', sync);
    sync();
    return () => form.removeEventListener('change', sync);
  });

  function alertDialog(heading, message) {
    const dialog = document.getElementById('job-dialog');
    dialog.querySelector('#job-dialog-title').textContent = heading;
    dialog.querySelector('[data-job-dialog-body]').replaceChildren(el('p', 'text-danger', message));
    dialog.querySelector('[data-job-dialog-background]').hidden = true;
    dialog.querySelector('[data-job-dialog-cancel]').hidden = true;
    const close = dialog.querySelector('[data-job-dialog-close]');
    close.hidden = false;
    close.addEventListener('click', () => dialog.close(), { once: true });
    dialog.showModal();
    close.focus();
  }
})();
