// Stacks: live output of stack jobs over SignalR. The compose.yaml and .env editors are editor.js.
(() => {
  'use strict';

  const MAX_LOG_LINES = 5000;
  const hub = () => SocklessPort.connection;

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  // ---------------------------------------------------------------- jobs

  const BADGES = { running: 'badge warning', succeeded: 'badge success', failed: 'badge danger', canceled: 'badge-stat' };

  function render(root, msg, state) {
    const $ = (selector) => root.querySelector(selector);
    $('[data-job-badge]')?.replaceChildren(el('span', BADGES[msg.state] ?? 'badge-stat', msg.badge));
    const cancel = $('[data-job-cancel]');
    if (cancel) cancel.hidden = msg.state !== 'running';

    const progress = $('[data-job-progress]');
    if (progress) {
      if (msg.state === 'running') {
        progress.removeAttribute('value'); // indeterminate
      } else {
        progress.max = 1;
        progress.value = msg.state === 'succeeded' ? 1 : 0;
      }
    }

    const summary = $('[data-job-summary]');
    if (summary) {
      summary.textContent = msg.summary;
      summary.classList.toggle('text-danger', msg.state === 'failed');
      summary.classList.toggle('text-secondary', msg.state !== 'failed');
    }
    const announce = $('[data-job-announce]');
    if (announce && state.lastState !== msg.state) announce.textContent = msg.summary;
    state.lastState = msg.state;

    const hints = $('[data-job-hints]');
    if (hints) {
      hints.replaceChildren(...msg.hints.map((hint) => el('li', '', hint)));
      hints.hidden = msg.hints.length === 0;
    }

    const log = $('[data-job-log]');
    if (log) {
      // The first message carries the whole log: replace what the server rendered.
      if (!state.logReset) {
        log.replaceChildren();
        state.logReset = true;
      }
      const atEnd = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
      const fragment = document.createDocumentFragment();
      for (const line of msg.log) fragment.append(el('div', line.startsWith('$ ') ? 'log-line log-command' : 'log-line', line));
      log.append(fragment);
      while (log.childElementCount > MAX_LOG_LINES) log.firstElementChild.remove();
      if (atEnd) log.scrollTop = log.scrollHeight;
    }
  }

  SocklessPort.component('[data-stack-job]', (root) => {
    const jobId = root.dataset.stackJob;
    let subscription = null;
    let sawRunning = false;
    const cancel = root.querySelector('[data-job-cancel]');
    const onCancel = () => hub().invoke('CancelStackJob', jobId).catch((err) => console.warn(SocklessPort.message(err)));
    cancel?.addEventListener('click', onCancel);

    async function start() {
      subscription?.dispose();
      await SocklessPort.ready;
      if (!root.isConnected) return;
      const state = { lastState: null, logReset: false };
      let last = null;
      subscription = hub().stream('WatchStackJob', jobId).subscribe({
        next: (msg) => {
          last = msg;
          if (msg.state === 'running') sawRunning = true;
          render(root, msg, state);
        },
        complete: () => {
          // The job ended while the page watched: refresh what it changed (state, badges, files).
          if (sawRunning && last && last.state !== 'running') {
            const details = root.querySelector('details');
            if (details && last.state === 'succeeded') details.open = false;
            htmx.trigger(document.body, 'stack-job-ended', { stack: last.stack });
          }
        },
        error: (err) => {
          const summary = root.querySelector('[data-job-summary]');
          if (summary) summary.textContent = SocklessPort.message(err);
        },
      });
    }

    const onReconnect = () => start();
    document.addEventListener('sockless:reconnected', onReconnect);
    start();
    return () => {
      subscription?.dispose();
      cancel?.removeEventListener('click', onCancel);
      document.removeEventListener('sockless:reconnected', onReconnect);
    };
  });
})();
