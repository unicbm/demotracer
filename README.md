# cs2-dtr-common

Shared infrastructure for the maintained CS2 DemoTracer components. This repository
owns native hook/signature utilities, the DemoTracer companion API, replay source
field declarations, and the generated economy/catalog data. Consumers pin it as
`.deps/common`; they must not copy or independently modify these sources.

The product's supported playback contract remains in `unicbm/demotracer`.
Changes to shared ABI or schema declarations require matching consumer changes and
the product integration checks. `version.txt` versions this infrastructure source
release independently of DLL, API, DTR and desktop product versions.

## Validation

Install PowerShell 7, Node.js 22, .NET 10, CMake 3.28 or newer, and a C++20
compiler. Set `MMSOURCE_DEV` to the recursively initialized Metamod checkout
pinned in `contracts/hook-runtime.v1.json`, then run `tools/check.ps1`.
Use `-DotnetPath` to select another .NET executable. The check verifies source
pins, generated data and catalog tests, builds DemoTracerApi in Release, and
runs the shared native integration tests. Full consumer DLL builds additionally
use the SDK and protoc source in `contracts/native-toolchain.v1.json`.

## Maintenance

Conventional `fix:` / `feat:` commits create a version/changelog PR. Merging that
PR publishes a source release. Consumers' component-update workflows propose its
gitlink in a reviewed PR; no original upstream source is automatically merged.
The data generator retains its pinned external data provenance in
`tools/cs2-lib-data/source.json`; changing that input is a separate reviewed change.

## Provenance and licenses

Extracted from [unicbm/demotracer at 012d978](https://github.com/unicbm/demotracer/tree/012d978ecdce8a949306fdcec39e4dcd9bf7624e).
Original history remains accessible there. First-party sources are AGPL-3.0-only.
`third_party/nlohmann/json.hpp` retains its embedded MIT notice. Shared native
compatibility sources retain their individual source notices. SDKs and Metamod are
build dependencies, not republished source owned by this project.
