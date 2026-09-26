# Source and dependency provenance

This library was extracted from `desktop/converter` in
[unicbm/demotracer](https://github.com/unicbm/demotracer), maintained source commit
`012d978ecdce8a949306fdcec39e4dcd9bf7624e`.
The root AGPL-3.0-only license is copied from that repository, and existing
source copyright/license headers are preserved.

Extraction changes repository-relative dependency and embedded-data paths, and
completes the feature-disabled stubs for the existing cancellation-aware read
entrypoints so `--no-default-features` can compile. It
preserves package `cs2-demotracer`, library `cs2_demotracer`, the public Rust API,
and the `.dtr`/manifest format behavior. This is a library dependency of the GUI,
not a separately supported command-line application.

The maintained parser is sourced from the pinned `.deps/demoparser` checkout;
its upstream MIT license, generated sources and maintenance notes remain with
that dependency. Its original source is
[LaihoE/demoparser](https://github.com/LaihoE/demoparser), maintained at
[unicbm/demoparser](https://github.com/unicbm/demoparser).

Shared contracts and the generated econ projection come from `.deps/common`.
The econ projection identifies its pinned `@ianlucas/cs2-lib` source in its own
metadata; the dependency retains its corresponding provenance and licensing.
Do not hand-edit this projection, duplicate catalog data in this library, or
substitute a newer catalog without updating and validating the shared revision.
