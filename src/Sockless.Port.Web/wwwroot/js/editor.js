// Code editors (CodeMirror over a <textarea>, which stays the form field) for stack files and
// volume files, and server-side check results shown in an editor's gutter.
(() => {
  'use strict';

  // Editors by their textarea's id, so check results can find theirs.
  const editors = new Map();

  // <textarea data-code-editor="yaml|json|env|text">: without the bundle (or scripts) it stays a plain text area.
  SocklessPort.component('textarea[data-code-editor]', (textarea) => {
    if (!window.SocklessEditor) return null;
    // A page restored from htmx's history cache still holds the previous editor's markup.
    for (const stale of textarea.parentElement.querySelectorAll(':scope > .cm-editor')) stale.remove();
    const editor = window.SocklessEditor.create(textarea, {
      language: textarea.dataset.codeEditor,
      label: document.querySelector(`label[for="${textarea.id}"]`)?.textContent.trim(),
    });
    textarea.hidden = true;
    editors.set(textarea.id, editor);
    // Labels point at the textarea; send their clicks to the editor instead.
    const label = document.querySelector(`label[for="${textarea.id}"]`);
    const focus = (event) => { event.preventDefault(); editor.focus(); };
    label?.addEventListener('click', focus);
    return () => {
      label?.removeEventListener('click', focus);
      editors.delete(textarea.id);
      editor.destroy();
    };
  });

  // <div data-editor-diagnostics="[{line, message}]" data-editor-for="textarea-id">: a check
  // result (Compose's, or the JSON/YAML check of a volume file) marks its lines in that editor.
  SocklessPort.component('[data-editor-diagnostics]', (result) => {
    let diagnostics = [];
    try {
      diagnostics = JSON.parse(result.dataset.editorDiagnostics || '[]');
    } catch {
      return null;
    }
    editors.get(result.dataset.editorFor)?.setDiagnostics(diagnostics);
    return null;
  });
})();
