# Source and dependency provenance

This library was extracted from `desktop/converter` in
[unicbm/demotracer](https://github.com/unicbm/demotracer), maintained source commit
`012d978ecdce8a949306fdcec39e4dcd9bf7624e`.
The root AGPL-3.0-only license is copied from that repository, and existing
source copyright/license headers are preserved.

The converter is maintained as the GUI's internal library, with one filesystem
export entry point and the pinned parser always enabled. The former memory-export
API and parser-disabled build are retired. The package and library identities,
existing source notices, and supported .dtr/manifest formats are preserved.

The maintained parser is sourced from the pinned `third_party/demoparser` checkout;
its upstream MIT license, generated sources and maintenance notes remain with
that dependency. Its original source is
[LaihoE/demoparser](https://github.com/LaihoE/demoparser), maintained at
[unicbm/demoparser](https://github.com/unicbm/demoparser).

Shared contracts and the generated econ projection come from `server/runtime/common`.
The econ projection identifies its pinned `@ianlucas/cs2-lib` source in its own
metadata; the dependency retains its corresponding provenance and licensing.
Do not hand-edit this projection, duplicate catalog data in this library, or
substitute a newer catalog without updating and validating the shared revision.
