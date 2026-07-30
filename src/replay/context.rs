use crate::model::{Cs2Rec, ParsedPlayerTick, ReplayLoadout};
use std::collections::BTreeSet;

pub(crate) fn first_weapon_def_index(rec: &Cs2Rec) -> i32 {
    rec.ticks
        .iter()
        .map(|tick| normalize_weapon_def_index(tick.weapon_def_index))
        .find(|def| is_known_weapon_def_index(*def))
        .unwrap_or(-1)
}

pub(crate) fn first_weapon_def_index_from_play_start(
    rec: &Cs2Rec,
    play_start_tick_index: u32,
) -> i32 {
    let start = (play_start_tick_index as usize).min(rec.ticks.len());
    rec.ticks
        .iter()
        .skip(start)
        .map(|tick| normalize_weapon_def_index(tick.weapon_def_index))
        .find(|def| is_known_weapon_def_index(*def))
        .unwrap_or_else(|| first_weapon_def_index(rec))
}

pub(crate) fn preload_weapon_def_indices_from_refs(
    rows: &[&ParsedPlayerTick],
    rec: &Cs2Rec,
) -> Vec<i32> {
    preload_weapon_def_indices_from_iter(rows.iter().copied(), rec)
}

pub(crate) fn preload_weapon_def_indices_from_refs_from_play_start(
    rows: &[&ParsedPlayerTick],
    rec: &Cs2Rec,
    play_start_tick_index: u32,
) -> Vec<i32> {
    let start = (play_start_tick_index as usize).min(rows.len());
    let mut seen = BTreeSet::new();
    let mut defs = Vec::new();
    let play_start_row = rows.get(start).copied();
    if let Some(row) = play_start_row {
        for raw_def in &row.inventory_as_ids {
            let def = normalize_weapon_def_index(*raw_def);
            if is_preload_weapon_def_index(def) && seen.insert(def) {
                defs.push(def);
            }
        }
    }
    let first_def = first_weapon_def_index_from_play_start(rec, play_start_tick_index);
    if is_preload_weapon_def_index(first_def) && seen.insert(first_def) {
        defs.push(first_def);
    }
    if defs.is_empty() && play_start_row.is_none() {
        preload_weapon_def_indices_from_refs(rows, rec)
    } else {
        defs
    }
}

fn preload_weapon_def_indices_from_iter<'a>(
    rows: impl IntoIterator<Item = &'a ParsedPlayerTick>,
    rec: &Cs2Rec,
) -> Vec<i32> {
    let mut seen = BTreeSet::new();
    let mut defs = Vec::new();
    for row in rows {
        for raw_def in &row.inventory_as_ids {
            let def = normalize_weapon_def_index(*raw_def);
            if is_preload_weapon_def_index(def) && seen.insert(def) {
                defs.push(def);
            }
        }
    }
    for tick in &rec.ticks {
        let def = normalize_weapon_def_index(tick.weapon_def_index);
        if is_preload_weapon_def_index(def) && seen.insert(def) {
            defs.push(def);
        }
    }
    defs
}

pub(crate) fn replay_loadout(row: &ParsedPlayerTick) -> ReplayLoadout {
    ReplayLoadout {
        weapon_def_indices: row
            .inventory_as_ids
            .iter()
            .map(|def| normalize_weapon_def_index(*def))
            .filter(|def| is_loadout_weapon_def_index(*def))
            .collect(),
        armor_value: row.armor_value,
        has_helmet: row.has_helmet,
        has_defuser: row.has_defuser,
    }
}

fn normalize_weapon_def_index(def: i32) -> i32 {
    if crate::export::valid_knife_item_def_index(def) {
        42
    } else {
        def
    }
}

fn is_known_weapon_def_index(def: i32) -> bool {
    crate::export::valid_replay_equipment_item_def_index(def)
}

fn is_preload_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 31 | 42 | 49)
}

fn is_loadout_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 42 | 49)
}
