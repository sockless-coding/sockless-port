// A git-backed stack's sign-in fields: only the section for the chosen way to sign in shows.
// Without scripts every section shows, and the server ignores the ones not chosen.
(() => {
  'use strict';

  SocklessPort.component('[data-git-auth]', (form) => {
    const choices = [...form.querySelectorAll('[data-git-auth-choice]')];
    const sections = [...form.querySelectorAll('[data-git-auth-for]')];
    const update = () => {
      const chosen = choices.find((c) => c.checked)?.value;
      for (const section of sections) section.hidden = section.dataset.gitAuthFor !== chosen;
    };
    for (const choice of choices) choice.addEventListener('change', update);
    update();
    return () => {
      for (const choice of choices) choice.removeEventListener('change', update);
    };
  });
})();
