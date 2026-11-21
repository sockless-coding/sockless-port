// Container console: xterm.js bridged to an exec session through the hub.
(() => {
  'use strict';

  // Output is pushed per session id. The first output (the prompt) can arrive before
  // OpenTerminal returns the id, so unknown sessions are buffered briefly.
  const sessions = new Map();
  const early = new Map();

  function handlersRegistered() {
    const connection = SocklessPort.connection;
    connection.on('terminalOutput', (id, data) => {
      const session = sessions.get(id);
      if (session) session.output(data);
      else early.set(id, (early.get(id) ?? '') + data);
    });
    connection.on('terminalExit', (id, code) => {
      const session = sessions.get(id);
      if (session) session.exit(code);
      else early.delete(id);
    });
    // The server closes every session when the connection drops.
    connection.onreconnecting(() => { for (const session of sessions.values()) session.exit(null, 'The connection to Sockless Port dropped.'); });
  }
  handlersRegistered();

  function theme() {
    const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      background: css('--sp-iron'),
      foreground: css('--sl-text'),
      cursor: css('--sl-accent'),
      cursorAccent: css('--sp-iron'),
      selectionBackground: `rgb(${css('--sl-accent-rgb')} / 0.35)`,
    };
  }

  SocklessPort.component('[data-terminal]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const form = $('[data-terminal-form]');
    const screen = $('[data-terminal-screen]');
    const status = $('[data-terminal-status]');
    const connectButton = $('[data-terminal-connect]');
    const disconnectButton = $('[data-terminal-disconnect]');
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

    let term = null;
    let sessionId = null;
    let resizeObserver = null;
    let disposables = [];

    function setConnected(connected) {
      connectButton.hidden = connected;
      disconnectButton.hidden = !connected;
      for (const input of form.querySelectorAll('input')) input.disabled = connected;
    }

    function teardown() {
      resizeObserver?.disconnect();
      resizeObserver = null;
      disposables.forEach((d) => d.dispose());
      disposables = [];
      if (sessionId) sessions.delete(sessionId);
      sessionId = null;
      setConnected(false);
    }

    async function connect() {
      teardown();
      term?.dispose();
      screen.hidden = false;
      term = new Terminal({
        cursorBlink: !reducedMotion,
        fontFamily: '"Roboto Mono", ui-monospace, monospace',
        fontSize: 13,
        scrollback: 5000,
        theme: theme(),
      });
      const fit = new FitAddon.FitAddon();
      term.loadAddon(fit);
      term.open(screen);
      fit.fit();
      status.textContent = 'Connecting…';
      connectButton.disabled = true;

      try {
        await SocklessPort.ready;
        const connection = SocklessPort.connection;
        sessionId = await connection.invoke('OpenTerminal', Number(root.dataset.endpoint), root.dataset.container,
          $('[data-terminal-command]').value, $('[data-terminal-user]').value, term.cols, term.rows);
      } catch (err) {
        status.textContent = `Couldn't start the command: ${SocklessPort.message(err)}`;
        connectButton.disabled = false;
        return;
      }
      connectButton.disabled = false;

      const id = sessionId;
      const connection = SocklessPort.connection;
      sessions.set(id, {
        output: (data) => term.write(data),
        exit: (code, reason) => {
          if (sessionId !== id) return;
          status.textContent = reason ?? (code == null ? 'The session ended.' : `The command exited with code ${code}.`);
          teardown();
        },
      });
      if (early.has(id)) {
        term.write(early.get(id));
        early.delete(id);
      }

      disposables.push(term.onData((data) => connection.invoke('TerminalInput', id, data).catch(() => {})));
      disposables.push(term.onResize(({ cols, rows }) => connection.invoke('TerminalResize', id, cols, rows).catch(() => {})));
      resizeObserver = new ResizeObserver(() => fit.fit());
      resizeObserver.observe(screen);
      setConnected(true);
      status.textContent = `Connected to ${$('[data-terminal-command]').value}. Type exit or use Disconnect to end the session.`;
      term.focus();
    }

    async function disconnect() {
      const id = sessionId;
      teardown();
      status.textContent = 'Disconnected.';
      if (id) await SocklessPort.connection.invoke('CloseTerminal', id).catch(() => {});
    }

    form.addEventListener('submit', (event) => {
      event.preventDefault();
      connect();
    });
    disconnectButton.addEventListener('click', disconnect);

    return () => {
      disconnect();
      term?.dispose();
    };
  });
})();
