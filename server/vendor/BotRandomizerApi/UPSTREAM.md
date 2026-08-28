# BotRandomizer API provenance

- Upstream repository: `https://github.com/unicbm/CS2-Bot-Randomizer`
- Upstream commit: `81d7b9e31eea917bcfd0dd21a691dfccfee7c7ea`
- Upstream paths: `BotRandomizerApi/BotRandomizerApi.csproj` and
  `BotRandomizerApi/IBotRandomizerApi.cs`
- Local status: API v2 replay-plan contract maintained with the bundled
  BotRandomizer 1.6 provider
- License: AGPL-3.0-only, the same license distributed in this repository's
  root `LICENSE` file

The snapshot keeps DemoTracer source builds self-contained. Runtime deployment
uses the single canonical `BotRandomizerApi.dll` installed by the playback
bundle under `addons/counterstrikesharp/shared/BotRandomizerApi/`. Compatible
BotRandomizer providers must reference that same API v2 contract rather than
shipping a private plugin-local copy. The provider validates complete desired
state and is the sole cosmetic entity writer.
