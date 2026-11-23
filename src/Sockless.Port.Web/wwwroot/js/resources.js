// The endpoint's Resources page: totals and charts, and a live table of what each running
// container (or stack) uses, fed by the StreamResourceUsage hub stream about every two seconds.
// Rows are keyed and updated in place; they're re-sorted only while the pointer and focus are
// outside the table, so a row doesn't move away from someone about to select it.
(() => {
  'use strict';

  const { line, push, percent, bytes, rate, timeLabel } = SocklessPort.charts;

  // Name sorts A to Z first; numbers biggest first.
  const defaultDirection = (key) => (key === 'name' ? 'ascending' : 'descending');

  SocklessPort.component('[data-resources]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const status = $('[data-resources-status]');
    const error = $('[data-resources-error]');
    const tbody = $('[data-resources-rows]');
    const table = tbody.closest('table');
    const empty = $('[data-resources-empty]');
    const template = $('[data-row-template]');
    const search = $('[data-resources-search]');
    const group = $('[data-resources-group]');
    const charts = {
      cpu: line($('[data-chart=cpu]'), ['CPU'], percent, 1),
      memory: line($('[data-chart=memory]'), ['Memory'], bytes),
      network: line($('[data-chart=network]'), ['Received', 'Sent'], rate, 1000),
      disk: line($('[data-chart=disk]'), ['Read', 'Written'], rate, 1000),
    };

    const rows = new Map(); // key -> <tr>
    let sort = { key: 'cpu', direction: 'descending' };
    let latest = null;
    let subscription = null;
    let pointerInside = false;
    let focusInside = false;

    // ---------------------------------------------------------------- totals

    function totals(s) {
      const label = timeLabel(new Date(s.time));
      const cpuShare = s.cpus > 0 ? s.cpu / s.cpus : s.cpu;
      $('[data-total=cpu]').textContent = percent(cpuShare);
      $('[data-total=cpu-meta]').textContent = s.cpus > 0
        ? `${(s.cpu / 100).toFixed(2)} of ${s.cpus} CPU${s.cpus === 1 ? '' : 's'} in use`
        : '100% is one full CPU';
      $('[data-total=memory]').textContent = bytes(s.memory);
      $('[data-total=memory-meta]').textContent = s.hostMemory > 0
        ? `of ${bytes(s.hostMemory)} on the host (${percent((100 * s.memory) / s.hostMemory)})`
        : ' ';
      $('[data-total=network]').textContent = `${rate(s.networkRx)} · ${rate(s.networkTx)}`;
      $('[data-total=disk]').textContent = `${rate(s.blockRead)} · ${rate(s.blockWrite)}`;

      push(charts.cpu, label, [cpuShare]);
      push(charts.memory, label, [s.memory]);
      push(charts.network, label, [s.networkRx, s.networkTx]);
      push(charts.disk, label, [s.blockRead, s.blockWrite]);

      const measured = s.containers.length;
      const count = `${measured} container${measured === 1 ? '' : 's'}`;
      status.textContent = s.running > measured
        ? `Live · measuring ${measured} of ${s.running} running containers · updated ${label}`
        : `Live · ${count} running · updated ${label}`;
    }

    // ---------------------------------------------------------------- rows

    function containerItems(s) {
      return s.containers.map((c) => ({
        key: `c:${c.id}`,
        name: c.name,
        url: root.dataset.containerUrl.replace('__id__', encodeURIComponent(c.id)),
        badges: c.stack ? [c.stack] : [],
        search: `${c.name} ${c.stack ?? ''}`.toLowerCase(),
        measured: c.measured,
        error: c.error,
        cpu: c.cpu,
        memory: c.memory,
        memoryLimit: c.memoryLimit,
        rx: c.networkRx,
        tx: c.networkTx,
        read: c.blockRead,
        write: c.blockWrite,
        pids: c.pids,
      }));
    }

    function stackItems(s) {
      const stacks = new Map();
      for (const c of s.containers) {
        const name = c.stack ?? null;
        let item = stacks.get(name);
        if (!item) {
          item = {
            key: `s:${name ?? ''}`,
            name: name ?? 'Not in a stack',
            url: name ? root.dataset.stackUrl.replace('__id__', encodeURIComponent(name)) : null,
            containers: 0,
            search: '',
            measured: false,
            error: null,
            cpu: 0, memory: 0, memoryLimit: 0, rx: 0, tx: 0, read: 0, write: 0, pids: 0,
          };
          stacks.set(name, item);
        }
        item.containers++;
        item.search += ` ${c.name} ${name ?? ''}`.toLowerCase();
        item.measured ||= c.measured;
        item.cpu += c.cpu;
        item.memory += c.memory;
        item.rx += c.networkRx;
        item.tx += c.networkTx;
        item.read += c.blockRead;
        item.write += c.blockWrite;
        item.pids += c.pids;
      }
      return [...stacks.values()].map((item) => ({
        ...item,
        badges: [`${item.containers} container${item.containers === 1 ? '' : 's'}`],
      }));
    }

    const value = (item, key) => ({
      name: item.name.toLowerCase(),
      cpu: item.cpu,
      memory: item.memory,
      network: item.rx + item.tx,
      disk: item.read + item.write,
      pids: item.pids,
    })[key];

    function compare(a, b) {
      const x = value(a, sort.key);
      const y = value(b, sort.key);
      const order = typeof x === 'string' ? x.localeCompare(y) : x - y;
      // Ties (often all zero) keep a stable order by name.
      return (sort.direction === 'ascending' ? order : -order) || a.name.localeCompare(b.name);
    }

    function fill(tr, item, s) {
      const cell = (name) => tr.querySelector(`[data-cell="${name}"]`);
      const link = cell('link');
      link.textContent = item.name;
      if (item.url) link.href = item.url; else link.removeAttribute('href');

      const badges = cell('badges');
      badges.replaceChildren(...item.badges.map((text) => {
        const badge = document.createElement('span');
        badge.className = 'badge-stat';
        badge.textContent = text;
        return badge;
      }));
      if (item.error) {
        const note = document.createElement('span');
        note.className = 'block w-full text-xs text-secondary';
        note.textContent = `Can't measure: ${item.error}`;
        badges.append(note);
      }

      const pending = !item.measured;
      const dash = item.error ? '—' : '…';
      cell('cpu').textContent = pending ? dash : percent(item.cpu);
      cell('cpu-bar').value = pending ? 0 : Math.min(100, s.cpus > 0 ? item.cpu / s.cpus : item.cpu);
      cell('memory').textContent = pending ? dash : bytes(item.memory);
      cell('memory-meta').textContent = !pending && item.memoryLimit > 0
        ? `of ${bytes(item.memoryLimit)} limit (${percent((100 * item.memory) / item.memoryLimit)})`
        : '';
      cell('memory-bar').value = pending || !(s.hostMemory > 0) ? 0 : Math.min(100, (100 * item.memory) / s.hostMemory);
      cell('network').textContent = pending ? dash : `${rate(item.rx)} · ${rate(item.tx)}`;
      cell('disk').textContent = pending ? dash : `${rate(item.read)} · ${rate(item.write)}`;
      cell('pids').textContent = pending ? dash : String(item.pids);
    }

    function render() {
      const s = latest;
      if (!s) return;
      const query = search.value.trim().toLowerCase();
      const all = group.value === 'stack' ? stackItems(s) : containerItems(s);
      const items = all.filter((item) => !query || item.search.includes(query)).sort(compare);

      const keep = new Set(items.map((item) => item.key));
      for (const [key, tr] of rows) {
        if (!keep.has(key)) { tr.remove(); rows.delete(key); }
      }
      const hold = pointerInside || focusInside;
      for (const item of items) {
        let tr = rows.get(item.key);
        if (!tr) {
          tr = template.content.firstElementChild.cloneNode(true);
          rows.set(item.key, tr);
          tbody.append(tr);
        } else if (!hold) {
          tbody.append(tr); // moves it into sorted order
        }
        fill(tr, item, s);
      }

      const none = items.length === 0;
      table.hidden = none;
      empty.hidden = !none;
      if (none) {
        $('[data-resources-empty-title]').textContent = all.length > 0 ? 'Nothing matches the search' : 'No running containers';
        $('[data-resources-empty-text]').textContent = all.length > 0 ? 'Clear the search to see everything.' : 'Start a container to see what it uses.';
      }
    }

    function snapshot(s) {
      latest = s;
      error.hidden = !s.error;
      $('[data-resources-error-text]').textContent = s.error ?? '';
      if (s.error) {
        status.textContent = 'Not measuring.';
        latest = { ...s, containers: [] };
        render();
        return;
      }
      totals(s);
      render();
    }

    // ---------------------------------------------------------------- controls

    function setSort(key) {
      sort = sort.key === key
        ? { key, direction: sort.direction === 'ascending' ? 'descending' : 'ascending' }
        : { key, direction: defaultDirection(key) };
      root.querySelectorAll('[data-sort-column]').forEach((th) => {
        th.setAttribute('aria-sort', th.dataset.sortColumn === sort.key ? sort.direction : 'none');
      });
      // The headings are outside the body, so a click here never holds the order.
      render();
    }

    const onSort = (event) => {
      const button = event.target.closest('[data-sort]');
      if (button) setSort(button.dataset.sort);
    };
    const onGroup = () => {
      $('[data-name-label]').textContent = group.value === 'stack' ? 'Stack' : 'Container';
      rows.forEach((tr) => tr.remove());
      rows.clear();
      render();
    };
    const onSearch = () => render();
    const onSubmit = (event) => event.preventDefault();
    const onPointerEnter = () => { pointerInside = true; };
    const onPointerLeave = () => { pointerInside = false; };
    const onFocusIn = () => { focusInside = true; };
    const onFocusOut = (event) => { focusInside = tbody.contains(event.relatedTarget); };

    const thead = table.querySelector('thead');
    const filters = $('[data-resources-filters]');
    thead.addEventListener('click', onSort);
    group.addEventListener('change', onGroup);
    search.addEventListener('input', onSearch);
    filters.addEventListener('submit', onSubmit);
    tbody.addEventListener('pointerenter', onPointerEnter);
    tbody.addEventListener('pointerleave', onPointerLeave);
    tbody.addEventListener('focusin', onFocusIn);
    tbody.addEventListener('focusout', onFocusOut);

    // ---------------------------------------------------------------- stream

    async function start() {
      subscription?.dispose();
      await SocklessPort.ready;
      if (!root.isConnected) return;
      subscription = SocklessPort.connection
        .stream('StreamResourceUsage', Number(root.dataset.endpoint))
        .subscribe({
          next: snapshot,
          complete: () => { status.textContent = 'Stopped.'; },
          error: (err) => { status.textContent = `Resource use isn't available: ${SocklessPort.message(err)}`; },
        });
    }

    const onReconnect = () => start();
    document.addEventListener('sockless:reconnected', onReconnect);
    start();

    return () => {
      subscription?.dispose();
      document.removeEventListener('sockless:reconnected', onReconnect);
      thead.removeEventListener('click', onSort);
      group.removeEventListener('change', onGroup);
      search.removeEventListener('input', onSearch);
      filters.removeEventListener('submit', onSubmit);
      Object.values(charts).forEach((c) => c.destroy());
    };
  });
})();
