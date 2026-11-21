// Container logs: streams lines over SignalR into [data-logs-output], with search, follow and wrap.
(() => {
  'use strict';

  const MAX_LINES = 10000;
  // Colour and cursor escape sequences; the log view shows plain text.
  const ANSI = /\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*(\x07|\x1b\\)/g;

  SocklessPort.component('[data-logs]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const output = $('[data-logs-output]');
    const status = $('[data-logs-status]');
    const search = $('[data-logs-search]');
    const tail = $('[data-logs-tail]');
    const timestamps = $('[data-logs-timestamps]');
    const follow = $('[data-logs-follow]');
    const wrap = $('[data-logs-wrap]');

    let subscription = null;
    let queue = [];
    let frame = 0;
    let query = '';

    const matches = (line) => query === '' || line.textContent.toLowerCase().includes(query);

    function render(message) {
      const line = document.createElement('div');
      line.className = message.stream === 'stderr' ? 'log-line stderr' : 'log-line';
      if (message.time) {
        const time = document.createElement('span');
        time.className = 'log-time';
        time.textContent = message.time;
        line.append(time);
      }
      line.append(document.createTextNode(message.text.replace(ANSI, '')));
      if (!matches(line)) line.hidden = true;
      return line;
    }

    // Lines can arrive by the thousand; add them once per animation frame.
    function flush() {
      frame = 0;
      const fragment = document.createDocumentFragment();
      for (const message of queue) fragment.append(render(message));
      queue = [];
      output.append(fragment);
      while (output.childElementCount > MAX_LINES) output.firstElementChild.remove();
      if (follow.checked) output.scrollTop = output.scrollHeight;
    }

    function add(message) {
      queue.push(message);
      if (!frame) frame = requestAnimationFrame(flush);
    }

    function stop() {
      subscription?.dispose();
      subscription = null;
    }

    async function start() {
      stop();
      output.replaceChildren();
      queue = [];
      status.textContent = 'Connecting…';
      await SocklessPort.ready;
      if (!root.isConnected) return;

      const stream = SocklessPort.connection.stream('StreamLogs', Number(root.dataset.endpoint), root.dataset.container,
        Number(tail.value), timestamps.checked);
      status.textContent = 'Following new output.';
      subscription = stream.subscribe({
        next: add,
        complete: () => { status.textContent = 'The log stream ended: the container stopped. Start it again and the stream resumes on reload.'; },
        error: (err) => { status.textContent = `The log stream failed: ${SocklessPort.message(err)}`; },
      });
    }

    let searchTimer = 0;
    search.addEventListener('input', () => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        query = search.value.trim().toLowerCase();
        for (const line of output.children) line.hidden = !matches(line);
      }, 150);
    });
    tail.addEventListener('change', start);
    timestamps.addEventListener('change', start);
    wrap.addEventListener('change', () => output.classList.toggle('wrap', wrap.checked));
    follow.addEventListener('change', () => { if (follow.checked) output.scrollTop = output.scrollHeight; });
    // Scrolling up to read stops following; scrolling back to the end resumes it.
    output.addEventListener('scroll', () => {
      const atEnd = output.scrollHeight - output.scrollTop - output.clientHeight < 24;
      if (follow.checked !== atEnd) follow.checked = atEnd;
    });
    $('[data-logs-clear]').addEventListener('click', () => output.replaceChildren());
    const onReconnect = () => start();
    document.addEventListener('sockless:reconnected', onReconnect);

    start();
    return () => {
      stop();
      cancelAnimationFrame(frame);
      document.removeEventListener('sockless:reconnected', onReconnect);
    };
  });
})();
