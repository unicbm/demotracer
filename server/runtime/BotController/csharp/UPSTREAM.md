# BotController public API provider

Imported from [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)
at commit `64f676c708b555080806e0402490379eb2cffc6f`, under AGPL-3.0-only.
The public API matches the BotControllerApi/Impl shipped in CS2-Bot-Improver 1.4.4.
See `LICENSE.AGPL3`. The maintained provider preserves the upstream API layout;
its experimental native weapon-drop recorder/executor is not included.

The maintained provider requires the matched DemoTracer native ABI 21, minor 38,
and its public-control marker. Native and managed replay ticks are all 228 bytes;
the 36-byte event tail is reserved and every field must be zero. Native loads
reject nonzero event data; JSON replay commands explain this unsupported input.
The removed experiment could lose deferred drops or overwrite multiple drops in
one tick. This bundle does not record or replay native drop events. These bytes
are not passed to the previous 192-byte ABI 18 runtime.
The shared API stays named `botcontroller:api`; the installation directory stays
`BotControllerImpl` for existing consumers and Panel checks.

Public mutations and chat replay commands reject humans and DemoTracer-owned
slots. Native input requests have independent tokens, reset at ownership and
connection boundaries, and never overwrite replay input. API consumers and chat
commands share one provider instance and one resource ledger. Disconnect, map
changes, and unload release only this provider's recordings, replay buffers,
input, locks, and buy plans; DemoTracer takeover drops control ownership while
preserving the recorder's separate cleanup. Stopped foreign replay buffers cannot
be overwritten through the public replay API. Shared projectile queues are not
globally cleared by this managed provider.

DTR file layouts are independent of this native ABI. DTR already stores movement
and commands using lossless columnar deltas and stores gameplay events separately.
Its reader initializes the native event tail to zero; DTR gameplay events retain
their existing single executor. The public recorder's JSON keeps all tick fields.
