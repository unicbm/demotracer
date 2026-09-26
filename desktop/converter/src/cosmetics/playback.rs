/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

//! Round-scoped replay selection. A known start inventory (even unpainted)
//! takes precedence over later observations; active weapons are a fallback.

use super::*;
use crate::inspect_link::{item_inspect, weapon_inspect};
use crate::model::{
    ReplayAgentCosmetic, ReplayCosmetics, ReplayItemCosmetic, ReplayScoreboardFlair,
};
use sha2::{Digest, Sha256};

pub(crate) fn replay_cosmetics_at(
    rows: &[&ParsedPlayerTick],
    cosmetic_rows: &[&ParsedPlayerTick],
    play_start_tick_index: u32,
    econ_glove_seeds: Option<&EconGloveSeedMap>,
    econ_knife_paints: Option<&EconKnifePaintMap>,
    export_stickers: bool,
    export_charms: bool,
) -> Option<ReplayCosmetics> {
    let play_start = (play_start_tick_index as usize).min(rows.len().saturating_sub(1));
    let active_rows = rows.get(play_start..).unwrap_or(rows);
    // Some(empty) is authoritative: an unpainted start inventory must suppress
    // later pickups. Only absent inventory evidence permits the active fallback.
    let inventory_weapons = live_start_inventory_weapon_cosmetics(
        rows,
        play_start_tick_index,
        export_stickers,
        export_charms,
    )
    .or_else(|| round_inventory_weapon_cosmetics(cosmetic_rows, export_stickers, export_charms));
    let mut cosmetics = replay_active_cosmetics(
        active_rows,
        econ_glove_seeds,
        econ_knife_paints,
        export_stickers,
        inventory_weapons.is_none(),
    )
    .unwrap_or_default();
    if let Some(agent) = replay_agent_cosmetic(rows) {
        cosmetics.agent = Some(agent);
    }
    if let Some(weapons) = inventory_weapons {
        cosmetics.weapons = weapons;
    }
    cosmetics
        .weapons
        .sort_by_key(|weapon| weapon.weapon_def_index);
    populate_cosmetic_inspect(&mut cosmetics);
    (!cosmetics.is_empty()).then_some(cosmetics)
}

fn populate_cosmetic_inspect(cosmetics: &mut ReplayCosmetics) {
    for weapon in &mut cosmetics.weapons {
        let rarity = weapon_cosmetic_rarity(weapon.weapon_def_index, weapon.paint_kit);
        let inspect = weapon_inspect(weapon, rarity);
        weapon.inspect = inspect;
    }
    if let Some(knife) = cosmetics.knife.as_mut() {
        let inspect = item_inspect(knife, Some(6));
        knife.inspect = inspect;
    }
    if let Some(glove) = cosmetics.glove.as_mut() {
        let inspect = item_inspect(glove, Some(6));
        glove.inspect = inspect;
    }
}

const AGENT_MODEL_FAMILIES: &[&str] = &[
    "ctm_diver",
    "ctm_fbi",
    "ctm_gendarmerie",
    "ctm_gign",
    "ctm_gsg9",
    "ctm_heavy",
    "ctm_idf",
    "ctm_sas",
    "ctm_st6",
    "ctm_swat",
    "tm_anarchist",
    "tm_balkan",
    "tm_jungle_raider",
    "tm_jumpsuit",
    "tm_leet",
    "tm_phoenix_heavy",
    "tm_phoenix",
    "tm_pirate",
    "tm_professional",
    "tm_separatist",
];

fn replay_agent_cosmetic(rows: &[&ParsedPlayerTick]) -> Option<ReplayAgentCosmetic> {
    let mut item_defs = BTreeSet::new();
    let mut names = BTreeSet::new();
    let mut model_paths = BTreeSet::new();

    for row in rows {
        if row.steam_id == 0 {
            continue;
        }
        let Some(item_def) = row.agent_item_def_index.filter(|value| *value != 0) else {
            continue;
        };
        if !valid_agent_item_def_index(item_def) {
            continue;
        }
        let Some(name) = row.agent_skin.as_deref() else {
            continue;
        };
        let Some(model_path) = agent_model_path_from_name(name) else {
            continue;
        };
        item_defs.insert(item_def);
        names.insert(name.to_string());
        model_paths.insert(model_path);
    }

    if item_defs.len() != 1 || model_paths.len() != 1 {
        return None;
    }

    Some(ReplayAgentCosmetic {
        item_def_index: *item_defs.iter().next()?,
        model_path: model_paths.into_iter().next()?,
        name: (names.len() == 1)
            .then(|| names.into_iter().next())
            .flatten(),
    })
}

