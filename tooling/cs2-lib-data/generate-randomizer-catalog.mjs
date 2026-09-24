/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { CS2_ITEMS, CS2RarityColorOrder } from "@ianlucas/cs2-lib";
import { english } from "@ianlucas/cs2-lib/translations/english";
import { readFileSync, writeFileSync } from "node:fs";
import { source, integrity, catalogPath } from "./source.mjs";

const policy = JSON.parse(readFileSync(new URL("randomizer-policy.json", import.meta.url), "utf8"));
const output = catalogPath;
const rarities = [null, "consumer", "industrial", "milSpec", "restricted", "classified", "covert", "contraband"];
const positive = (value, label) => {
  if (!Number.isSafeInteger(value) || value <= 0) throw new Error(`Invalid ${label}: ${value}`);
  return value;
};
const variants = (type) => CS2_ITEMS.filter(item => item.type === type && item.variantIndex > 0);
const unique = (values) => [...new Set(values)].sort((a, b) => a - b);
const wear = (item) => {
  const wearMin = item.wearMin;
  const wearMax = item.wearMax;
  if (!Number.isFinite(wearMin) || !Number.isFinite(wearMax) ||
      wearMin < 0 || wearMax > 1 || wearMin > wearMax) {
    throw new Error(`Invalid wear on item ${item.id}`);
  }
  return { wearMin, wearMax };
};
const paintKit = (item) => positive(item.variantIndex, `paint on ${item.id}`);
const definition = (item) => positive(item.definitionIndex, `definition on ${item.id}`);
const byPaint = (a, b) => a.paintKit - b.paintKit;
const group = (items) => unique(items.map(definition)).map(defIndex => ({
  defIndex, items: items.filter(item => item.definitionIndex === defIndex),
}));
const weapons = group(variants("weapon")).map(({ defIndex, items }) => {
  const base = CS2_ITEMS.find(item => item.type === "weapon" && item.isBase && item.definitionIndex === defIndex);
  if (!base?.modelKey) throw new Error(`Missing weapon base ${defIndex}`);
  return {
    designerName: `weapon_${base.modelKey}`, defIndex,
    stickerSchemaCount: positive(base.stickerSchemaCount, `sticker schemas on ${defIndex}`),
    legacyStickerSchemaCount: positive(base.legacyStickerSchemaCount ?? base.stickerSchemaCount, `legacy schemas on ${defIndex}`),
    paints: items.map(item => {
      const rarity = rarities[CS2RarityColorOrder[item.rarityColor]];
      if (!rarity) throw new Error(`Unknown rarity on item ${item.id}`);
      return { paintKit: paintKit(item), rarity, legacy: item.isLegacyModel === true, ...wear(item) };
    }).sort(byPaint),
  };
});
const knives = group(variants("melee")).map(({ defIndex, items }) => ({
  defIndex,
  paints: items.map(item => {
    const finish = english[item.id]?.name?.split(" | ")[1];
    if (!finish) throw new Error(`Missing knife finish on item ${item.id}`);
    return { paintKit: paintKit(item), finish, ...wear(item) };
  }).sort(byPaint),
}));
const gloves = variants("glove").map(item => ({
  defIndex: definition(item), paintKit: paintKit(item), ...wear(item),
})).sort((a, b) => a.defIndex - b.defIndex || byPaint(a, b));
const stickers = variants("sticker");
const stickerCategories = [...new Set(stickers.map(item => {
  const category = english[item.id]?.categoryName;
  if (!category) throw new Error(`Missing sticker category on item ${item.id}`);
  return category;
}))].sort((a, b) => {
  const left = a.toLowerCase();
  const right = b.toLowerCase();
  return left < right ? -1 : left > right ? 1 : a < b ? -1 : a > b ? 1 : 0;
});
const stickerKits = stickers.map(item => {
  const translation = english[item.id];
  if (!translation.name) throw new Error(`Missing sticker name on item ${item.id}`);
  const finish = /\((Glitter|Holo|Foil|Gold|Lenticular)(?:,|\))/.exec(translation.name)?.[1].toLowerCase() ?? "paper";
  return { defIndex: paintKit(item), finish, category: stickerCategories.indexOf(translation.categoryName) };
}).sort((a, b) => a.defIndex - b.defIndex);
const catalog = {
  source: { repository: "ianlucas/cs2-lib", commit: source.commit,
    package: source.package, version: source.version, integrity, proDemo: policy.proDemo },
  weapons, knives, knifeFinishPreferences: policy.knifeFinishPreferences, gloves,
  stickerCategories, stickerKits,
  keychainDefinitions: unique(variants("keychain").map(paintKit)),
  musicKits: unique(variants("musickit").map(paintKit)).filter(id => !policy.excludedMusicKits.includes(id)),
};
const generated = `${JSON.stringify(catalog, null, 2)}\n`;
if (process.argv.includes("--check")) {
  if (readFileSync(output, "utf8") !== generated) throw new Error("Randomizer catalog is stale; run npm run generate");
  console.log(`Verified Randomizer catalog against ${source.package}@${source.version}.`);
} else {
  writeFileSync(output, generated);
  console.log(`Wrote Randomizer catalog: ${weapons.length} weapons, ${stickerKits.length} stickers.`);
}
