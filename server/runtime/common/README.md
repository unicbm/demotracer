# Shared DemoTracer infrastructure

This product module owns native hook/signature utilities, DemoTracerApi, source
field declarations, generated catalogs and their tools. Consumers reference
this directory directly. Changes and affected consumers land together.

## Check

With Node.js 22, .NET 10, CMake 3.28+, a C++20 compiler and `MMSOURCE_DEV`
pointing to the recursively initialized Metamod revision in
[hook-runtime.v1.json](contracts/hook-runtime.v1.json), run from the repository root:

```powershell
pwsh -NoProfile -File server/runtime/common/tools/check.ps1
```

This checks generated econ data, builds the companion API and runs shared native
tests under `.build/native-tests`. Only tests link the standalone KHook engine;
product DLLs use Metamod's service. Catalog updates are documented in
[cs2-lib-data](tools/cs2-lib-data/README.md). Product compatibility is in
[playback-contract.v1.json](../../../shared/contracts/playback-contract.v1.json).

## Provenance and licenses

Originally extracted from DemoTracer commit
`012d978ecdce8a949306fdcec39e4dcd9bf7624e`; consolidated from
`c6b91341eeb0677dc7f84a7eba6d52180ab1ad8e`. Its original history is retained at
[component-archive/common/heads/main](https://github.com/unicbm/demotracer/tree/component-archive/common/heads/main).
First-party sources remain AGPL-3.0-only. Third-party headers and compatibility
sources retain their original notices and licenses.
