/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { ProSteamIdIdentity } from "./proSteamIdCatalog";

/** Display and search projection; provenance stays in the build-time source catalogs. */
export interface ProfessionalPlayerSummary {
  handle: string;
  realName: string | null;
  country: string | null;
  countryCode: string | null;
  birthDate: string | null;
  roles: readonly string[];
  searchText: string;
}

export interface ProfessionalPlayerCountry {
  name: string;
  code: string;
}

export interface ProfessionalPlayerHltvProfile {
  playerId: number;
  registeredHandle: string;
  realName: string | null;
  country: ProfessionalPlayerCountry | null;
  sourceUrl: string;
  verifiedAt: string;
}

export interface ProfessionalPlayerIdentity {
  steamId: string;
  handle: string;
  aliases: readonly string[];
  evidenceResult: "confirmed" | "corrected" | "hltv-roster" | "direct" | "curated";
  evidenceRecords: number;
  verifiedAt: string;
  catalogVersion: string;
  hltv: ProfessionalPlayerHltvProfile | null;
  registry: (ProSteamIdIdentity & {
    catalogVersion: string;
    repository: string;
  }) | null;
}

interface ProfessionalPlayerRecord {
  steamId: string;
  handle: string;
  aliases: readonly string[];
  evidence: {
    result: "confirmed" | "corrected" | "hltv-roster";
    records: number;
    verifiedAt: string;
  };
  hltv: ProfessionalPlayerHltvProfile | null;
}

export interface ProfessionalPlayerCatalog {
  schemaVersion: 2;
  catalogVersion: string;
  players: ReadonlyMap<string, ProfessionalPlayerRecord>;
}

function isoDateParts(birthDate: string | null | undefined) {
  const match = birthDate?.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const validated = new Date(Date.UTC(year, month - 1, day));
  if (
    validated.getUTCFullYear() !== year
    || validated.getUTCMonth() !== month - 1
    || validated.getUTCDate() !== day
  ) {
    return null;
  }
  return { year, month, day };
}

export function completedAge(birthDate: string | null | undefined, at = new Date()): number | null {
  const parts = isoDateParts(birthDate);
  if (!parts || !Number.isFinite(at.getTime())) return null;

  const birthdayHasPassed = at.getMonth() + 1 > parts.month
    || (at.getMonth() + 1 === parts.month && at.getDate() >= parts.day);
  const age = at.getFullYear() - parts.year - (birthdayHasPassed ? 0 : 1);
  return age >= 0 && age <= 150 ? age : null;
}

export function formattedBirthDateWithAge(
  birthDate: string | null | undefined,
  language: "zh" | "en",
  at = new Date(),
): string | null {
  const parts = isoDateParts(birthDate);
  const age = completedAge(birthDate, at);
  if (!parts || age === null) return null;
  if (language === "zh") {
    return `${parts.year}年${parts.month}月${parts.day}日 （${age} 岁）`;
  }
  const englishDate = new Intl.DateTimeFormat("en-US", {
    month: "long",
    day: "numeric",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(parts.year, parts.month - 1, parts.day)));
  return `${englishDate} (age ${age})`;
}

const PROFESSIONAL_ROLE_LABELS: Readonly<Record<string, string>> = {
  awp: "AWPer",
  awper: "AWPer",
  rifle: "Rifler",
  rifler: "Rifler",
  entry: "Entry",
  "entry fragger": "Entry",
  igl: "IGL",
  "in-game leader": "IGL",
  lurk: "Lurker",
  lurker: "Lurker",
  support: "Support",
};

export function formattedProfessionalRoles(roles: readonly string[] | null | undefined): string | null {
  const labels = (roles ?? [])
    .flatMap((role) => role.split(","))
    .map((role) => role.trim())
    .filter(Boolean)
    .map((role) => {
      const normalized = role.toLocaleLowerCase();
      return PROFESSIONAL_ROLE_LABELS[normalized]
        ?? `${normalized.charAt(0).toLocaleUpperCase()}${normalized.slice(1)}`;
    });
  const uniqueLabels = [...new Set(labels)];
  return uniqueLabels.length > 0 ? uniqueLabels.join(" · ") : null;
}

function objectValue(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${context} must be an object`);
  }
  return value as Record<string, unknown>;
}

function stringValue(value: unknown, context: string): string {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${context} must be a non-empty string`);
  }
  return value.trim();
}

function positiveInteger(value: unknown, context: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${context} must be a positive integer`);
  }
  return value;
}

function parseHltvProfile(
  value: unknown,
  context: string,
): ProfessionalPlayerHltvProfile {
  const profile = objectValue(value, context);
  const playerId = positiveInteger(profile.playerId, `${context}.playerId`);
  const registeredHandle = stringValue(
    profile.registeredHandle,
    `${context}.registeredHandle`,
  );
  const source = objectValue(profile.source, `${context}.source`);
  const sourceUrl = stringValue(source.url, `${context}.source.url`);
  const expectedPrefix = `https://www.hltv.org/player/${playerId}/`;
  if (!sourceUrl.startsWith(expectedPrefix)) {
    throw new Error(`${context}.source.url must identify HLTV Player ID ${playerId}`);
  }

  let realName: string | null = null;
  if (profile.realName !== undefined) {
    realName = stringValue(profile.realName, `${context}.realName`);
  }

  let country: ProfessionalPlayerCountry | null = null;
  if (profile.country !== undefined) {
    const countryValue = objectValue(profile.country, `${context}.country`);
    const code = stringValue(countryValue.code, `${context}.country.code`).toUpperCase();
    if (!/^[A-Z]{2}$/.test(code)) {
      throw new Error(`${context}.country.code must be a two-letter country code`);
    }
    country = {
      name: stringValue(countryValue.name, `${context}.country.name`),
      code,
    };
  }

  return {
    playerId,
    registeredHandle,
    realName,
    country,
    sourceUrl,
    verifiedAt: stringValue(source.verifiedAt, `${context}.source.verifiedAt`),
  };
}

