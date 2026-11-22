// Stacks: the compose.yaml and .env editors (CodeMirror over a <textarea>, which stays the form
// field), validation errors in the editor's gutter, and live output of stack jobs over SignalR.
(() => {
  'use strict';

  const MAX_LOG_LINES = 5000;
  const hub = () => SocklessPort.connection;
  // Editors by their textarea's id, so validation results can find the compose editor.
  const editors = new Map();

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  // ---------------------------------------------------------------- editors

  // <textarea data-code-editor="yaml|env">: without the bundle (or scripts) it stays a plain text area.
  SocklessPort.component('textarea[data-code-editor]', (textarea) => {
    if (!window.SocklessEditor) return null;
    // A page restored from htmx's history cache still holds the previous editor's markup.
    for (const stale of textarea.parentElement.querySelectorAll(':scope > .cm-editor')) stale.remove();
    const editor = window.SocklessEditor.create(textarea, {
      language: textarea.dataset.codeEditor,
      label: document.querySelector(`label[for="${textarea.id}"]`)?.textContent.trim(),
    });
    textarea.hidden = true;
    editors.set(textarea.id, editor);
    // Labels point at the textarea; send their clicks to the editor instead.
    const label = document.querySelector(`label[for="${textarea.id}"]`);
    const focus = (event) => { event.preventDefault(); editor.focus(); };
    label?.addEventListener('click', focus);
    return () => {
      label?.removeEventListener('click', focus);
      editors.delete(textarea.id);
      editor.destroy();
    };
  });

  // Validation results carry the errors that name a line: show them in the compose editor.
  SocklessPort.component('[data-compose-diagnostics]', (result) => {
    let diagnostics = [];
    try {
      diagnostics = JSON.parse(result.dataset.composeDiagnostics || '[]');
    } catch {
      return null;
    }
    editors.get('Input_Compose')?.setDiagnostics(diagnostics);
    return null;
  });

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
