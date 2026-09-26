/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

//! Inventory ownership, item identity and first-valid appearance accumulation.
//! Purchases prove ownership even without a sampled inventory. Holding another
//! player's item does not make it part of the holder's cosmetic collection.

use super::{combine_item_id, inventory_item_cosmetic_evidence, STEAM_ID64_BASE};
use crate::model::{ParsedInventoryWeaponCosmetic, ParsedPlayerTick, ReplayWeaponCosmetic};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write as _;
use std::sync::Arc;

pub(crate) type InventoryItems = BTreeMap<InventoryItemIdentity, ObservedInventoryItem>;

#[derive(Clone)]
pub(crate) struct ObservedInventoryItem {
    pub(crate) appearance: ReplayWeaponCosmetic,
    pub(crate) sides: BTreeSet<u8>,
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) enum InventoryItemIdentity {
    ItemId(u64),
    Spec(String),
}

pub(crate) fn inventory_item_owner(item: &ParsedInventoryWeaponCosmetic) -> Option<u64> {
    item.item_account_id
        .filter(|id| *id > 1)
        .map(|id| STEAM_ID64_BASE + u64::from(id))
        .or_else(|| item.original_owner_xuid.filter(|id| *id != 0))
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

pub(crate) fn observe_inventory_item(
    items: &mut InventoryItems,
    item: &ParsedInventoryWeaponCosmetic,
    side: Option<u8>,
) {
    let observed = match items.entry(inventory_item_identity(item)) {
        std::collections::btree_map::Entry::Occupied(entry) => entry.into_mut(),
        std::collections::btree_map::Entry::Vacant(entry) => {
            let Some(appearance) = inventory_item_cosmetic_evidence(item) else {
                return;
            };
            entry.insert(ObservedInventoryItem {
                appearance,
                sides: BTreeSet::new(),
            })
        }
    };
    if let Some(side) = side.filter(|side| matches!(side, 2 | 3)) {
        observed.sides.insert(side);
    }
}

/// Borrow snapshots for this scan only. The source ParsedDemo keeps their Arc
/// allocations alive, so pointer equality cannot confuse recycled addresses.
#[derive(Default)]
pub(crate) struct InventorySnapshotTracker<'a> {
    last: BTreeMap<u64, (u8, &'a Arc<[ParsedInventoryWeaponCosmetic]>)>,
}

impl<'a> InventorySnapshotTracker<'a> {
    pub(crate) fn changed(&mut self, row: &'a ParsedPlayerTick) -> bool {
        let current = (row.team_num, &row.inventory_weapon_cosmetics);
        match self.last.insert(row.steam_id, current) {
            Some((side, previous)) => side != current.0 || !Arc::ptr_eq(previous, current.1),
            None => true,
        }
    }
}
