# GUI catalogs

These generated files are tracked so clean checkouts build without a separate
data checkout. Keep their embedded revisions, checksums and license metadata.

| File | Source |
| --- | --- |
| `cs2-pro-steamid-lib.v1.jsonl` | Pinned [CS2-pro-steamid-lib](../../../../third_party/cs2-pro-steamid-lib/README.vendor.md) snapshot; CC0/CC BY-SA provenance |
| `professional-players.v2.json` | Demo-verified subset of XBribo/CS2-Bot-Hider plus exact HLTV roster matches; upstream AGPL-3.0-only attribution retained |
| `cs2-cosmetic-catalog.v1.json` | [cs2-lib](../../../../third_party/cs2-lib/README.vendor.md) cosmetic projection |

## Refresh

Run from the repository root. The identity importer requires a clean checkout
of the revision in [`pro-steamid-catalog-source.json`](../../pro-steamid-catalog-source.json):

```powershell
node desktop/gui/scripts/import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib>
node desktop/gui/scripts/generate-cosmetic-catalog.mjs <cs2-lib-checkout>
```

The identity importer reads committed data offline. Refresh the demo-verified
supplement from its local evidence with:

```powershell
py -3 desktop/gui/scripts/generate-professional-player-catalog.py `
  --comparison <comparison.csv> --summary <audit-summary.json> `
  --enrichment desktop/gui/src/data/professional-player-enrichment.v1.json `
  --demo-memberships <player-demo-memberships.csv> `
  --hltv-scoreboards <hltv-scoreboards.csv> --hltv-verified-at <YYYY-MM-DD> `
  --output desktop/gui/src/data/professional-players.v2.json
```

The supplement requires demo-verified SteamIDs. HLTV links require an exact
ten-player roster match; enrichment applies only to verified Player IDs.
Do not add local paths, raw demos, photos, rankings or market estimates.
