/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
