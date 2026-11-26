// A wide table's horizontal scrollbar, always within reach.
//
// A table wider than its column scrolls sideways inside .table-wrap, whose own scrollbar sits
// under the table's last row: on a long list, out of sight until you scroll down to it. This
// wraps the table in .table-scroll with a stand-in scrollbar after it, which sticks to the bottom
// of the content area while any of the table is on screen, and sits right under the table at its
// end. The two scroll together; the table's own bar is hidden (CSS), so there's only ever one.
// The stand-in shows only while the table overflows.
(() => {
  SocklessPort.component('.table-wrap', (wrap) => {
    const holder = document.createElement('div');
    holder.className = 'table-scroll';
    const bar = document.createElement('div');
    bar.className = 'table-scrollbar';
    // A copy of a scrollbar the table already has: pointer users drag it, everyone else scrolls
    // the table itself (focus moves it, and Shift+wheel or a trackpad work on it).
    bar.setAttribute('aria-hidden', 'true');
    bar.tabIndex = -1;
    bar.hidden = true;
    const spacer = document.createElement('div');
    spacer.className = 'table-scrollbar-width';
    bar.append(spacer);

    wrap.before(holder);
    holder.append(wrap, bar);

    // Each follows the other; equal positions stop the echo.
    const fromWrap = () => { if (bar.scrollLeft !== wrap.scrollLeft) bar.scrollLeft = wrap.scrollLeft; };
    const fromBar = () => { if (wrap.scrollLeft !== bar.scrollLeft) wrap.scrollLeft = bar.scrollLeft; };
    wrap.addEventListener('scroll', fromWrap, { passive: true });
    bar.addEventListener('scroll', fromBar, { passive: true });

    // Sizes come from the CSSOM (allowed by the CSP, unlike style attributes).
    const measure = () => {
      const overflows = wrap.scrollWidth > wrap.clientWidth + 1;
      holder.classList.toggle('is-overflowing', overflows);
      bar.hidden = !overflows;
      if (!overflows) return;
      spacer.style.width = `${wrap.scrollWidth}px`;
      fromWrap();
    };
    const resized = new ResizeObserver(measure);
    resized.observe(wrap);
    const table = wrap.querySelector('table');
    if (table) resized.observe(table);
    measure();

    return () => {
      resized.disconnect();
      wrap.removeEventListener('scroll', fromWrap);
      bar.removeEventListener('scroll', fromBar);
    };
  });
})();
