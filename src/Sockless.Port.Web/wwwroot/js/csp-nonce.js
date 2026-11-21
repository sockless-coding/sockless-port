// Must load before any other script. Libraries that build <style> elements at runtime (xterm.js)
// don't set a CSP nonce; stamp the page's style nonce on every <style> created by script so the
// policy can allow them without 'unsafe-inline'. The nonce comes from <meta name="csp-style-nonce">.
(() => {
  'use strict';
  const meta = document.querySelector('meta[name="csp-style-nonce"]');
  const nonce = meta?.content;
  if (!nonce) return;
  meta.remove(); // keep it out of the DOM; the closure holds it

  const create = Document.prototype.createElement;
  Document.prototype.createElement = function (tagName, options) {
    const element = create.call(this, tagName, options);
    if (typeof tagName === 'string' && tagName.toLowerCase() === 'style') element.nonce = nonce;
    return element;
  };
})();
