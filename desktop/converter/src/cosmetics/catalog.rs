/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

//! Validated item catalog shared by appearance and equipment playback.

use serde::Deserialize;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::OnceLock;

#[inline]
pub(crate) fn valid_weapon_cosmetic_paint(weapon_def_index: i32, paint_kit: u32) -> bool {
    cs2_lib_econ_index()
        .weapon_paints
        .contains(&(normalize_weapon_def_index(weapon_def_index), paint_kit))
}

#[inline]
pub(crate) fn weapon_cosmetic_rarity(weapon_def_index: i32, paint_kit: u32) -> Option<u32> {
    cs2_lib_econ_index()
        .weapon_paint_rarities
        .get(&(normalize_weapon_def_index(weapon_def_index), paint_kit))
        .copied()
}

#[inline]
pub(crate) fn valid_paint_kit(paint_kit: u32) -> bool {
    cs2_lib_econ_index().paint_kit_ids.contains(&paint_kit)
}

#[inline]
pub(crate) fn valid_sticker_id(sticker_id: u32) -> bool {
    cs2_lib_econ_index().sticker_ids.contains(&sticker_id)
}

#[inline]
pub(crate) fn valid_keychain_id(keychain_id: u32) -> bool {
    cs2_lib_econ_index().keychain_ids.contains(&keychain_id)
}

#[inline]
pub(crate) fn valid_music_kit_id(music_kit_id: u32) -> bool {
    cs2_lib_econ_index().music_kit_ids.contains(&music_kit_id)
}

#[inline]
pub(crate) fn valid_music_kit_evidence_id(music_kit_id: u32) -> bool {
    // Valve's CS2 and CS:GO soundtracks are free defaults. A player-state ID
    // can preserve playback configuration without proving cosmetic ownership.
    valid_music_kit_id(music_kit_id) && !matches!(music_kit_id, 1 | 70)
}

#[inline]
pub(crate) fn valid_scoreboard_flair_item_def(item_def_index: u32) -> bool {
    item_def_index == 0
        || cs2_lib_econ_index()
            .scoreboard_flair_defidx
            .contains(&item_def_index)
}

#[inline]
pub(crate) fn valid_glove_item_def_index(item_def_index: i32) -> bool {
    cs2_lib_econ_index().glove_defidx.contains(&item_def_index)
}

#[inline]
pub(crate) fn valid_knife_item_def_index(item_def_index: i32) -> bool {
    cs2_lib_econ_index().knife_defidx.contains(&item_def_index)
}

#[inline]
pub(crate) fn valid_agent_item_def_index(item_def_index: u32) -> bool {
    cs2_lib_econ_index().agent_defidx.contains(&item_def_index)
}

#[derive(Debug, Deserialize)]
struct RawCs2LibEconIndex {
    weapon_paints: Vec<RawPaintPair>,
    paint_kit_ids: Vec<u32>,
    replay_equipment_defidx: Vec<i32>,
    replay_equipment: Vec<RawReplayEquipment>,
    knife_defidx: Vec<i32>,
    glove_defidx: Vec<i32>,
    agent_defidx: Vec<u32>,
    sticker_ids: Vec<u32>,
    keychain_ids: Vec<u32>,
    music_kit_ids: Vec<u32>,
    scoreboard_flair_defidx: Vec<u32>,
}

#[derive(Debug, Deserialize)]
struct RawPaintPair {
    weapon_defidx: i32,
    paint_kit: u32,
    rarity: Option<u32>,
}

#[derive(Debug, Deserialize)]
struct RawReplayEquipment {
    weapon_defidx: i32,
    class_name: String,
    replay_slot: ReplayEquipmentSlot,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "snake_case")]
enum ReplayEquipmentSlot {
    Primary,
    Secondary,
    Utility,
    C4,
    Taser,
    Knife,
}

#[derive(Debug)]
struct Cs2LibEconIndex {
    weapon_paints: BTreeSet<(i32, u32)>,
    weapon_paint_rarities: BTreeMap<(i32, u32), u32>,
    paint_kit_ids: BTreeSet<u32>,
    replay_equipment_defidx: BTreeSet<i32>,
    replay_equipment_by_class: BTreeMap<String, i32>,
    replay_equipment_class_by_defidx: BTreeMap<i32, String>,
    replay_equipment_slot_by_defidx: BTreeMap<i32, ReplayEquipmentSlot>,
    knife_defidx: BTreeSet<i32>,
    glove_defidx: BTreeSet<i32>,
    agent_defidx: BTreeSet<u32>,
    sticker_ids: BTreeSet<u32>,
    keychain_ids: BTreeSet<u32>,
    music_kit_ids: BTreeSet<u32>,
    scoreboard_flair_defidx: BTreeSet<u32>,
}

