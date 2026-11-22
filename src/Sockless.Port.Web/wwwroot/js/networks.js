// Sockless Port: the create-network form shows only the fields that apply to the chosen driver.
// Fields carry data-drivers="macvlan ipvlan"; without scripts every field stays visible.
(() => {
  'use strict';

  SocklessPort.component('[data-network-form]', (form) => {
    const select = form.querySelector('[data-network-driver]');
    if (!select) return null;

    const update = () => {
      for (const el of form.querySelectorAll('[data-drivers]')) {
        const applies = el.dataset.drivers.split(' ').includes(select.value);
        // Keep a field that has a value or an error visible, so nothing is hidden that would be sent.
        const input = el.querySelector('input:not([type=hidden]), textarea');
        const filled = input && (input.type === 'checkbox' ? input.checked : input.value.trim() !== '');
        el.hidden = !applies && !filled && !el.querySelector('.error, [aria-invalid=true]');
      }
    };

    select.addEventListener('change', update);
    update();
    return () => select.removeEventListener('change', update);
  });
})();
