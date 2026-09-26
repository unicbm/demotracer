/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

//! Demo-backed appearance rules shared by player evidence and replay output.
//! Ownership and appearance validation live here; consumers retain their own
//! whole-demo or round-start selection policy. Wire decoding stays in the parser.

pub(crate) mod catalog;
pub(crate) mod inventory;
pub(crate) mod playback;

use crate::model::{
    ParsedDemo, ParsedEconItem, ParsedInventoryWeaponAttribute, ParsedInventoryWeaponCosmetic,
    ParsedPlayerTick, ParsedWeaponSticker, ReplayWeaponCharm, ReplayWeaponCosmetic,
    ReplayWeaponSticker,
};
use catalog::*;
use std::collections::{BTreeMap, BTreeSet};

pub(crate) const STEAM_ID64_BASE: u64 = 76_561_197_960_265_728;
pub(crate) const KEYCHAIN_SLOT_0_ID_ATTR: u32 = 299;
pub(crate) const KEYCHAIN_SLOT_0_OFFSET_X_ATTR: u32 = 300;
pub(crate) const KEYCHAIN_SLOT_0_OFFSET_Y_ATTR: u32 = 301;
pub(crate) const KEYCHAIN_SLOT_0_OFFSET_Z_ATTR: u32 = 302;
pub(crate) const KEYCHAIN_SLOT_0_SEED_ATTR: u32 = 306;
pub(crate) const KEYCHAIN_SLOT_0_HIGHLIGHT_ATTR: u32 = 314;
pub(crate) const KEYCHAIN_SLOT_0_STICKER_ATTR: u32 = 321;

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct EconGloveKey {
    item_def_index: i32,
    paint_kit: u32,
    wear_bits: u32,
}

pub(crate) type EconGloveSeedMap = BTreeMap<EconGloveKey, Option<u32>>;
pub(crate) type EconGloveSeedIndex = BTreeMap<u64, EconGloveSeedMap>;

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct EconKnifePaint {
    pub(crate) paint_kit: u32,
    pub(crate) seed: u32,
    pub(crate) wear_bits: u32,
    pub(crate) custom_name: Option<String>,
}

pub(crate) type EconKnifePaintMap = BTreeMap<i32, Option<EconKnifePaint>>;
pub(crate) type EconKnifePaintIndex = BTreeMap<u64, EconKnifePaintMap>;

pub(crate) fn knife_econ_paint_index(parsed: &ParsedDemo) -> EconKnifePaintIndex {
    let mut paints_by_player = EconKnifePaintIndex::new();
    for item in &parsed.econ_items {
        let Some(steam_id) = item.steam_id else {
            continue;
        };
        let Some(item_def_index) = item
            .item_def_index
            .and_then(|value| i32::try_from(value).ok())
            .filter(|value| is_knife_cosmetic_def_index(*value))
        else {
            continue;
        };
        let Some(spec) = cosmetic_paint_spec(
            item.paint_kit,
            item.paint_seed,
            item.paint_wear_raw.map(f32::from_bits).or(item.paint_wear),
        ) else {
            continue;
        };
        let paint = EconKnifePaint {
            paint_kit: spec.paint_kit,
            seed: spec.seed,
            wear_bits: spec.wear_bits,
            custom_name: cosmetic_custom_name_value(item.custom_name.as_deref()),
        };
        let paints = paints_by_player.entry(steam_id).or_default();
        match paints.entry(item_def_index) {
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert(Some(paint));
            }
            std::collections::btree_map::Entry::Occupied(mut entry) => {
                let Some(current) = entry.get_mut() else {
                    continue;
                };
                if current.paint_kit != paint.paint_kit
                    || current.seed != paint.seed
                    || current.wear_bits != paint.wear_bits
                {
                    *entry.get_mut() = None;
                    continue;
                }
                match (&current.custom_name, paint.custom_name) {
                    (None, Some(name)) => current.custom_name = Some(name),
                    (Some(existing), Some(name)) if existing != &name => {
                        current.custom_name = None;
                    }
                    _ => {}
                }
            }
        }
    }
    paints_by_player
}

fn matching_econ_knife_paint(
    paints: Option<&EconKnifePaintMap>,
    item_def_index: i32,
) -> Option<EconKnifePaint> {
    paints?.get(&item_def_index).cloned().flatten()
}

