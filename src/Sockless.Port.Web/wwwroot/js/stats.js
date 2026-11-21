// Live container stats: four Chart.js line charts fed by the StreamStats hub stream.
// Colours come from the theme tokens. Two-series charts use accent (copper-light) then info
// (gaslight): that pair passes colour-vision-deficiency checks, unlike accent + success, and
// success stays reserved for status.
(() => {
  'use strict';

  const POINTS = 60; // about two minutes at one sample every two seconds

  function token(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  function rgba(hex, alpha) {
    const value = hex.replace('#', '');
    const n = parseInt(value.length === 3 ? value.replace(/./g, '$&$&') : value, 16);
    return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
  }

  function theme() {
    return {
      series: [`rgb(${token('--sl-accent-rgb')})`, `rgb(${token('--sl-info-rgb')})`],
      text: token('--sl-text-secondary'),
      grid: rgba(token('--sl-border'), 0.2),
      tooltipBg: rgba(token('--sp-iron'), 0.92),
      tooltipBorder: `rgb(${token('--sl-accent-rgb')} / 0.3)`,
      heading: token('--sl-text-heading'),
      body: token('--sl-text'),
    };
  }

  const percent = (v) => `${v.toFixed(v >= 10 ? 0 : 1)}%`;
  const bytes = (v) => SocklessPort.bytes(v);
  const rate = (v) => `${SocklessPort.bytes(v)}/s`;

  // suggestedMax keeps an idle chart's axis readable (0 to 1 kB/s, not 0, 1, 1 B/s).
  function makeChart(canvas, labels, format, colors, suggestedMax) {
    const t = theme();
    return new Chart(canvas, {
      type: 'line',
      data: {
        labels: [],
        datasets: labels.map((label, i) => ({
          label,
          data: [],
          borderColor: colors[i],
          backgroundColor: colors[i],
          borderWidth: 2,
          pointRadius: 0,
          pointHoverRadius: 4,
          pointHoverBorderWidth: 2,
          pointHoverBorderColor: token('--sl-card'),
          tension: 0.25,
          fill: false,
        })),
      },
      options: {
        animation: false,
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        scales: {
          x: { display: false },
          y: {
            beginAtZero: true,
            suggestedMax,
            border: { display: false },
            grid: { color: t.grid },
            ticks: { color: t.text, font: { family: 'Roboto Mono', size: 11 }, maxTicksLimit: 4, callback: (v) => format(Number(v)) },
          },
        },
        plugins: {
          // A single series is named by the card's title; two get a legend.
          legend: {
            display: labels.length > 1,
            position: 'top',
            align: 'end',
            labels: { color: t.text, boxWidth: 10, boxHeight: 2, font: { family: 'Raleway', size: 12 } },
          },
          tooltip: {
            backgroundColor: t.tooltipBg,
            borderColor: t.tooltipBorder,
            borderWidth: 1,
            titleColor: t.heading,
            bodyColor: t.body,
            titleFont: { family: 'Roboto Mono', size: 11 },
            bodyFont: { family: 'Roboto Mono', size: 12 },
            callbacks: { label: (ctx) => `${ctx.dataset.label}: ${format(ctx.parsed.y)}` },
          },
        },
      },
    });
  }

  SocklessPort.component('[data-stats]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const status = $('[data-stats-status]');
    const t = theme();
    const charts = {
      cpu: makeChart($('[data-chart=cpu]'), ['CPU'], percent, t.series, 1),
      memory: makeChart($('[data-chart=memory]'), ['Memory'], bytes, t.series),
      network: makeChart($('[data-chart=network]'), ['Received', 'Sent'], rate, t.series, 1000),
      disk: makeChart($('[data-chart=disk]'), ['Read', 'Written'], rate, t.series, 1000),
    };

    let previous = null;
    let subscription = null;

    function push(chart, label, values) {
      chart.data.labels.push(label);
      values.forEach((v, i) => chart.data.datasets[i].data.push(v));
      if (chart.data.labels.length > POINTS) {
        chart.data.labels.shift();
        chart.data.datasets.forEach((d) => d.data.shift());
      }
      chart.update('none');
    }

    function sample(s) {
      const time = new Date(s.time);
      const label = time.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
      // Counters are cumulative; rates need two readings.
      const seconds = previous ? Math.max(0.001, (time - new Date(previous.time)) / 1000) : 0;
      const perSecond = (now, before) => (previous && now >= before ? (now - before) / seconds : 0);
      const rx = perSecond(s.networkRx, previous?.networkRx);
      const tx = perSecond(s.networkTx, previous?.networkTx);
      const read = perSecond(s.blockRead, previous?.blockRead);
      const write = perSecond(s.blockWrite, previous?.blockWrite);

      $('[data-stat=cpu]').textContent = percent(s.cpu);
      $('[data-stat=memory]').textContent = bytes(s.memory);
      $('[data-stat=memory-limit]').textContent = s.memoryLimit > 0
        ? `of ${bytes(s.memoryLimit)} (${percent((100 * s.memory) / s.memoryLimit)})`
        : 'No limit';
      $('[data-stat=network]').textContent = `${rate(rx)} · ${rate(tx)}`;
      $('[data-stat=disk]').textContent = `${rate(read)} · ${rate(write)}`;
      $('[data-stat=pids]').textContent = String(s.pids);

      push(charts.cpu, label, [s.cpu]);
      push(charts.memory, label, [s.memory]);
      if (previous) {
        push(charts.network, label, [rx, tx]);
        push(charts.disk, label, [read, write]);
      }
      previous = s;
      status.textContent = `Live · updated ${label}`;
    }

    async function start() {
      subscription?.dispose();
      await SocklessPort.ready;
      if (!root.isConnected) return;
      subscription = SocklessPort.connection
        .stream('StreamStats', Number(root.dataset.endpoint), root.dataset.container)
        .subscribe({
          next: sample,
          complete: () => { status.textContent = 'Stats stopped: the container is no longer running.'; },
          error: (err) => { status.textContent = `Stats aren't available: ${SocklessPort.message(err)}`; },
        });
    }

    const onReconnect = () => { previous = null; start(); };
    document.addEventListener('sockless:reconnected', onReconnect);
    start();

    return () => {
      subscription?.dispose();
      document.removeEventListener('sockless:reconnected', onReconnect);
      Object.values(charts).forEach((c) => c.destroy());
    };
  });
})();