pub(crate) fn agent_model_path_from_name(name: &str) -> Option<String> {
    let normalized = name.trim().to_ascii_lowercase();
    let stem = normalized.strip_prefix("customplayer_")?;
    if stem == "t_map_based" || stem == "ct_map_based" {
        return None;
    }
    if !stem
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
    {
        return None;
    }

    let family = AGENT_MODEL_FAMILIES
        .iter()
        .filter(|family| stem == **family || stem.starts_with(&format!("{family}_")))
        .max_by_key(|family| family.len())?;
    Some(format!("agents\\models\\{family}\\{stem}.vmdl"))
}

#[derive(Clone, Copy, Debug)]
struct ObservedGloveSpec {
    key: EconGloveKey,
    seed: Option<u32>,
}

fn replay_active_cosmetics(
    rows: &[&ParsedPlayerTick],
    econ_glove_seeds: Option<&EconGloveSeedMap>,
    econ_knife_paints: Option<&EconKnifePaintMap>,
    export_stickers: bool,
    collect_weapons: bool,
) -> Option<ReplayCosmetics> {
    let mut weapons = BTreeMap::<i32, WeaponEvidence>::new();
    let mut knife_specs = BTreeSet::new();
    let mut knife_custom_names = BTreeSet::new();
    let mut glove = None;

    for row in rows {
        if !row.is_alive || row.steam_id == 0 {
            continue;
        }

        let raw_def = row.item_def_idx;
        if is_knife_cosmetic_def_index(raw_def) {
            let active_spec = active_cosmetic_owned_by(row)
                .then(|| {
                    cosmetic_paint_spec(
                        row.active_weapon_paint_kit,
                        row.active_weapon_paint_seed,
                        row.active_weapon_paint_wear,
                    )
                })
                .flatten();
            let econ_paint = matching_active_econ_knife_paint(econ_knife_paints, row);
            let econ_spec = econ_paint.as_ref().map(|paint| CosmeticPaintSpec {
                paint_kit: paint.paint_kit,
                seed: paint.seed,
                wear_bits: paint.wear_bits,
            });
            if let Some(spec) = active_spec.or(econ_spec) {
                knife_specs.insert((raw_def, spec));
            }
            if active_cosmetic_owned_by(row) {
                if let Some(name) = active_cosmetic_custom_name(row) {
                    knife_custom_names.insert((raw_def, name));
                }
            }
            if let Some(name) = econ_paint.and_then(|paint| paint.custom_name) {
                knife_custom_names.insert((raw_def, name));
            }
        } else if collect_weapons {
            let def = normalize_weapon_def_index(raw_def);
            if is_weapon_cosmetic_def_index(def) && has_trusted_active_weapon_cosmetic_identity(row)
            {
                let evidence = weapons.entry(def).or_default();
                if let Some(spec) = cosmetic_paint_spec(
                    row.active_weapon_paint_kit,
                    row.active_weapon_paint_seed,
                    row.active_weapon_paint_wear,
                )
                .filter(|spec| valid_weapon_cosmetic_paint(def, spec.paint_kit))
                {
                    evidence.paint.observe(spec);
                }
                evidence
                    .name
                    .observe_option(active_cosmetic_custom_name(row));
                evidence.owner.observe_option(
                    row.active_weapon_original_owner_steam_id
                        .filter(|v| *v != 0),
                );
                evidence
                    .account
                    .observe_option(row.active_weapon_item_account_id.filter(|v| *v > 1));
                evidence
                    .item_id
                    .observe_option(row.active_weapon_item_id.filter(|v| *v != 0));
                if export_stickers {
                    match active_cosmetic_sticker_set(row) {
                        Some(stickers) => evidence.stickers.observe(stickers),
                        // Active-weapon evidence requires every observation to
                        // contain the same attachment set; absence is not proof.
                        None => evidence.stickers = Unique::Conflict,
                    }
                }
            }
        }

        if glove.is_none_or(|spec: ObservedGloveSpec| spec.seed.is_none()) {
            if let Some(candidate) = observed_glove_spec(row, econ_glove_seeds) {
                match &mut glove {
                    None => glove = Some(candidate),
                    Some(current)
                        if current.seed.is_none()
                            && candidate.seed.is_some()
                            && current.key == candidate.key =>
                    {
                        current.seed = candidate.seed;
                    }
                    Some(_) => {}
                }
            }
        }
    }

    let mut cosmetics = ReplayCosmetics::default();
    cosmetics.weapons = weapons
        .into_iter()
        .filter_map(|(def, evidence)| evidence.finish(def))
        .collect();

    if knife_specs.len() == 1 {
        if let Some((item_def_index, spec)) = knife_specs.iter().next().copied() {
            cosmetics.knife = Some(ReplayItemCosmetic {
                item_def_index: Some(item_def_index),
                paint_kit: spec.paint_kit,
                seed: spec.seed,
                seed_known: None,
                wear: f32::from_bits(spec.wear_bits),
                custom_name: stable_knife_custom_name(&knife_custom_names, item_def_index),
                inspect: None,
            });
        }
    }

    if let Some(glove) = glove {
        let seed_known = glove.seed.is_some();
        cosmetics.glove = Some(ReplayItemCosmetic {
            item_def_index: Some(glove.key.item_def_index),
            paint_kit: glove.key.paint_kit,
            seed: glove
                .seed
                .unwrap_or_else(|| stable_glove_fallback_seed(rows, glove.key)),
            seed_known: (!seed_known).then_some(false),
            wear: f32::from_bits(glove.key.wear_bits),
            custom_name: None,
            inspect: None,
        });
    }

    cosmetics
        .weapons
        .sort_by_key(|weapon| weapon.weapon_def_index);
    (!cosmetics.is_empty()).then_some(cosmetics)
}

