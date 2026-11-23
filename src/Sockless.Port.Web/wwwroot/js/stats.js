// Live container stats: four line charts (charts.js) fed by the StreamStats hub stream.
(() => {
  'use strict';

  const { line, push, percent, bytes, rate, timeLabel } = SocklessPort.charts;

  SocklessPort.component('[data-stats]', (root) => {
    const $ = (selector) => root.querySelector(selector);
    const status = $('[data-stats-status]');
    const charts = {
      cpu: line($('[data-chart=cpu]'), ['CPU'], percent, 1),
      memory: line($('[data-chart=memory]'), ['Memory'], bytes),
      network: line($('[data-chart=network]'), ['Received', 'Sent'], rate, 1000),
      disk: line($('[data-chart=disk]'), ['Read', 'Written'], rate, 1000),
    };

    let previous = null;
    let subscription = null;

    function sample(s) {
      const time = new Date(s.time);
      const label = timeLabel(time);
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
