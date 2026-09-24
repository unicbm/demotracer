# Optional cosmetic-plan API v3

Reference the shared `BotRandomizerApi.dll` with `Private=false` and resolve
`PluginCapability<IBotRandomizerApi>(BotRandomizerContract.Capability)` after
plugins load. The capability is `botrandomizer:replay-cosmetic-plan:v3`.
Ordinary randomization has no dependency on a consumer being installed.

On the server thread:

1. Probe `GetProviderInfo()`: check API, readiness, draining state and the
   required econ writer / prebuild capabilities.
2. Query each bot with `TryGetManagedBot`; use its current slot/incarnation.
3. Submit a complete validated batch with `AcquireReplayPlan(owner, plans,
   ownerLifetime.Token)`. Keep the returned token only after success.
4. Use `ReplaceReplayPlan(token, plans)` to atomically update that batch.
5. Call `ReleaseReplayPlan` on stop; cancel the owner's token on consumer
   unload. All calls, including cancellation, belong on the server thread.

Plans never expire on a timer. Two owners cannot claim the same bot. Invalid
batches leave existing ownership intact. Incarnations prevent slot reuse from
inheriting old plans. Map change and provider unload revoke all plans. Subscribe
to `BotRandomizerContract.ProviderChanged` to discard cached capabilities and
reconnect; defer work and unsubscribe on unload. Probe again after reconnection.

Plan items include exact definition, paint, seed, wear, identity, sticker and
keychain evidence; agent policies distinguish explicit models, random defaults
and preserving the engine default. Weapon/model combinations and ID domains
are validated before the provider writes from its normal spawn and
GiveNamedItem Pre/Post lifecycle. Consumers must not write econ entities directly.

The owner string is an identifier, not an authentication boundary between CSS
plugins. `DemoTracerOwner` is a compatibility convenience; other consumers may
use their own owner. Provider and consumers must load the same shared API DLL;
never place private copies in plugin folders. This contract does not grant
ownership of human players, movement control, or other providers' resources.
