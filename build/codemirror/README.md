# CodeMirror bundle

`wwwroot/lib/codemirror/codemirror.js` is built from `entry.js` here, so the app needs no Node at
build or run time. To change it or bump a version:

```sh
cd build/codemirror
npm install
npm run build
```

Then update the versions in `THIRD-PARTY-NOTICES.md`. Colours aren't in the bundle: the editor and
syntax classes (`.cm-*`, `.tok-*`) are styled with tokens in `Styles/app.css`.
