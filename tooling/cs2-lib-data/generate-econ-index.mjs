/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import {
  CS2_ITEMS,
  CS2_PAINTABLE_ITEMS,
  CS2RarityColorOrder,
} from "@ianlucas/cs2-lib";
import { source, integrity, econPath } from "./source.mjs";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";

const PACKAGE_NAME = "@ianlucas/cs2-lib";
const REPOSITORY = "https://github.com/ianlucas/cs2-lib";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const outputPath = fileURLToPath(econPath);

function requirePositiveInteger(value, field, item) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`Invalid ${field} on cs2-lib item ${item.id}`);
  }
  return value;
}

function sortedUnique(values) {
  return [...new Set(values)].sort((left, right) => left - right);
}

function itemDefIndices(type) {
  return sortedUnique(
    CS2_ITEMS.filter((item) => item.type === type).map((item) =>
      requirePositiveInteger(item.definitionIndex, "def", item),
    ),
  );
}

function itemIndices(type) {
  return sortedUnique(
    CS2_ITEMS.filter(
      (item) =>
        item.type === type && Number.isSafeInteger(item.variantIndex) && item.variantIndex > 0,
    ).map((item) => requirePositiveInteger(item.variantIndex, "index", item)),
  );
}

function weaponPaints(predicate = () => true) {
  const pairs = new Map();
  for (const item of CS2_ITEMS.filter(
    (candidate) =>
      candidate.type === "weapon" &&
      candidate.variantIndex !== undefined &&
      predicate(candidate),
  )) {
    const weaponDefIndex = requirePositiveInteger(item.definitionIndex, "def", item);
    const paintKit = requirePositiveInteger(item.variantIndex, "index", item);
    const rarity = CS2RarityColorOrder[item.rarityColor];
    if (!Number.isSafeInteger(rarity) || rarity < 1 || rarity > 7) {
      throw new Error(`Invalid rarity on cs2-lib item ${item.id}`);
    }

    const key = `${weaponDefIndex}:${paintKit}`;
    const pair = {
      weapon_defidx: weaponDefIndex,
      paint_kit: paintKit,
      rarity,
    };
    const existing = pairs.get(key);
    if (existing !== undefined && existing.rarity !== rarity) {
      throw new Error(`Conflicting rarity for cs2-lib weapon paint ${key}`);
    }
    pairs.set(key, pair);
  }
  return [...pairs.values()].sort(
    (left, right) =>
      left.weapon_defidx - right.weapon_defidx ||
      left.paint_kit - right.paint_kit,
  );
}

function replayEquipmentSlot(item) {
  if (item.type === "melee") {
    return "knife";
  }
  if (item.type === "utility") {
    return "utility";
  }
  switch (item.loadoutCategory) {
    case "rifle":
    case "heavy":
    case "smg":
      return "primary";
    case "secondary":
      return "secondary";
    case "equipment":
      if (item.modelKey !== "taser") {
        throw new Error(`Unsupported cs2-lib equipment model on item ${item.id}`);
      }
      return "taser";
    case "c4":
      if (item.modelKey !== "c4") {
        throw new Error(`Unsupported cs2-lib c4 model on item ${item.id}`);
      }
      return "c4";
    default:
      throw new Error(`Unsupported replay equipment category on cs2-lib item ${item.id}`);
  }
}

function replayEquipmentDefinitions() {
  const definitions = new Map();
  const classNames = new Set();
  for (const item of CS2_ITEMS.filter(
    (candidate) =>
      candidate.isBase === true &&
      ["weapon", "utility", "melee"].includes(candidate.type),
  )) {
    const weaponDefIndex = requirePositiveInteger(item.definitionIndex, "def", item);
    if (typeof item.modelKey !== "string" || item.modelKey.length === 0) {
      throw new Error(`Missing model on cs2-lib replay equipment item ${item.id}`);
    }

    const className = `weapon_${item.modelKey}`;
    if (definitions.has(weaponDefIndex)) {
      throw new Error(`Duplicate cs2-lib replay equipment defindex ${weaponDefIndex}`);
    }
    if (classNames.has(className)) {
      throw new Error(`Duplicate cs2-lib replay equipment class ${className}`);
    }
    definitions.set(weaponDefIndex, {
      weapon_defidx: weaponDefIndex,
      class_name: className,
      replay_slot: replayEquipmentSlot(item),
    });
    classNames.add(className);
  }
  return [...definitions.values()].sort(
    (left, right) => left.weapon_defidx - right.weapon_defidx,
  );
}

