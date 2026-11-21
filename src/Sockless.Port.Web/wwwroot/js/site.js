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
  });

  async function start(attempt = 0) {
    try {
      await connection.start();
      await syncWatch();
    } catch {
      // Signed out or server restarting: back off, give up after ~5 minutes.
      if (attempt < 20) setTimeout(() => start(attempt + 1), Math.min(30000, 2000 * (attempt + 1)));
    }
  }

  document.addEventListener('htmx:afterSettle', syncWatch);
  start();

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
})();