pub(crate) fn matching_active_econ_knife_paint(
    paints: Option<&EconKnifePaintMap>,
    row: &ParsedPlayerTick,
) -> Option<EconKnifePaint> {
    let paint = matching_econ_knife_paint(paints, row.item_def_idx)?;
    if active_cosmetic_owned_by(row) {
        if let Some(observed_wear) = row
            .active_weapon_paint_wear
            .filter(|wear| wear.is_finite() && (0.0..=1.0).contains(wear))
        {
            return (row.active_weapon_paint_seed == Some(paint.seed)
                && observed_wear.to_bits() == paint.wear_bits)
                .then_some(paint);
        }
    }

    // Some tournament demos expose the exact custom knife defindex while every
    // live econ field remains at its network default. The end-of-match inventory
    // is already scoped to this SteamID; when it contains one unambiguous paint
    // for that same knife type, the active defindex is the missing linkage.
    let live_econ_unavailable = row.active_weapon_paint_kit.is_none_or(|value| value == 0)
        && row.active_weapon_paint_seed.is_none_or(|value| value == 0)
        && row.active_weapon_paint_wear.is_none()
        && row
            .active_weapon_original_owner_steam_id
            .is_none_or(|value| value == 0)
        && row
            .active_weapon_item_account_id
            .is_none_or(|value| value <= 1)
        && row.active_weapon_item_id.is_none_or(|value| value == 0);
    live_econ_unavailable.then_some(paint)
}

pub(crate) fn glove_econ_seed_index(parsed: &ParsedDemo) -> EconGloveSeedIndex {
    let mut seeds_by_player = EconGloveSeedIndex::new();
    for item in &parsed.econ_items {
        let Some(steam_id) = item.steam_id else {
            continue;
        };
        let Some((key, seed)) = econ_glove_seed(item) else {
            continue;
        };
        let seeds = seeds_by_player.entry(steam_id).or_default();
        match seeds.entry(key) {
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert(Some(seed));
            }
            std::collections::btree_map::Entry::Occupied(mut entry) => {
                if entry.get().is_some_and(|current| current != seed) {
                    entry.insert(None);
                }
            }
        }
    }
    seeds_by_player
}

pub(crate) fn matching_econ_glove_seed(
    seeds: Option<&EconGloveSeedMap>,
    item_def_index: i32,
    paint_kit: u32,
    wear_bits: u32,
) -> Option<u32> {
    seeds?
        .get(&EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        })
        .copied()
        .flatten()
}

fn econ_glove_seed(item: &ParsedEconItem) -> Option<(EconGloveKey, u32)> {
    let item_def_index = i32::try_from(item.item_def_index?).ok()?;
    if !valid_glove_item_def_index(item_def_index) {
        return None;
    }
    let paint_kit = item.paint_kit.filter(|value| valid_paint_kit(*value))?;
    let seed = item.paint_seed?;
    let wear_bits = item.paint_wear_raw?;
    let wear = f32::from_bits(wear_bits);
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    Some((
        EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        },
        seed,
    ))
}

/// Normalize a single appearance after the caller establishes ownership. Uses
/// the shared output model with inspect left unset; final consumers encode links.
pub(crate) fn inventory_item_cosmetic_evidence(
    item: &ParsedInventoryWeaponCosmetic,
) -> Option<ReplayWeaponCosmetic> {
    let weapon_def_index = normalize_weapon_def_index(item.item_def_index);
    // The evidence collector establishes ownership from a purchase, account
    // ID, or original owner before calling this appearance-only conversion.
    if !is_weapon_cosmetic_def_index(weapon_def_index) {
        return None;
    }
    let spec = inventory_cosmetic_paint_spec(item)?;
    if !valid_weapon_cosmetic_paint(weapon_def_index, spec.paint_kit) {
        return None;
    }

    let stickers = cosmetic_sticker_set_from_slice(&item.stickers)
        .unwrap_or_default()
        .into_iter()
        .map(Into::into)
        .collect();
    let charms = cosmetic_charm_set_from_attributes(&item.attributes)
        .unwrap_or_default()
        .into_iter()
        .map(Into::into)
        .collect();
    let cosmetic = ReplayWeaponCosmetic {
        weapon_def_index,
        paint_kit: spec.paint_kit,
        seed: spec.seed,
        wear: f32::from_bits(spec.wear_bits),
        quality: (item.entity_quality == Some(9)).then_some(9),
        stattrak_counter: inventory_stattrak_counter(item),
        original_owner_steam_id: item.original_owner_xuid.filter(|value| *value != 0),
        item_account_id: item.item_account_id.filter(|value| *value != 0),
        item_id: combine_item_id(item.item_id_high, item.item_id_low).filter(|value| *value != 0),
        custom_name: cosmetic_custom_name_value(item.custom_name.as_deref()),
        stickers,
        charms,
        inspect: None,
    };
    Some(cosmetic)
}

