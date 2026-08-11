/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::export::{
    glove_econ_seed_index, inventory_item_cosmetic_evidence, is_knife_cosmetic_def_index,
    knife_econ_paint_index, matching_active_econ_knife_paint, matching_econ_glove_seed,
    valid_music_kit_evidence_id, EconGloveSeedIndex,
};
use crate::inspect_link::item_inspect;
use crate::model::{
    ParsedDemo, ParsedInventoryWeaponCosmetic, ParsedPlayerTick, ReplayItemCosmetic,
    ReplayWeaponCharm, ReplayWeaponSticker,
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write as _;

const STEAM_ID64_BASE: u64 = 76_561_197_960_265_728;

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserPlayerDetails {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub headshot_kills: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub total_damage: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stats_rounds: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_kills: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_deaths: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub two_k_rounds: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub three_k_rounds: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub four_k_rounds: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub five_k_rounds: Option<u32>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub crosshair_codes: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub viewmodels: Vec<BrowserViewmodelEvidence>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub cosmetics: Vec<BrowserCosmeticEvidence>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub music_kit_ids: Vec<u32>,
}

impl BrowserPlayerDetails {
    pub fn is_empty(&self) -> bool {
        self.headshot_kills.is_none()
            && self.total_damage.is_none()
            && self.stats_rounds.is_none()
            && self.first_kills.is_none()
            && self.first_deaths.is_none()
            && self.two_k_rounds.is_none()
            && self.three_k_rounds.is_none()
            && self.four_k_rounds.is_none()
            && self.five_k_rounds.is_none()
            && self.crosshair_codes.is_empty()
            && self.viewmodels.is_empty()
            && self.cosmetics.is_empty()
            && self.music_kit_ids.is_empty()
    }

    pub fn restrict_cosmetic_export(
        &mut self,
        export_cosmetics: bool,
        export_stickers: bool,
        export_charms: bool,
    ) {
        if !export_cosmetics {
            self.cosmetics.clear();
            self.music_kit_ids.clear();
            return;
        }
        for cosmetic in &mut self.cosmetics {
            let removed_inspect_evidence = (!export_stickers && !cosmetic.stickers.is_empty())
                || (!export_charms && !cosmetic.charms.is_empty());
            if !export_stickers {
                cosmetic.stickers.clear();
            }
            if !export_charms {
                cosmetic.charms.clear();
            }
            if removed_inspect_evidence {
                cosmetic.inspect_command = None;
                cosmetic.inspect_url = None;
            }
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserViewmodelEvidence {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fov: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_x: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_y: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_z: Option<f32>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserCosmeticEvidence {
    pub kind: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub side: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_def_index: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub paint_kit: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub finish_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seed: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wear: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quality: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stattrak_counter: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub original_owner_steam_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_account_id: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_name: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stickers: Vec<BrowserStickerEvidence>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub charms: Vec<BrowserCharmEvidence>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inspect_command: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inspect_url: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserStickerEvidence {
    pub slot: u8,
    pub sticker_id: u32,
    pub wear: f32,
    pub offset_x: f32,
    pub offset_y: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scale: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rotation: Option<f32>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserCharmEvidence {
    pub slot: u8,
    pub charm_id: u32,
    pub offset_x: f32,
    pub offset_y: f32,
    pub offset_z: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seed: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub highlight: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sticker_id: Option<u32>,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct ViewmodelKey {
    fov: Option<u32>,
    offset_x: Option<u32>,
    offset_y: Option<u32>,
    offset_z: Option<u32>,
}

impl ViewmodelKey {
    fn from_row(row: &ParsedPlayerTick) -> Option<Self> {
        let value = Self {
            fov: finite_bits(row.viewmodel_fov),
            offset_x: finite_bits(row.viewmodel_offset_x),
            offset_y: finite_bits(row.viewmodel_offset_y),
            offset_z: finite_bits(row.viewmodel_offset_z),
        };
        (value.completeness() > 0).then_some(value)
    }

    fn completeness(self) -> usize {
        [self.fov, self.offset_x, self.offset_y, self.offset_z]
            .into_iter()
            .flatten()
            .count()
    }

    fn into_evidence(self) -> BrowserViewmodelEvidence {
        BrowserViewmodelEvidence {
            fov: self.fov.map(f32::from_bits),
            offset_x: self.offset_x.map(f32::from_bits),
            offset_y: self.offset_y.map(f32::from_bits),
            offset_z: self.offset_z.map(f32::from_bits),
        }
    }
}

#[derive(Clone)]
struct ObservedInventoryItem {
    item: ParsedInventoryWeaponCosmetic,
    sides: BTreeSet<u8>,
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
enum InventoryItemIdentity {
    ItemId(u64),
    Spec(String),
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct ItemSpec {
    item_def_index: i32,
    paint_kit: u32,
    seed: u32,
    wear_bits: u32,
    custom_name: Option<String>,
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct GloveSpec {
    item_def_index: i32,
    paint_kit: u32,
    seed: Option<u32>,
    wear_bits: u32,
}

#[derive(Clone)]
struct ObservedAgent {
    item_def_index: u32,
    name: String,
}

#[derive(Clone, Copy)]
struct StatsSnapshot {
    tick: i32,
    headshot_kills: Option<u32>,
    total_damage: Option<u32>,
}

#[derive(Default)]
struct EvidenceAccumulator {
    rounds: BTreeSet<u32>,
    crosshair_codes: BTreeSet<String>,
    viewmodels: BTreeSet<ViewmodelKey>,
    inventory_items: BTreeMap<(u8, i32), ParsedInventoryWeaponCosmetic>,
    knives: BTreeMap<u8, ItemSpec>,
    gloves: BTreeMap<u8, GloveSpec>,
    agents: BTreeMap<u8, ObservedAgent>,
    music_kit_ids: BTreeSet<u32>,
    stats: Option<StatsSnapshot>,
}

pub(super) fn summarize_player_details(
    parsed: &ParsedDemo,
    match_window: Option<(i32, i32)>,
    stats_rounds: Option<u32>,
) -> BTreeMap<u64, BrowserPlayerDetails> {
    let mut econ_glove_seeds = None;
    let econ_knife_paints = knife_econ_paint_index(parsed);
    let mut accumulators = BTreeMap::<u64, EvidenceAccumulator>::new();
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && match_window
                .is_none_or(|(start_tick, end_tick)| row.tick >= start_tick && row.tick <= end_tick)
    }) {
        let accumulator = accumulators.entry(row.steam_id).or_default();
        accumulator.rounds.insert(row.round);
        update_stats(accumulator, row);
        if let Some(music_kit_id) = row
            .music_kit_id
            .filter(|value| valid_music_kit_evidence_id(*value))
        {
            accumulator.music_kit_ids.insert(music_kit_id);
        }
        if !row.is_alive {
            continue;
        }
        if let Some(code) = row
            .crosshair_code
            .as_deref()
            .map(str::trim)
            .filter(|code| code.starts_with("CSGO-") && code.len() > 5)
        {
            if !accumulator.crosshair_codes.contains(code) {
                accumulator.crosshair_codes.insert(code.to_string());
            }
        }
        if let Some(viewmodel) = ViewmodelKey::from_row(row) {
            accumulator.viewmodels.insert(viewmodel);
        }

        let side = row.team_num;
        for item in row.inventory_weapon_cosmetics.iter() {
            if !inventory_item_owned_by(item, row.steam_id) {
                continue;
            }
            let slot = (side, item.item_def_index);
            if accumulator.inventory_items.contains_key(&slot) {
                continue;
            }
            if inventory_item_cosmetic_evidence(item).is_some() {
                accumulator.inventory_items.insert(slot, item.clone());
            }
        }

        if is_knife(row.item_def_idx) {
            let active_spec = active_item_owned_by(row)
                .then(|| active_item_spec(row))
                .flatten();
            let econ_spec =
                matching_active_econ_knife_paint(econ_knife_paints.get(&row.steam_id), row).map(
                    |paint| ItemSpec {
                        item_def_index: row.item_def_idx,
                        paint_kit: paint.paint_kit,
                        seed: paint.seed,
                        wear_bits: paint.wear_bits,
                        custom_name: paint.custom_name,
                    },
                );
            let spec = match (active_spec, econ_spec) {
                (Some(mut active), Some(econ))
                    if active.item_def_index == econ.item_def_index
                        && active.paint_kit == econ.paint_kit
                        && active.seed == econ.seed
                        && active.wear_bits == econ.wear_bits =>
                {
                    active.custom_name = active.custom_name.or(econ.custom_name);
                    Some(active)
                }
                (Some(active), _) => Some(active),
                (None, econ) => econ,
            };
            if let Some(spec) = spec {
                remember_stable_side_item(&mut accumulator.knives, side, spec);
            }
        }
        observe_glove(
            &mut accumulator.gloves,
            side,
            row,
            parsed,
            &mut econ_glove_seeds,
        );
        if let (Some(item_def_index), Some(name)) = (
            row.agent_item_def_index.filter(|value| *value != 0),
            row.agent_skin
                .as_deref()
                .map(str::trim)
                .filter(|name| !name.is_empty() && !is_map_based_default_agent(name)),
        ) {
            accumulator
                .agents
                .entry(side)
                .or_insert_with(|| ObservedAgent {
                    item_def_index,
                    name: name.to_string(),
                });
        }
    }

    accumulators
        .into_iter()
        .filter_map(|(steam_id, accumulator)| {
            let details = finish_details(parsed, steam_id, accumulator, stats_rounds);
            (!details.is_empty()).then_some((steam_id, details))
        })
        .collect()
}

fn update_stats(accumulator: &mut EvidenceAccumulator, row: &ParsedPlayerTick) {
    if row.scoreboard_headshot_kills.is_none() && row.scoreboard_damage.is_none() {
        return;
    }
    let mut candidate = StatsSnapshot {
        tick: row.tick,
        headshot_kills: row.scoreboard_headshot_kills,
        total_damage: row.scoreboard_damage,
    };
    if let Some(current) = accumulator.stats {
        if candidate.tick < current.tick {
            return;
        }
        candidate.headshot_kills = candidate.headshot_kills.or(current.headshot_kills);
        candidate.total_damage = candidate.total_damage.or(current.total_damage);
    }
    accumulator.stats = Some(candidate);
}

fn finish_details(
    parsed: &ParsedDemo,
    steam_id: u64,
    accumulator: EvidenceAccumulator,
    stats_rounds: Option<u32>,
) -> BrowserPlayerDetails {
    let max_viewmodel_fields = accumulator
        .viewmodels
        .iter()
        .map(|value| value.completeness())
        .max()
        .unwrap_or_default();
    let mut cosmetics = Vec::new();

    for observed in stable_inventory_items_by_identity(accumulator.inventory_items).into_values() {
        let Some(cosmetic) = inventory_item_cosmetic_evidence(&observed.item) else {
            continue;
        };
        let (item_name, finish_name) = cosmetic_names(
            parsed,
            steam_id,
            cosmetic.weapon_def_index,
            cosmetic.paint_kit,
        );
        cosmetics.push(BrowserCosmeticEvidence {
            kind: "weapon".to_string(),
            side: summarized_side(&observed.sides),
            item_def_index: Some(cosmetic.weapon_def_index),
            item_name: item_name.or_else(|| weapon_display_name(cosmetic.weapon_def_index)),
            paint_kit: Some(cosmetic.paint_kit),
            finish_name,
            seed: Some(cosmetic.seed),
            wear: Some(cosmetic.wear),
            quality: cosmetic.quality,
            stattrak_counter: cosmetic.stattrak_counter,
            original_owner_steam_id: cosmetic
                .original_owner_steam_id
                .map(|value| value.to_string()),
            item_account_id: cosmetic.item_account_id,
            item_id: cosmetic.item_id.map(|value| value.to_string()),
            custom_name: cosmetic.custom_name,
            stickers: cosmetic
                .stickers
                .into_iter()
                .map(sticker_evidence)
                .collect(),
            charms: cosmetic.charms.into_iter().map(charm_evidence).collect(),
            inspect_command: cosmetic.inspect.as_ref().map(|value| value.command.clone()),
            inspect_url: cosmetic.inspect.and_then(|value| value.steam_url),
        });
    }

    for (spec, sides) in stable_items_by_spec(accumulator.knives) {
        let mut cosmetic = ReplayItemCosmetic {
            item_def_index: Some(spec.item_def_index),
            paint_kit: spec.paint_kit,
            seed: spec.seed,
            seed_known: None,
            wear: f32::from_bits(spec.wear_bits),
            custom_name: spec.custom_name,
            inspect: None,
        };
        cosmetic.inspect = item_inspect(&cosmetic, Some(6));
        let (item_name, finish_name) =
            cosmetic_names(parsed, steam_id, spec.item_def_index, spec.paint_kit);
        cosmetics.push(BrowserCosmeticEvidence {
            kind: "knife".to_string(),
            side: summarized_side(&sides),
            item_def_index: Some(spec.item_def_index),
            item_name: item_name.or_else(|| weapon_display_name(spec.item_def_index)),
            paint_kit: Some(spec.paint_kit),
            finish_name,
            seed: Some(spec.seed),
            wear: Some(f32::from_bits(spec.wear_bits)),
            quality: None,
            stattrak_counter: None,
            original_owner_steam_id: None,
            item_account_id: None,
            item_id: None,
            custom_name: cosmetic.custom_name,
            stickers: Vec::new(),
            charms: Vec::new(),
            inspect_command: cosmetic.inspect.as_ref().map(|value| value.command.clone()),
            inspect_url: cosmetic.inspect.and_then(|value| value.steam_url),
        });
    }

    for (spec, sides) in stable_gloves_by_spec(accumulator.gloves) {
        let wear = f32::from_bits(spec.wear_bits);
        let inspect = spec.seed.and_then(|seed| {
            let cosmetic = ReplayItemCosmetic {
                item_def_index: Some(spec.item_def_index),
                paint_kit: spec.paint_kit,
                seed,
                seed_known: None,
                wear,
                custom_name: None,
                inspect: None,
            };
            item_inspect(&cosmetic, Some(6))
        });
        let (item_name, finish_name) =
            cosmetic_names(parsed, steam_id, spec.item_def_index, spec.paint_kit);
        cosmetics.push(BrowserCosmeticEvidence {
            kind: "glove".to_string(),
            side: summarized_side(&sides),
            item_def_index: Some(spec.item_def_index),
            item_name: item_name.or_else(|| weapon_display_name(spec.item_def_index)),
            paint_kit: Some(spec.paint_kit),
            finish_name,
            seed: spec.seed,
            wear: Some(wear),
            quality: None,
            stattrak_counter: None,
            original_owner_steam_id: None,
            item_account_id: None,
            item_id: None,
            custom_name: None,
            stickers: Vec::new(),
            charms: Vec::new(),
            inspect_command: inspect.as_ref().map(|value| value.command.clone()),
            inspect_url: inspect.and_then(|value| value.steam_url),
        });
    }

    for ((item_def_index, name), sides) in stable_agents_by_spec(accumulator.agents) {
        cosmetics.push(BrowserCosmeticEvidence {
            kind: "agent".to_string(),
            side: summarized_side(&sides),
            item_def_index: i32::try_from(item_def_index).ok(),
            item_name: Some(name),
            paint_kit: None,
            finish_name: None,
            seed: None,
            wear: None,
            quality: None,
            stattrak_counter: None,
            original_owner_steam_id: None,
            item_account_id: None,
            item_id: None,
            custom_name: None,
            stickers: Vec::new(),
            charms: Vec::new(),
            inspect_command: None,
            inspect_url: None,
        });
    }

    cosmetics.sort_by(|left, right| {
        (
            kind_rank(&left.kind),
            &left.item_name,
            left.item_def_index,
            left.paint_kit,
            &left.side,
        )
            .cmp(&(
                kind_rank(&right.kind),
                &right.item_name,
                right.item_def_index,
                right.paint_kit,
                &right.side,
            ))
    });

    BrowserPlayerDetails {
        headshot_kills: accumulator.stats.and_then(|value| value.headshot_kills),
        total_damage: accumulator.stats.and_then(|value| value.total_damage),
        stats_rounds: accumulator.stats.and_then(|value| value.total_damage).and(
            stats_rounds.filter(|value| {
                *value > 0 && usize::try_from(*value) == Ok(accumulator.rounds.len())
            }),
        ),
        first_kills: None,
        first_deaths: None,
        two_k_rounds: None,
        three_k_rounds: None,
        four_k_rounds: None,
        five_k_rounds: None,
        crosshair_codes: accumulator.crosshair_codes.into_iter().collect(),
        viewmodels: accumulator
            .viewmodels
            .into_iter()
            .filter(|value| value.completeness() == max_viewmodel_fields)
            .map(ViewmodelKey::into_evidence)
            .collect(),
        cosmetics,
        music_kit_ids: accumulator.music_kit_ids.into_iter().collect(),
    }
}

fn sticker_evidence(sticker: ReplayWeaponSticker) -> BrowserStickerEvidence {
    BrowserStickerEvidence {
        slot: sticker.slot,
        sticker_id: sticker.sticker_id,
        wear: sticker.wear,
        offset_x: sticker.offset_x,
        offset_y: sticker.offset_y,
        scale: sticker.scale,
        rotation: sticker.rotation,
    }
}

fn charm_evidence(charm: ReplayWeaponCharm) -> BrowserCharmEvidence {
    BrowserCharmEvidence {
        slot: charm.slot,
        charm_id: charm.charm_id,
        offset_x: charm.offset_x,
        offset_y: charm.offset_y,
        offset_z: charm.offset_z,
        seed: charm.seed,
        highlight: charm.highlight,
        sticker_id: charm.sticker_id,
    }
}

fn inventory_item_owned_by(item: &ParsedInventoryWeaponCosmetic, steam_id: u64) -> bool {
    let account_id = steam_id
        .checked_sub(STEAM_ID64_BASE)
        .and_then(|value| u32::try_from(value).ok());
    item.item_account_id
        .zip(account_id)
        .is_some_and(|(actual, expected)| actual == expected)
        || item.original_owner_xuid == Some(steam_id)
}

fn active_item_owned_by(row: &ParsedPlayerTick) -> bool {
    let account_id = row
        .steam_id
        .checked_sub(STEAM_ID64_BASE)
        .and_then(|value| u32::try_from(value).ok());
    row.active_weapon_item_account_id
        .zip(account_id)
        .is_some_and(|(actual, expected)| actual == expected)
        || row.active_weapon_original_owner_steam_id == Some(row.steam_id)
}

fn inventory_item_identity(item: &ParsedInventoryWeaponCosmetic) -> InventoryItemIdentity {
    if let Some(item_id) =
        combine_item_id(item.item_id_high, item.item_id_low).filter(|value| *value != 0)
    {
        return InventoryItemIdentity::ItemId(item_id);
    }
    let mut key = format!(
        "spec:{}:{}:{}:{}:{:?}:{:?}:{:?}:{:?}",
        item.item_def_index,
        item.paint_kit,
        item.paint_seed,
        item.paint_wear.to_bits(),
        item.entity_quality,
        item.original_owner_xuid,
        item.item_account_id,
        item.custom_name
    );
    for sticker in &item.stickers {
        let _ = write!(
            key,
            "|s:{}:{}:{}:{}:{}:{:?}:{:?}",
            sticker.slot,
            sticker.sticker_id,
            sticker.wear.to_bits(),
            sticker.offset_x.to_bits(),
            sticker.offset_y.to_bits(),
            sticker.scale.map(f32::to_bits),
            sticker.rotation.map(f32::to_bits)
        );
    }
    for attribute in &item.attributes {
        if attribute.definition_index == 80 {
            continue;
        }
        let _ = write!(
            key,
            "|a:{}:{}",
            attribute.definition_index, attribute.raw_value_bits
        );
    }
    InventoryItemIdentity::Spec(key)
}

fn stable_inventory_items_by_identity(
    items: BTreeMap<(u8, i32), ParsedInventoryWeaponCosmetic>,
) -> BTreeMap<InventoryItemIdentity, ObservedInventoryItem> {
    let mut by_identity = BTreeMap::<InventoryItemIdentity, ObservedInventoryItem>::new();
    for ((side, _), item) in items {
        let identity = inventory_item_identity(&item);
        by_identity
            .entry(identity)
            .or_insert_with(|| ObservedInventoryItem {
                item,
                sides: BTreeSet::new(),
            })
            .sides
            .insert(side);
    }
    by_identity
}

fn stable_agents_by_spec(
    agents: BTreeMap<u8, ObservedAgent>,
) -> BTreeMap<(u32, String), BTreeSet<u8>> {
    let mut by_spec = BTreeMap::<(u32, String), BTreeSet<u8>>::new();
    for (side, agent) in agents {
        by_spec
            .entry((agent.item_def_index, agent.name))
            .or_default()
            .insert(side);
    }
    by_spec
}

fn active_item_spec(row: &ParsedPlayerTick) -> Option<ItemSpec> {
    let paint_kit = row.active_weapon_paint_kit.filter(|value| *value != 0)?;
    let seed = row.active_weapon_paint_seed?;
    let wear = row
        .active_weapon_paint_wear
        .filter(|value| value.is_finite() && (0.0..=1.0).contains(value))?;
    Some(ItemSpec {
        item_def_index: row.item_def_idx,
        paint_kit,
        seed,
        wear_bits: normalized_f32_bits(wear),
        custom_name: row
            .active_weapon_custom_name
            .as_deref()
            .and_then(clean_name),
    })
}

fn glove_spec(row: &ParsedPlayerTick) -> Option<GloveSpec> {
    let item_def_index = row.glove_item_def_index.filter(|value| is_glove(*value))?;
    let paint_kit = row.glove_paint_kit.filter(|value| *value != 0)?;
    let wear = row
        .glove_paint_wear
        .filter(|value| value.is_finite() && (0.0..=1.0).contains(value))?;
    Some(GloveSpec {
        item_def_index,
        paint_kit,
        seed: row.glove_paint_seed,
        wear_bits: normalized_f32_bits(wear),
    })
}

fn observe_glove(
    gloves: &mut BTreeMap<u8, GloveSpec>,
    side: u8,
    row: &ParsedPlayerTick,
    parsed: &ParsedDemo,
    econ_glove_seeds: &mut Option<EconGloveSeedIndex>,
) {
    if gloves.get(&side).is_some_and(|spec| spec.seed.is_some()) {
        return;
    }
    let Some(mut candidate) = glove_spec(row) else {
        return;
    };
    if candidate.seed.is_none() {
        let seeds = econ_glove_seeds.get_or_insert_with(|| glove_econ_seed_index(parsed));
        candidate.seed = matching_econ_glove_seed(
            seeds.get(&row.steam_id),
            candidate.item_def_index,
            candidate.paint_kit,
            candidate.wear_bits,
        );
    }
    match gloves.entry(side) {
        std::collections::btree_map::Entry::Vacant(entry) => {
            entry.insert(candidate);
        }
        std::collections::btree_map::Entry::Occupied(mut entry) => {
            let current = entry.get_mut();
            if current.seed.is_none()
                && candidate.seed.is_some()
                && current.item_def_index == candidate.item_def_index
                && current.paint_kit == candidate.paint_kit
                && current.wear_bits == candidate.wear_bits
            {
                current.seed = candidate.seed;
            }
        }
    }
}

fn remember_stable_side_item(items: &mut BTreeMap<u8, ItemSpec>, side: u8, candidate: ItemSpec) {
    match items.entry(side) {
        std::collections::btree_map::Entry::Vacant(entry) => {
            entry.insert(candidate);
        }
        std::collections::btree_map::Entry::Occupied(mut entry) => {
            let current = entry.get();
            let same_item = current.item_def_index == candidate.item_def_index
                && current.paint_kit == candidate.paint_kit
                && current.seed == candidate.seed
                && current.wear_bits == candidate.wear_bits;
            if same_item && current.custom_name.is_none() && candidate.custom_name.is_some() {
                entry.insert(candidate);
            }
        }
    }
}

fn stable_items_by_spec(items: BTreeMap<u8, ItemSpec>) -> BTreeMap<ItemSpec, BTreeSet<u8>> {
    let mut by_spec = BTreeMap::<ItemSpec, BTreeSet<u8>>::new();
    for (side, spec) in items {
        by_spec.entry(spec).or_default().insert(side);
    }
    by_spec
}

fn stable_gloves_by_spec(items: BTreeMap<u8, GloveSpec>) -> BTreeMap<GloveSpec, BTreeSet<u8>> {
    let mut by_spec = BTreeMap::<GloveSpec, BTreeSet<u8>>::new();
    for (side, spec) in items {
        by_spec.entry(spec).or_default().insert(side);
    }
    by_spec
}

fn cosmetic_names(
    parsed: &ParsedDemo,
    steam_id: u64,
    item_def_index: i32,
    paint_kit: u32,
) -> (Option<String>, Option<String>) {
    let mut item_names = BTreeSet::new();
    let mut finish_names = BTreeSet::new();
    for item in &parsed.econ_items {
        if item.steam_id != Some(steam_id)
            || item
                .item_def_index
                .and_then(|value| i32::try_from(value).ok())
                != Some(item_def_index)
            || item.paint_kit != Some(paint_kit)
        {
            continue;
        }
        if let Some(name) = item.item_name.as_deref().and_then(clean_name) {
            if !name.starts_with('#') && !name.starts_with("weapon_") {
                item_names.insert(name);
            }
        }
        if let Some(name) = item.skin_name.as_deref().and_then(clean_name) {
            if !name.starts_with('#') {
                finish_names.insert(name);
            }
        }
    }
    (single_value(item_names), single_value(finish_names))
}

fn single_value(mut values: BTreeSet<String>) -> Option<String> {
    (values.len() == 1).then(|| values.pop_first()).flatten()
}

fn clean_name(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty()).then(|| value.to_string())
}

fn is_map_based_default_agent(name: &str) -> bool {
    matches!(
        name,
        "customplayer_t_map_based" | "customplayer_ct_map_based"
    )
}

fn summarized_side(sides: &BTreeSet<u8>) -> Option<String> {
    (sides.len() == 1)
        .then(|| {
            sides
                .iter()
                .next()
                .map(|side| if *side == 2 { "t" } else { "ct" }.to_string())
        })
        .flatten()
}

fn finite_bits(value: Option<f32>) -> Option<u32> {
    value
        .filter(|value| value.is_finite())
        .map(normalized_f32_bits)
}

fn normalized_f32_bits(value: f32) -> u32 {
    (if value == 0.0 { 0.0 } else { value }).to_bits()
}

fn combine_item_id(high: Option<u32>, low: Option<u32>) -> Option<u64> {
    Some((u64::from(high?) << 32) | u64::from(low?))
}

fn is_knife(def: i32) -> bool {
    is_knife_cosmetic_def_index(def)
}

fn is_glove(def: i32) -> bool {
    matches!(def, 5027..=5035)
}

fn kind_rank(kind: &str) -> u8 {
    match kind {
        "knife" => 0,
        "glove" => 1,
        "weapon" => 2,
        "agent" => 3,
        _ => 4,
    }
}

fn weapon_display_name(def: i32) -> Option<String> {
    let name = match def {
        1 => "Desert Eagle",
        2 => "Dual Berettas",
        3 => "Five-SeveN",
        4 => "Glock-18",
        7 => "AK-47",
        8 => "AUG",
        9 => "AWP",
        10 => "FAMAS",
        11 => "G3SG1",
        13 => "Galil AR",
        14 => "M249",
        16 => "M4A4",
        17 => "MAC-10",
        19 => "P90",
        23 => "MP5-SD",
        24 => "UMP-45",
        25 => "XM1014",
        26 => "PP-Bizon",
        27 => "MAG-7",
        28 => "Negev",
        29 => "Sawed-Off",
        30 => "Tec-9",
        31 => "Zeus x27",
        32 => "P2000",
        33 => "MP7",
        34 => "MP9",
        35 => "Nova",
        36 => "P250",
        38 => "SCAR-20",
        39 => "SG 553",
        40 => "SSG 08",
        60 => "M4A1-S",
        61 => "USP-S",
        63 => "CZ75-Auto",
        64 => "R8 Revolver",
        500 => "Bayonet",
        503 => "Classic Knife",
        505 => "Flip Knife",
        506 => "Gut Knife",
        507 => "Karambit",
        508 => "M9 Bayonet",
        509 => "Huntsman Knife",
        512 => "Falchion Knife",
        514 => "Bowie Knife",
        515 => "Butterfly Knife",
        516 => "Shadow Daggers",
        517 => "Paracord Knife",
        518 => "Survival Knife",
        519 => "Ursus Knife",
        520 => "Navaja Knife",
        521 => "Nomad Knife",
        522 => "Stiletto Knife",
        523 => "Talon Knife",
        526 => "Kukri Knife",
        5027 => "Bloodhound Gloves",
        5030 => "Sport Gloves",
        5031 => "Driver Gloves",
        5032 => "Hand Wraps",
        5033 => "Moto Gloves",
        5034 => "Specialist Gloves",
        5035 => "Hydra Gloves",
        _ => return None,
    };
    Some(name.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{ParsedEconItem, ParsedWeaponSticker};

    fn inventory_item(account_id: u32, item_id: u32) -> ParsedInventoryWeaponCosmetic {
        ParsedInventoryWeaponCosmetic {
            item_def_index: 16,
            item_id_high: Some(1),
            item_id_low: Some(item_id),
            item_account_id: Some(account_id),
            paint_kit: 926,
            paint_seed: 42,
            paint_wear: 0.123,
            stickers: vec![ParsedWeaponSticker {
                slot: 0,
                sticker_id: 225,
                wear: 0.01,
                offset_x: 0.1,
                offset_y: -0.2,
                scale: Some(0.9),
                rotation: Some(12.5),
            }],
            ..ParsedInventoryWeaponCosmetic::default()
        }
    }

    fn player_row(
        steam_id: u64,
        tick: i32,
        team_num: u8,
        inventory_weapon_cosmetics: Vec<ParsedInventoryWeaponCosmetic>,
    ) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id,
            name: "player".to_string(),
            team_num,
            is_alive: true,
            round: 1,
            round_in_progress: true,
            crosshair_code: Some(
                if tick == 100 {
                    "CSGO-AAAAA"
                } else {
                    "CSGO-BBBBB"
                }
                .to_string(),
            ),
            viewmodel_fov: Some(68.0),
            viewmodel_offset_x: Some(2.5),
            viewmodel_offset_y: Some(0.0),
            viewmodel_offset_z: Some(-1.5),
            scoreboard_headshot_kills: Some(if tick == 100 { 2 } else { 3 }),
            scoreboard_damage: Some(if tick == 100 { 800 } else { 1_250 }),
            music_kit_id: Some(42),
            inventory_weapon_cosmetics: inventory_weapon_cosmetics.into(),
            ..ParsedPlayerTick::default()
        }
    }

    #[test]
    fn keeps_only_owned_cosmetics_and_merges_identical_side_evidence() {
        let account_id = 123;
        let steam_id = STEAM_ID64_BASE + u64::from(account_id);
        let owned = inventory_item(account_id, 7);
        let picked_up = inventory_item(account_id + 1, 8);
        let mut ignored_replacement = inventory_item(account_id, 9);
        ignored_replacement.paint_kit = 309;
        let parsed = ParsedDemo {
            rows: vec![
                player_row(steam_id, 100, 2, vec![owned.clone(), picked_up]),
                player_row(steam_id, 200, 3, vec![owned]),
                player_row(steam_id, 300, 2, vec![ignored_replacement]),
            ],
            econ_items: vec![ParsedEconItem {
                steam_id: Some(steam_id),
                item_def_index: Some(16),
                paint_kit: Some(926),
                item_name: Some("M4A4".to_string()),
                skin_name: Some("In Living Color".to_string()),
                ..ParsedEconItem::default()
            }],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        assert_eq!(details.headshot_kills, Some(3));
        assert_eq!(details.total_damage, Some(1_250));
        assert_eq!(details.stats_rounds, Some(1));
        assert_eq!(details.crosshair_codes, ["CSGO-AAAAA", "CSGO-BBBBB"]);
        assert_eq!(details.viewmodels.len(), 1);
        assert_eq!(details.music_kit_ids, [42]);
        assert_eq!(details.cosmetics.len(), 1);
        let cosmetic = &details.cosmetics[0];
        assert_eq!(cosmetic.side, None);
        assert_eq!(cosmetic.item_name.as_deref(), Some("M4A4"));
        assert_eq!(cosmetic.finish_name.as_deref(), Some("In Living Color"));
        assert_eq!(cosmetic.stickers[0].rotation, Some(12.5));
        assert!(cosmetic.inspect_command.is_some());

        let incomplete_participation = summarize_player_details(&parsed, None, Some(2))
            .remove(&steam_id)
            .expect("player evidence");
        assert_eq!(incomplete_participation.stats_rounds, None);
    }

    #[test]
    fn includes_owned_taser_cosmetic_evidence() {
        let account_id = 123;
        let steam_id = STEAM_ID64_BASE + u64::from(account_id);
        let mut zeus = inventory_item(account_id, 31);
        zeus.item_def_index = 31;
        zeus.paint_kit = 1183;
        zeus.paint_seed = 733;
        zeus.paint_wear = 0.139_071_45;
        let parsed = ParsedDemo {
            rows: vec![player_row(steam_id, 100, 2, vec![zeus])],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        assert_eq!(details.cosmetics.len(), 1);
        let cosmetic = &details.cosmetics[0];
        assert_eq!(cosmetic.kind, "weapon");
        assert_eq!(cosmetic.item_def_index, Some(31));
        assert_eq!(cosmetic.item_name.as_deref(), Some("Zeus x27"));
        assert_eq!(cosmetic.paint_kit, Some(1183));
        assert_eq!(cosmetic.seed, Some(733));
    }

    #[test]
    fn persisted_details_obey_cosmetic_and_attachment_export_gates() {
        let account_id = 123;
        let steam_id = STEAM_ID64_BASE + u64::from(account_id);
        let parsed = ParsedDemo {
            rows: vec![player_row(
                steam_id,
                100,
                2,
                vec![inventory_item(account_id, 7)],
            )],
            ..ParsedDemo::default()
        };
        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        let mut without_cosmetics = details.clone();
        without_cosmetics.restrict_cosmetic_export(false, false, false);
        assert!(without_cosmetics.cosmetics.is_empty());
        assert!(without_cosmetics.music_kit_ids.is_empty());

        let mut without_stickers = details;
        without_stickers.restrict_cosmetic_export(true, false, false);
        assert!(without_stickers.cosmetics[0].stickers.is_empty());
        assert!(without_stickers.cosmetics[0].inspect_command.is_none());
        assert!(without_stickers.cosmetics[0].inspect_url.is_none());
    }

    #[test]
    fn keeps_one_owned_knife_and_glove_per_side() {
        let account_id = 123;
        let steam_id = STEAM_ID64_BASE + u64::from(account_id);
        let mut t_side = player_row(steam_id, 100, 2, Vec::new());
        t_side.item_def_idx = 515;
        t_side.active_weapon_item_account_id = Some(account_id);
        t_side.active_weapon_paint_kit = Some(568);
        t_side.active_weapon_paint_seed = Some(142);
        t_side.active_weapon_paint_wear = Some(0.01);
        t_side.glove_item_def_index = Some(5032);
        t_side.glove_paint_kit = Some(10036);
        t_side.glove_paint_seed = Some(23);
        t_side.glove_paint_wear = Some(0.13);

        let mut ignored_t_replacement = t_side.clone();
        ignored_t_replacement.tick = 200;
        ignored_t_replacement.item_def_idx = 508;
        ignored_t_replacement.active_weapon_paint_kit = Some(38);
        ignored_t_replacement.glove_item_def_index = Some(5034);
        ignored_t_replacement.glove_paint_kit = Some(10033);

        let mut ct_side = player_row(steam_id, 300, 3, Vec::new());
        ct_side.item_def_idx = 507;
        ct_side.active_weapon_item_account_id = Some(account_id);
        ct_side.active_weapon_paint_kit = Some(12);
        ct_side.active_weapon_paint_seed = Some(7);
        ct_side.active_weapon_paint_wear = Some(0.2);
        ct_side.glove_item_def_index = Some(5034);
        ct_side.glove_paint_kit = Some(10033);
        ct_side.glove_paint_seed = Some(260);
        ct_side.glove_paint_wear = Some(0.2);

        let parsed = ParsedDemo {
            rows: vec![t_side, ignored_t_replacement, ct_side],
            ..ParsedDemo::default()
        };
        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");
        let equipment = details
            .cosmetics
            .iter()
            .filter(|cosmetic| matches!(cosmetic.kind.as_str(), "knife" | "glove"))
            .map(|cosmetic| {
                (
                    cosmetic.kind.as_str(),
                    cosmetic.side.as_deref(),
                    cosmetic.item_def_index,
                    cosmetic.paint_kit,
                )
            })
            .collect::<BTreeSet<_>>();

        assert_eq!(
            equipment,
            BTreeSet::from([
                ("knife", Some("t"), Some(515), Some(568)),
                ("knife", Some("ct"), Some(507), Some(12)),
                ("glove", Some("t"), Some(5032), Some(10036)),
                ("glove", Some("ct"), Some(5034), Some(10033)),
            ])
        );
    }

    #[test]
    fn omits_owned_default_knife_paint_artifacts() {
        let steam_id = 76561198169234291;
        let mut t_side = player_row(steam_id, 100, 2, Vec::new());
        t_side.item_def_idx = 59;
        t_side.active_weapon_paint_kit = Some(38);
        t_side.active_weapon_paint_seed = Some(774);
        t_side.active_weapon_paint_wear = Some(0.014_620_723);
        t_side.active_weapon_original_owner_steam_id = Some(steam_id);

        let mut ct_side = player_row(steam_id, 200, 3, Vec::new());
        ct_side.item_def_idx = 42;
        ct_side.active_weapon_paint_kit = Some(38);
        ct_side.active_weapon_paint_seed = Some(774);
        ct_side.active_weapon_paint_wear = Some(0.014_620_723);
        ct_side.active_weapon_original_owner_steam_id = Some(steam_id);

        let parsed = ParsedDemo {
            rows: vec![t_side, ct_side],
            ..ParsedDemo::default()
        };
        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        assert!(!details
            .cosmetics
            .iter()
            .any(|cosmetic| cosmetic.kind == "knife"));
    }

    #[test]
    fn player_evidence_recovers_player_scoped_econ_knife_when_live_econ_is_missing() {
        let naf_steam_id = 76561198001151695;
        let elige_steam_id = 76561198066693739;
        let wear = 0.031_718_593_f32;
        let elige_wear = 0.016_897_384_f32;
        let mut naf = player_row(naf_steam_id, 100, 2, Vec::new());
        naf.item_def_idx = 507;
        naf.active_weapon_paint_seed = Some(330);
        naf.active_weapon_paint_wear = Some(wear);
        naf.active_weapon_item_account_id = u32::try_from(naf_steam_id - STEAM_ID64_BASE).ok();
        let mut elige = player_row(elige_steam_id, 100, 2, Vec::new());
        elige.item_def_idx = 507;
        let parsed = ParsedDemo {
            rows: vec![naf, elige],
            econ_items: vec![
                ParsedEconItem {
                    steam_id: Some(naf_steam_id),
                    item_def_index: Some(507),
                    paint_kit: Some(570),
                    paint_seed: Some(330),
                    paint_wear_raw: Some(wear.to_bits()),
                    paint_wear: Some(wear),
                    ..ParsedEconItem::default()
                },
                ParsedEconItem {
                    steam_id: Some(elige_steam_id),
                    item_def_index: Some(507),
                    paint_kit: Some(570),
                    paint_seed: Some(80),
                    paint_wear_raw: Some(elige_wear.to_bits()),
                    paint_wear: Some(elige_wear),
                    custom_name: Some("player scoped knife".to_string()),
                    ..ParsedEconItem::default()
                },
            ],
            ..ParsedDemo::default()
        };
        let details = summarize_player_details(&parsed, None, Some(1));

        let naf_knife = details[&naf_steam_id]
            .cosmetics
            .iter()
            .find(|cosmetic| cosmetic.kind == "knife")
            .expect("NAF knife");
        assert_eq!(naf_knife.item_def_index, Some(507));
        assert_eq!(naf_knife.paint_kit, Some(570));
        assert_eq!(naf_knife.seed, Some(330));
        assert_eq!(naf_knife.wear.map(f32::to_bits), Some(wear.to_bits()));

        let elige_knife = details[&elige_steam_id]
            .cosmetics
            .iter()
            .find(|cosmetic| cosmetic.kind == "knife")
            .expect("player-scoped econ knife");
        assert_eq!(elige_knife.item_def_index, Some(507));
        assert_eq!(elige_knife.paint_kit, Some(570));
        assert_eq!(elige_knife.seed, Some(80));
        assert_eq!(
            elige_knife.wear.map(f32::to_bits),
            Some(elige_wear.to_bits())
        );
        assert_eq!(
            elige_knife.custom_name.as_deref(),
            Some("player scoped knife")
        );
    }

    #[test]
    fn keeps_glove_evidence_when_the_live_seed_is_missing() {
        let steam_id = STEAM_ID64_BASE + 123;
        let mut row = player_row(steam_id, 100, 2, Vec::new());
        row.glove_item_def_index = Some(5030);
        row.glove_paint_kit = Some(10038);
        row.glove_paint_seed = None;
        row.glove_paint_wear = Some(0.106_909_76);
        let parsed = ParsedDemo {
            rows: vec![row],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");
        let glove = details
            .cosmetics
            .iter()
            .find(|cosmetic| cosmetic.kind == "glove")
            .expect("glove evidence");

        assert_eq!(glove.item_def_index, Some(5030));
        assert_eq!(glove.paint_kit, Some(10038));
        assert_eq!(glove.seed, None);
        assert_eq!(glove.wear, Some(0.106_909_76));
        assert!(glove.inspect_command.is_none());
        assert!(glove.inspect_url.is_none());
        assert!(glove.custom_name.is_none());
    }

    #[test]
    fn fills_a_missing_live_glove_seed_only_from_an_exact_econ_match() {
        let steam_id = STEAM_ID64_BASE + 123;
        let wear = 0.106_909_76_f32;
        let mut row = player_row(steam_id, 100, 2, Vec::new());
        row.glove_item_def_index = Some(5030);
        row.glove_paint_kit = Some(10038);
        row.glove_paint_seed = None;
        row.glove_paint_wear = Some(wear);
        let mut parsed = ParsedDemo {
            rows: vec![row],
            econ_items: vec![ParsedEconItem {
                steam_id: Some(steam_id),
                item_def_index: Some(5030),
                paint_kit: Some(10038),
                paint_seed: Some(724),
                paint_wear_raw: Some(wear.to_bits()),
                paint_wear: Some(wear),
                ..ParsedEconItem::default()
            }],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");
        let glove = details
            .cosmetics
            .iter()
            .find(|cosmetic| cosmetic.kind == "glove")
            .expect("glove evidence");
        assert_eq!(glove.seed, Some(724));
        assert!(glove.inspect_command.is_some());

        parsed.econ_items[0].paint_wear_raw = Some(0.2_f32.to_bits());
        parsed.econ_items[0].paint_wear = Some(0.2);
        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");
        let glove = details
            .cosmetics
            .iter()
            .find(|cosmetic| cosmetic.kind == "glove")
            .expect("glove evidence");
        assert_eq!(glove.seed, None);
        assert!(glove.inspect_command.is_none());
    }

    #[test]
    fn omits_free_default_music_kits_from_cosmetic_evidence() {
        let steam_id = STEAM_ID64_BASE + 123;
        let mut cs2_default = player_row(steam_id, 100, 2, Vec::new());
        cs2_default.music_kit_id = Some(1);
        let mut csgo_default = player_row(steam_id, 200, 2, Vec::new());
        csgo_default.music_kit_id = Some(70);
        let parsed = ParsedDemo {
            rows: vec![cs2_default, csgo_default],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        assert!(details.music_kit_ids.is_empty());
    }

    #[test]
    fn preserves_all_distinct_valid_music_kit_evidence() {
        let steam_id = STEAM_ID64_BASE + 123;
        let mut cs2_default = player_row(steam_id, 100, 2, Vec::new());
        cs2_default.music_kit_id = Some(1);
        let mut csgo_default = player_row(steam_id, 150, 2, Vec::new());
        csgo_default.music_kit_id = Some(70);
        let mut first_custom_row = player_row(steam_id, 200, 2, Vec::new());
        first_custom_row.music_kit_id = Some(42);
        let mut ignored_later_row = player_row(steam_id, 300, 3, Vec::new());
        ignored_later_row.music_kit_id = Some(43);
        let parsed = ParsedDemo {
            rows: vec![
                cs2_default,
                csgo_default,
                first_custom_row,
                ignored_later_row,
            ],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");

        assert_eq!(details.music_kit_ids, [42, 43]);
    }

    #[test]
    fn ignores_map_based_default_agents_but_keeps_real_agent_evidence() {
        let steam_id = STEAM_ID64_BASE + 123;
        let mut default_t = player_row(steam_id, 100, 2, Vec::new());
        default_t.agent_item_def_index = Some(5036);
        default_t.agent_skin = Some("customplayer_t_map_based".to_string());
        let mut default_ct = player_row(steam_id, 200, 3, Vec::new());
        default_ct.agent_item_def_index = Some(5037);
        default_ct.agent_skin = Some("customplayer_ct_map_based".to_string());
        let mut real_agent = player_row(steam_id, 300, 3, Vec::new());
        real_agent.agent_item_def_index = Some(4713);
        real_agent.agent_skin = Some("customplayer_ctm_swat_variantg".to_string());
        let mut ignored_replacement = player_row(steam_id, 400, 3, Vec::new());
        ignored_replacement.agent_item_def_index = Some(4749);
        ignored_replacement.agent_skin = Some("customplayer_ctm_sas_variantf".to_string());
        let parsed = ParsedDemo {
            rows: vec![default_t, default_ct, real_agent, ignored_replacement],
            ..ParsedDemo::default()
        };

        let details = summarize_player_details(&parsed, None, Some(1))
            .remove(&steam_id)
            .expect("player evidence");
        let agents = details
            .cosmetics
            .iter()
            .filter(|cosmetic| cosmetic.kind == "agent")
            .collect::<Vec<_>>();

        assert_eq!(agents.len(), 1);
        assert_eq!(agents[0].item_def_index, Some(4713));
        assert_eq!(
            agents[0].item_name.as_deref(),
            Some("customplayer_ctm_swat_variantg")
        );
        assert_eq!(agents[0].side.as_deref(), Some("ct"));
    }
}