export function parseProfessionalPlayerCatalog(value: unknown): ProfessionalPlayerCatalog {
  const root = objectValue(value, "professional player catalog");
  if (root.schemaVersion !== 2) {
    throw new Error("professional player catalog schemaVersion must be 2");
  }
  const catalogVersion = stringValue(root.catalogVersion, "catalogVersion");
  if (!Array.isArray(root.players)) {
    throw new Error("professional player catalog players must be an array");
  }

  const players = new Map<string, ProfessionalPlayerRecord>();
  root.players.forEach((candidate, index) => {
    const context = `players[${index}]`;
    const player = objectValue(candidate, context);
    const steamId = stringValue(player.steamId, `${context}.steamId`);
    if (!/^\d{17}$/.test(steamId)) {
      throw new Error(`${context}.steamId must be a 17-digit SteamID64`);
    }
    if (players.has(steamId)) {
      throw new Error(`professional player catalog contains duplicate SteamID64 ${steamId}`);
    }
    if (!Array.isArray(player.aliases)) {
      throw new Error(`${context}.aliases must be an array`);
    }
    const aliases = player.aliases.map((alias, aliasIndex) => (
      stringValue(alias, `${context}.aliases[${aliasIndex}]`)
    ));
    const evidence = objectValue(player.evidence, `${context}.evidence`);
    if (
      evidence.result !== "confirmed"
      && evidence.result !== "corrected"
      && evidence.result !== "hltv-roster"
    ) {
      throw new Error(
        `${context}.evidence.result must be confirmed, corrected, or hltv-roster`,
      );
    }
    players.set(steamId, {
      steamId,
      handle: stringValue(player.handle, `${context}.handle`),
      aliases,
      evidence: {
        result: evidence.result,
        records: positiveInteger(evidence.records, `${context}.evidence.records`),
        verifiedAt: stringValue(evidence.verifiedAt, `${context}.evidence.verifiedAt`),
      },
      hltv: player.hltv === undefined
        ? null
        : parseHltvProfile(player.hltv, `${context}.hltv`),
    });
  });

  return { schemaVersion: 2, catalogVersion, players };
}

export function resolveProfessionalPlayerFromCatalog(
  catalog: ProfessionalPlayerCatalog,
  steamId: string,
): ProfessionalPlayerIdentity | null {
  const player = catalog.players.get(steamId);
  if (!player) return null;
  return {
    steamId,
    handle: player.handle,
    aliases: player.aliases,
    evidenceResult: player.evidence.result,
    evidenceRecords: player.evidence.records,
    verifiedAt: player.evidence.verifiedAt,
    catalogVersion: catalog.catalogVersion,
    hltv: player.hltv,
    registry: null,
  };
}

function uniqueAliases(values: readonly (string | undefined)[], handle: string): string[] {
  const normalizedHandle = handle.toLocaleLowerCase();
  const seen = new Set<string>();
  return values.flatMap((value) => {
    const alias = value?.trim();
    if (!alias) return [];
    const normalized = alias.toLocaleLowerCase();
    if (normalized === normalizedHandle || seen.has(normalized)) return [];
    seen.add(normalized);
    return [alias];
  });
}

export function mergeProfessionalPlayerIdentities(
  demoIdentity: ProfessionalPlayerIdentity | null,
  registryIdentity: ProSteamIdIdentity | null,
  registryCatalog: { catalogVersion: string; repository: string },
): ProfessionalPlayerIdentity | null {
  if (!demoIdentity && !registryIdentity) return null;
  if (!registryIdentity) return demoIdentity;

  const steamId = registryIdentity.steamId;
  const handle = demoIdentity?.handle ?? registryIdentity.handle;
  const mappingEvidence = registryIdentity.mappingSources.some((source) => source.identityEvidence === "direct")
    ? "direct"
    : "curated";
  return {
    steamId,
    handle,
    aliases: uniqueAliases([
      ...(demoIdentity?.aliases ?? []),
      registryIdentity.handle,
      ...registryIdentity.aliases,
    ], handle),
    evidenceResult: demoIdentity?.evidenceResult ?? mappingEvidence,
    evidenceRecords: (demoIdentity?.evidenceRecords ?? 0) + registryIdentity.mappingSources.length,
    verifiedAt: registryIdentity.mappingVerifiedAt,
    catalogVersion: [demoIdentity?.catalogVersion, registryCatalog.catalogVersion].filter(Boolean).join(" + "),
    hltv: demoIdentity?.hltv ?? null,
    registry: {
      ...registryIdentity,
      catalogVersion: registryCatalog.catalogVersion,
      repository: registryCatalog.repository,
    },
  };
}
