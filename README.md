# CS2 DemoTracer Playback

This public repository contains the CounterStrikeSharp playback plugin, matched
native runtimes, shared infrastructure and the DTR v12 playback contract.

The desktop GUI and its converter are maintained privately from the split at
`600b43d295320e91579a3e398cb93ced2ed8ea9d`. Earlier public desktop versions retain
their original licenses. Current desktop build sources are not part of this tree.

- [Build and test](docs/DEVELOPMENT.md)
- [Playback commands](docs/COMMANDS.md)
- [DTR v12 format](docs/FORMAT.md)
- [Engine signatures](docs/SIGNATURES.md)

The maintained playback code remains AGPL-3.0-only unless a file records its
upstream license. Preserve all attribution and license notices in `server/`.
Counter-Strike assets, trademarks and other third-party material retain their
respective rights. DemoTracer is an independent product. Product naming is
governed by [TRADEMARKS.md](TRADEMARKS.md).

Release downloads and issue reporting continue in this repository. Open a pull
request for playback changes; private desktop commits must never be merged into
the public history. The public CI builds playback without desktop source access.