fn inventory_cosmetic_paint_spec(
    item: &ParsedInventoryWeaponCosmetic,
) -> Option<CosmeticPaintSpec> {
    cosmetic_paint_spec(
        Some(item.paint_kit),
        Some(item.paint_seed),
        Some(item.paint_wear),
    )
}

fn inventory_stattrak_counter(item: &ParsedInventoryWeaponCosmetic) -> Option<i32> {
    if let Some(counter) = item.stattrak_counter.filter(|counter| *counter >= 0) {
        return Some(counter);
    }
    if item.entity_quality != Some(9) {
        return None;
    }
    item.attributes
        .iter()
        .find(|attribute| attribute.definition_index == 80)
        .and_then(|attribute| i32::try_from(attribute.raw_value_bits).ok())
}

fn has_trusted_inventory_weapon_cosmetic_identity(item: &ParsedInventoryWeaponCosmetic) -> bool {
    combine_item_id(item.item_id_high, item.item_id_low).is_some_and(|value| value != 0)
        || item.item_account_id.is_some_and(|value| value > 1)
}

fn has_trusted_active_weapon_cosmetic_identity(row: &ParsedPlayerTick) -> bool {
    row.active_weapon_item_id.is_some_and(|value| value != 0)
        || row
            .active_weapon_item_account_id
            .is_some_and(|value| value > 1)
}

pub(crate) fn active_cosmetic_owned_by(row: &ParsedPlayerTick) -> bool {
    let account_id = row
        .steam_id
        .checked_sub(STEAM_ID64_BASE)
        .and_then(|value| u32::try_from(value).ok());
    row.active_weapon_item_account_id
        .zip(account_id)
        .is_some_and(|(actual, expected)| actual == expected)
        || row.active_weapon_original_owner_steam_id == Some(row.steam_id)
}

fn active_cosmetic_custom_name(row: &ParsedPlayerTick) -> Option<String> {
    cosmetic_custom_name_value(row.active_weapon_custom_name.as_deref())
}

fn cosmetic_custom_name_value(value: Option<&str>) -> Option<String> {
    let cleaned = value?
        .trim()
        .chars()
        .filter(|ch| !ch.is_control() || *ch == '\t')
        .take(128)
        .collect::<String>();
    let cleaned = cleaned.trim();
    (!cleaned.is_empty()).then(|| cleaned.to_string())
}

pub(crate) fn combine_item_id(high: Option<u32>, low: Option<u32>) -> Option<u64> {
    Some((u64::from(high?) << 32) | u64::from(low?))
}

fn cosmetic_sticker_set_from_slice(
    stickers: &[ParsedWeaponSticker],
) -> Option<Vec<CosmeticStickerSpec>> {
    if stickers.is_empty() {
        return None;
    }

    let mut slots = BTreeSet::new();
    let mut parsed = Vec::with_capacity(stickers.len());
    for sticker in stickers {
        if sticker.slot > 4
            || sticker.sticker_id == 0
            || !valid_sticker_id(sticker.sticker_id)
            || !sticker.wear.is_finite()
            || !(0.0..=1.0).contains(&sticker.wear)
            || !sticker.offset_x.is_finite()
            || !sticker.offset_y.is_finite()
            || sticker.scale.is_some_and(|value| !value.is_finite())
            || sticker.rotation.is_some_and(|value| !value.is_finite())
            || !slots.insert(sticker.slot)
        {
            return None;
        }
        parsed.push(CosmeticStickerSpec {
            slot: sticker.slot,
            sticker_id: sticker.sticker_id,
            wear_bits: sticker.wear.to_bits(),
            offset_x_bits: sticker.offset_x.to_bits(),
            offset_y_bits: sticker.offset_y.to_bits(),
            scale_bits: sticker.scale.map(f32::to_bits),
            rotation_bits: sticker.rotation.map(f32::to_bits),
        });
    }
    parsed.sort();
    (!parsed.is_empty()).then_some(parsed)
}

