# Third-party notices

Sockless Port ships the following third-party assets in `src/Sockless.Port.Web`.

| Component | Version | License | Location |
|---|---|---|---|
| [htmx](https://htmx.org) | 2.0.11 | BSD Zero Clause (0BSD) | `wwwroot/lib/htmx/` |
| [ASP.NET Core SignalR JavaScript client](https://www.npmjs.com/package/@microsoft/signalr) | 11.0.0 | MIT | `wwwroot/lib/signalr/` |
| [Chart.js](https://www.chartjs.org) | 4.6.1 | MIT | `wwwroot/lib/chartjs/` |
| [xterm.js](https://xtermjs.org) (`@xterm/xterm`) | 6.0.0 | MIT | `wwwroot/lib/xterm/` |
| xterm.js fit addon (`@xterm/addon-fit`) | 0.11.0 | MIT | `wwwroot/lib/xterm/addon-fit.js` |
| [CodeMirror](https://codemirror.net) (`@codemirror/view` 6.43.15, `state` 6.7.6, `commands` 6.11.1, `language` 6.13.1, `search` 6.7.2, `lint` 6.9.8, `lang-yaml` 6.1.3, `lang-json` 6.0.2; `@lezer/highlight` 1.2.5, `@lezer/yaml` 1.0.4, `@lezer/json` 1.0.3, `@lezer/common` 1.5.6, `@lezer/lr` 1.4.12, `style-mod` 4.1.5, `w3c-keyname` 2.2.8, `crelt` 1.0.7, `@marijn/find-cluster-break` 1.0.4) | 6 | MIT | `wwwroot/lib/codemirror/` (bundled from `build/codemirror/`) |
| [Heroicons](https://heroicons.com) | 2.2.0 | MIT | `Icons/` |
| [Oswald](https://fonts.google.com/specimen/Oswald) (via Fontsource) | 5.3.0 | SIL Open Font License 1.1 | `wwwroot/fonts/oswald-*` |
| [Raleway](https://fonts.google.com/specimen/Raleway) (via Fontsource) | 5.3.0 | SIL Open Font License 1.1 | `wwwroot/fonts/raleway-*` |
| [Roboto Mono](https://fonts.google.com/specimen/Roboto+Mono) (via Fontsource) | 5.3.0 | Apache License 2.0 | `wwwroot/fonts/roboto-mono-*` |

The server uses [YamlDotNet](https://github.com/aaubry/YamlDotNet) 18.1.0 (MIT) to check YAML files edited in the volume file browser.

The server image also ships [Docker Compose](https://github.com/docker/compose) 5.7.0 (Apache License 2.0) as `/usr/local/bin/docker-compose`, downloaded from its GitHub release and checked against the published SHA-256.

It also ships [Trivy](https://github.com/aquasecurity/trivy) 0.76.2 (Apache License 2.0) as `/usr/local/bin/trivy`, downloaded and checked the same way. Its vulnerability databases aren't part of the image: Trivy downloads them at run time into the data volume.

Build-time only (not redistributed): [Tailwind CSS](https://tailwindcss.com) 3.4.19 standalone CLI, MIT; [esbuild](https://esbuild.github.io) 0.28.2, MIT, to bundle CodeMirror.
