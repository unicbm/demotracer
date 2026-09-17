# Playback server

This area contains everything installed on a local CS2 server for `.dtr`
playback. It is deliberately separate from the desktop converter.

| Path | Responsibility |
| --- | --- |
| [`plugins/`](plugins/) | CounterStrikeSharp orchestration, commands, tests, and companion API |
| [`runtime/`](runtime/) | Native Metamod replay and bot-presentation runtimes |

The maintained release combines these projects as one versioned playback
bundle. Do not mix binaries from different builds: the manifest, native ABI,
BotHider API, and CounterStrikeSharp reader must remain compatible.

Presentation and cosmetic plans are owned by the consumer's lifetime, not a
periodically renewed timeout. BotHider API v2 and BotRandomizer API v3 require
a cancellation token at acquisition; consumers cancel it on the server thread
on unload. Replacement keeps the same owner, and map changes or provider unload
revoke the plans. Provider lifecycle notifications trigger reconnection, so idle
playback does not poll providers or rebuild empty cosmetic plans each tick.
The GUI's runtime health file remains periodic because it reports liveness
across processes.

The native runtime is built on the foundational work in
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) and
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider). See the root
[credits](../README.md#credits-and-foundations) and the runtime-specific
upstream notes for the exact maintenance boundary.