fn cosmetic_charm_set_from_attributes(
    attributes: &[ParsedInventoryWeaponAttribute],
) -> Option<Vec<CosmeticCharmSpec>> {
    let charm_id = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_ID_ATTR)
        .filter(|id| valid_keychain_id(*id))?;
    let offset_x = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_X_ATTR)?;
    let offset_y = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_Y_ATTR)?;
    let offset_z = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_Z_ATTR)?;
    if !offset_x.is_finite() || !offset_y.is_finite() || !offset_z.is_finite() {
        return None;
    }

    let seed = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_SEED_ATTR);
    let highlight = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_HIGHLIGHT_ATTR)
        .filter(|value| *value > 0);
    let sticker_id = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_STICKER_ATTR)
        .filter(|value| valid_sticker_id(*value));
    Some(vec![CosmeticCharmSpec {
        slot: 0,
        charm_id,
        offset_x_bits: offset_x.to_bits(),
        offset_y_bits: offset_y.to_bits(),
        offset_z_bits: offset_z.to_bits(),
        seed,
        highlight,
        sticker_id,
    }])
}

fn inventory_attribute_u32(
    attributes: &[ParsedInventoryWeaponAttribute],
    definition_index: u32,
) -> Option<u32> {
    attributes
        .iter()
        .find(|attribute| attribute.definition_index == definition_index)
        .map(|attribute| attribute.raw_value_bits)
}

fn inventory_attribute_f32(
    attributes: &[ParsedInventoryWeaponAttribute],
    definition_index: u32,
) -> Option<f32> {
    attributes
        .iter()
        .find(|attribute| attribute.definition_index == definition_index)
        .map(|attribute| attribute.raw_value)
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticPaintSpec {
    paint_kit: u32,
    seed: u32,
    wear_bits: u32,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticStickerSpec {
    slot: u8,
    sticker_id: u32,
    wear_bits: u32,
    offset_x_bits: u32,
    offset_y_bits: u32,
    scale_bits: Option<u32>,
    rotation_bits: Option<u32>,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticCharmSpec {
    slot: u8,
    charm_id: u32,
    offset_x_bits: u32,
    offset_y_bits: u32,
    offset_z_bits: u32,
    seed: Option<u32>,
    highlight: Option<u32>,
    sticker_id: Option<u32>,
}

fn cosmetic_paint_spec(
    paint_kit: Option<u32>,
    seed: Option<u32>,
    wear: Option<f32>,
) -> Option<CosmeticPaintSpec> {
    let paint_kit = paint_kit.filter(|value| valid_paint_kit(*value))?;
    let seed = seed?;
    let wear = wear?;
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    Some(CosmeticPaintSpec {
        paint_kit,
        seed,
        wear_bits: wear.to_bits(),
    })
}

impl From<CosmeticStickerSpec> for ReplayWeaponSticker {
    fn from(sticker: CosmeticStickerSpec) -> Self {
        ReplayWeaponSticker {
            slot: sticker.slot,
            sticker_id: sticker.sticker_id,
            wear: f32::from_bits(sticker.wear_bits),
            offset_x: f32::from_bits(sticker.offset_x_bits),
            offset_y: f32::from_bits(sticker.offset_y_bits),
            scale: sticker.scale_bits.map(f32::from_bits),
            rotation: sticker.rotation_bits.map(f32::from_bits),
        }
    }
}

impl From<CosmeticCharmSpec> for ReplayWeaponCharm {
    fn from(charm: CosmeticCharmSpec) -> Self {
        ReplayWeaponCharm {
            slot: charm.slot,
            charm_id: charm.charm_id,
            offset_x: f32::from_bits(charm.offset_x_bits),
            offset_y: f32::from_bits(charm.offset_y_bits),
            offset_z: f32::from_bits(charm.offset_z_bits),
            seed: charm.seed,
            highlight: charm.highlight,
            sticker_id: charm.sticker_id,
        }
    }
}
