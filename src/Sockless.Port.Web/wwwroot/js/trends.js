// The Resources page's trends: charts of the recorded history for a period (the endpoint's totals
// or one container's use) and a table of what each container used over it. Read from the page's
// History handler when the choice changes, and again every minute while the page is open.
(() => {
  'use strict';

  const { line, percent, bytes, rate } = SocklessPort.charts;
  const REFRESH = 60_000;

  const defaultDirection = (key) => (key === 'name' ? 'ascending' : 'descending');
  const present = (values) => values.filter((v) => v !== null && v !== undefined);
  const average = (values) => (values.length ? values.reduce((a, b) => a + b, 0) / values.length : null);
  const peak = (values) => (values.length ? Math.max(...values) : null);

  SocklessPort.component('[data-trends]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const status = $('[data-trends-status]');
    const range = $('[data-trends-range]');
    const name = $('[data-trends-name]');
    const filters = $('[data-trends-filters]');
    const table = $('[data-trends-table]');
    const tbody = $('[data-trends-rows]');
    const empty = $('[data-trends-empty]');
    const template = $('[data-trends-row]');
    const thead = table.querySelector('thead');
    const charts = {
      cpu: line($('[data-trend-chart=cpu]'), ['Average', 'Peak'], percent, 1, { xAxis: true }),
      memory: line($('[data-trend-chart=memory]'), ['Average', 'Peak'], bytes, undefined, { xAxis: true }),
      network: line($('[data-trend-chart=network]'), ['Received', 'Sent'], rate, 1000, { xAxis: true }),
      disk: line($('[data-trend-chart=disk]'), ['Read', 'Written'], rate, 1000, { xAxis: true }),
    };

    let data = null;
    let sort = { key: 'cpu', direction: 'descending' };
    let controller = null;

    // ---------------------------------------------------------------- charts and cards

    function setChart(chart, labels, series) {
      chart.data.labels = labels;
      series.forEach((values, i) => { chart.data.datasets[i].data = values; });
      chart.update('none');
    }

    function charted(d) {
      const points = d.points;
      const labels = points.map((p) => p.label);
      // Share of the host, like the live cards; 100% is one CPU when the host's CPUs aren't known.
      const share = (v) => (v === null ? null : d.cpus > 0 ? v / d.cpus : v);
      const cpu = points.map((p) => share(p.cpu));
      const cpuPeak = points.map((p) => share(p.cpuPeak));
      setChart(charts.cpu, labels, [cpu, cpuPeak]);
      setChart(charts.memory, labels, [points.map((p) => p.memory), points.map((p) => p.memoryPeak)]);
      setChart(charts.network, labels, [points.map((p) => p.networkRx), points.map((p) => p.networkTx)]);
      setChart(charts.disk, labels, [points.map((p) => p.blockRead), points.map((p) => p.blockWrite)]);

      const avgCpu = average(present(cpu));
      const maxCpu = peak(present(cpuPeak));
      const avgMemory = average(present(points.map((p) => p.memory)));
      const maxMemory = peak(present(points.map((p) => p.memoryPeak)));
      $('[data-trend=cpu]').textContent = avgCpu === null ? '—' : percent(avgCpu);
      $('[data-trend=cpu-meta]').textContent = avgCpu === null ? ' '
        : `average · peak ${percent(maxCpu)}${d.cpus > 0 ? ` of ${d.cpus} CPU${d.cpus === 1 ? '' : 's'}` : ''}`;
      $('[data-trend=memory]').textContent = avgMemory === null ? '—' : bytes(avgMemory);
      $('[data-trend=memory-meta]').textContent = avgMemory === null ? ' '
        : `average · peak ${bytes(maxMemory)}${d.hostMemory > 0 ? ` of ${bytes(d.hostMemory)}` : ''}`;

      // Bytes moved, from the table's rows: the server counts them from the samples, so a
      // partly recorded point isn't counted as a whole one.
      const rows = name.value ? d.containers.filter((c) => c.name === name.value) : d.containers;
      const sum = (key) => rows.reduce((total, c) => total + c[key], 0);
      $('[data-trend=network]').textContent = rows.length ? bytes(sum('networkRx')) + ' · ' + bytes(sum('networkTx')) : '—';
      $('[data-trend=disk]').textContent = rows.length ? bytes(sum('blockRead')) + ' · ' + bytes(sum('blockWrite')) : '—';
    }

    // ---------------------------------------------------------------- table

    const value = (c, key) => (key === 'name' ? c.name.toLowerCase() : c[key]);

    function compare(a, b) {
      const x = value(a, sort.key);
      const y = value(b, sort.key);
      const order = typeof x === 'string' ? x.localeCompare(y) : x - y;
      return (sort.direction === 'ascending' ? order : -order) || a.name.localeCompare(b.name);
    }

    function tabled(d) {
      const rows = [...d.containers].sort(compare).map((c) => {
        const tr = template.content.firstElementChild.cloneNode(true);
        const cell = (key) => tr.querySelector(`[data-cell="${key}"]`);
        const button = cell('name');
        button.textContent = c.name;
        button.dataset.name = c.name;
        button.setAttribute('aria-label', `Chart ${c.name}`);
        if (c.stack) {
          const badge = document.createElement('span');
          badge.className = 'badge-stat';
          badge.textContent = c.stack;
          cell('badges').append(badge);
        }
        cell('cpu').textContent = percent(c.cpu);
        cell('cpuPeak').textContent = percent(c.cpuPeak);
        cell('memory').textContent = bytes(c.memory);
        cell('memoryPeak').textContent = bytes(c.memoryPeak);
        cell('network').textContent = bytes(c.network);
        cell('disk').textContent = bytes(c.disk);
        return tr;
      });
      tbody.replaceChildren(...rows);
      table.hidden = rows.length === 0;
      empty.hidden = rows.length > 0;
    }

    // The Show list keeps its choice, and lists a chosen container even once it's out of the period.
    function named(d) {
      const chosen = name.value;
      const names = d.containers.map((c) => c.name).sort((a, b) => a.localeCompare(b));
      if (chosen && !names.includes(chosen)) names.unshift(chosen);
      const options = names.map((n) => {
        const option = document.createElement('option');
        option.value = n;
        option.textContent = n;
        return option;
      });
      name.replaceChildren(name.options[0], ...options);
      name.value = chosen;
    }

    // ---------------------------------------------------------------- loading

    async function load() {
      controller?.abort();
      controller = new AbortController();
      const url = new URL(root.dataset.url, window.location.origin);
      url.searchParams.set('range', range.value);
      if (name.value) url.searchParams.set('name', name.value);
      try {
        const response = await fetch(url, { signal: controller.signal, headers: { Accept: 'application/json' }, credentials: 'same-origin' });
        if (!response.ok) throw new Error(`the server answered ${response.status}.`);
        const d = await response.json();
        if (!root.isConnected) return;
        if (!d.enabled) {
          status.textContent = 'Resource history is off. Reload the page to see the setting.';
          return;
        }
        data = d;
        charted(d);
        named(d);
        tabled(d);
        status.textContent = name.value ? `${name.value}: ${d.period}` : `All containers: ${d.period}`;
      } catch (err) {
        if (err.name === 'AbortError') return;
        status.textContent = `Trends aren't available: ${err.message}`;
      }
    }

    // ---------------------------------------------------------------- controls

    const onChange = () => load();
    const onSubmit = (event) => event.preventDefault();
    const onTableClick = (event) => {
      const sortButton = event.target.closest('[data-sort]');
      if (sortButton) {
        const key = sortButton.dataset.sort;
        sort = sort.key === key
          ? { key, direction: sort.direction === 'ascending' ? 'descending' : 'ascending' }
          : { key, direction: defaultDirection(key) };
        thead.querySelectorAll('[data-sort-column]').forEach((th) => {
          th.setAttribute('aria-sort', th.dataset.sortColumn === sort.key ? sort.direction : 'none');
        });
        if (data) tabled(data);
        return;
      }
      const nameButton = event.target.closest('[data-cell=name]');
      if (nameButton) {
        name.value = nameButton.dataset.name;
        load();
        root.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
      }
    };

    range.addEventListener('change', onChange);
    name.addEventListener('change', onChange);
    filters.addEventListener('submit', onSubmit);
    table.addEventListener('click', onTableClick);
    const timer = window.setInterval(load, REFRESH);
    load();

    return () => {
      controller?.abort();
      window.clearInterval(timer);
      range.removeEventListener('change', onChange);
      name.removeEventListener('change', onChange);
      filters.removeEventListener('submit', onSubmit);
      table.removeEventListener('click', onTableClick);
      Object.values(charts).forEach((c) => c.destroy());
    };
  });
})();
