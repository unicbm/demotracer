# CS2 CSS DemoTracer

The CounterStrikeSharp playback component for CS2 DemoTracer, maintained in
[`unicbm/cs2-css-demotracer`](https://github.com/unicbm/cs2-css-demotracer).
The runtime assembly and plugin directory remain `DemoTracer`; the companion
capability remains `demotracer:api` (API 7).

## Repository layout

- [`src/DemoTracer`](src/DemoTracer): the plugin project, grouped by responsibility.
- [`tests/DemoTracer.Tests`](tests/DemoTracer.Tests): the complete managed regression suite.
- [`config`](config): sanitized configuration and native projectile profile inputs.
- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md): source map, dependencies, build, and package guidance.
- [`tools`](tools): standalone validation and packaging entry points.

Clone recursively, then run with .NET 10:

```powershell
git submodule update --init --recursive
pwsh -NoProfile -File tools/check.ps1
pwsh -NoProfile -File tools/package.ps1
```

Both scripts accept `-DotnetPath`. Use the matched CS2 DemoTracer playback bundle
to install the plugin with its native providers and shared APIs.

AGPL-3.0-only. See [LICENSE](LICENSE), [UPSTREAM.md](UPSTREAM.md), and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