fn stable_glove_fallback_seed(rows: &[&ParsedPlayerTick], key: EconGloveKey) -> u32 {
    let identity = rows
        .iter()
        .find(|row| row.steam_id != 0)
        .map(|row| (row.steam_id, row.team_num))
        .unwrap_or_default();
    let mut hasher = Sha256::new();
    hasher.update(b"cs2-demotracer-glove-fallback-seed-v1\0");
    hasher.update(identity.0.to_le_bytes());
    hasher.update(identity.1.to_le_bytes());
    hasher.update(key.item_def_index.to_le_bytes());
    hasher.update(key.paint_kit.to_le_bytes());
    hasher.update(key.wear_bits.to_le_bytes());
    let digest = hasher.finalize();
    1 + u32::from_le_bytes(digest[..4].try_into().expect("SHA-256 prefix")) % 1_000
}

fn live_start_inventory_weapon_cosmetics(
    rows: &[&ParsedPlayerTick],
    play_start_tick_index: u32,
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let row = live_start_inventory_cosmetic_row(rows, play_start_tick_index)?;
    Some(
        inventory_weapon_cosmetics_for_row(row, export_stickers, export_charms).unwrap_or_default(),
    )
}

pub(crate) fn round_inventory_weapon_cosmetics(
    rows: &[&ParsedPlayerTick],
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let mut by_def = BTreeMap::new();
    let mut snapshots = inventory::InventorySnapshotTracker::default();
    for row in rows {
        if !row.is_alive || row.steam_id == 0 || !snapshots.changed(row) {
            continue;
        }
        let Some(weapons) = inventory_weapon_cosmetics_for_row(row, export_stickers, export_charms)
        else {
            continue;
        };
        for weapon in weapons {
            by_def.entry(weapon.weapon_def_index).or_insert(weapon);
        }
    }
    let weapons = by_def.into_values().collect::<Vec<_>>();
    (!weapons.is_empty()).then_some(weapons)
}

fn live_start_inventory_cosmetic_row<'a>(
    rows: &[&'a ParsedPlayerTick],
    play_start_tick_index: u32,
) -> Option<&'a ParsedPlayerTick> {
    if rows.is_empty() {
        return None;
    }
    let center = (play_start_tick_index as usize).min(rows.len().saturating_sub(1));
    let mut candidates = Vec::with_capacity(5);
    candidates.push(center);
    for offset in 1..=2 {
        if center + offset < rows.len() {
            candidates.push(center + offset);
        }
    }
    for offset in 1..=2 {
        if let Some(idx) = center.checked_sub(offset) {
            candidates.push(idx);
        }
    }
    candidates
        .into_iter()
        .map(|idx| rows[idx])
        .find(|row| !row.inventory_weapon_cosmetics.is_empty())
}

