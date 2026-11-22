// Sockless Port's CodeMirror 6 bundle: a YAML/plain-text editor bound to a <textarea>.
// Built with esbuild into wwwroot/lib/codemirror/codemirror.js (see THIRD-PARTY-NOTICES.md).
// Colours come from app.css (.cm-* and .tok-* rules using --sl-* tokens), not from here.
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, highlightSpecialChars } from '@codemirror/view';
import { EditorState, Compartment } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { indentOnInput, bracketMatching, foldGutter, foldKeymap, syntaxHighlighting, indentUnit } from '@codemirror/language';
import { searchKeymap, highlightSelectionMatches } from '@codemirror/search';
import { lintGutter, setDiagnostics } from '@codemirror/lint';
import { classHighlighter } from '@lezer/highlight';
import { yaml } from '@codemirror/lang-yaml';

function create(textarea, options = {}) {
  const language = options.language === 'yaml' ? [yaml()] : [];
  const readOnly = new Compartment();
  const view = new EditorView({
    parent: textarea.parentElement,
    state: EditorState.create({
      doc: textarea.value,
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        history(),
        foldGutter(),
        drawSelection(),
        indentOnInput(),
        bracketMatching(),
        highlightActiveLine(),
        highlightSelectionMatches(),
        syntaxHighlighting(classHighlighter),
        lintGutter(),
        indentUnit.of('  '),
        EditorState.tabSize.of(2),
        keymap.of([...defaultKeymap, ...historyKeymap, ...searchKeymap, ...foldKeymap, indentWithTab]),
        readOnly.of(EditorState.readOnly.of(!!textarea.readOnly)),
        EditorView.contentAttributes.of({
          'aria-label': options.label ?? textarea.getAttribute('aria-label') ?? 'Editor',
          ...(textarea.getAttribute('aria-describedby') ? { 'aria-describedby': textarea.getAttribute('aria-describedby') } : {}),
        }),
        EditorView.updateListener.of((update) => {
          if (!update.docChanged) return;
          textarea.value = update.state.doc.toString();
          textarea.dispatchEvent(new Event('input', { bubbles: true }));
        }),
        ...language,
      ],
    }),
  });
  textarea.parentElement.insertBefore(view.dom, textarea);
  return {
    view,
    focus: () => view.focus(),
    // diagnostics: [{ line, message, severity }], line 1-based.
    setDiagnostics(diagnostics) {
      const doc = view.state.doc;
      const mapped = diagnostics
        .filter((d) => d.line >= 1 && d.line <= doc.lines)
        .map((d) => {
          const line = doc.line(d.line);
          return { from: line.from, to: line.to, severity: d.severity ?? 'error', message: d.message };
        });
      view.dispatch(setDiagnostics(view.state, mapped));
    },
    destroy: () => view.destroy(),
  };
}

window.SocklessEditor = { create };
