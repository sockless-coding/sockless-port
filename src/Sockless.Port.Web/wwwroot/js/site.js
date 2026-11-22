// Sockless Port: live updates and small UI behaviours.
// Loaded once by the layout; survives hx-boost navigation, so everything here is re-evaluated
// on htmx:afterSettle rather than on page load. No inline handlers: the CSP forbids them.
(() => {
  'use strict';

  const REFRESH_DEBOUNCE_MS = 750;
  const body = () => document.body;

  // ---------------------------------------------------------------- live updates

  // The endpoint whose page is showing, from <div data-endpoint-id> in the page content.
  const pageEndpoint = () => document.querySelector('[data-endpoint-id]')?.dataset.endpointId ?? null;

  let watched = null;
  let refreshTimer = null;

  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/engine')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  let markReady;
  const ready = new Promise((resolve) => { markReady = resolve; });

  async function syncWatch() {
    if (connection.state !== signalR.HubConnectionState.Connected) return;
    const wanted = pageEndpoint();
    if (wanted === watched) return;
    try {
      if (watched) await connection.invoke('Unwatch', Number(watched));
      watched = wanted;
      if (wanted) await connection.invoke('Watch', Number(wanted));
    } catch (err) {
      watched = null;
      console.warn('Sockless Port: could not watch endpoint', err);
    }
  }

  // Engine events arrive in bursts (a compose up emits dozens); refresh once per burst.
  connection.on('engineEvent', (e) => {
    if (String(e.endpointId) !== watched) return;
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => htmx.trigger(body(), 'engine-changed', e), REFRESH_DEBOUNCE_MS);
  });

  // Any endpoint: lists and the switcher. The page's own endpoint: its content too.
  connection.on('endpointState', (e) => {
    htmx.trigger(body(), 'endpoints-changed', e);
    if (String(e.endpointId) === pageEndpoint()) htmx.trigger(body(), 'endpoint-state', e);
  });

  connection.onreconnected(() => {
    watched = null;
    syncWatch();
    // Events may have been missed while disconnected.
    htmx.trigger(body(), 'endpoints-changed', {});
    htmx.trigger(body(), 'endpoint-state', {});
    document.dispatchEvent(new CustomEvent('sockless:reconnected'));
  });

  async function start(attempt = 0) {
    try {
      await connection.start();
      markReady();
      await syncWatch();
    } catch {
      // Signed out or server restarting: back off, give up after ~5 minutes.
      if (attempt < 20) setTimeout(() => start(attempt + 1), Math.min(30000, 2000 * (attempt + 1)));
    }
  }

  // ---------------------------------------------------------------- page components

  // SocklessPort.component('[data-logs]', (el) => { …; return () => cleanup(); })
  // runs init for each matching element once it's on the page, and the returned cleanup when the
  // element is swapped out (boosted navigation or an htmx refresh).
  const components = [];
  const cleanups = new WeakMap();

  function mount(root) {
    for (const { selector, init } of components) {
      const elements = root.matches?.(selector) ? [root] : [];
      elements.push(...root.querySelectorAll(selector));
      for (const el of elements) {
        if (cleanups.has(el)) continue;
        cleanups.set(el, null);
        try {
          cleanups.set(el, init(el) ?? null);
        } catch (err) {
          console.error('Sockless Port: component failed', selector, err);
        }
      }
    }
  }

  document.addEventListener('htmx:beforeCleanupElement', (event) => {
    const cleanup = cleanups.get(event.target);
    if (typeof cleanup === 'function') cleanup();
    cleanups.delete(event.target);
  });

  window.SocklessPort = {
    connection,
    ready,
    beforeSubmit(form, hook) {
      beforeSubmitHooks.set(form, hook);
      return () => { if (beforeSubmitHooks.get(form) === hook) beforeSubmitHooks.delete(form); };
    },
    component(selector, init) {
      components.push({ selector, init });
      if (document.readyState !== 'loading') mount(document.body);
    },
    // Bytes in decimal units, like the server's Format.Bytes.
    bytes(value) {
      if (!(value > 0)) return '0 B';
      const units = ['B', 'kB', 'MB', 'GB', 'TB'];
      let unit = 0;
      while (value >= 1000 && unit < units.length - 1) { value /= 1000; unit++; }
      return `${unit === 0 || value >= 100 ? Math.round(value) : value.toFixed(value >= 10 ? 1 : 2)} ${units[unit]}`;
    },
    // Hub errors arrive as "An unexpected error occurred invoking 'X' on the server. HubException: message".
    message(err) {
      const text = String(err?.message ?? err);
      const marker = 'HubException: ';
      return text.includes(marker) ? text.slice(text.indexOf(marker) + marker.length) : text;
    },
  };

  // Mount as soon as new content is in the DOM (afterSwap), not ~20 ms later at settle: a click
  // in between would otherwise hit an unwired form. Mounting twice is a no-op.
  document.addEventListener('htmx:afterSwap', (event) => mount(event.target));
  document.addEventListener('htmx:afterSettle', (event) => {
    syncWatch();
    mount(event.target);
  });
  document.addEventListener('DOMContentLoaded', () => mount(document.body));
  start();

  // ---------------------------------------------------------------- confirm dialog

  // <button data-confirm="Remove web?"> and <button data-confirm-bulk="Remove {list}?"> (the
  // checked rows' data-name) ask first. htmx raises htmx:confirm for every request it makes.
  const dialog = () => document.getElementById('confirm-dialog');

  function ask(message, label) {
    const d = dialog();
    if (!d) return Promise.resolve(window.confirm(message));
    d.querySelector('#confirm-message').textContent = message;
    d.querySelector('[data-confirm-ok]').textContent = label || 'Confirm';
    d.returnValue = '';
    d.showModal();
    return new Promise((resolve) => d.addEventListener('close', () => resolve(d.returnValue === 'confirm'), { once: true }));
  }

  function confirmationFor(submitter) {
    if (!submitter) return null;
    if (submitter.dataset.confirm) return submitter.dataset.confirm;
    if (submitter.dataset.confirmBulk) {
      const form = submitter.form;
      const names = [...form.querySelectorAll('input[name=ids]:checked')].map((box) => box.dataset.name || box.value);
      const list = names.length <= 5 ? names.join(', ') : `${names.length} ${form.dataset.bulkNoun || 'containers'}`;
      return submitter.dataset.confirmBulk.replace('{list}', list);
    }
    return null;
  }

  // SocklessPort.beforeSubmit(form, async (submitter) => true | false): runs after any confirmation
  // and before the request; false stops it. Used to pull an image with live progress first.
  const beforeSubmitHooks = new WeakMap();

  document.addEventListener('htmx:confirm', (event) => {
    const submitter = event.detail.triggeringEvent?.submitter;
    const message = confirmationFor(submitter);
    const form = event.detail.elt;
    const hook = form instanceof HTMLFormElement ? beforeSubmitHooks.get(form) : undefined;
    if (!message && !hook) return;
    event.preventDefault();
    (message ? ask(message, submitter.textContent.trim()) : Promise.resolve(true))
      .then(async (ok) => {
        if (!ok) return;
        // Request values are collected after this, so a hook may still change the form.
        if (hook && !(await hook(submitter))) return;
        event.detail.issueRequest(true);
      })
      .catch((err) => console.error('Sockless Port: before-submit hook failed', err));
  });

  // ---------------------------------------------------------------- bulk selection

  function updateBulk(form) {
    const checked = form.querySelectorAll('input[name=ids]:checked').length;
    for (const button of form.querySelectorAll('[data-bulk-action]')) button.disabled = checked === 0;
    const all = form.querySelector('[data-select-all]');
    const boxes = form.querySelectorAll('input[name=ids]');
    if (all) {
      all.checked = checked > 0 && checked === boxes.length;
      all.indeterminate = checked > 0 && checked < boxes.length;
    }
    const count = form.querySelector('[data-bulk-count]');
    if (count && checked > 0) count.textContent = `${checked} selected`;
  }

  document.addEventListener('change', (event) => {
    const box = event.target;
    if (!(box instanceof HTMLInputElement) || box.type !== 'checkbox' || !box.form) return;
    if (box.matches('[data-select-all]')) {
      for (const other of box.form.querySelectorAll('input[name=ids]')) other.checked = box.checked;
    }
    if (box.matches('[data-select-all], input[name=ids]')) updateBulk(box.form);
  });

  // Live refreshes of a list would throw away the user's selection: hold them while rows are checked.
  document.addEventListener('htmx:beforeRequest', (event) => {
    const el = event.detail.elt;
    if (el.matches?.('[data-pause-when-selected]') && el.querySelector('input[name=ids]:checked') && event.detail.requestConfig?.verb === 'get') {
      event.preventDefault();
    }
  });

  // ---------------------------------------------------------------- copy

  document.addEventListener('click', async (event) => {
    const button = event.target.closest?.('[data-copy]');
    if (!button) return;
    const source = document.querySelector(button.dataset.copy);
    if (!source) return;
    try {
      await navigator.clipboard.writeText(source.textContent);
      const label = button.textContent;
      button.textContent = 'Copied';
      setTimeout(() => { button.textContent = label; }, 1500);
    } catch {
      button.textContent = 'Copy failed';
    }
  });

  // ---------------------------------------------------------------- menus

  // <details data-menu>: close on outside click, on Escape, and after following a link.
  document.addEventListener('click', (event) => {
    for (const menu of document.querySelectorAll('details[data-menu][open]')) {
      if (!menu.contains(event.target) || event.target.closest('a')) menu.open = false;
    }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    for (const menu of document.querySelectorAll('details[data-menu][open]')) {
      menu.open = false;
      menu.querySelector('summary')?.focus();
    }
  });

  // ---------------------------------------------------------------- time zone

  // "Local" times are rendered by the server in the browser's zone, which it learns from this
  // cookie (Infrastructure/TimeZones.cs).
  try {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const current = document.cookie.split('; ').find((c) => c.startsWith('sockless-port.tz='))?.slice('sockless-port.tz='.length);
    if (zone && /^[A-Za-z0-9_+\-/]+$/.test(zone) && zone !== current) {
      document.cookie = `sockless-port.tz=${zone}; path=/; max-age=31536000; samesite=strict`;
    }
    // For servers that don't know the zone's rules: the offset now (it changes with daylight saving time).
    document.cookie = `sockless-port.tzo=${-new Date().getTimezoneOffset()}; path=/; max-age=31536000; samesite=strict`;
  } catch {
    // No Intl time zone: times stay in UTC, and say so.
  }

  // <form data-autosubmit>: a select that submits its form when changed; the button is for no-script.
  SocklessPort.component('form[data-autosubmit]', (form) => {
    for (const button of form.querySelectorAll('[data-autosubmit-button]')) button.hidden = true;
    const submit = (event) => { if (event.target.matches('select')) form.requestSubmit(); };
    form.addEventListener('change', submit);
    return () => form.removeEventListener('change', submit);
  });
})();