fn inventory_weapon_cosmetics_for_row(
    row: &ParsedPlayerTick,
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let mut weapons = BTreeMap::<i32, WeaponEvidence>::new();
    for item in row.inventory_weapon_cosmetics.iter() {
        let def = normalize_weapon_def_index(item.item_def_index);
        if !is_weapon_cosmetic_def_index(def)
            || !has_trusted_inventory_weapon_cosmetic_identity(item)
        {
            continue;
        }
        let evidence = weapons.entry(def).or_default();
        if let Some(spec) = inventory_cosmetic_paint_spec(item)
            .filter(|spec| valid_weapon_cosmetic_paint(def, spec.paint_kit))
        {
            evidence.paint.observe(spec);
        }
        evidence
            .name
            .observe_option(cosmetic_custom_name_value(item.custom_name.as_deref()));
        evidence
            .quality
            .observe_option((item.entity_quality == Some(9)).then_some(9));
        evidence
            .stattrak
            .observe_option(inventory_stattrak_counter(item));
        evidence
            .owner
            .observe_option(item.original_owner_xuid.filter(|v| *v != 0));
        evidence
            .account
            .observe_option(item.item_account_id.filter(|v| *v != 0));
        evidence.item_id.observe_option(
            combine_item_id(item.item_id_high, item.item_id_low).filter(|v| *v != 0),
        );
        if export_stickers {
            evidence
                .stickers
                .observe_option(cosmetic_sticker_set_from_slice(&item.stickers));
        }
        if export_charms {
            evidence
                .charms
                .observe_option(cosmetic_charm_set_from_attributes(&item.attributes));
        }
    }
    let weapons: Vec<_> = weapons
        .into_iter()
        .filter_map(|(def, evidence)| evidence.finish(def))
        .collect();
    (!weapons.is_empty()).then_some(weapons)
}

