/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { ProfessionalPlayerSummary } from "../src/professionalPlayersCatalog.ts";

interface DemoPlayer {
  steamId: string;
  handle: string;
  aliases: string[];
  hltv?: {
    registeredHandle: string;
    realName?: string;
    country?: { name: string; code: string };
  };
}

interface RegistryPlayer {
  steamId: string;
  handle: string;
  aliases: string[];
  nameNative?: string;
  nameLatin?: string;
  country?: string;
  countryCode?: string;
  birthDate?: string;
  roles?: string[];
}

// Pinned snapshots are validated on import; project only the fields used by the GUI.
export function buildProfessionalPlayerRuntime(demoSource: { players: DemoPlayer[] }, registrySource: string) {
  const demo = new Map(demoSource.players.map((player) => [player.steamId, player]));
  const registryPlayers: RegistryPlayer[] = registrySource.trim().split(/\r?\n/).slice(1).map((line) => JSON.parse(line));
  const registry = new Map(registryPlayers.map((player) => [player.steamId, player]));
  const steamIds = new Set([...demo.keys(), ...registry.keys()]);
  return Object.fromEntries([...steamIds].sort().map((steamId) => {
    const source = demo.get(steamId);
    const player = registry.get(steamId);
    const hltv = source?.hltv;
    const handle = source?.handle ?? player!.handle;
    const aliases = player
      ? [...new Set([...(source?.aliases ?? []), player.handle, ...player.aliases].map((alias) => alias.trim().toLowerCase()))]
        .filter((alias) => alias && alias !== handle.toLowerCase())
      : source!.aliases;
    return [steamId, {
      handle,
      realName: player?.nameLatin ?? player?.nameNative ?? hltv?.realName ?? null,
      country: player?.country ?? hltv?.country?.name ?? null,
      countryCode: player?.countryCode ?? hltv?.country?.code ?? null,
      birthDate: player?.birthDate ?? null,
      roles: player?.roles ?? [],
      searchText: [
        handle,
        ...aliases,
        player?.nameNative,
        player?.nameLatin,
        player?.country,
        hltv?.registeredHandle,
        hltv?.realName,
      ].filter(Boolean).join(" ").toLowerCase(),
    } satisfies ProfessionalPlayerSummary];
  }));
}