const weaponPaintPairs = weaponPaints();
const legacyBodygroupPaintPairs = weaponPaints((item) => item.isLegacyModel === true).map(
  ({ weapon_defidx, paint_kit }) => ({ weapon_defidx, paint_kit }),
);
const paintKitIds = sortedUnique(
  CS2_ITEMS.filter(
    (item) =>
      CS2_PAINTABLE_ITEMS.includes(item.type) &&
      Number.isSafeInteger(item.variantIndex) &&
      item.variantIndex > 0,
  ).map((item) => requirePositiveInteger(item.variantIndex, "index", item)),
);
const replayEquipmentDefIndices = sortedUnique(
  CS2_ITEMS.filter((item) =>
    ["weapon", "utility", "melee"].includes(item.type),
  ).map((item) => requirePositiveInteger(item.definitionIndex, "def", item)),
);
const replayEquipment = replayEquipmentDefinitions();
if (
  replayEquipment.length !== replayEquipmentDefIndices.length ||
  replayEquipment.some(
    (item, index) => item.weapon_defidx !== replayEquipmentDefIndices[index],
  )
) {
  throw new Error("cs2-lib replay equipment definitions do not cover every replay defindex");
}
const knifeDefIndices = itemDefIndices("melee");
const gloveDefIndices = itemDefIndices("glove");
const agentDefIndices = itemDefIndices("agent");
const stickerIds = itemIndices("sticker");
const keychainIds = itemIndices("keychain");
const musicKitIds = itemIndices("musickit");
const scoreboardFlairDefIndices = itemDefIndices("collectible");

const index = {
  name: "cs2-lib-econ-index",
  schema_version: 1,
  description:
    "Generated runtime projection of the pinned @ianlucas/cs2-lib item catalog for CS2 DemoTracer.",
  source: {
    package: PACKAGE_NAME,
    version: source.version,
    integrity,
    repository: REPOSITORY,
  },
  generation_policy:
    "Generated exclusively from @ianlucas/cs2-lib exports; do not hand-edit or add local item identifiers.",
  id_space_warning:
    "Do not compare IDs across arrays unless the array name/domain matches.",
  weapon_paints: weaponPaintPairs,
  legacy_bodygroup_paints: legacyBodygroupPaintPairs,
  paint_kit_ids: paintKitIds,
  replay_equipment_defidx: replayEquipmentDefIndices,
  replay_equipment: replayEquipment,
  knife_defidx: knifeDefIndices,
  glove_defidx: gloveDefIndices,
  agent_defidx: agentDefIndices,
  sticker_ids: stickerIds,
  keychain_ids: keychainIds,
  music_kit_ids: musicKitIds,
  scoreboard_flair_defidx: scoreboardFlairDefIndices,
  counts: {
    weapon_paints: weaponPaintPairs.length,
    legacy_bodygroup_paints: legacyBodygroupPaintPairs.length,
    paint_kit_ids: paintKitIds.length,
    replay_equipment_defidx: replayEquipmentDefIndices.length,
    replay_equipment: replayEquipment.length,
    knife_defidx: knifeDefIndices.length,
    glove_defidx: gloveDefIndices.length,
    agent_defidx: agentDefIndices.length,
    sticker_ids: stickerIds.length,
    keychain_ids: keychainIds.length,
    music_kit_ids: musicKitIds.length,
    scoreboard_flair_defidx: scoreboardFlairDefIndices.length,
    weapon_paint_rarities: weaponPaintPairs.length,
  },
};
const generated = `${JSON.stringify(index, null, 2)}\n`;

if (process.argv.includes("--check")) {
  const existing = readFileSync(outputPath, "utf8");
  if (existing !== generated) {
    throw new Error(
      `${outputPath} is stale; run npm.cmd run generate in ${scriptDirectory}`,
    );
  }
  console.log(`Verified ${outputPath} against ${PACKAGE_NAME}@${source.version}.`);
} else {
  writeFileSync(outputPath, generated, "utf8");
  console.log(
    `Wrote ${outputPath} from ${PACKAGE_NAME}@${source.version} ` +
      `(${weaponPaintPairs.length} weapon paints, ${stickerIds.length} stickers).`,
  );
}