pub(crate) fn stable_music_kit_id(rows: &[&ParsedPlayerTick]) -> Option<u32> {
    let mut values = BTreeSet::new();
    for row in rows {
        if let Some(value) = row.music_kit_id.filter(|value| valid_music_kit_id(*value)) {
            values.insert(value);
        }
    }
    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

pub(crate) fn stable_scoreboard_flair(rows: &[&ParsedPlayerTick]) -> Option<ReplayScoreboardFlair> {
    let mut values = BTreeSet::new();
    for row in rows {
        let Some(flair) = row.scoreboard_flair else {
            continue;
        };
        if !valid_scoreboard_flair_item_def(flair.item_def_index) {
            continue;
        }
        values.insert(ReplayScoreboardFlair {
            item_def_index: flair.item_def_index,
        });
    }

    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn stable_knife_custom_name(
    names: &BTreeSet<(i32, String)>,
    item_def_index: i32,
) -> Option<String> {
    let mut matching = names
        .iter()
        .filter_map(|(def, name)| (*def == item_def_index).then_some(name.clone()))
        .collect::<BTreeSet<_>>();
    if matching.len() == 1 {
        matching.pop_first()
    } else {
        None
    }
}

fn active_cosmetic_sticker_set(row: &ParsedPlayerTick) -> Option<Vec<CosmeticStickerSpec>> {
    cosmetic_sticker_set_from_slice(&row.active_weapon_stickers)
}

fn observed_glove_spec(
    row: &ParsedPlayerTick,
    econ_glove_seeds: Option<&EconGloveSeedMap>,
) -> Option<ObservedGloveSpec> {
    let item_def_index = row
        .glove_item_def_index
        .filter(|value| valid_glove_item_def_index(*value))?;
    let paint_kit = row
        .glove_paint_kit
        .filter(|value| valid_paint_kit(*value))?;
    let wear = row.glove_paint_wear?;
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    let wear_bits = wear.to_bits();
    Some(ObservedGloveSpec {
        key: EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        },
        seed: row.glove_paint_seed.or_else(|| {
            matching_econ_glove_seed(econ_glove_seeds, item_def_index, paint_kit, wear_bits)
        }),
    })
}

/// Only uniqueness matters to replay output. Conflict is sticky, and missing
/// evidence is distinct from disagreement; no set of discarded values is needed.
enum Unique<T> {
    Missing,
    Value(T),
    Conflict,
}

impl<T> Default for Unique<T> {
    fn default() -> Self {
        Self::Missing
    }
}

impl<T: PartialEq> Unique<T> {
    fn observe(&mut self, value: T) {
        match self {
            Self::Missing => *self = Self::Value(value),
            Self::Value(previous) if *previous != value => *self = Self::Conflict,
            _ => {}
        }
    }

    fn observe_option(&mut self, value: Option<T>) {
        if let Some(value) = value {
            self.observe(value);
        }
    }

    fn into_value(self) -> Option<T> {
        match self {
            Self::Value(value) => Some(value),
            _ => None,
        }
    }
}

#[derive(Default)]
struct WeaponEvidence {
    paint: Unique<CosmeticPaintSpec>,
    name: Unique<String>,
    quality: Unique<i32>,
    stattrak: Unique<i32>,
    owner: Unique<u64>,
    account: Unique<u32>,
    item_id: Unique<u64>,
    stickers: Unique<Vec<CosmeticStickerSpec>>,
    charms: Unique<Vec<CosmeticCharmSpec>>,
}

impl WeaponEvidence {
    fn finish(self, weapon_def_index: i32) -> Option<ReplayWeaponCosmetic> {
        let paint = self.paint.into_value()?;
        Some(ReplayWeaponCosmetic {
            weapon_def_index,
            paint_kit: paint.paint_kit,
            seed: paint.seed,
            wear: f32::from_bits(paint.wear_bits),
            quality: self.quality.into_value(),
            stattrak_counter: self.stattrak.into_value(),
            original_owner_steam_id: self.owner.into_value(),
            item_account_id: self.account.into_value(),
            item_id: self.item_id.into_value(),
            custom_name: self.name.into_value(),
            stickers: self
                .stickers
                .into_value()
                .unwrap_or_default()
                .into_iter()
                .map(Into::into)
                .collect(),
            charms: self
                .charms
                .into_value()
                .unwrap_or_default()
                .into_iter()
                .map(Into::into)
                .collect(),
            inspect: None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inventory_conflicts_are_sticky_and_do_not_remove_independent_evidence() {
        let first = ParsedInventoryWeaponCosmetic {
            item_def_index: 16,
            paint_kit: 926,
            paint_seed: 42,
            paint_wear: 0.123,
            item_account_id: Some(123),
            entity_quality: Some(9),
            stattrak_counter: Some(1),
            custom_name: Some("first".to_string()),
            stickers: vec![ParsedWeaponSticker {
                slot: 0,
                sticker_id: 225,
                wear: 0.1,
                offset_x: 0.0,
                offset_y: 0.0,
                scale: None,
                rotation: None,
            }],
            ..ParsedInventoryWeaponCosmetic::default()
        };
        let mut conflicting = first.clone();
        conflicting.custom_name = Some("other".to_string());
        conflicting.stattrak_counter = Some(2);
        conflicting.stickers[0].sticker_id = 7891;
        let row = ParsedPlayerTick {
            inventory_weapon_cosmetics: vec![first.clone(), conflicting.clone(), first.clone()]
                .into(),
            ..ParsedPlayerTick::default()
        };
        let weapons = inventory_weapon_cosmetics_for_row(&row, true, true).unwrap();
        assert_eq!(weapons.len(), 1);
        let weapon = &weapons[0];
        assert_eq!(weapon.paint_kit, 926);
        assert_eq!(weapon.wear.to_bits(), first.paint_wear.to_bits());
        assert_eq!(weapon.item_account_id, Some(123));
        assert_eq!(weapon.quality, Some(9));
        assert_eq!(weapon.custom_name, None);
        assert_eq!(weapon.stattrak_counter, None);
        assert!(weapon.stickers.is_empty());

        conflicting.paint_kit = 309;
        let row = ParsedPlayerTick {
            inventory_weapon_cosmetics: vec![first.clone(), conflicting, first].into(),
            ..row
        };
        assert!(inventory_weapon_cosmetics_for_row(&row, true, true).is_none());
    }
}