#[inline]
fn cs2_lib_econ_index() -> &'static Cs2LibEconIndex {
    static INDEX: OnceLock<Cs2LibEconIndex> = OnceLock::new();
    INDEX.get_or_init(|| {
        let raw: RawCs2LibEconIndex = serde_json::from_str(include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../shared/econ/cs2-lib-econ-index.v1.json"
        )))
        .expect("embedded cs2-lib-econ-index.v1.json must be valid JSON");
        let knife_defidx = raw.knife_defidx.into_iter().collect::<BTreeSet<_>>();
        let replay_equipment_defidx = raw
            .replay_equipment_defidx
            .into_iter()
            .collect::<BTreeSet<_>>();
        let mut replay_equipment_by_class = BTreeMap::new();
        let mut replay_equipment_class_by_defidx = BTreeMap::new();
        let mut replay_equipment_slot_by_defidx = BTreeMap::new();
        for item in raw.replay_equipment {
            assert!(
                replay_equipment_defidx.contains(&item.weapon_defidx),
                "cs2-lib replay equipment mapping contains an unknown defindex"
            );
            assert!(
                replay_equipment_by_class
                    .insert(item.class_name.clone(), item.weapon_defidx)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate class"
            );
            assert!(
                replay_equipment_class_by_defidx
                    .insert(item.weapon_defidx, item.class_name)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate defindex"
            );
            assert!(
                replay_equipment_slot_by_defidx
                    .insert(item.weapon_defidx, item.replay_slot)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate slot defindex"
            );
        }
        assert_eq!(
            replay_equipment_class_by_defidx.len(),
            replay_equipment_defidx.len(),
            "cs2-lib replay equipment mapping must cover every replay defindex"
        );
        let default_knife_defidx = *replay_equipment_by_class
            .get("weapon_knife")
            .expect("cs2-lib replay equipment mapping must contain weapon_knife");
        let mut weapon_paints = BTreeSet::new();
        let mut weapon_paint_rarities = BTreeMap::new();
        for pair in raw.weapon_paints {
            if pair.paint_kit == 0 {
                continue;
            }
            let key = (
                normalize_weapon_def_index_with_knives(
                    pair.weapon_defidx,
                    &knife_defidx,
                    default_knife_defidx,
                ),
                pair.paint_kit,
            );
            weapon_paints.insert(key);
            if let Some(rarity) = pair.rarity.filter(|value| *value <= 7) {
                weapon_paint_rarities.insert(key, rarity);
            }
        }
        Cs2LibEconIndex {
            weapon_paints,
            weapon_paint_rarities,
            paint_kit_ids: raw
                .paint_kit_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            replay_equipment_defidx,
            replay_equipment_by_class,
            replay_equipment_class_by_defidx,
            replay_equipment_slot_by_defidx,
            knife_defidx,
            glove_defidx: raw.glove_defidx.into_iter().collect(),
            agent_defidx: raw.agent_defidx.into_iter().collect(),
            sticker_ids: raw
                .sticker_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            keychain_ids: raw
                .keychain_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            music_kit_ids: raw
                .music_kit_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            scoreboard_flair_defidx: raw
                .scoreboard_flair_defidx
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
        }
    })
}

#[inline]
pub(crate) fn normalize_weapon_def_index(def: i32) -> i32 {
    let index = cs2_lib_econ_index();
    normalize_weapon_def_index_with_knives(
        def,
        &index.knife_defidx,
        *index
            .replay_equipment_by_class
            .get("weapon_knife")
            .expect("cs2-lib replay equipment mapping must contain weapon_knife"),
    )
}

#[inline]
fn normalize_weapon_def_index_with_knives(
    def: i32,
    knife_defidx: &BTreeSet<i32>,
    default_knife_defidx: i32,
) -> i32 {
    if knife_defidx.contains(&def) {
        default_knife_defidx
    } else {
        def
    }
}

#[inline]
pub(crate) fn valid_replay_equipment_item_def_index(def: i32) -> bool {
    cs2_lib_econ_index()
        .replay_equipment_defidx
        .contains(&normalize_weapon_def_index(def))
}

#[inline]
pub(crate) fn is_known_weapon_def_index(def: i32) -> bool {
    valid_replay_equipment_item_def_index(def)
}

#[inline]
pub(crate) fn is_replay_equipment_event_def(def: i32) -> bool {
    cs2_lib_econ_index()
        .replay_equipment_slot_by_defidx
        .get(&normalize_weapon_def_index(def))
        == Some(&ReplayEquipmentSlot::Utility)
}

#[inline]
pub(crate) fn is_weapon_cosmetic_def_index(def: i32) -> bool {
    matches!(
        cs2_lib_econ_index()
            .replay_equipment_slot_by_defidx
            .get(&normalize_weapon_def_index(def)),
        Some(
            ReplayEquipmentSlot::Primary
                | ReplayEquipmentSlot::Secondary
                | ReplayEquipmentSlot::Taser
        )
    )
}

#[inline]
pub(crate) fn is_knife_cosmetic_def_index(def: i32) -> bool {
    // The CT/T defaults are playback equipment, not ownable knife models.
    valid_knife_item_def_index(def) && !matches!(def, 42 | 59)
}

#[inline]
pub(crate) fn weapon_def_index_from_item_name(item_name: &str) -> Option<i32> {
    cs2_lib_econ_index()
        .replay_equipment_by_class
        .get(&normalize_item_event_name(item_name))
        .copied()
        .map(normalize_weapon_def_index)
}

#[inline]
pub(crate) fn weapon_item_name(def: i32) -> Option<&'static str> {
    cs2_lib_econ_index()
        .replay_equipment_class_by_defidx
        .get(&normalize_weapon_def_index(def))
        .map(String::as_str)
}

#[inline]
pub(crate) fn normalize_item_event_name(item_name: &str) -> String {
    let lower = item_name.trim().to_ascii_lowercase();
    match lower.as_str() {
        "decoy_grenade" | "weapon_decoy_grenade" => "weapon_decoy".to_string(),
        "c4" | "weapon_c4_explosive" => "weapon_c4".to_string(),
        value if value.starts_with("weapon_") => value.to_string(),
        value => format!("weapon_{value}"),
    }
}
