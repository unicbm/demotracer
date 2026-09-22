/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import {
  mergeProfessionalPlayerIdentities,
  parseProfessionalPlayerCatalog,
  resolveProfessionalPlayerFromCatalog,
  type ProfessionalPlayerSummary,
} from "../src/professionalPlayersCatalog.ts";
import { parseProSteamIdCatalogJsonl } from "../src/proSteamIdCatalog.ts";

export function buildProfessionalPlayerRuntime(demoSource: unknown, registrySource: string) {
  const demo = parseProfessionalPlayerCatalog(demoSource);
  const registry = parseProSteamIdCatalogJsonl(registrySource);
  const steamIds = new Set([...demo.players.keys(), ...registry.players.keys()]);
  return Object.fromEntries([...steamIds].sort().map((steamId) => {
    const identity = mergeProfessionalPlayerIdentities(
      resolveProfessionalPlayerFromCatalog(demo, steamId),
      registry.players.get(steamId) ?? null,
      registry,
    )!;
    const player = identity.registry;
    const hltv = identity.hltv;
    return [steamId, {
      handle: identity.handle,
      realName: player?.nameLatin ?? player?.nameNative ?? hltv?.realName ?? null,
      country: player?.country ?? hltv?.country?.name ?? null,
      countryCode: player?.countryCode ?? hltv?.country?.code ?? null,
      birthDate: player?.birthDate ?? null,
      roles: player?.roles ?? [],
      searchText: [
        identity.handle,
        ...identity.aliases,
        player?.nameNative,
        player?.nameLatin,
        player?.country,
        hltv?.registeredHandle,
        hltv?.realName,
      ].filter(Boolean).join(" ").toLowerCase(),
    } satisfies ProfessionalPlayerSummary];
  }));
}
