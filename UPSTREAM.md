# Source provenance

This component was extracted from
[`unicbm/demotracer`](https://github.com/unicbm/demotracer) commit
`012d978ecdce8a949306fdcec39e4dcd9bf7624e`:

- production source: `server/plugins/DemoTracer/`;
- complete managed regression suite: `server/plugins/DemoTracer.Tests/`;
- license: the original root `LICENSE` (AGPL-3.0-only).

`unicbm/cs2-css-demotrace` is the maintained source repository for this
component. Subsequent product builds consume a pinned dependency revision;
the product repository does not maintain another editable source copy.
Runtime assembly, install directory, capability, and public API identifiers
are preserved by the extraction. Dependency path changes do not introduce a
new playback protocol or change native ABI requirements.

ZstdSharp.Port/Zstandard attribution and license texts remain in
`THIRD_PARTY_NOTICES.md`. Econ data is generated and maintained in the pinned
common dependency, with source revisions embedded in that data. Public API and
native-provider test projects remain owned by their respective dependencies.
