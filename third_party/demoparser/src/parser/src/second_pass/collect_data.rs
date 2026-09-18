use super::entities::PlayerMetaData;
use super::variants::Variant;
use super::variants::{into_shared_slice, InventoryWeaponAttribute, InventoryWeaponCosmetic, Sticker};
use crate::demo_network_handle::{
    demo_network_ehandle_index, DEMO_NETWORK_EHANDLE_INVALID_INDEX,
};
use crate::first_pass::prop_controller::*;
use crate::first_pass::read_bits::DemoParserError;
use crate::maps::BUTTONMAP;
use crate::maps::PLAYER_COLOR;
use crate::second_pass::entities::{Entity, EntityType};
use crate::second_pass::parser_settings::{PlayerInventorySnapshot, SecondPassParser};
use crate::second_pass::variants::PropColumn;
use crate::second_pass::variants::VarVec;
use csgoproto::maps::AGENTSMAP;
use csgoproto::maps::PAINTKITS;
use csgoproto::maps::STICKER_ID_TO_NAME;
use csgoproto::maps::WEAPINDICIES;
use std::cell::Ref;
use std::fmt;
use std::sync::Arc;

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum PropType {
    Team,
    Rules,
    Custom,
    Controller,
    Player,
    Weapon,
    Button,
    Name,
    Steamid,
    Tick,
    GameTime,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum SharedStringSource {
    Name, Controller, Team, Weapon, Agent, Color, WeaponName, SkinName, OriginalOwner,
}

pub(crate) fn make_shared_string_sources(props: &PropController) -> Vec<Option<SharedStringSource>> {
    props.prop_infos.iter().map(|prop| {
        use SharedStringSource::*;
        match (prop.prop_type, prop.prop_name.as_str(), prop.id) {
            (PropType::Name, _, _) => Some(Name),
            (PropType::Controller, "CCSPlayerController.m_szCrosshairCodes", _) => Some(Controller),
            (PropType::Team, "CCSTeam.m_szTeamname" | "CCSTeam.m_szClanTeamname", _) => Some(Team),
            (PropType::Weapon, "m_szCustomName", _) => Some(Weapon),
            (PropType::Custom, _, AGENT_SKIN_ID) => Some(Agent),
            (PropType::Custom, "CCSPlayerController.m_iCompTeammateColor", _) => Some(Color),
            (PropType::Custom, _, WEAPON_NAME_ID) => Some(WeaponName),
            (PropType::Custom, _, WEAPON_SKIN_NAME) => Some(SkinName),
            (PropType::Custom, _, WEAPON_ORIGINGAL_OWNER_ID) => Some(OriginalOwner),
            _ => None,
        }
    }).collect()
}

pub(crate) fn make_dense_column_slots(count: usize, is_deferred: impl Fn(usize) -> bool) -> Vec<usize> {
    (0..count).filter(|&slot| !is_deferred(slot)).collect()
}

// DONT KNOW IF THESE ARE CORRECT. SEEMS TO GIVE CORRECT VALUES
const CELL_BITS: i32 = 9;
const MAX_COORD: f32 = (1 << 14) as f32;
// https://github.com/markus-wa/demoinfocs-golang/blob/master/pkg/demoinfocs/constants/constants.go#L11
const IS_AIRBORNE_CONST: u32 = 0xFFFFFF;
const ECON_ATTR_SET_ITEM_TEXTURE_PREFAB: u32 = 6;
const ECON_ATTR_SET_ITEM_TEXTURE_SEED: u32 = 7;
const ECON_ATTR_SET_ITEM_TEXTURE_WEAR: u32 = 8;

#[derive(Debug, Clone, Default)]
pub struct ProjectileRecord {
    pub steamid: Option<u64>,
    pub name: Option<String>,
    pub x: Option<f32>,
    pub y: Option<f32>,
    pub z: Option<f32>,
    pub tick: Option<i32>,
    pub grenade_type: Option<String>,
    pub entity_id: Option<i32>,
    pub entity_serial: Option<u32>,
    pub is_incendiary: Option<bool>,
    pub initial_position: Option<[f32; 3]>,
    pub initial_velocity: Option<[f32; 3]>,
    pub smoke_detonation_position: Option<[f32; 3]>,
    pub bounces: Option<i32>,
}

fn variant_to_f32(value: &Option<Variant>) -> Option<f32> {
    match value {
        Some(Variant::F32(value)) => Some(*value),
        _ => None,
    }
}

fn projectile_vec3_is_meaningful(value: [f32; 3]) -> bool {
    value.iter().all(|coordinate| coordinate.is_finite())
        && value.iter().any(|coordinate| coordinate.abs() > f32::EPSILON)
}

#[derive(Clone, Copy, Debug, Default)]
struct StickerState {
    id: Option<u32>,
    wear: Option<f32>,
    offset_x: Option<f32>,
    offset_y: Option<f32>,
    scale: Option<f32>,
    rotation: Option<f32>,
}

#[derive(Clone, Copy, Debug)]
struct StickerAttribute {
    definition_index: u32,
    raw_value: f32,
}

impl StickerState {
    fn set_id(&mut self, id: u32) {
        self.id = (id != 0).then_some(id);
    }

    fn set_wear(&mut self, wear: f32) {
        self.wear = Some(wear);
    }

    fn set_scale(&mut self, scale: f32) {
        self.scale = Some(scale);
    }

    fn set_offset_x(&mut self, offset_x: f32) {
        self.offset_x = Some(offset_x);
    }

    fn set_offset_y(&mut self, offset_y: f32) {
        self.offset_y = Some(offset_y);
    }

    fn set_rotation(&mut self, rotation: f32) {
        self.rotation = Some(rotation);
    }

    fn into_sticker(self, slot: u32) -> Option<Sticker> {
        let id = self.id?;
        if id == 0 {
            return None;
        }

        let name = STICKER_ID_TO_NAME.get(&id)?;
        let wear = self.wear.unwrap_or(0.0).max(0.0);
        let x = self.offset_x.unwrap_or(0.0);
        let y = self.offset_y.unwrap_or(0.0);
        if !wear.is_finite()
            || wear > 1.0
            || !x.is_finite()
            || !y.is_finite()
            || self.scale.is_some_and(|value| !value.is_finite())
            || self.rotation.is_some_and(|value| !value.is_finite())
        {
            return None;
        }

        Some(Sticker {
            slot,
            id,
            name: (*name).to_string(),
            wear,
            x,
            y,
            scale: self.scale,
            rotation: self.rotation,
        })
    }
}

fn stickers_from_attributes(attributes: impl IntoIterator<Item = StickerAttribute>) -> Vec<Sticker> {
    let mut layers = vec![[
        StickerState::default(),
        StickerState::default(),
        StickerState::default(),
        StickerState::default(),
        StickerState::default(),
    ]];

    for attribute in attributes {
        let definition_index = attribute.definition_index;
        let raw_value = attribute.raw_value;
        match definition_index {
            113 | 117 | 121 | 125 | 129 => {
                let slot = ((definition_index - 113) / 4) as usize;
                if layers.last().is_some_and(|layer| layer[slot].id.is_some()) {
                    layers.push([
                        StickerState::default(),
                        StickerState::default(),
                        StickerState::default(),
                        StickerState::default(),
                        StickerState::default(),
                    ]);
                }
                layers
                    .last_mut()
                    .expect("at least one sticker layer")[slot]
                    .set_id(raw_value.to_bits());
            }
            114 | 118 | 122 | 126 | 130 => {
                let slot = ((definition_index - 114) / 4) as usize;
                layers
                    .last_mut()
                    .expect("at least one sticker layer")[slot]
                    .set_wear(raw_value);
            }
            115 | 119 | 123 | 127 | 131 => {
                let slot = ((definition_index - 115) / 4) as usize;
                layers
                    .last_mut()
                    .expect("at least one sticker layer")[slot]
                    .set_scale(raw_value);
            }
            116 | 120 | 124 | 128 | 132 => {
                let slot = ((definition_index - 116) / 4) as usize;
                layers
                    .last_mut()
                    .expect("at least one sticker layer")[slot]
                    .set_rotation(raw_value);
            }
            278..=287 => {
                let slot = ((definition_index - 278) / 2) as usize;
                if slot >= 5 {
                    continue;
                }
                if (definition_index - 278) % 2 == 0 {
                    layers
                        .last_mut()
                        .expect("at least one sticker layer")[slot]
                        .set_offset_x(raw_value);
                } else {
                    layers
                        .last_mut()
                        .expect("at least one sticker layer")[slot]
                        .set_offset_y(raw_value);
                }
            }
            _ => {}
        }
    }

    (0..5)
        .filter_map(|slot| {
            layers
                .iter()
                .find_map(|layer| layer[slot].into_sticker(slot as u32))
        })
        .collect()
}

pub enum CoordinateAxis {
    X,
    Y,
    Z,
}

/// These entity links cannot change while a single player row is collected.
/// Resolve them once without changing the public getters used by game events.
struct RowCollectionContext<'a> {
    pawn: Option<&'a Entity>,
    controller: Option<&'a Entity>,
    rules: Option<&'a Entity>,
    team: Option<&'a Entity>,
    weapon: Option<&'a Entity>,
    metadata_weapon: Option<&'a Entity>,
    metadata_weapon_id: Option<i32>,
    eye_angles: Option<[f32; 3]>,
}

impl<'a> RowCollectionContext<'a> {
    fn new(parser: &'a SecondPassParser<'_>, entity_id: i32, player: &PlayerMetaData) -> Self {
        let ids = &parser.prop_controller.special_ids;
        let pawn = Self::entity(parser, Some(entity_id));
        if parser.entity_projection.as_ref().is_some_and(|plan| plan.direct_rows_only) {
            return Self { pawn, controller: Self::entity(parser, player.controller_entid),
                rules: Self::entity(parser, parser.rules_entity_id), team: None,
                weapon: None, metadata_weapon: None, metadata_weapon_id: None, eye_angles: None };
        }
        let weapon_id = Self::u32_property(pawn, ids.active_weapon).map(demo_network_ehandle_index);
        // Some legacy custom getters follow PlayerMetaData rather than the
        // roster key. Preserve that distinction even for incomplete metadata.
        let metadata_weapon_id = if player.player_entity_id == Some(entity_id) {
            weapon_id
        } else {
            Self::u32_property(Self::entity(parser, player.player_entity_id), ids.active_weapon)
                .map(demo_network_ehandle_index)
        };
        let weapon = Self::entity(parser, weapon_id);
        let metadata_weapon = if metadata_weapon_id == weapon_id {
            weapon
        } else {
            Self::entity(parser, metadata_weapon_id)
        };
        let team_id = match Self::u32_property(pawn, ids.player_team_pointer) {
            Some(1) => parser.teams.team1_entid,
            Some(2) => parser.teams.team2_entid,
            Some(3) => parser.teams.team3_entid,
            _ => None,
        };
        let eye_angles = ids.eye_angles.and_then(|id| match pawn?.props.get(&id)? {
            Variant::VecXYZ(value) => Some(*value),
            _ => None,
        });
        Self {
            pawn,
            controller: Self::entity(parser, player.controller_entid),
            rules: Self::entity(parser, parser.rules_entity_id),
            team: Self::entity(parser, team_id),
            weapon,
            metadata_weapon,
            metadata_weapon_id,
            eye_angles,
        }
    }

    fn entity(parser: &'a SecondPassParser<'_>, id: Option<i32>) -> Option<&'a Entity> {
        parser.entities.get(id? as usize)?.as_ref()
    }

    #[inline]
    fn property(entity: Option<&Entity>, id: u32) -> Option<Variant> {
        entity?.props.get(&id).cloned()
    }

    fn u32_property(entity: Option<&Entity>, id: Option<u32>) -> Option<u32> {
        match entity?.props.get(&id?)? {
            Variant::U32(value) => Some(*value),
            _ => None,
        }
    }

    fn coordinate(&self, cell_id: Option<u32>, offset_id: Option<u32>) -> Option<Variant> {
        let cell = Self::property(self.pawn, cell_id?)
            .ok_or(PropCollectionError::GetPropFromEntPropNotFound);
        let offset = Self::property(self.pawn, offset_id?)
            .ok_or(PropCollectionError::GetPropFromEntPropNotFound);
        coord_from_cell(cell, offset).ok().map(Variant::F32)
    }

    fn weapon_skin_id(&self) -> Option<u32> {
        match self.weapon?.props.get(&WEAPON_SKIN_ID)? {
            Variant::F32(value) if value.fract() == 0.0 && *value >= 0.0 => Some(*value as u32),
            _ => None,
        }
    }
}

// This file collects the data that is converted into a dataframe in the end in parser.parse_ticks()

fn should_collect_player_rows(
    all_player_rows: bool,
    event_with_velocity: bool,
    wanted_events_present: bool,
    wanted_ticks_present: bool,
    current_tick_wanted: bool,
) -> bool {
    all_player_rows
        || event_with_velocity
        || (!wanted_events_present && (!wanted_ticks_present || current_tick_wanted))
}

pub(crate) fn make_dense_player_columns(
    props: &PropController,
    order_by_steamid: bool,
    parse_projectiles: bool,
) -> Option<Vec<PropColumn>> {
    // Velocity reads previously appended output rows. Query filters and shared
    // property IDs retain the legacy path, including its partial-row behavior.
    if order_by_steamid
        || parse_projectiles
        || props.event_with_velocity
        || !props.wanted_prop_state_infos.is_empty()
    {
        return None;
    }
    let mut ids = ahash::AHashSet::with_capacity(props.prop_infos.len());
    for prop in &props.prop_infos {
        if matches!(prop.id, VELOCITY_ID | VELOCITY_X_ID | VELOCITY_Y_ID | VELOCITY_Z_ID)
            || !ids.insert(prop.id)
        {
            return None;
        }
    }
    Some(props.prop_infos.iter().map(|_| PropColumn::new()).collect())
}

impl<'a> SecondPassParser<'a> {
    pub fn collect_entities(&mut self) {
        if !should_collect_player_rows(
            self.decode_plan.all_player_rows,
            self.prop_controller.event_with_velocity,
            !self.wanted_events.is_empty(),
            !self.wanted_ticks.is_empty(),
            self.wanted_ticks.contains(&self.tick),
        ) {
            return;
        }
        if self.parse_projectiles {
            self.collect_projectiles(true);
            return;
        }
        if self.collect_projectile_records {
            self.collect_projectiles(false);
        }
        if let Some(mut columns) = self.dense_player_columns.take() {
            self.collect_dense_player_rows(&mut columns);
            self.dense_player_columns = Some(columns);
            return;
        }
        // iterate every player and every wanted prop name
        // if either one is missing then push None to output
        for (entity_id, player) in &self.players {
            // iterate every wanted prop state
            // if any prop's state for this tick is not the wanted state, dont extract info from tick
            for wanted_prop_state_info in &self.prop_controller.wanted_prop_state_infos {
                match self.find_prop(&wanted_prop_state_info.base, entity_id, player) {
                    Ok(prop) => {
                        if prop != wanted_prop_state_info.wanted_prop_state {
                            return;
                        }
                    }
                    Err(_e) => return,
                }
            }

            let player_steamid = player.steamid.unwrap_or(0);
            if !self.wanted_players.is_empty() && !self.wanted_players.contains(&player_steamid) {
                continue;
            }
            let mut velocity_indicies: Option<Vec<usize>> = None;
            let mut button_mask: Option<Option<u64>> = None;
            if self.order_by_steamid {
                for prop_info in &self.prop_controller.prop_infos {
                    let val = self.find_prop_with_collect_cache(
                        prop_info,
                        entity_id,
                        player,
                        &mut velocity_indicies,
                        &mut button_mask,
                    );
                    self.df_per_player
                        .entry(player_steamid)
                        .or_default()
                        .entry(prop_info.id)
                        .or_insert_with(PropColumn::new)
                        .push(val);
                }
            } else {
                for prop_info in &self.prop_controller.prop_infos {
                    let val = self.find_prop_with_collect_cache(
                        prop_info,
                        entity_id,
                        player,
                        &mut velocity_indicies,
                        &mut button_mask,
                    );
                    self.output
                        .entry(prop_info.id)
                        .or_insert_with(PropColumn::new)
                        .push(val);
                }
            }
        }
    }

    fn collect_dense_player_rows(&mut self, columns: &mut [PropColumn]) {
        // The immutable property list and dense columns share their order for
        // the entire second pass. Detaching the columns allows normal getters
        // and their per-player caches without a hash lookup for every cell.
        let mut sparse = self.sparse_scalar_columns.take();
        let mut property_profile = self.property_profile.take();
        for (entity_id, player) in &self.players {
            let player_steamid = player.steamid.unwrap_or(0);
            if !self.wanted_players.is_empty() && !self.wanted_players.contains(&player_steamid) {
                continue;
            }
            let row = RowCollectionContext::new(self, *entity_id, player);
            if let Some(sparse) = sparse.as_mut() {
                sparse.record_linked_row([row.pawn, row.controller, row.rules, row.team, row.weapon]
                    .map(|entity| entity.map(|entity| entity.entity_id)));
            }
            let mut button_mask = None;
            if property_profile.as_mut().is_some_and(|profile| profile.sample_row()) {
                let profile = property_profile.as_mut().unwrap();
                for &slot in &self.dense_column_slots {
                    let prop_info = &self.prop_controller.prop_infos[slot];
                    let column = &mut columns[slot];
                    let started = std::time::Instant::now();
                    if !self.push_dense_shared_string(self.shared_string_sources[slot], prop_info, player, &row, column) {
                        let val = self.find_dense_prop(prop_info, entity_id, player, &row, &mut button_mask);
                        column.push(val);
                    }
                    profile.record(slot, started);
                }
            } else {
                for &slot in &self.dense_column_slots {
                    let prop_info = &self.prop_controller.prop_infos[slot];
                    let column = &mut columns[slot];
                    if !self.push_dense_shared_string(self.shared_string_sources[slot], prop_info, player, &row, column) {
                        let val = self.find_dense_prop(prop_info, entity_id, player, &row, &mut button_mask);
                        column.push(val);
                    }
                }
            }
        }
        self.sparse_scalar_columns = sparse;
        self.property_profile = property_profile;
    }

    #[inline]
    fn push_dense_shared_string(
        &self,
        source: Option<SharedStringSource>,
        prop: &PropInfo,
        player: &PlayerMetaData,
        row: &RowCollectionContext<'_>,
        column: &mut PropColumn,
    ) -> bool {
        use SharedStringSource::*;
        let Some(source) = source else { return false; };
        let ids = &self.prop_controller.special_ids;
        let value = match source {
            Name => player.name.as_deref(),
            Controller | Team | Weapon => {
                let entity = match source {
                    Controller => row.controller,
                    Team => row.team,
                    Weapon => row.weapon,
                    _ => unreachable!(),
                };
                match entity.and_then(|entity| entity.props.get(&prop.id)) {
                    Some(Variant::String(value)) => Some(value.as_str()),
                    // Keep the legacy collector's handling of unexpected types.
                    Some(_) => return false,
                    None => None,
                }
            }
            Agent => self.find_agent_skin_name(player).ok(),
            Color => match row.controller.and_then(|entity| entity.props.get(&prop.id)) {
                Some(Variant::I32(value)) => match PLAYER_COLOR.get(value) {
                    Some(name) => Some(*name),
                    None => { column.push_shared_i32(*value); return true; }
                },
                _ => None,
            },
            WeaponName => RowCollectionContext::u32_property(row.weapon, ids.item_def)
                .and_then(|id| WEAPINDICIES.get(&id)).copied(),
            SkinName => row.weapon_skin_id().and_then(|id| PAINTKITS.get(&id)).copied(),
            OriginalOwner => {
                if let (Some(low), Some(high)) = (
                    RowCollectionContext::u32_property(row.weapon, ids.orig_own_low),
                    RowCollectionContext::u32_property(row.weapon, ids.orig_own_high),
                ) {
                    column.push_shared_u64((u64::from(high) << 32) | u64::from(low));
                    return true;
                }
                None
            }
        };
        column.push_shared_string(value);
        true
    }

    #[inline]
    fn find_dense_prop(
        &self,
        prop_info: &PropInfo,
        entity_id: &i32,
        player: &PlayerMetaData,
        row: &RowCollectionContext<'_>,
        button_mask: &mut Option<Option<u64>>,
    ) -> Option<Variant> {
        match prop_info.prop_type {
            PropType::Player => RowCollectionContext::property(row.pawn, prop_info.id),
            PropType::Controller => RowCollectionContext::property(row.controller, prop_info.id),
            PropType::Rules => RowCollectionContext::property(row.rules, prop_info.id),
            PropType::Team => RowCollectionContext::property(row.team, prop_info.id),
            PropType::Weapon => RowCollectionContext::property(row.weapon, prop_info.id),
            PropType::Button => self.get_button_prop_cached(prop_info, entity_id, button_mask).ok(),
            PropType::Custom => {
                let ids = &self.prop_controller.special_ids;
                match prop_info.id {
                    PLAYER_X_ID => row.coordinate(ids.cell_x_player, ids.cell_x_offset_player),
                    PLAYER_Y_ID => row.coordinate(ids.cell_y_player, ids.cell_y_offset_player),
                    PLAYER_Z_ID => row.coordinate(ids.cell_z_player, ids.cell_z_offset_player),
                    PITCH_ID => row.eye_angles.map(|angles| Variant::F32(angles[0])),
                    YAW_ID => row.eye_angles.map(|angles| Variant::F32(angles[1])),
                    WEAPON_RESERVE_AMMO_SECONDARY => RowCollectionContext::property(row.weapon, WEAPON_RESERVE_AMMO_BASE + 1),
                    WEAPON_NAME_ID => RowCollectionContext::u32_property(row.weapon, ids.item_def)
                        .and_then(|id| WEAPINDICIES.get(&id))
                        .map(|name| Variant::String((*name).to_string())),
                    WEAPON_SKIN_ID => row.weapon_skin_id().map(Variant::U32),
                    WEAPON_SKIN_NAME => row.weapon_skin_id()
                        .and_then(|id| PAINTKITS.get(&id))
                        .map(|name| Variant::String((*name).to_string())),
                    WEAPON_FLOAT => RowCollectionContext::property(row.metadata_weapon, WEAPON_FLOAT),
                    WEAPON_PAINT_SEED => Some(Variant::U32(match RowCollectionContext::property(row.metadata_weapon, WEAPON_PAINT_SEED) {
                        Some(Variant::F32(value)) => value as u32,
                        _ => 0,
                    })),
                    WEAPON_ORIGINGAL_OWNER_ID => {
                        let low = RowCollectionContext::u32_property(row.weapon, ids.orig_own_low)?;
                        let high = RowCollectionContext::u32_property(row.weapon, ids.orig_own_high)?;
                        Some(Variant::String(((u64::from(high) << 32) | u64::from(low)).to_string()))
                    }
                    WEAPON_STICKERS_ID => self.find_stickers(&row.metadata_weapon_id?).ok(),
                    IS_ALIVE_ID => Some(Variant::Bool(RowCollectionContext::u32_property(row.pawn, ids.life_state) == Some(0))),
                    _ => self.create_custom_prop(prop_info, entity_id, player).ok(),
                }
            }
            _ => self.find_prop(prop_info, entity_id, player).ok(),
        }
    }

    #[inline(always)]
    fn find_prop_with_collect_cache(
        &self,
        prop_info: &PropInfo,
        entity_id: &i32,
        player: &PlayerMetaData,
        velocity_indicies: &mut Option<Vec<usize>>,
        button_mask: &mut Option<Option<u64>>,
    ) -> Option<Variant> {
        match prop_info.id {
            VELOCITY_ID => self.collect_velocity_cached(player, velocity_indicies).ok(),
            VELOCITY_X_ID => self
                .collect_velocity_axis_cached(player, CoordinateAxis::X, velocity_indicies)
                .ok(),
            VELOCITY_Y_ID => self
                .collect_velocity_axis_cached(player, CoordinateAxis::Y, velocity_indicies)
                .ok(),
            VELOCITY_Z_ID => self
                .collect_velocity_axis_cached(player, CoordinateAxis::Z, velocity_indicies)
                .ok(),
            _ if prop_info.prop_type == PropType::Button => self
                .get_button_prop_cached(prop_info, entity_id, button_mask)
                .ok(),
            _ => self.find_prop(prop_info, entity_id, player).ok(),
        }
    }

    pub fn find_prop(&self, prop_info: &PropInfo, entity_id: &i32, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        match prop_info.prop_type {
            PropType::Tick => return self.create_tick(),
            PropType::Name => return self.create_name(player),
            PropType::Steamid => return self.create_steamid(player),
            PropType::Player => return self.get_prop_from_ent(&prop_info.id, &entity_id),
            PropType::Team => return self.find_team_prop(&prop_info.id, &entity_id),
            PropType::Custom => self.create_custom_prop(prop_info, entity_id, player),
            PropType::Weapon => return self.find_weapon_prop(&prop_info.id, &entity_id),
            PropType::Button => return self.get_button_prop(&prop_info, &entity_id),
            PropType::Controller => return self.get_controller_prop(&prop_info.id, player),
            PropType::Rules => return self.get_rules_prop(prop_info),
            PropType::GameTime => self.tick_interval
                .map(|interval| Variant::F32(self.net_tick as f32 * interval))
                .ok_or(PropCollectionError::GetPropFromEntPropNotFound),
        }
    }
    pub fn get_prop_from_ent(&self, prop_id: &u32, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        match self.entities.get(*entity_id as usize) {
            Some(Some(e)) => match e.props.get(&prop_id) {
                None => return Err(PropCollectionError::GetPropFromEntPropNotFound),
                Some(prop) => return Ok(prop.clone()),
            },
            _ => return Err(PropCollectionError::GetPropFromEntEntityNotFound),
        }
    }
    fn create_tick(&self) -> Result<Variant, PropCollectionError> {
        // This can't actually fail
        return Ok(Variant::I32(self.tick));
    }
    pub fn create_steamid(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        match player.steamid {
            Some(steamid) => return Ok(Variant::U64(steamid)),
            // Revisit this as it was related to pandas null support with u64's
            _ => return Ok(Variant::U64(0)),
        }
    }
    pub fn create_name(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        match &player.name {
            Some(name) => return Ok(Variant::String(name.to_string())),
            _ => return Err(PropCollectionError::PlayerMetaDataNameNone),
        }
    }
    pub fn get_button_prop(&self, prop_info: &PropInfo, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        match self.prop_controller.special_ids.buttons {
            None => Err(PropCollectionError::ButtonsSpecialIDNone),
            Some(button_id) => match self.get_prop_from_ent(&button_id, &entity_id) {
                Ok(Variant::U64(button_mask)) => match BUTTONMAP.get(&prop_info.prop_name) {
                    Some(button_flag) => Ok(Variant::Bool(button_mask & button_flag != 0)),
                    None => return Err(PropCollectionError::ButtonsMapNoEntryFound),
                },
                Ok(_) => return Err(PropCollectionError::ButtonMaskNotU64Variant),
                Err(e) => Err(e),
            },
        }
    }
    fn get_button_prop_cached(
        &self,
        prop_info: &PropInfo,
        entity_id: &i32,
        button_mask_cache: &mut Option<Option<u64>>,
    ) -> Result<Variant, PropCollectionError> {
        if button_mask_cache.is_none() {
            *button_mask_cache = Some(match self.prop_controller.special_ids.buttons {
                Some(button_id) => match self.get_prop_from_ent(&button_id, entity_id) {
                    Ok(Variant::U64(mask)) => Some(mask),
                    _ => None,
                },
                None => None,
            });
        }
        match button_mask_cache.unwrap_or(None) {
            Some(button_mask) => match BUTTONMAP.get(&prop_info.prop_name) {
                Some(button_flag) => Ok(Variant::Bool(button_mask & button_flag != 0)),
                None => Err(PropCollectionError::ButtonsMapNoEntryFound),
            },
            None => Err(PropCollectionError::ButtonsSpecialIDNone),
        }
    }
    pub fn get_rules_prop(&self, prop_info: &PropInfo) -> Result<Variant, PropCollectionError> {
        match self.rules_entity_id {
            Some(entid) => return self.get_prop_from_ent(&prop_info.id, &entid),
            None => return Err(PropCollectionError::RulesEntityIdNotSet),
        }
    }
    pub fn get_controller_prop(&self, prop_id: &u32, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        match player.controller_entid {
            Some(entid) => return self.get_prop_from_ent(prop_id, &entid),
            None => return Err(PropCollectionError::ControllerEntityIdNotSet),
        }
    }
    fn find_owner_entid(&self, entity_id: &i32) -> Result<u32, PropCollectionError> {
        let owner_id = match self.prop_controller.special_ids.grenade_owner_id {
            Some(owner_id) => owner_id,
            None => return Err(PropCollectionError::GrenadeOwnerIdNotSet),
        };
        match self.get_prop_from_ent(&owner_id, entity_id) {
            Ok(Variant::U32(prop)) => Ok(demo_network_ehandle_index(prop) as u32),
            Ok(_) => return Err(PropCollectionError::GrenadeOwnerIdPropIncorrectVariant),
            Err(e) => return Err(e),
        }
    }
    pub fn find_player_metadata(&self, entity_id: i32) -> Result<&PlayerMetaData, PropCollectionError> {
        match self.players.get(&entity_id) {
            Some(metadata) => Ok(metadata),
            None => Err(PropCollectionError::PlayerNotFound),
        }
    }
    pub fn find_thrower_steamid(&self, entity_id: &i32) -> Result<u64, PropCollectionError> {
        let owner_entid = self.find_owner_entid(entity_id)?;
        let metadata = self.find_player_metadata(owner_entid as i32)?;
        match metadata.steamid {
            Some(s) => Ok(s),
            // Watch out
            None => Ok(0),
        }
    }
    pub fn find_thrower_name(&self, entity_id: &i32) -> Result<String, PropCollectionError> {
        let owner_entid = self.find_owner_entid(entity_id)?;
        let metadata = self.find_player_metadata(owner_entid as i32)?;
        match &metadata.name {
            Some(s) => Ok(s.to_owned()),
            None => Err(PropCollectionError::PlayerMetaDataNameNone),
        }
    }

    fn find_grenade_type(&self, entity_id: &i32) -> Option<String> {
        if let Some(Some(ent)) = self.entities.get(*entity_id as usize) {
            if let Some(cls) = self.cls_by_id.get(ent.cls_id as usize) {
                return Some(cls.name.to_string());
            }
        }
        None
    }

    pub fn collect_projectiles(&mut self, write_output: bool) {
        let projectile_ids = self.projectiles.iter().copied().collect::<Vec<_>>();
        for projectile_entid in projectile_ids {
            if !write_output {
                self.collect_projectile_record_once(projectile_entid);
                continue;
            }

            let grenade_type = match self.find_grenade_type(&projectile_entid) {
                Some(t) => {
                    if !t.contains("Projectile") && !self.parse_grenades {
                        continue;
                    } else {
                        t
                    }
                }
                None => continue,
            };
            let steamid = match self.find_thrower_steamid(&projectile_entid) {
                Ok(u) => u,
                _ => continue,
            };
            let name = match self.find_thrower_name(&projectile_entid) {
                Ok(x) => x,
                _ => continue,
            };
            // Projectiles are the only ones with coordinates others map to 0.0, map them to None as it is clearer.
            let (x, y, z) = if grenade_type.contains("Project") {
                let x = self.collect_cell_coordinate_grenade(CoordinateAxis::X, &projectile_entid).ok();
                let y = self.collect_cell_coordinate_grenade(CoordinateAxis::Y, &projectile_entid).ok();
                let z = self.collect_cell_coordinate_grenade(CoordinateAxis::Z, &projectile_entid).ok();
                (x, y, z)
            } else {
                (None, None, None)
            };

            self.projectile_records.push(ProjectileRecord {
                steamid: Some(steamid),
                name: Some(name.clone()),
                x: variant_to_f32(&x),
                y: variant_to_f32(&y),
                z: variant_to_f32(&z),
                tick: Some(self.tick),
                grenade_type: Some(grenade_type.clone()),
                entity_id: Some(projectile_entid),
                entity_serial: self.projectile_serial(projectile_entid),
                is_incendiary: self.projectile_is_incendiary(projectile_entid),
                initial_position: self.collect_projectile_vec3(
                    self.prop_controller.special_ids.grenade_initial_position,
                    &projectile_entid,
                ),
                initial_velocity: self.collect_projectile_vec3(
                    self.prop_controller.special_ids.initial_velocity,
                    &projectile_entid,
                ),
                smoke_detonation_position: self.collect_projectile_vec3(
                    self.prop_controller
                        .special_ids
                        .grenade_smoke_detonation_position,
                    &projectile_entid,
                ),
                bounces: self.collect_projectile_i32(
                    self.prop_controller.special_ids.grenade_bounces,
                    &projectile_entid,
                ),
            });

            // Insert these always
            let pairs = vec![
                (GRENADE_TYPE_ID, Some(Variant::String(grenade_type))),
                (STEAMID_ID, Some(Variant::U64(steamid))),
                (NAME_ID, Some(Variant::String(name))),
                (TICK_ID, Some(Variant::I32(self.tick))),
                (ENTITY_ID_ID, Some(Variant::I32(projectile_entid))),
                (GRENADE_X, x),
                (GRENADE_Y, y),
                (GRENADE_Z, z),
            ];
            for pair in pairs {
                self.output.entry(pair.0).or_insert_with(|| PropColumn::new()).push(pair.1);
            }

            for prop_info in &self.prop_controller.prop_infos {
                // Do these above, props in this loop are from the weapon entity.
                if prop_info.id == STEAMID_ID
                    || prop_info.id == NAME_ID
                    || prop_info.id == TICK_ID
                    || prop_info.id == GRENADE_TYPE_ID
                    || prop_info.id == ENTITY_ID_ID
                    || prop_info.id == GRENADE_X
                    || prop_info.id == GRENADE_Y
                    || prop_info.id == GRENADE_Z
                {
                    continue;
                }
                let prop = match self.get_prop_from_ent(&prop_info.id, &projectile_entid) {
                    Ok(p) => Some(p),
                    _ => None,
                };
                match prop {
                    Some(prop) => {
                        self.output.entry(prop_info.id).or_insert_with(|| PropColumn::new()).push(Some(prop));
                    }
                    None => {
                        self.output.entry(prop_info.id).or_insert_with(|| PropColumn::new()).push(None);
                    }
                }
            }
        }
    }

    fn collect_projectile_record_once(&mut self, projectile_entid: i32) {
        if let Some(index) = self
            .projectile_record_indices
            .get(&projectile_entid)
            .copied()
        {
            // A create/full-packet can replace an index without a preceding delete.
            // Only reuse the record when it still describes the same instance.
            if self.projectile_serial(projectile_entid).is_some_and(|serial| {
                self.projectile_records[index].entity_serial == Some(serial)
            }) {
                self.update_projectile_record_effects(index, projectile_entid);
                return;
            }
        }

        let initial_position = match self.collect_projectile_vec3(
            self.prop_controller.special_ids.grenade_initial_position,
            &projectile_entid,
        ) {
            Some(value) if projectile_vec3_is_meaningful(value) => value,
            _ => return,
        };
        let initial_velocity = match self.collect_projectile_vec3(
            self.prop_controller.special_ids.initial_velocity,
            &projectile_entid,
        ) {
            Some(value) if projectile_vec3_is_meaningful(value) => value,
            _ => return,
        };
        let grenade_type = match self.find_grenade_type(&projectile_entid) {
            Some(t) => {
                if !t.contains("Projectile") && !self.parse_grenades {
                    return;
                }
                t
            }
            None => return,
        };
        let steamid = match self.find_thrower_steamid(&projectile_entid) {
            Ok(value) => value,
            _ => return,
        };
        let name = match self.find_thrower_name(&projectile_entid) {
            Ok(value) => value,
            _ => return,
        };
        let (x, y, z) = if grenade_type.contains("Project") {
            let x = self
                .collect_cell_coordinate_grenade(CoordinateAxis::X, &projectile_entid)
                .ok();
            let y = self
                .collect_cell_coordinate_grenade(CoordinateAxis::Y, &projectile_entid)
                .ok();
            let z = self
                .collect_cell_coordinate_grenade(CoordinateAxis::Z, &projectile_entid)
                .ok();
            (x, y, z)
        } else {
            (None, None, None)
        };

        let index = self.projectile_records.len();
        self.projectile_records.push(ProjectileRecord {
            steamid: Some(steamid),
            name: Some(name),
            x: variant_to_f32(&x),
            y: variant_to_f32(&y),
            z: variant_to_f32(&z),
            tick: Some(self.tick),
            grenade_type: Some(grenade_type),
            entity_id: Some(projectile_entid),
            entity_serial: self.projectile_serial(projectile_entid),
            is_incendiary: self.projectile_is_incendiary(projectile_entid),
            initial_position: Some(initial_position),
            initial_velocity: Some(initial_velocity),
            smoke_detonation_position: self.collect_projectile_vec3(
                self.prop_controller
                    .special_ids
                    .grenade_smoke_detonation_position,
                &projectile_entid,
            ),
            bounces: self.collect_projectile_i32(
                self.prop_controller.special_ids.grenade_bounces,
                &projectile_entid,
            ),
        });
        self.projectile_record_indices
            .insert(projectile_entid, index);
        self.update_projectile_record_effects(index, projectile_entid);
    }

    fn update_projectile_record_effects(&mut self, index: usize, projectile_entid: i32) {
        let is_incendiary = self.projectile_is_incendiary(projectile_entid);
        let smoke_detonation_position = self.collect_projectile_vec3(
            self.prop_controller
                .special_ids
                .grenade_smoke_detonation_position,
            &projectile_entid,
        );
        let bounces = self.collect_projectile_i32(
            self.prop_controller.special_ids.grenade_bounces,
            &projectile_entid,
        );

        if let Some(record) = self.projectile_records.get_mut(index) {
            if is_incendiary.is_some() {
                record.is_incendiary = is_incendiary;
            }
            if smoke_detonation_position
                .is_some_and(projectile_vec3_is_meaningful)
            {
                record.smoke_detonation_position = smoke_detonation_position;
            }
            if bounces.is_some() {
                record.bounces = bounces;
            }
        }
    }

    fn projectile_serial(&self, entity_id: i32) -> Option<u32> {
        Some(self.entities.get(entity_id as usize)?.as_ref()?.serial)
    }

    fn projectile_is_incendiary(&self, entity_id: i32) -> Option<bool> {
        let prop_id = self.prop_controller.special_ids.is_incendiary_grenade?;
        match self.get_prop_from_ent(&prop_id, &entity_id).ok()? {
            Variant::Bool(value) => Some(value),
            _ => None,
        }
    }

    fn collect_projectile_vec3(&self, prop_id: Option<u32>, entity_id: &i32) -> Option<[f32; 3]> {
        let prop_id = prop_id?;
        match self.get_prop_from_ent(&prop_id, entity_id).ok()? {
            Variant::VecXYZ(value) => Some(value),
            _ => None,
        }
    }

    fn collect_projectile_i32(&self, prop_id: Option<u32>, entity_id: &i32) -> Option<i32> {
        let prop_id = prop_id?;
        match self.get_prop_from_ent(&prop_id, entity_id).ok()? {
            Variant::I32(value) => Some(value),
            Variant::U32(value) => Some(value as i32),
            _ => None,
        }
    }

    fn find_weapon_name(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let item_def_id = match self.prop_controller.special_ids.item_def {
            Some(x) => x,
            None => return Err(PropCollectionError::SpecialidsItemDefNotSet),
        };
        match self.find_weapon_prop(&item_def_id, entity_id) {
            Ok(Variant::U32(def_idx)) => {
                match WEAPINDICIES.get(&def_idx) {
                    Some(v) => return Ok(Variant::String(v.to_string())),
                    None => return Err(PropCollectionError::WeaponIdxMappingNotFound),
                };
            }
            Ok(_) => return Err(PropCollectionError::WeaponDefVariantWrongType),
            Err(e) => Err(e),
        }
    }
    pub fn collect_cell_coordinate_player(&self, axis: CoordinateAxis, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let coordinate = match axis {
            CoordinateAxis::X => {
                let x_prop_id = match self.prop_controller.special_ids.cell_x_player {
                    Some(x) => x,
                    None => return Err(PropCollectionError::PlayerSpecialIDCellXMissing),
                };
                let x_offset_id = match self.prop_controller.special_ids.cell_x_offset_player {
                    Some(x) => x,
                    None => return Err(PropCollectionError::PlayerSpecialIDOffsetXMissing),
                };
                let offset = self.get_prop_from_ent(&x_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&x_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
            CoordinateAxis::Y => {
                let y_prop_id = match self.prop_controller.special_ids.cell_y_player {
                    Some(y) => y,
                    None => return Err(PropCollectionError::PlayerSpecialIDCellYMissing),
                };
                let y_offset_id = match self.prop_controller.special_ids.cell_y_offset_player {
                    Some(y) => y,
                    None => return Err(PropCollectionError::PlayerSpecialIDOffsetYMissing),
                };
                let offset = self.get_prop_from_ent(&y_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&y_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
            CoordinateAxis::Z => {
                let z_prop_id = match self.prop_controller.special_ids.cell_z_player {
                    Some(z) => z,
                    None => return Err(PropCollectionError::PlayerSpecialIDCellZMissing),
                };
                let z_offset_id = match self.prop_controller.special_ids.cell_z_offset_player {
                    Some(z) => z,
                    None => return Err(PropCollectionError::PlayerSpecialIDOffsetZMissing),
                };
                let offset = self.get_prop_from_ent(&z_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&z_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
        };
        Ok(Variant::F32(coordinate?))
    }
    pub fn collect_cell_coordinate_grenade(&self, axis: CoordinateAxis, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        // Todo rename to be consistent with player special ids
        let coordinate = match axis {
            CoordinateAxis::X => {
                let x_prop_id = match self.prop_controller.special_ids.m_cell_x_grenade {
                    Some(x) => x,
                    None => return Err(PropCollectionError::GrenadeSpecialIDCellXMissing),
                };
                let x_offset_id = match self.prop_controller.special_ids.m_vec_x_grenade {
                    Some(x) => x,
                    None => return Err(PropCollectionError::GrenadeSpecialIDOffsetXMissing),
                };
                let offset = self.get_prop_from_ent(&x_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&x_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
            CoordinateAxis::Y => {
                let y_prop_id = match self.prop_controller.special_ids.m_cell_y_grenade {
                    Some(y) => y,
                    None => return Err(PropCollectionError::GrenadeSpecialIDCellYMissing),
                };
                let y_offset_id = match self.prop_controller.special_ids.m_vec_y_grenade {
                    Some(y) => y,
                    None => return Err(PropCollectionError::GrenadeSpecialIDOffsetYMissing),
                };

                let offset = self.get_prop_from_ent(&y_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&y_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
            CoordinateAxis::Z => {
                let z_prop_id = match self.prop_controller.special_ids.m_cell_z_grenade {
                    Some(z) => z,
                    None => return Err(PropCollectionError::GrenadeSpecialIDCellZMissing),
                };
                let z_offset_id = match self.prop_controller.special_ids.m_vec_z_grenade {
                    Some(z) => z,
                    None => return Err(PropCollectionError::GrenadeSpecialIDOffsetZMissing),
                };
                let offset = self.get_prop_from_ent(&z_offset_id, entity_id);
                let cell = self.get_prop_from_ent(&z_prop_id, entity_id);
                coord_from_cell(cell, offset)
            }
        };
        Ok(Variant::F32(coordinate?))
    }
    fn find_pitch_or_yaw(&self, entity_id: &i32, idx: usize) -> Result<Variant, PropCollectionError> {
        match self.prop_controller.special_ids.eye_angles {
            Some(prop_id) => match self.get_prop_from_ent(&prop_id, entity_id) {
                Ok(Variant::VecXYZ(v)) => return Ok(Variant::F32(v[idx])),
                Ok(_) => return Err(PropCollectionError::EyeAnglesWrongVariant),
                Err(e) => return Err(e),
            },
            None => Err(PropCollectionError::SpecialidsEyeAnglesNotSet),
        }
    }
    pub fn create_custom_prop(
        &self,
        prop_info: &PropInfo,
        entity_id: &i32,
        player: &PlayerMetaData,
    ) -> Result<Variant, PropCollectionError> {
        match prop_info.id {
            SERVER_TICK_ID => Ok(Variant::U32(self.net_tick)),
            WEAPON_RESERVE_AMMO_SECONDARY => self.find_weapon_prop(&(WEAPON_RESERVE_AMMO_BASE + 1), entity_id),
            PLAYER_X_ID => self.collect_cell_coordinate_player(CoordinateAxis::X, entity_id),
            PLAYER_Y_ID => self.collect_cell_coordinate_player(CoordinateAxis::Y, entity_id),
            PLAYER_Z_ID => self.collect_cell_coordinate_player(CoordinateAxis::Z, entity_id),
            VELOCITY_ID => self.collect_velocity(player),
            VELOCITY_X_ID => self.collect_velocity_axis(player, CoordinateAxis::X),
            VELOCITY_Y_ID => self.collect_velocity_axis(player, CoordinateAxis::Y),
            VELOCITY_Z_ID => self.collect_velocity_axis(player, CoordinateAxis::Z),
            PITCH_ID => self.find_pitch_or_yaw(entity_id, 0),
            YAW_ID => self.find_pitch_or_yaw(entity_id, 1),
            WEAPON_NAME_ID => self.find_weapon_name(entity_id),
            WEAPON_SKIN_NAME => self.find_weapon_skin_from_player(entity_id),
            WEAPON_SKIN_ID => self.find_weapon_skin_id_from_player(entity_id),
            WEAPON_PAINT_SEED => self.find_skin_paint_seed(player),
            WEAPON_FLOAT => self.find_skin_float(player),
            WEAPON_STICKERS_ID => self.find_stickers_from_active_weapon(player),
            WEAPON_ORIGINGAL_OWNER_ID => self.find_weapon_original_owner(entity_id),
            INVENTORY_ID => self.find_my_inventory(entity_id),
            INVENTORY_AS_IDS_ID => self.find_my_inventory_as_ids(entity_id),
            INVENTORY_WEAPON_COSMETICS_ID => self.find_my_inventory_weapon_cosmetics(entity_id),
            INVENTORY_AS_IDS_BITMASK => self.find_my_inventory_as_bitmask(entity_id),
            ENTITY_ID_ID => Ok(Variant::I32(*entity_id)),
            IS_ALIVE_ID => self.find_is_alive(entity_id),
            USERID_ID => self.get_userid(player),
            IS_AIRBORNE_ID => self.find_is_airborne(player),
            AGENT_SKIN_ID => self.find_agent_skin(player),
            USERCMD_INPUT_HISTORY_BASEID => {
                self.get_prop_from_ent(&USERCMD_INPUT_HISTORY_BASEID, entity_id)
            }
            USERCMD_SUBTICK_MOVES_BASEID => {
                self.get_prop_from_ent(&USERCMD_SUBTICK_MOVES_BASEID, entity_id)
            }
            USERCMD_CLIENT_TICK
            | USERCMD_ATTACK_START_HISTORY_INDEX_1
            | USERCMD_ATTACK_START_HISTORY_INDEX_2 => {
                self.get_prop_from_ent(&prop_info.id, entity_id)
            }
            GLOVE_PAINT_ID => self.find_glove_skin_id(entity_id),
            GLOVE_SKIN => self.find_glove_skin(entity_id),
            GLOVE_PAINT_SEED => self.find_glove_paint_seed(entity_id),
            GLOVE_PAINT_FLOAT => self.find_glove_paint_float(entity_id),
            _ => match prop_info.prop_name.as_str() {
                "CCSPlayerPawn.m_bSpottedByMask" => self.find_spotted(entity_id, prop_info),
                "CCSPlayerController.m_iCompTeammateColor" => self.find_player_color(player, prop_info),
                _ => Err(PropCollectionError::UnknownCustomPropName),
            },
        }
    }
    pub fn get_userid(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        for (_, st_player) in &self.stringtable_players {
            if player.steamid == Some(st_player.steamid) {
                return Ok(Variant::I32(st_player.userid));
            }
        }
        Err(PropCollectionError::UseridNotFound)
    }
    pub fn find_player_color(&self, player: &PlayerMetaData, prop_info: &PropInfo) -> Result<Variant, PropCollectionError> {
        if let Ok(Variant::I32(v)) = self.get_controller_prop(&prop_info.id, player) {
            let color = if let Some(col) = PLAYER_COLOR.get(&v) {
                col.to_string()
            } else {
                v.to_string()
            };
            return Ok(Variant::String(color));
        }
        Err(PropCollectionError::UseridNotFound)
    }
    pub fn find_is_airborne(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        if let Some(player_entity_id) = &player.player_entity_id {
            if let Some(id) = self.prop_controller.special_ids.is_airborn {
                if let Ok(Variant::U32(airborn_h)) = self.get_prop_from_ent(&id, &player_entity_id) {
                    return Ok(Variant::Bool(airborn_h == IS_AIRBORNE_CONST));
                }
            }
        }
        Ok(Variant::Bool(false))
    }
    pub fn find_skin_float(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        if let Some(player_entity_id) = &player.player_entity_id {
            return self.find_weapon_prop(&WEAPON_FLOAT, &player_entity_id);
        }
        Err(PropCollectionError::PlayerNotFound)
    }
    pub fn find_stickers_from_active_weapon(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        let p = match self.prop_controller.special_ids.active_weapon {
            Some(p) => p,
            None => return Err(PropCollectionError::SpecialidsActiveWeaponNotSet),
        };
        if let Some(eid) = player.player_entity_id {
            return match self.get_prop_from_ent(&p, &eid) {
                Ok(Variant::U32(weap_handle)) => {
                    // Could be more specific
                    let weapon_entity_id = demo_network_ehandle_index(weap_handle);
                    self.find_stickers(&weapon_entity_id)
                }
                Ok(_) => Err(PropCollectionError::WeaponHandleIncorrectVariant),
                Err(e) => Err(e),
            };
        }
        Err(PropCollectionError::PlayerNotFound)
    }

    pub fn find_stickers(&self, weapon_entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let signature = self.entities.get(*weapon_entity_id as usize)
            .and_then(Option::as_ref)
            .map(|entity| (entity.serial, entity.cosmetic_revision));
        if let Some(signature) = signature {
            if let Some((cached_signature, stickers)) = self.weapon_sticker_cache.borrow().get(weapon_entity_id) {
                if *cached_signature == signature {
                    return Ok(Variant::Stickers(Arc::clone(stickers)));
                }
            }
        }

        let stickers: Arc<[Sticker]> = if let Some(cosmetic) = self.cached_weapon_cosmetic(weapon_entity_id) {
            into_shared_slice(cosmetic.stickers.clone())
        } else {
            // Preserve the legacy partial-evidence behavior for malformed weapon
            // entities that expose attributes without a usable item definition.
            let mut attributes = Vec::new();
            for idx in 0..64 {
                let Ok(Variant::U32(definition_index)) = self.get_prop_from_ent(&(WEAPON_ATTRIBUTE_DEF_INDEX_ID + idx), weapon_entity_id) else {
                    continue;
                };
                let Ok(Variant::F32(raw_value)) = self.get_prop_from_ent(&(WEAPON_SKIN_ID + idx), weapon_entity_id) else {
                    continue;
                };
                attributes.push(StickerAttribute {
                    definition_index,
                    raw_value,
                });
            }
            into_shared_slice(stickers_from_attributes(attributes))
        };

        if let Some(signature) = signature {
            self.weapon_sticker_cache.borrow_mut().insert(*weapon_entity_id, (signature, Arc::clone(&stickers)));
        }
        Ok(Variant::Stickers(stickers))
    }
    pub fn find_skin_paint_seed(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        if let Some(player_entity_id) = &player.player_entity_id {
            if let Ok(Variant::F32(f)) = self.find_weapon_prop(&WEAPON_PAINT_SEED, &player_entity_id) {
                return Ok(Variant::U32(f as u32));
            }
        }
        return Ok(Variant::U32(0));
    }
    pub fn find_agent_skin(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        self.find_agent_skin_name(player).map(|name| Variant::String(name.to_owned()))
    }
    fn find_agent_skin_name(&self, player: &PlayerMetaData) -> Result<&'static str, PropCollectionError> {
        let cache_key = player.steamid.zip(player.team_num);
        if let Some(key) = cache_key {
            if let Some(agent_id) = self.stable_agent_skin_cache.borrow().get(&key) {
                if let Some(agent) = AGENTSMAP.get(agent_id) {
                    return Ok(*agent);
                }
            }
        }
        let id = match self.prop_controller.special_ids.agent_skin_idx {
            Some(i) => i,
            None => return Err(PropCollectionError::AgentSpecialIdNotSet),
        };
        match self.get_controller_prop(&id, player) {
            Ok(Variant::U32(agent_id)) => match AGENTSMAP.get(&agent_id) {
                Some(agent) => {
                    if let Some(key) = cache_key.filter(|_| !is_map_based_default_agent(&agent)) {
                        self.stable_agent_skin_cache
                            .borrow_mut()
                            .insert(key, agent_id);
                    }
                    return Ok(*agent);
                }
                None => return Err(PropCollectionError::AgentIdNotFound),
            },
            Ok(_) => return Err(PropCollectionError::AgentIncorrectVariant),
            Err(_) => return Err(PropCollectionError::AgentPropNotFound),
        }
    }
    pub fn collect_velocity(&self, player: &PlayerMetaData) -> Result<Variant, PropCollectionError> {
        if let Some(s) = player.steamid {
            let steamids = self.output.get(&STEAMID_ID);
            let indicies = self.find_wanted_indicies(steamids, s);

            let x = self.velocity_from_indicies(&indicies, CoordinateAxis::X)?;
            let y = self.velocity_from_indicies(&indicies, CoordinateAxis::Y)?;

            if let (Variant::F32(x), Variant::F32(y)) = (x, y) {
                return Ok(Variant::F32((f32::powi(x, 2) + f32::powi(y, 2)).sqrt()));
            }
        }
        return Err(PropCollectionError::PlayerNotFound);
    }
    fn collect_velocity_cached(
        &self,
        player: &PlayerMetaData,
        indicies_cache: &mut Option<Vec<usize>>,
    ) -> Result<Variant, PropCollectionError> {
        let indicies = self.cached_velocity_indicies(player, indicies_cache)?;
        let x = self.velocity_from_indicies(indicies, CoordinateAxis::X)?;
        let y = self.velocity_from_indicies(indicies, CoordinateAxis::Y)?;

        if let (Variant::F32(x), Variant::F32(y)) = (x, y) {
            return Ok(Variant::F32((f32::powi(x, 2) + f32::powi(y, 2)).sqrt()));
        }
        Err(PropCollectionError::VelocityNotFound)
    }
    pub fn collect_velocity_axis(&self, player: &PlayerMetaData, axis: CoordinateAxis) -> Result<Variant, PropCollectionError> {
        if let Some(s) = player.steamid {
            let steamids = self.output.get(&STEAMID_ID);
            let indicies = self.find_wanted_indicies(steamids, s);
            return Ok(self.velocity_from_indicies(&indicies, axis)?);
        }
        return Err(PropCollectionError::PlayerNotFound);
    }
    fn collect_velocity_axis_cached(
        &self,
        player: &PlayerMetaData,
        axis: CoordinateAxis,
        indicies_cache: &mut Option<Vec<usize>>,
    ) -> Result<Variant, PropCollectionError> {
        let indicies = self.cached_velocity_indicies(player, indicies_cache)?;
        self.velocity_from_indicies(indicies, axis)
    }
    fn cached_velocity_indicies<'b>(
        &self,
        player: &PlayerMetaData,
        indicies_cache: &'b mut Option<Vec<usize>>,
    ) -> Result<&'b [usize], PropCollectionError> {
        if indicies_cache.is_none() {
            let steamid = player.steamid.ok_or(PropCollectionError::PlayerNotFound)?;
            *indicies_cache = Some(self.find_wanted_indicies(self.output.get(&STEAMID_ID), steamid));
        }
        Ok(indicies_cache.as_deref().unwrap_or(&[]))
    }
    fn find_most_recent_coordinate_idx(&self, optv: Option<&PropColumn>, wanted_steamid: u64) -> Option<usize> {
        if let Some(v) = optv {
            if let Some(VarVec::U64(steamid_vec)) = &v.data {
                for idx in (0..steamid_vec.len()).rev() {
                    if steamid_vec[idx] == Some(wanted_steamid) {
                        return Some(idx);
                    }
                }
            }
        }
        None
    }
    fn find_last_coordinate_idx(&self, optv: Option<&PropColumn>, wanted_steamid: u64, cur_idx: Option<usize>) -> Option<usize> {
        let cur_idx = cur_idx?;
        if let VarVec::U64(steamid_vec) = optv?.data.as_ref()? {
            // iterate backwards until steamid is our wanted player and > 1sec ago
            for idx in (0..steamid_vec.len()).rev() {
                let sid = steamid_vec[idx];
                if sid == Some(wanted_steamid) && idx != cur_idx {
                    return Some(idx);
                }
            }
        }
        None
    }
    fn find_wanted_indicies(&self, optv: Option<&PropColumn>, wanted_steamid: u64) -> Vec<usize> {
        let idx1 = self.find_most_recent_coordinate_idx(optv, wanted_steamid);
        let idx2 = self.find_last_coordinate_idx(optv, wanted_steamid, idx1);
        if let (Some(idx1), Some(idx2)) = (idx1, idx2) {
            return vec![idx1, idx2];
        }
        vec![]
    }

    fn velocity_from_indicies(&self, indicies: &[usize], axis: CoordinateAxis) -> Result<Variant, PropCollectionError> {
        let col = match axis {
            CoordinateAxis::X => self.output.get(&PLAYER_X_ID),
            CoordinateAxis::Y => self.output.get(&PLAYER_Y_ID),
            CoordinateAxis::Z => self.output.get(&PLAYER_Z_ID),
        };
        if let Some(c) = col {
            if let Some((Some(v1), Some(v2))) = self.index_coordinates_from_propcol(c, indicies) {
                return Ok(Variant::F32((v1 * 64.0) - (v2 * 64.0)));
            }
        }
        return Err(PropCollectionError::VelocityNotFound);
    }
    fn index_coordinates_from_propcol(&self, propcol: &PropColumn, indicies: &[usize]) -> Option<(Option<f32>, Option<f32>)> {
        if indicies.len() != 2 {
            return None;
        }
        if let Some(VarVec::F32(steamid_vec)) = &propcol.data {
            let first = steamid_vec[indicies[0]];
            let second = steamid_vec[indicies[1]];
            return Some((first, second));
        }
        None
    }

    pub fn find_is_alive(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        match self.prop_controller.special_ids.life_state {
            Some(id) => match self.get_prop_from_ent(&id, entity_id) {
                Ok(Variant::U32(0)) => return Ok(Variant::Bool(true)),
                Ok(_) => {}
                Err(_) => {}
            },
            None => {}
        }
        Ok(Variant::Bool(false))
    }
    pub fn find_spotted(&self, entity_id: &i32, prop_info: &PropInfo) -> Result<Variant, PropCollectionError> {
        match self.get_prop_from_ent(&prop_info.id, entity_id) {
            Ok(Variant::U32(mask)) => {
                return Ok(Variant::U64Vec(self.steamids_from_mask(mask)));
            }
            Ok(_) => return Err(PropCollectionError::SpottedIncorrectVariant),
            Err(e) => return Err(e),
        }
    }
    fn steamids_from_mask(&self, uid: u32) -> Vec<u64> {
        let mut steamids = vec![];
        for i in 0..16 {
            if (uid & (1 << i)) != 0 {
                if let Some(user) = self.find_user_by_controller_id((i + 1) as i32) {
                    steamids.push(user.steamid.unwrap_or(0))
                }
            }
        }
        steamids
    }
    pub fn find_my_inventory(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let mut names = vec![];
        let mut unique_eids = vec![];

        match self.find_is_alive(entity_id) {
            Ok(Variant::Bool(true)) => {}
            _ => return Ok(Variant::StringVec(vec![])),
        };
        let inventory_max_len = match self.get_prop_from_ent(&(MY_WEAPONS_OFFSET as u32), entity_id) {
            Ok(Variant::U32(p)) => p,
            _ => return Err(PropCollectionError::InventoryMaxNotFound),
        };
        for i in 1..inventory_max_len + 1 {
            let prop_id = MY_WEAPONS_OFFSET + i;
            match self.get_prop_from_ent(&(prop_id as u32), entity_id) {
                Err(_e) => {}
                Ok(Variant::U32(x)) => {
                    let eid = demo_network_ehandle_index(x);
                    // Sometimes multiple references to same eid?
                    if unique_eids.contains(&eid) {
                        continue;
                    }
                    unique_eids.push(eid);

                    if let Some(item_def_id) = &self.prop_controller.special_ids.item_def {
                        let res = match self.get_prop_from_ent(item_def_id, &eid) {
                            Err(_e) => continue,
                            Ok(def) => def,
                        };
                        self.insert_equipment_name(&mut names, res, entity_id);
                    }
                }
                _ => {}
            }
        }
        Ok(Variant::StringVec(names))
    }
    pub fn find_my_inventory_as_ids(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let snapshot = self.player_inventory_snapshot(entity_id, false)?;
        Ok(Variant::U32Vec(snapshot.ids.clone()))
    }
    pub fn find_my_inventory_weapon_cosmetics(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let snapshot = self.player_inventory_snapshot(entity_id, true)?;
        let cosmetics = snapshot.cosmetics.as_ref().map(Arc::clone).unwrap_or_default();
        Ok(Variant::InventoryWeaponCosmetics(cosmetics))
    }

    fn player_inventory_snapshot(
        &self,
        entity_id: &i32,
        include_cosmetics: bool,
    ) -> Result<Ref<'_, PlayerInventorySnapshot>, PropCollectionError> {
        if let Some(snapshot) = current_inventory_snapshot(
            &self.player_inventory_snapshot_cache,
            *entity_id,
            self.inventory_generation,
        ) {
            if include_cosmetics && snapshot.cosmetics.is_none() {
                let cosmetics = self.collect_inventory_cosmetics(&snapshot.weapon_eids);
                // Release the read before upgrading the cached snapshot. Only the
                // cosmetics Arc changes; its inventory vectors remain in the cache.
                drop(snapshot);
                self.player_inventory_snapshot_cache
                    .borrow_mut()
                    .get_mut(entity_id)
                    .expect("current inventory snapshot")
                    .cosmetics = Some(cosmetics);
                return Ok(current_inventory_snapshot(
                    &self.player_inventory_snapshot_cache,
                    *entity_id,
                    self.inventory_generation,
                ).expect("upgraded inventory snapshot"));
            }
            return Ok(snapshot);
        }

        let alive = matches!(self.find_is_alive(entity_id), Ok(Variant::Bool(true)));
        // Preserve the previous cache entry if a live player's inventory length
        // is unavailable. Once validation succeeds, reuse its owned allocations.
        let inventory_max_len = if alive {
            match self.get_prop_from_ent(&(MY_WEAPONS_OFFSET as u32), entity_id) {
                Ok(Variant::U32(value)) => value,
                _ => return Err(PropCollectionError::InventoryMaxNotFound),
            }
        } else {
            0
        };
        let player_signature = self.inventory_player_signature(entity_id);
        let mut snapshot = self.player_inventory_snapshot_cache.borrow_mut()
            .remove(entity_id)
            .unwrap_or_else(|| PlayerInventorySnapshot {
                generation: self.inventory_generation,
                player_signature,
                weapon_eids: Vec::new(),
                weapon_signature: Vec::new(),
                ids: Vec::new(),
                cosmetics: None,
            });
        snapshot.generation = self.inventory_generation;
        snapshot.weapon_eids.clear();
        snapshot.ids.clear();
        if !alive {
            snapshot.player_signature = player_signature;
            snapshot.weapon_signature.clear();
            snapshot.cosmetics = include_cosmetics.then(Arc::default);
            self.player_inventory_snapshot_cache
                .borrow_mut()
                .insert(*entity_id, snapshot);
            return Ok(current_inventory_snapshot(
                &self.player_inventory_snapshot_cache,
                *entity_id,
                self.inventory_generation,
            ).expect("inserted inventory snapshot"));
        }

        for index in 1..=inventory_max_len {
            let prop_id = MY_WEAPONS_OFFSET + index;
            let eid = match self.get_prop_from_ent(&(prop_id as u32), entity_id) {
                Ok(Variant::U32(handle)) => demo_network_ehandle_index(handle),
                _ => continue,
            };
            if !snapshot.weapon_eids.contains(&eid) {
                snapshot.weapon_eids.push(eid);
            }
        }

        let mut reusable_cosmetics = snapshot.player_signature == player_signature
            && snapshot.weapon_signature.len() == snapshot.weapon_eids.len();
        for (index, eid) in snapshot.weapon_eids.iter().enumerate() {
            let entity = self.entities.get(*eid as usize).and_then(Option::as_ref);
            let signature = (
                *eid,
                entity.map(|entity| entity.serial).unwrap_or(u32::MAX),
                entity.map(|entity| entity.cosmetic_revision).unwrap_or(u64::MAX),
            );
            if let Some(previous) = snapshot.weapon_signature.get_mut(index) {
                reusable_cosmetics &= *previous == signature;
                *previous = signature;
            } else {
                snapshot.weapon_signature.push(signature);
            }
        }
        snapshot.weapon_signature.truncate(snapshot.weapon_eids.len());
        snapshot.player_signature = player_signature;
        if let Some(item_def_id) = self.prop_controller.special_ids.item_def {
            for eid in &snapshot.weapon_eids {
                if let Ok(item_def) = self.get_prop_from_ent(&item_def_id, eid) {
                    self.insert_equipment_id(&mut snapshot.ids, item_def, entity_id);
                }
            }
        }

        if !reusable_cosmetics {
            snapshot.cosmetics = None;
        }
        if include_cosmetics && snapshot.cosmetics.is_none() {
            snapshot.cosmetics = Some(self.collect_inventory_cosmetics(&snapshot.weapon_eids));
        }
        self.player_inventory_snapshot_cache
            .borrow_mut()
            .insert(*entity_id, snapshot);
        Ok(current_inventory_snapshot(
            &self.player_inventory_snapshot_cache,
            *entity_id,
            self.inventory_generation,
        ).expect("inserted inventory snapshot"))
    }

    fn inventory_player_signature(
        &self,
        player_entity_id: &i32,
    ) -> (u32, Option<u64>, Option<u32>) {
        let serial = self
            .entities
            .get(*player_entity_id as usize)
            .and_then(Option::as_ref)
            .map(|entity| entity.serial)
            .unwrap_or(u32::MAX);
        let player = self.players.get(player_entity_id);
        (
            serial,
            player.and_then(|player| player.steamid),
            player.and_then(|player| player.team_num),
        )
    }

    fn collect_inventory_cosmetics(
        &self,
        weapon_eids: &[i32],
    ) -> Arc<[InventoryWeaponCosmetic]> {
        // Entity/revision caching preserves each item's actual appearance. A
        // player/side/weapon slot must not overwrite later purchased items.
        into_shared_slice(weapon_eids
            .iter()
            .filter_map(|eid| self.cached_weapon_cosmetic(eid))
            .map(|item| item.as_ref().clone())
            .collect())
    }

    pub(crate) fn cached_weapon_cosmetic(
        &self,
        weapon_entity_id: &i32,
    ) -> Option<Arc<InventoryWeaponCosmetic>> {
        let signature = self
            .entities
            .get(*weapon_entity_id as usize)?
            .as_ref()
            .map(|entity| (entity.serial, entity.cosmetic_revision))?;
        {
            let cache = self.weapon_econ_snapshot_cache.borrow();
            if let Some((cached_signature, cached)) = cache.get(weapon_entity_id) {
                if *cached_signature == signature {
                    return cached.as_ref().map(Arc::clone);
                }
            }
        }

        let cosmetic = self
            .collect_weapon_cosmetic(weapon_entity_id)
            .map(Arc::new);
        self.weapon_econ_snapshot_cache
            .borrow_mut()
            .insert(
                *weapon_entity_id,
                (signature, cosmetic.as_ref().map(Arc::clone)),
            );
        cosmetic
    }

    fn collect_weapon_cosmetic(
        &self,
        weapon_entity_id: &i32,
    ) -> Option<InventoryWeaponCosmetic> {
        let item_def_id = self.prop_controller.special_ids.item_def?;
        let item_def_index = match self.get_prop_from_ent(&item_def_id, weapon_entity_id) {
            Ok(Variant::U32(def)) => def,
            Ok(Variant::I32(def)) if def >= 0 => def as u32,
            _ => return None,
        };
        let item_id_high = self.weapon_prop_u32(
            self.prop_controller.special_ids.item_id_high,
            weapon_entity_id,
        );
        let item_id_low = self.weapon_prop_u32(
            self.prop_controller.special_ids.item_id_low,
            weapon_entity_id,
        );
        let item_account_id = self.weapon_prop_u32(
            self.prop_controller.special_ids.item_account_id,
            weapon_entity_id,
        );
        let original_owner_xuid = self.weapon_original_owner_from_eid(weapon_entity_id);

        let paint_kit = match self.find_weapon_skin_id(weapon_entity_id) {
            Ok(Variant::U32(value)) => value,
            _ => 0,
        };
        let paint_seed = match self.get_prop_from_ent(&WEAPON_PAINT_SEED, weapon_entity_id) {
            Ok(Variant::F32(value)) if value.is_finite() && value >= 0.0 => value as u32,
            Ok(Variant::U32(value)) => value,
            _ => 0,
        };
        let paint_wear = match self.get_prop_from_ent(&WEAPON_FLOAT, weapon_entity_id) {
            Ok(Variant::F32(value)) => value,
            _ => -1.0,
        };
        let entity_quality = self
            .prop_controller
            .special_ids
            .entity_quality
            .and_then(|quality_id| match self.get_prop_from_ent(&quality_id, weapon_entity_id) {
                Ok(Variant::I32(value)) => Some(value),
                Ok(Variant::U32(value)) => i32::try_from(value).ok(),
                Ok(Variant::F32(value)) if value.is_finite() && value.fract() == 0.0 => {
                    Some(value as i32)
                }
                _ => None,
            });
        let (attributes, stickers) =
            self.find_weapon_econ_attributes_and_stickers(weapon_entity_id);
        let stattrak_counter =
            self.weapon_stattrak_counter(weapon_entity_id, &attributes);
        let custom_name = self
            .prop_controller
            .special_ids
            .custom_name
            .and_then(|custom_name_id| {
                match self.get_prop_from_ent(&custom_name_id, weapon_entity_id) {
                    Ok(Variant::String(value)) => Some(value),
                    _ => None,
                }
            });
        Some(InventoryWeaponCosmetic {
            item_def_index,
            item_id_high,
            item_id_low,
            item_account_id,
            original_owner_xuid,
            paint_kit,
            paint_seed,
            paint_wear,
            entity_quality,
            stattrak_counter,
            attributes,
            custom_name,
            stickers,
        })
    }

    fn find_weapon_econ_attributes_and_stickers(
        &self,
        weapon_entity_id: &i32,
    ) -> (Vec<InventoryWeaponAttribute>, Vec<Sticker>) {
        let mut attributes = Vec::new();
        let mut sticker_attributes = Vec::new();
        for idx in 0..64 {
            let Ok(Variant::U32(definition_index)) =
                self.get_prop_from_ent(&(WEAPON_ATTRIBUTE_DEF_INDEX_ID + idx), weapon_entity_id)
            else {
                continue;
            };
            let Ok(raw_value) = self.get_prop_from_ent(&(WEAPON_SKIN_ID + idx), weapon_entity_id)
            else {
                continue;
            };
            if let Variant::F32(raw_value) = &raw_value {
                sticker_attributes.push(StickerAttribute {
                    definition_index,
                    raw_value: *raw_value,
                });
            }
            let Some((raw_value, raw_value_bits)) = econ_attribute_raw_value(raw_value) else {
                continue;
            };
            attributes.push(InventoryWeaponAttribute {
                definition_index,
                raw_value,
                raw_value_bits,
            });
        }
        (attributes, stickers_from_attributes(sticker_attributes))
    }

    pub fn find_my_inventory_as_bitmask(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let mut bitmask = 0;
        let mut unique_eids = vec![];

        match self.find_is_alive(entity_id) {
            Ok(Variant::Bool(true)) => {}
            _ => return Ok(Variant::U64(0)),
        };
        let inventory_max_len = match self.get_prop_from_ent(&(MY_WEAPONS_OFFSET as u32), entity_id) {
            Ok(Variant::U32(p)) => p,
            _ => return Err(PropCollectionError::InventoryMaxNotFound),
        };

        for i in 1..inventory_max_len + 1 {
            let prop_id = MY_WEAPONS_OFFSET + i;
            match self.get_prop_from_ent(&(prop_id as u32), entity_id) {
                Err(_e) => {}
                Ok(Variant::U32(x)) => {
                    let eid = demo_network_ehandle_index(x);
                    // Sometimes multiple references to same eid?
                    if unique_eids.contains(&eid) {
                        continue;
                    }
                    unique_eids.push(eid);
                    if let Some(item_def_id) = &self.prop_controller.special_ids.item_def {
                        let res = match self.get_prop_from_ent(item_def_id, &eid) {
                            Err(_e) => continue,
                            Ok(def) => def,
                        };
                        self.insert_equipment_id_bitmask(&mut bitmask, res, entity_id);
                    }
                }
                _ => {}
            }
        }
        Ok(Variant::U64(bitmask))
    }

    fn insert_equipment_id_bitmask(&self, bitmask: &mut u64, res: Variant, player_entid: &i32) {
        if let Variant::U32(def_idx) = res {
            match WEAPINDICIES.get(&def_idx) {
                None => return,
                Some(weap_name) => {
                    match weap_name {
                        // Check how many flashbangs player has (only prop that works like this)
                        &"Flashbang" => {
                            if let Ok(Variant::U32(2)) = self.get_prop_from_ent(&GRENADE_AMMO_ID, player_entid) {
                                *bitmask |= 1 << def_idx;
                            }
                            *bitmask |= 1 << def_idx;
                        }
                        // c4 seems bugged. Find c4 entity and check owner from it.
                        &"C4 Explosive" => {
                            if let Some(c4_owner_id) = self.find_c4_owner() {
                                if *player_entid == c4_owner_id {
                                    *bitmask |= 1 << def_idx;
                                }
                            }
                        }
                        _ => {
                            *bitmask |= 1 << def_idx;
                        }
                    }
                }
            };
        }
    }
    fn insert_equipment_id(&self, names: &mut Vec<u32>, res: Variant, player_entid: &i32) {
        if let Variant::U32(def_idx) = res {
            match WEAPINDICIES.get(&def_idx) {
                None => return,
                Some(weap_name) => {
                    match weap_name {
                        // Check how many flashbangs player has (only prop that works like this)
                        &"Flashbang" => {
                            if let Ok(Variant::U32(2)) = self.get_prop_from_ent(&FLASHBANG_AMMO_ID, player_entid) {
                                names.push(def_idx);
                            }
                            names.push(def_idx);
                        }
                        // c4 seems bugged. Find c4 entity and check owner from it.
                        &"C4 Explosive" => {
                            if let Some(c4_owner_id) = self.find_c4_owner() {
                                if *player_entid == c4_owner_id {
                                    names.push(def_idx);
                                }
                            }
                        }
                        _ => {
                            names.push(def_idx);
                        }
                    }
                }
            };
        }
    }

    fn insert_equipment_name(&self, names: &mut Vec<String>, res: Variant, player_entid: &i32) {
        if let Variant::U32(def_idx) = res {
            match WEAPINDICIES.get(&def_idx) {
                None => return,
                Some(weap_name) => {
                    match weap_name {
                        // Check how many flashbangs player has (only prop that works like this)
                        &"Flashbang" => {
                            if let Ok(Variant::U32(2)) = self.get_prop_from_ent(&FLASHBANG_AMMO_ID, player_entid) {
                                names.push(weap_name.to_string());
                            }
                            names.push(weap_name.to_string());
                        }
                        // c4 seems bugged. Find c4 entity and check owner from it.
                        &"C4 Explosive" => {
                            if let Some(c4_owner_id) = self.find_c4_owner() {
                                if *player_entid == c4_owner_id {
                                    names.push(weap_name.to_string());
                                }
                            }
                        }
                        _ => {
                            names.push(weap_name.to_string());
                        }
                    }
                }
            };
        }
    }
    fn find_c4_owner(&self) -> Option<i32> {
        if let Some(c4ent) = self.c4_entity_id {
            if let Some(id) = self.prop_controller.special_ids.h_owner_entity {
                if let Ok(Variant::U32(u)) = self.get_prop_from_ent(&id, &c4ent) {
                    return Some(demo_network_ehandle_index(u));
                }
            }
        }
        None
    }
    pub fn find_weapon_original_owner(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let low_id = match self.prop_controller.special_ids.orig_own_low {
            Some(id) => id,
            None => return Err(PropCollectionError::OriginalOwnerXuidIdLowNotSet),
        };
        let high_id = match self.prop_controller.special_ids.orig_own_high {
            Some(id) => id,
            None => return Err(PropCollectionError::OriginalOwnerXuidIdHighNotSet),
        };
        let low_bits = match self.find_weapon_prop(&low_id, entity_id) {
            Ok(Variant::U32(val)) => val,
            Ok(_) => return Err(PropCollectionError::OriginalOwnerXuidlowIncorrectVariant),
            Err(_e) => return Err(PropCollectionError::OriginalOwnerXuidLowNotFound),
        };
        let high_bits = match self.find_weapon_prop(&high_id, entity_id) {
            Ok(Variant::U32(val)) => val,
            Ok(_) => return Err(PropCollectionError::OriginalOwnerXuidHighIncorrectVariant),
            Err(_e) => return Err(PropCollectionError::OriginalOwnerXuidHighNotFound),
        };
        let combined = (high_bits as u64) << 32 | (low_bits as u64);
        Ok(Variant::String(combined.to_string()))
    }

    fn weapon_prop_u32(&self, prop_id: Option<u32>, weapon_entity_id: &i32) -> Option<u32> {
        let prop_id = prop_id?;
        self.get_prop_from_ent(&prop_id, weapon_entity_id)
            .ok()
            .and_then(variant_to_nonnegative_u32)
    }

    fn weapon_stattrak_counter(
        &self,
        weapon_entity_id: &i32,
        attributes: &[InventoryWeaponAttribute],
    ) -> Option<i32> {
        let fallback = self.prop_controller
            .special_ids
            .fallback_stattrak
            .and_then(|stattrak_id| match self.get_prop_from_ent(&stattrak_id, weapon_entity_id) {
                Ok(Variant::I32(value)) => Some(value),
                Ok(Variant::U32(value)) => i32::try_from(value).ok(),
                Ok(Variant::F32(value)) if value.is_finite() && value.fract() == 0.0 => {
                    Some(value as i32)
                }
                _ => None,
            });
        if fallback.is_some() {
            return fallback;
        }
        attributes
            .iter()
            .find(|attribute| attribute.definition_index == 80)
            .and_then(|attribute| i32::try_from(attribute.raw_value_bits).ok())
    }

    fn weapon_original_owner_from_eid(&self, weapon_entity_id: &i32) -> Option<u64> {
        let low = self.weapon_prop_u32(self.prop_controller.special_ids.orig_own_low, weapon_entity_id)?;
        let high = self.weapon_prop_u32(self.prop_controller.special_ids.orig_own_high, weapon_entity_id)?;
        let combined = (u64::from(high) << 32) | u64::from(low);
        (combined != 0).then_some(combined)
    }

    pub fn find_weapon_skin(&self, weapon_entity_id: &i32) -> Result<Variant, PropCollectionError> {
        match self.get_prop_from_ent(&WEAPON_SKIN_ID, weapon_entity_id) {
            Ok(Variant::F32(f)) => {
                // The value is stored as a float for some reason
                if f.fract() == 0.0 && f >= 0.0 {
                    let idx = f as u32;
                    match PAINTKITS.get(&idx) {
                        Some(kit) => Ok(Variant::String(kit.to_string())),
                        None => Err(PropCollectionError::WeaponSkinNoSkinMapping),
                    }
                } else {
                    return Err(PropCollectionError::WeaponSkinFloatConvertionError);
                }
            }
            Ok(_) => return Err(PropCollectionError::WeaponSkinIdxIncorrectVariant),
            Err(e) => return Err(e),
        }
    }
    pub fn find_weapon_skin_id_from_player(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        let p = match self.prop_controller.special_ids.active_weapon {
            Some(p) => p,
            None => return Err(PropCollectionError::SpecialidsActiveWeaponNotSet),
        };
        return match self.get_prop_from_ent(&p, player_entid) {
            Ok(Variant::U32(weap_handle)) => {
                let weapon_entity_id = demo_network_ehandle_index(weap_handle);
                self.find_weapon_skin_id(&weapon_entity_id)
            }
            Ok(_) => Err(PropCollectionError::WeaponHandleIncorrectVariant),
            Err(e) => Err(e),
        };
    }
    pub fn find_weapon_skin_id(&self, weapon_entity_id: &i32) -> Result<Variant, PropCollectionError> {
        match self.get_prop_from_ent(&WEAPON_SKIN_ID, weapon_entity_id) {
            Ok(Variant::F32(f)) => {
                // The value is stored as a float for some reason
                if f.fract() == 0.0 && f >= 0.0 {
                    return Ok(Variant::U32(f as u32));
                } else {
                    return Err(PropCollectionError::WeaponSkinFloatConvertionError);
                }
            }
            Ok(_) => return Err(PropCollectionError::WeaponSkinIdxIncorrectVariant),
            Err(e) => return Err(e),
        }
    }
    pub fn find_weapon_skin_from_player(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        let p = match self.prop_controller.special_ids.active_weapon {
            Some(p) => p,
            None => return Err(PropCollectionError::SpecialidsActiveWeaponNotSet),
        };
        return match self.get_prop_from_ent(&p, player_entid) {
            Ok(Variant::U32(weap_handle)) => {
                let weapon_entity_id = demo_network_ehandle_index(weap_handle);
                self.find_weapon_skin(&weapon_entity_id)
            }
            Ok(_) => Err(PropCollectionError::WeaponHandleIncorrectVariant),
            Err(e) => Err(e),
        };
    }
    pub fn find_glove_skin_id(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        match self.find_glove_attribute_value(player_entid, ECON_ATTR_SET_ITEM_TEXTURE_PREFAB) {
            Ok(Variant::F32(f)) => {
                // The value is stored as a float for some reason
                if f.fract() == 0.0 && f >= 0.0 {
                    return Ok(Variant::U32(f as u32));
                } else {
                    return Err(PropCollectionError::GloveSkinFloatConvertionError);
                }
            }
            Ok(_) => return Err(PropCollectionError::GloveSkinIdxIncorrectVariant),
            Err(e) => return Err(e),
        }
    }

    pub fn find_glove_skin(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        match self.find_glove_attribute_value(player_entid, ECON_ATTR_SET_ITEM_TEXTURE_PREFAB) {
            Ok(Variant::F32(f)) => {
                // The value is stored as a float for some reason
                if f.fract() == 0.0 && f >= 0.0 {
                    let idx = f as u32;
                    match PAINTKITS.get(&idx) {
                        Some(kit) => Ok(Variant::String(kit.to_string())),
                        None => Err(PropCollectionError::GloveSkinNoSkinMapping),
                    }
                } else {
                    return Err(PropCollectionError::GloveSkinFloatConvertionError);
                }
            }
            Ok(_) => return Err(PropCollectionError::GloveSkinIdxIncorrectVariant),
            Err(e) => return Err(e),
        }
    }

    pub fn find_glove_paint_seed(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        match self.find_glove_attribute_value(player_entid, ECON_ATTR_SET_ITEM_TEXTURE_SEED) {
            Ok(value) => glove_paint_seed_from_attribute(value)
                .map(Variant::U32)
                .ok_or(PropCollectionError::GloveSkinIdxIncorrectVariant),
            Err(e) => Err(e),
        }
    }

    pub fn find_glove_paint_float(&self, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        match self.find_glove_attribute_value(player_entid, ECON_ATTR_SET_ITEM_TEXTURE_WEAR) {
            Ok(p) => Ok(p),
            Err(e) => return Err(e),
        }
    }

    fn find_glove_attribute_value(
        &self,
        player_entid: &i32,
        definition_index: u32,
    ) -> Result<Variant, PropCollectionError> {
        let value_index = match definition_index {
            ECON_ATTR_SET_ITEM_TEXTURE_PREFAB => 0,
            ECON_ATTR_SET_ITEM_TEXTURE_SEED => 1,
            ECON_ATTR_SET_ITEM_TEXTURE_WEAR => 2,
            _ => return Err(PropCollectionError::GetPropFromEntPropNotFound),
        };
        let entity = self
            .entities
            .get(*player_entid as usize)
            .and_then(Option::as_ref)
            .ok_or(PropCollectionError::GetPropFromEntEntityNotFound)?;
        let signature = (entity.serial, entity.cosmetic_revision);
        if let Some((cached_signature, values)) =
            self.glove_attribute_cache.borrow().get(player_entid)
        {
            if *cached_signature == signature {
                return values[value_index]
                    .clone()
                    .ok_or(PropCollectionError::GetPropFromEntPropNotFound);
            }
        }

        let mut values: [Option<Variant>; 3] = Default::default();
        for idx in 0..64 {
            let Ok(current_definition_index) =
                self.get_prop_from_ent(&(GLOVE_ATTRIBUTE_DEF_INDEX_ID + idx), player_entid)
            else {
                continue;
            };
            let target = match variant_to_nonnegative_u32(current_definition_index) {
                Some(ECON_ATTR_SET_ITEM_TEXTURE_PREFAB) => 0,
                Some(ECON_ATTR_SET_ITEM_TEXTURE_SEED) => 1,
                Some(ECON_ATTR_SET_ITEM_TEXTURE_WEAR) => 2,
                _ => continue,
            };
            if values[target].is_none() {
                values[target] = self
                    .get_prop_from_ent(&(GLOVE_PAINT_ID + idx), player_entid)
                    .ok();
            }
        }

        for (index, legacy_id) in [GLOVE_PAINT_ID, GLOVE_PAINT_SEED, GLOVE_PAINT_FLOAT]
            .into_iter()
            .enumerate()
        {
            if values[index].is_none() {
                values[index] = self.get_prop_from_ent(&legacy_id, player_entid).ok();
            }
        }
        let result = values[value_index].clone();
        self.glove_attribute_cache
            .borrow_mut()
            .insert(*player_entid, (signature, values));
        result.ok_or(PropCollectionError::GetPropFromEntPropNotFound)
    }

    pub fn find_weapon_prop(&self, prop: &u32, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        let p = match self.prop_controller.special_ids.active_weapon {
            Some(p) => p,
            None => return Err(PropCollectionError::SpecialidsActiveWeaponNotSet),
        };
        match self.get_prop_from_ent(&p, player_entid) {
            Ok(Variant::U32(weap_handle)) => {
                // Could be more specific
                let weapon_entity_id = demo_network_ehandle_index(weap_handle);
                match self.get_prop_from_ent(&prop, &weapon_entity_id) {
                    Ok(p) => Ok(p),
                    Err(e) => match e {
                        PropCollectionError::GetPropFromEntEntityNotFound => Err(PropCollectionError::WeaponEntityNotFound),
                        PropCollectionError::GetPropFromEntPropNotFound => Err(PropCollectionError::WeaponEntityWantedPropNotFound),
                        _ => Err(e),
                    },
                }
            }
            Ok(_) => Err(PropCollectionError::WeaponHandleIncorrectVariant),
            Err(e) => Err(e),
        }
    }
    pub fn find_team_prop(&self, prop: &u32, player_entid: &i32) -> Result<Variant, PropCollectionError> {
        match self.prop_controller.special_ids.player_team_pointer {
            None => return Err(PropCollectionError::SpecialidsPlayerTeamPointerNotSet),
            Some(p) => {
                match self.get_prop_from_ent(&p, player_entid) {
                    Ok(Variant::U32(team_num)) => {
                        let team_entid = match team_num {
                            // 1 should be spectator
                            1 => self.teams.team1_entid,
                            2 => self.teams.team2_entid,
                            3 => self.teams.team3_entid,
                            _ => return Err(PropCollectionError::IllegalTeamValue),
                        };
                        // Get prop from team entity
                        match team_entid {
                            Some(eid) => return self.get_prop_from_ent(prop, &eid),
                            None => return Err(PropCollectionError::TeamEntityIdNotSet),
                        }
                    }
                    Ok(_) => Err(PropCollectionError::TeamNumIncorrectVariant),
                    Err(e) => Err(e),
                }
            }
        }
    }
    pub fn gather_extra_info(&mut self, entity_id: &i32, is_baseline: bool) -> Result<(), DemoParserError> {
        // Boring stuff.. function does some bookkeeping
        let entity = match self.entities.get(*entity_id as usize) {
            Some(Some(entity)) => entity,
            _ => return Err(DemoParserError::EntityNotFound),
        };
        if !(entity.entity_type == EntityType::PlayerController || entity.entity_type == EntityType::Team) {
            return Ok(());
        }
        if entity.entity_type == EntityType::Team && !is_baseline {
            if let Some(team_num_id) = self.prop_controller.special_ids.team_team_num {
                if let Ok(Variant::U32(t)) = self.get_prop_from_ent(&team_num_id, entity_id) {
                    match t {
                        1 => self.teams.team1_entid = Some(*entity_id),
                        2 => self.teams.team2_entid = Some(*entity_id),
                        3 => self.teams.team3_entid = Some(*entity_id),
                        _ => {}
                    }
                }
            }
        }
        if entity.entity_type == EntityType::PlayerController {
            let team_num = match self.prop_controller.special_ids.teamnum {
                Some(team_num_id) => match self.get_prop_from_ent(&team_num_id, entity_id) {
                    Ok(Variant::U32(team_num)) => Some(team_num),
                    Ok(_) => return Err(DemoParserError::IncorrectMetaDataProp),
                    Err(_) => None,
                },
                _ => None,
            };
            let name = match self.prop_controller.special_ids.player_name {
                Some(id) => match entity.props.get(&id) {
                    Some(Variant::String(name)) => Some(name.as_str()),
                    Some(_) => return Err(DemoParserError::IncorrectMetaDataProp),
                    None => None,
                },
                _ => None,
            };
            let steamid = match self.prop_controller.special_ids.steamid {
                Some(id) => match self.get_prop_from_ent(&id, entity_id) {
                    Ok(Variant::U64(sid)) => Some(sid),
                    Ok(_) => return Err(DemoParserError::IncorrectMetaDataProp),
                    Err(_) => None,
                },
                _ => None,
            };
            let player_entid = match self.prop_controller.special_ids.player_pawn {
                Some(id) => match self.get_prop_from_ent(&id, entity_id) {
                    Ok(Variant::U32(handle)) => Some(demo_network_ehandle_index(handle)),
                    Ok(_) => return Err(DemoParserError::IncorrectMetaDataProp),
                    Err(_) => None,
                },
                _ => None,
            };
            if let Some(e) = player_entid {
                if e != DEMO_NETWORK_EHANDLE_INVALID_INDEX
                    && steamid != Some(0)
                    && team_num != Some(SPECTATOR_TEAM_NUM)
                {
                    let previous_entity_id = self.should_remove(steamid);
                    if previous_entity_id == Some(e) && self.players.get(&e).is_some_and(|existing|
                        existing.name.as_deref() == name && existing.team_num == team_num
                        && existing.player_entity_id == player_entid && existing.steamid == steamid
                        && existing.controller_entid == Some(*entity_id)) {
                        // Most controller updates change only counters. Keep
                        // identical roster metadata without cloning its name
                        // and removing/reinserting its tree entry. The first
                        // matching SteamID check preserves collision cleanup.
                        return Ok(());
                    }
                    let metadata = PlayerMetaData {
                        name: name.map(str::to_owned),
                        team_num,
                        player_entity_id: player_entid,
                        steamid,
                        controller_entid: Some(*entity_id),
                    };
                    // Controllers are gathered after every entity update. Replacing
                    // identical metadata is harmless, but a moved/replaced player can
                    // change the inventory cache's pawn and ownership signature.
                    if previous_entity_id.is_some_and(|previous| previous != e)
                        || self.players.get(&e) != Some(&metadata)
                    {
                        self.invalidate_inventory_snapshots();
                    }
                    match previous_entity_id {
                        Some(eid) => {
                            self.players.remove(&eid);
                        }
                        None => {}
                    }
                    self.players.insert(e, metadata);
                }
            }
        }
        Ok(())
    }
    pub fn should_remove(&self, steamid: Option<u64>) -> Option<i32> {
        for (entid, player) in &self.players {
            if player.steamid == steamid {
                return Some(*entid);
            }
        }
        None
    }
}

fn coord_from_cell(cell: Result<Variant, PropCollectionError>, offset: Result<Variant, PropCollectionError>) -> Result<f32, PropCollectionError> {
    // Both cell and offset are needed for calculation
    match (offset, cell) {
        (Ok(Variant::F32(offset)), Ok(Variant::U32(cell))) => {
            let cell_coord = ((cell as f32 * (1 << CELL_BITS) as f32) - MAX_COORD) as f32;
            Ok(cell_coord + offset)
        }
        (Err(_), Err(_)) => Err(PropCollectionError::CoordinateBothNone),
        (Ok(Variant::F32(_offset)), Err(_)) => Err(PropCollectionError::CoordinateCellNone),
        (Err(_), Ok(Variant::U32(_cell))) => Err(PropCollectionError::CoordinateOffsetNone),
        (_, _) => Err(PropCollectionError::CoordinateIncorrectTypes),
    }
}

fn econ_attribute_raw_value(value: Variant) -> Option<(f32, u32)> {
    match value {
        Variant::F32(value) => Some((value, value.to_bits())),
        Variant::U32(value) => Some((value as f32, value)),
        Variant::I32(value) if value >= 0 => Some((value as f32, value as u32)),
        _ => None,
    }
}

fn is_map_based_default_agent(agent: &str) -> bool {
    matches!(
        agent,
        "customplayer_t_map_based" | "customplayer_ct_map_based"
    )
}

fn variant_to_nonnegative_u32(value: Variant) -> Option<u32> {
    match value {
        Variant::U32(value) => Some(value),
        Variant::I32(value) if value >= 0 => Some(value as u32),
        Variant::F32(value) if value.is_finite() && value.fract() == 0.0 && value >= 0.0 => {
            Some(value as u32)
        }
        _ => None,
    }
}

fn glove_paint_seed_from_attribute(value: Variant) -> Option<u32> {
    match value {
        Variant::U32(value) => Some(value),
        Variant::I32(value) if value >= 0 => Some(value as u32),
        // CS2 transmits paint seeds through the float-valued econ attribute lane.
        // Rendering converts that value to the integer pattern index, matching the
        // existing weapon paint-seed path.
        Variant::F32(value) if value.is_finite() && value >= 0.0 => Some(value as u32),
        _ => None,
    }
}

fn inventory_cosmetics_are_reusable(
    cached_player_signature: (u32, Option<u64>, Option<u32>),
    cached_weapon_signature: &[(i32, u32, u64)],
    current_player_signature: (u32, Option<u64>, Option<u32>),
    current_weapon_signature: &[(i32, u32, u64)],
) -> bool {
    cached_player_signature == current_player_signature
        && cached_weapon_signature == current_weapon_signature
}

fn current_inventory_snapshot(
    cache: &std::cell::RefCell<ahash::AHashMap<i32, PlayerInventorySnapshot>>,
    entity_id: i32,
    generation: u64,
) -> Option<Ref<'_, PlayerInventorySnapshot>> {
    Ref::filter_map(cache.borrow(), |cache| {
        cache
            .get(&entity_id)
            .filter(|snapshot| snapshot.generation == generation)
    }).ok()
}

#[cfg(test)]
mod tests {
    use super::{
        current_inventory_snapshot, inventory_cosmetics_are_reusable,
        glove_paint_seed_from_attribute, is_map_based_default_agent,
        stickers_from_attributes, should_collect_player_rows, StickerAttribute,
    };
    use crate::second_pass::parser_settings::PlayerInventorySnapshot;
    use crate::second_pass::variants::{PropColumn, Variant};
    use crate::first_pass::parser_settings::{FirstPassParser, ParserInputs};
    use crate::first_pass::prop_controller::{
        PropInfo, WantedPropStateInfo, NAME_ID, PLAYER_X_ID, PLAYER_Y_ID, PLAYER_Z_ID,
        STEAMID_ID, TICK_ID, VELOCITY_ID, VELOCITY_X_ID, VELOCITY_Y_ID, VELOCITY_Z_ID,
    };
    use crate::parse_demo::DecodePlan;
    use crate::second_pass::entities::{Entity, EntityType, PlayerMetaData};
    use crate::second_pass::parser::SecondPassOutput;
    use crate::second_pass::parser_settings::SecondPassParser;
    use super::PropType;
    use ahash::AHashMap;
    use std::cell::RefCell;
    use std::sync::Arc;

    #[derive(Clone, Copy, Debug)]
    enum CollectionCase {
        Rows,
        NoPlayers,
        NoSelectedPlayers,
        TickFilter,
        DuplicateProperty,
        Velocity,
        EventVelocity,
        StateFilter,
        PerPlayer,
        Projectiles,
    }

    fn collection_property(id: u32, prop_type: PropType) -> PropInfo {
        PropInfo {
            id,
            prop_type,
            prop_name: id.to_string(),
            prop_friendly_name: id.to_string(),
            is_player_prop: true,
        }
    }

    #[test]
    fn dense_slot_selection_preserves_original_column_indices() {
        assert_eq!(super::make_dense_column_slots(7, |slot| matches!(slot, 0 | 3 | 5)), vec![1, 2, 4, 6]);
        assert_eq!(super::make_dense_column_slots(3, |_| false), vec![0, 1, 2]);
        assert!(super::make_dense_column_slots(3, |_| true).is_empty());
    }

    fn run_collection_case(case: CollectionCase, force_legacy: bool) -> (bool, SecondPassOutput) {
        let huf = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(),
            wanted_players: if matches!(case, CollectionCase::NoSelectedPlayers) { vec![999] } else { vec![] },
            wanted_player_props: vec![],
            wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: if matches!(case, CollectionCase::TickFilter) { vec![11] } else { vec![] },
            wanted_events: vec![],
            parse_ents: true,
            parse_projectiles: matches!(case, CollectionCase::Projectiles),
            collect_projectile_records: false,
            parse_grenades: false,
            only_header: false,
            only_convars: false,
            huffman_lookup_table: &huf,
            order_by_steamid: matches!(case, CollectionCase::PerPlayer),
            list_props: false,
            fallback_bytes: None,
            cancelled: None,
        };
        let mut first = FirstPassParser::new(&settings);
        first.cls_by_id = Some(Arc::new(Vec::new()));
        first.prop_controller.prop_infos = vec![
            collection_property(TICK_ID, PropType::Tick),
            collection_property(STEAMID_ID, PropType::Steamid),
            collection_property(NAME_ID, PropType::Name),
            collection_property(PLAYER_X_ID, PropType::Custom),
            collection_property(PLAYER_Y_ID, PropType::Custom),
            collection_property(PLAYER_Z_ID, PropType::Custom),
            collection_property(120, PropType::Player),
            collection_property(121, PropType::Player),
        ];
        let ids = &mut first.prop_controller.special_ids;
        ids.cell_x_player = Some(101);
        ids.cell_y_player = Some(101);
        ids.cell_z_player = Some(101);
        ids.cell_x_offset_player = Some(102);
        ids.cell_y_offset_player = Some(102);
        ids.cell_z_offset_player = Some(102);
        if matches!(case, CollectionCase::DuplicateProperty) {
            first.prop_controller.prop_infos.push(collection_property(120, PropType::Player));
        }
        if matches!(case, CollectionCase::Velocity) {
            for id in [VELOCITY_ID, VELOCITY_X_ID, VELOCITY_Y_ID, VELOCITY_Z_ID] {
                first.prop_controller.prop_infos.push(collection_property(id, PropType::Custom));
            }
        }
        first.prop_controller.event_with_velocity = matches!(case, CollectionCase::EventVelocity);
        if matches!(case, CollectionCase::StateFilter) {
            first.prop_controller.wanted_prop_state_infos.push(WantedPropStateInfo {
                base: collection_property(120, PropType::Player),
                wanted_prop_state: Variant::F32(1.0),
            });
        }
        let mut parser = SecondPassParser::new(
            first.create_first_pass_output().unwrap(), 0, false, None, DecodePlan::FULL,
        ).unwrap();
        let dense_enabled = parser.dense_player_columns.is_some();
        if force_legacy {
            parser.dense_player_columns = None;
        }
        if !matches!(case, CollectionCase::NoPlayers) {
            for (entity_id, steamid) in [(7, 1001), (9, 1002)] {
                parser.players.insert(entity_id, PlayerMetaData {
                    player_entity_id: Some(entity_id),
                    steamid: Some(steamid),
                    controller_entid: None,
                    name: (entity_id == 7).then(|| "first".to_string()),
                    team_num: Some(2),
                });
                parser.entities[entity_id as usize] = Some(Entity {
                    cls_id: 0,
                    entity_id,
                    serial: 1,
                    props: [(101, Variant::U32(32)), (102, Variant::F32(10.0)),
                            (120, Variant::F32(if entity_id == 7 { 1.0 } else { 2.0 }))].into_iter().collect(),
                    entity_type: EntityType::Normal,
                    cosmetic_revision: 0,
                });
            }
        }
        for tick in 10..=12 {
            parser.tick = tick;
            if let Some(entity) = parser.entities[7].as_mut() {
                entity.props.insert(102, Variant::F32(tick as f32));
                entity.props.insert(120, Variant::F32(if tick == 11 { 2.0 } else { 1.0 }));
                if tick == 11 {
                    // A previously all-null column resolves its type after rows exist.
                    entity.props.insert(121, Variant::U32(55));
                }
            }
            if tick == 12 {
                parser.players.remove(&9);
            }
            parser.collect_entities();
        }
        (dense_enabled, parser.create_output())
    }

    #[test]
    fn dense_collection_matches_legacy_rows_and_fallback_modes() {
        for case in [
            CollectionCase::Rows, CollectionCase::NoPlayers, CollectionCase::NoSelectedPlayers,
            CollectionCase::TickFilter, CollectionCase::DuplicateProperty, CollectionCase::Velocity,
            CollectionCase::EventVelocity, CollectionCase::StateFilter, CollectionCase::PerPlayer,
            CollectionCase::Projectiles,
        ] {
            let (enabled, dense) = run_collection_case(case, false);
            let (_, legacy) = run_collection_case(case, true);
            assert_eq!(enabled, matches!(case,
                CollectionCase::Rows | CollectionCase::NoPlayers |
                CollectionCase::NoSelectedPlayers | CollectionCase::TickFilter,
            ), "{case:?}");
            assert_eq!(dense.df, legacy.df, "{case:?}");
            assert_eq!(dense.df_per_player, legacy.df_per_player, "{case:?}");
            if matches!(case, CollectionCase::NoPlayers | CollectionCase::NoSelectedPlayers) {
                assert!(dense.df.is_empty());
            }
        }
    }

    #[test]
    fn unchanged_controller_metadata_preserves_collision_cleanup_and_moves() {
        use crate::first_pass::parser_settings::{FirstPassParser, ParserInputs};
        use crate::first_pass::read_bits::DemoParserError;
        use crate::parse_demo::DecodePlan;
        use ahash::AHashMap;
        let huffman = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(), wanted_players: vec![],
            wanted_player_props: vec![], wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(), wanted_ticks: vec![], wanted_events: vec![],
            parse_ents: true, parse_projectiles: false, collect_projectile_records: false,
            parse_grenades: false, only_header: false, only_convars: false,
            huffman_lookup_table: &huffman, order_by_steamid: false, list_props: false,
            fallback_bytes: None, cancelled: None,
        };
        let mut first = FirstPassParser::new(&settings);
        first.cls_by_id = Some(Arc::new(vec![]));
        first.prop_controller.special_ids.teamnum = Some(100);
        first.prop_controller.special_ids.player_name = Some(101);
        first.prop_controller.special_ids.steamid = Some(102);
        first.prop_controller.special_ids.player_pawn = Some(103);
        let mut parser = SecondPassParser::new(first.create_first_pass_output().unwrap(),
            0, true, None, DecodePlan::FULL).unwrap();
        parser.entities[1] = Some(Entity {
            cls_id: 0, entity_id: 1, serial: 1, entity_type: EntityType::PlayerController,
            cosmetic_revision: 0, props: AHashMap::from_iter([
                (100, Variant::U32(2)), (101, Variant::String("first".into())),
                (102, Variant::U64(70)), (103, Variant::U32(8)),
            ]),
        });
        parser.gather_extra_info(&1, false).unwrap();
        let expected = parser.players[&8].clone();
        let generation = parser.inventory_generation;
        parser.gather_extra_info(&1, false).unwrap();
        assert_eq!(parser.players[&8], expected);
        assert_eq!(parser.inventory_generation, generation);
        parser.players.insert(7, PlayerMetaData { player_entity_id: Some(7), ..expected.clone() });
        parser.gather_extra_info(&1, false).unwrap();
        assert_eq!(parser.players.len(), 1);
        assert_eq!(parser.players[&8], expected);
        parser.entities[1].as_mut().unwrap().props.insert(101, Variant::U32(1));
        assert_eq!(parser.gather_extra_info(&1, false), Err(DemoParserError::IncorrectMetaDataProp));
        assert_eq!(parser.players[&8], expected);
        parser.entities[1].as_mut().unwrap().props.extend([
            (100, Variant::U32(3)), (101, Variant::String("renamed".into())), (103, Variant::U32(9)),
        ]);
        parser.gather_extra_info(&1, false).unwrap();
        assert_eq!(parser.players.len(), 1);
        assert_eq!(parser.players[&9].name.as_deref(), Some("renamed"));
        assert_eq!(parser.players[&9].team_num, Some(3));
    }

    #[test]
    fn row_context_matches_legacy_for_missing_links_and_incomplete_metadata() {
        use super::RowCollectionContext;
        use crate::first_pass::prop_controller::*;

        for case in 0..22 {
            let huf = Vec::new();
            let settings = ParserInputs {
                real_name_to_og_name: AHashMap::default(), wanted_players: vec![],
                wanted_player_props: vec![], wanted_other_props: vec![],
                wanted_prop_states: AHashMap::default(), wanted_ticks: vec![], wanted_events: vec![],
                parse_ents: true, parse_projectiles: false, collect_projectile_records: false,
                parse_grenades: false, only_header: false, only_convars: false,
                huffman_lookup_table: &huf, order_by_steamid: false, list_props: false,
                fallback_bytes: None, cancelled: None,
            };
            let mut first = FirstPassParser::new(&settings);
            first.cls_by_id = Some(Arc::new(Vec::new()));
            let ids = &mut first.prop_controller.special_ids;
            ids.active_weapon = Some(100);
            ids.player_team_pointer = Some(101);
            ids.eye_angles = Some(102);
            ids.life_state = Some(103);
            ids.item_def = Some(104);
            ids.orig_own_low = Some(105);
            ids.orig_own_high = Some(106);
            ids.cell_x_player = Some(107);
            ids.cell_y_player = Some(107);
            ids.cell_z_player = Some(107);
            ids.cell_x_offset_player = Some(108);
            ids.cell_y_offset_player = Some(108);
            ids.cell_z_offset_player = Some(108);
            if case == 19 {
                *ids = crate::second_pass::parser_settings::SpecialIDs::new();
            }
            let mut parser = SecondPassParser::new(
                first.create_first_pass_output().unwrap(), 0, false, None, DecodePlan::FULL,
            ).unwrap();
            parser.rules_entity_id = Some(3);
            parser.teams.team2_entid = Some(4);
            let mut player = PlayerMetaData {
                player_entity_id: Some(7), steamid: Some(1001), controller_entid: Some(2),
                name: Some("player".to_string()), team_num: Some(2),
            };
            for entity_id in [2, 3, 4, 7, 8, 10, 11] {
                let props = [
                    (100, Variant::U32(if entity_id == 8 { 11 } else { 10 })),
                    (101, Variant::U32(2)), (102, Variant::VecXYZ([10.0, 20.0, 30.0])),
                    (103, Variant::U32(0)), (104, Variant::U32(7)),
                    (105, Variant::U32(1000 + entity_id as u32)), (106, Variant::U32(1)),
                    (107, Variant::U32(32)), (108, Variant::F32(14.0)),
                    (142, Variant::I32(entity_id)),
                    (143, Variant::String(format!("entity {entity_id}"))),
                    (WEAPON_SKIN_ID, Variant::F32(44.0)),
                    (WEAPON_FLOAT, Variant::F32(entity_id as f32 / 100.0)),
                    (WEAPON_PAINT_SEED, Variant::F32(entity_id as f32 + 0.5)),
                    (WEAPON_RESERVE_AMMO_BASE + 1, Variant::U32(5)),
                ].into_iter().collect();
                parser.entities[entity_id as usize] = Some(Entity {
                    cls_id: 0, entity_id, serial: 1, props,
                    entity_type: EntityType::Normal, cosmetic_revision: 0,
                });
            }
            match case {
                1 => parser.entities[7] = None,
                2 => { parser.entities[7].as_mut().unwrap().props.remove(&100); }
                3 => { parser.entities[7].as_mut().unwrap().props.insert(100, Variant::I32(10)); }
                4 => { parser.entities[7].as_mut().unwrap().props.insert(100, Variant::U32(u32::MAX)); }
                5 => parser.entities[10] = None,
                6 => {
                    let props = &mut parser.entities[10].as_mut().unwrap().props;
                    props.insert(104, Variant::F32(7.0));
                    props.insert(105, Variant::I32(1000));
                    props.insert(WEAPON_SKIN_ID, Variant::I32(44));
                    props.insert(WEAPON_PAINT_SEED, Variant::U32(12));
                    props.insert(WEAPON_FLOAT, Variant::U32(1));
                }
                7 => player.player_entity_id = None,
                8 => player.player_entity_id = Some(8),
                9 => player.player_entity_id = Some(999),
                10 => player.controller_entid = None,
                11 => parser.entities[2] = None,
                12 => parser.rules_entity_id = None,
                13 => parser.entities[3] = None,
                14 => { parser.entities[7].as_mut().unwrap().props.insert(101, Variant::U32(99)); }
                15 => { parser.entities[7].as_mut().unwrap().props.insert(101, Variant::I32(2)); }
                16 => parser.teams.team2_entid = None,
                17 => parser.entities[4] = None,
                18 => parser.entities[7].as_mut().unwrap().props.clear(),
                20 => { parser.entities[7].as_mut().unwrap().props.insert(100, Variant::U32((19 << 14) | 10)); }
                21 => { parser.entities[7].as_mut().unwrap().props.insert(103, Variant::I32(0)); }
                _ => {}
            }
            let row = RowCollectionContext::new(&parser, 7, &player);
            let mut props: Vec<_> = [PropType::Player, PropType::Controller, PropType::Rules, PropType::Team, PropType::Weapon]
                .into_iter().map(|kind| collection_property(142, kind)).collect();
            props.extend([
                PLAYER_X_ID, PLAYER_Y_ID, PLAYER_Z_ID, PITCH_ID, YAW_ID, WEAPON_NAME_ID,
                WEAPON_SKIN_ID, WEAPON_SKIN_NAME, WEAPON_FLOAT, WEAPON_PAINT_SEED,
                WEAPON_ORIGINGAL_OWNER_ID, WEAPON_STICKERS_ID, WEAPON_RESERVE_AMMO_SECONDARY,
                IS_ALIVE_ID,
            ].into_iter().map(|id| collection_property(id, PropType::Custom)));
            props.push(collection_property(NAME_ID, PropType::Name));
            for (name, kind) in [
                ("CCSPlayerController.m_szCrosshairCodes", PropType::Controller),
                ("CCSTeam.m_szTeamname", PropType::Team),
                ("CCSTeam.m_szClanTeamname", PropType::Team),
                ("m_szCustomName", PropType::Weapon),
                ("CCSPlayerController.m_iCompTeammateColor", PropType::Custom),
            ] {
                let mut prop = collection_property(if kind == PropType::Custom { 142 } else { 143 }, kind);
                prop.prop_name = name.to_owned();
                props.push(prop);
            }
            let mut source_props = crate::first_pass::prop_controller::PropController::new(
                vec![], vec![], AHashMap::default(), AHashMap::default(), false, &[], false,
            );
            source_props.prop_infos = props.clone();
            let sources = super::make_shared_string_sources(&source_props);
            for (prop, source) in props.into_iter().zip(sources) {
                let expected = parser.find_prop(&prop, &7, &player).ok();
                assert_eq!(
                    parser.find_dense_prop(&prop, &7, &player, &row, &mut None),
                    expected,
                    "case {case}, property {} {:?}", prop.id, prop.prop_type,
                );
                let mut actual_column = PropColumn::new();
                if !parser.push_dense_shared_string(source, &prop, &player, &row, &mut actual_column) {
                    actual_column.push(parser.find_dense_prop(&prop, &7, &player, &row, &mut None));
                }
                let mut expected_column = PropColumn::new();
                expected_column.push(expected);
                assert_eq!(actual_column, expected_column, "shared string case {case}, property {}", prop.id);
            }
        }
    }

    #[test]
    fn explicit_full_player_rows_do_not_depend_on_synthetic_velocity() {
        assert!(should_collect_player_rows(true, false, true, false, false));
        assert!(!should_collect_player_rows(false, false, true, false, false));
        assert!(should_collect_player_rows(false, true, true, false, false));
    }

    #[test]
    fn map_based_player_models_are_not_stable_agent_evidence() {
        assert!(is_map_based_default_agent("customplayer_t_map_based"));
        assert!(is_map_based_default_agent("customplayer_ct_map_based"));
        assert!(!is_map_based_default_agent(
            "customplayer_ctm_swat_variantg"
        ));
    }

    #[test]
    fn fractional_glove_seed_attributes_use_the_integer_pattern_index() {
        assert_eq!(
            glove_paint_seed_from_attribute(Variant::F32(699.1735)),
            Some(699)
        );
        assert_eq!(
            glove_paint_seed_from_attribute(Variant::F32(147.18864)),
            Some(147)
        );
        assert_eq!(
            glove_paint_seed_from_attribute(Variant::U32(3)),
            Some(3)
        );
        assert_eq!(
            glove_paint_seed_from_attribute(Variant::F32(f32::NAN)),
            None
        );
        assert_eq!(
            glove_paint_seed_from_attribute(Variant::F32(-1.0)),
            None
        );
    }

    #[test]
    fn inventory_snapshot_reuse_requires_same_player_side_and_weapon_revisions() {
        let player = (12, Some(76561198000000001), Some(2));
        let weapons = [(41, 3, 8), (42, 1, 5)];
        assert!(inventory_cosmetics_are_reusable(
            player,
            &weapons,
            player,
            &weapons,
        ));
        assert!(!inventory_cosmetics_are_reusable(
            player,
            &weapons,
            (12, Some(76561198000000001), Some(3)),
            &weapons,
        ));
        assert!(!inventory_cosmetics_are_reusable(
            player,
            &weapons,
            player,
            &[(41, 3, 9), (42, 1, 5)],
        ));
    }

    #[test]
    fn inventory_snapshot_releases_cache_borrow_before_upgrade() {
        let cache = RefCell::new(AHashMap::default());
        cache.borrow_mut().insert(
            7,
            PlayerInventorySnapshot {
                generation: 3,
                player_signature: (1, Some(76561198000000001), Some(2)),
                weapon_eids: vec![41],
                weapon_signature: vec![(41, 2, 5)],
                ids: vec![7],
                cosmetics: None,
            },
        );

        let snapshot = current_inventory_snapshot(&cache, 7, 3).unwrap();
        let ids = snapshot.ids.clone();
        drop(snapshot);
        cache.borrow_mut().get_mut(&7).unwrap().cosmetics = Some(Arc::from([]));

        assert!(cache.borrow().get(&7).unwrap().cosmetics.is_some());
        assert_eq!(ids, vec![7]);
        assert!(current_inventory_snapshot(&cache, 7, 4).is_none());
        assert!(current_inventory_snapshot(&cache, 8, 3).is_none());

        let snapshot = current_inventory_snapshot(&cache, 7, 3).unwrap();
        let cosmetics = snapshot.cosmetics.as_ref().map(Arc::clone).unwrap();
        drop(snapshot);
        assert!(Arc::ptr_eq(
            &cosmetics,
            cache.borrow().get(&7).unwrap().cosmetics.as_ref().unwrap(),
        ));
    }

    #[test]
    fn inventory_snapshot_reuses_allocations_and_preserves_invalidation() {
        let huf = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(), wanted_players: vec![],
            wanted_player_props: vec![], wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(), wanted_ticks: vec![], wanted_events: vec![],
            parse_ents: true, parse_projectiles: false, collect_projectile_records: false,
            parse_grenades: false, only_header: false, only_convars: false,
            huffman_lookup_table: &huf, order_by_steamid: false, list_props: false,
            fallback_bytes: None, cancelled: None,
        };
        let mut first = FirstPassParser::new(&settings);
        first.cls_by_id = Some(Arc::new(Vec::new()));
        first.prop_controller.special_ids.life_state = Some(101);
        first.prop_controller.special_ids.item_def = Some(102);
        let mut parser = SecondPassParser::new(
            first.create_first_pass_output().unwrap(), 0, false, None, DecodePlan::FULL,
        ).unwrap();
        parser.players.insert(7, PlayerMetaData {
            player_entity_id: Some(7), steamid: Some(1001), controller_entid: None,
            name: None, team_num: Some(2),
        });
        for entity_id in [7, 41, 42] {
            parser.entities[entity_id as usize] = Some(Entity {
                cls_id: 0, entity_id, serial: 1, props: AHashMap::default(),
                entity_type: EntityType::Normal, cosmetic_revision: 0,
            });
        }
        let inventory = super::MY_WEAPONS_OFFSET;
        parser.entities[7].as_mut().unwrap().props.extend([
            (101, Variant::U32(0)), (inventory, Variant::U32(3)),
            (inventory + 1, Variant::U32(41)), (inventory + 2, Variant::U32(42)),
            // Duplicate handles must not duplicate an inventory item.
            (inventory + 3, Variant::U32(41)),
        ]);
        parser.entities[41].as_mut().unwrap().props.insert(102, Variant::U32(7));
        parser.entities[42].as_mut().unwrap().props.insert(102, Variant::U32(9));
        let allocation = |snapshot: &PlayerInventorySnapshot| (
            snapshot.weapon_eids.as_ptr() as usize,
            snapshot.weapon_signature.as_ptr() as usize,
            snapshot.ids.as_ptr() as usize,
        );
        let snapshot = parser.player_inventory_snapshot(&7, false).unwrap();
        assert_eq!(snapshot.weapon_eids, vec![41, 42]);
        assert_eq!(snapshot.ids, vec![7, 9]);
        assert!(snapshot.cosmetics.is_none());
        let initial_allocations = allocation(&snapshot);
        drop(snapshot);
        let snapshot = parser.player_inventory_snapshot(&7, true).unwrap();
        let cosmetics = Arc::clone(snapshot.cosmetics.as_ref().unwrap());
        assert_eq!(cosmetics.len(), 2);
        assert_eq!(allocation(&snapshot), initial_allocations);
        drop(snapshot);

        parser.inventory_generation += 1;
        let snapshot = parser.player_inventory_snapshot(&7, false).unwrap();
        assert_eq!(allocation(&snapshot), initial_allocations);
        assert!(Arc::ptr_eq(snapshot.cosmetics.as_ref().unwrap(), &cosmetics));
        drop(snapshot);

        // Failed inventory length validation must leave the old entry intact.
        parser.inventory_generation += 1;
        parser.entities[7].as_mut().unwrap().props.remove(&inventory);
        assert!(parser.player_inventory_snapshot(&7, true).is_err());
        {
            let cache = parser.player_inventory_snapshot_cache.borrow();
            let cached = cache.get(&7).unwrap();
            assert_eq!(cached.generation, parser.inventory_generation - 1);
            assert_eq!(cached.ids, vec![7, 9]);
            assert_eq!(allocation(cached), initial_allocations);
        }
        parser.entities[7].as_mut().unwrap().props.insert(inventory, Variant::U32(3));
        parser.entities[41].as_mut().unwrap().cosmetic_revision += 1;
        let snapshot = parser.player_inventory_snapshot(&7, false).unwrap();
        assert!(snapshot.cosmetics.is_none());
        assert_eq!(allocation(&snapshot), initial_allocations);
        drop(snapshot);
        let snapshot = parser.player_inventory_snapshot(&7, true).unwrap();
        assert!(!Arc::ptr_eq(snapshot.cosmetics.as_ref().unwrap(), &cosmetics));
        drop(snapshot);

        // Truncation must invalidate cosmetics and drop old signature entries,
        // without freeing any of the three reusable vector allocations.
        parser.inventory_generation += 1;
        parser.entities[7].as_mut().unwrap().props.insert(inventory, Variant::U32(1));
        let snapshot = parser.player_inventory_snapshot(&7, false).unwrap();
        assert_eq!(snapshot.weapon_eids, vec![41]);
        assert_eq!(snapshot.weapon_signature.len(), 1);
        assert_eq!(snapshot.ids, vec![7]);
        assert!(snapshot.cosmetics.is_none());
        assert_eq!(allocation(&snapshot), initial_allocations);
        drop(snapshot);
        let snapshot = parser.player_inventory_snapshot(&7, true).unwrap();
        assert_eq!(snapshot.cosmetics.as_ref().unwrap().len(), 1);
        drop(snapshot);

        // Player side changes still invalidate an otherwise identical list.
        parser.inventory_generation += 1;
        parser.players.get_mut(&7).unwrap().team_num = Some(3);
        assert!(parser.player_inventory_snapshot(&7, false).unwrap().cosmetics.is_none());

        // Dead-player snapshots retain the documented empty/lazy semantics.
        parser.inventory_generation += 1;
        parser.entities[7].as_mut().unwrap().props.insert(101, Variant::U32(1));
        parser.entities[7].as_mut().unwrap().props.remove(&inventory);
        let snapshot = parser.player_inventory_snapshot(&7, false).unwrap();
        assert!(snapshot.weapon_eids.is_empty() && snapshot.weapon_signature.is_empty() && snapshot.ids.is_empty());
        assert!(snapshot.cosmetics.is_none());
        assert_eq!(allocation(&snapshot), initial_allocations);
        drop(snapshot);
        assert!(parser.player_inventory_snapshot(&7, true).unwrap().cosmetics.as_ref().unwrap().is_empty());
    }

    #[test]
    fn sticker_snapshots_share_storage_and_invalidate_on_revision_and_entity_reuse() {
        let huf = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(), wanted_players: vec![],
            wanted_player_props: vec![], wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(), wanted_ticks: vec![], wanted_events: vec![],
            parse_ents: true, parse_projectiles: false, collect_projectile_records: false,
            parse_grenades: false, only_header: false, only_convars: false,
            huffman_lookup_table: &huf, order_by_steamid: false, list_props: false,
            fallback_bytes: None, cancelled: None,
        };
        let mut first = FirstPassParser::new(&settings);
        first.cls_by_id = Some(Arc::new(Vec::new()));
        first.prop_controller.special_ids.item_def = Some(102);
        let mut parser = SecondPassParser::new(
            first.create_first_pass_output().unwrap(), 0, false, None, DecodePlan::FULL,
        ).unwrap();
        parser.entities[41] = Some(Entity {
            cls_id: 0, entity_id: 41, serial: 1,
            props: AHashMap::from_iter([
                (super::WEAPON_ATTRIBUTE_DEF_INDEX_ID, Variant::U32(113)),
                (super::WEAPON_SKIN_ID, Variant::F32(f32::from_bits(477))),
            ]),
            entity_type: EntityType::Normal, cosmetic_revision: 0,
        });
        let snapshot = |parser: &SecondPassParser<'_>| {
            let Variant::Stickers(stickers) = parser.find_stickers(&41).unwrap() else { unreachable!() };
            stickers
        };

        // Missing item-definition evidence keeps the legacy partial sticker output.
        let original = snapshot(&parser);
        assert_eq!(original.len(), 1);
        assert_eq!(original[0].id, 477);
        assert!(Arc::ptr_eq(&original, &snapshot(&parser)));

        let entity = parser.entities[41].as_mut().unwrap();
        entity.props.insert(super::WEAPON_SKIN_ID, Variant::F32(f32::from_bits(478)));
        entity.cosmetic_revision += 1;
        let changed = snapshot(&parser);
        assert_eq!(changed[0].id, 478);
        assert!(!Arc::ptr_eq(&original, &changed));
        assert_eq!(original[0].id, 477);
        assert!(Arc::ptr_eq(&changed, &snapshot(&parser)));

        // Once item-definition evidence arrives, the full cosmetic-cache path agrees.
        let entity = parser.entities[41].as_mut().unwrap();
        entity.props.insert(102, Variant::U32(7));
        entity.cosmetic_revision += 1;
        let complete = snapshot(&parser);
        assert_eq!(complete.as_ref(), changed.as_ref());
        assert!(!Arc::ptr_eq(&complete, &changed));
        assert!(Arc::ptr_eq(&complete, &snapshot(&parser)));

        let entity = parser.entities[41].as_mut().unwrap();
        entity.serial += 1;
        entity.cosmetic_revision = 0;
        entity.props.insert(super::WEAPON_SKIN_ID, Variant::F32(f32::from_bits(477)));
        let replacement = snapshot(&parser);
        assert_eq!(replacement[0].id, 477);
        assert!(!Arc::ptr_eq(&replacement, &complete));
        assert_eq!(complete[0].id, 478);

        parser.entities[41] = None;
        assert!(snapshot(&parser).is_empty());
    }

    #[test]
    fn sticker_layers_keep_transform_state_with_matching_id_layer() {
        let stickers = stickers_from_attributes([
            attr(113, f32::from_bits(60)),
            attr(117, f32::from_bits(76)),
            attr(125, f32::from_bits(103)),
            attr(117, f32::from_bits(5946)),
            attr(118, 0.995967),
            attr(120, 24.0),
            attr(121, f32::from_bits(4885)),
            attr(122, 1.0),
            attr(124, 105.0),
            attr(125, f32::from_bits(4885)),
            attr(126, 1.0),
            attr(128, 102.0),
            attr(129, f32::from_bits(4893)),
            attr(130, 1.0),
            attr(132, 141.0),
            attr(278, -0.116377234),
            attr(279, 0.007121563),
            attr(280, -0.30349553),
            attr(281, 0.011387974),
            attr(282, -0.28971416),
            attr(283, -0.0014955997),
            attr(284, -0.2608658),
            attr(285, -0.00951612),
            attr(286, 0.043130986),
            attr(287, 0.03563851),
        ]);

        let ibp = stickers.iter().find(|sticker| sticker.slot == 0).unwrap();
        assert_eq!(ibp.id, 60);
        assert_eq!(ibp.name, "kat2014_ibuypower_holo");
        assert_eq!(ibp.x, 0.0);
        assert_eq!(ibp.y, 0.0);

        let titan = stickers.iter().find(|sticker| sticker.slot == 1).unwrap();
        assert_eq!(titan.id, 76);
        assert_eq!(titan.name, "kat2014_titan_holo");
        assert_eq!(titan.wear, 0.0);
        assert_eq!(titan.rotation, None);
        assert_eq!(titan.x, 0.0);
        assert_eq!(titan.y, 0.0);

        let banana = stickers.iter().find(|sticker| sticker.slot == 2).unwrap();
        assert_eq!(banana.id, 4885);
        assert_eq!(banana.wear, 1.0);
        assert_eq!(banana.rotation, Some(105.0));
        assert_eq!(banana.x, -0.28971416);
        assert_eq!(banana.y, -0.0014955997);

        let howling = stickers.iter().find(|sticker| sticker.slot == 3).unwrap();
        assert_eq!(howling.id, 103);
        assert_eq!(howling.name, "comm01_howling_dawn");
        assert_eq!(howling.x, 0.0);
        assert_eq!(howling.y, 0.0);

        let war = stickers.iter().find(|sticker| sticker.slot == 4).unwrap();
        assert_eq!(war.id, 4893);
        assert_eq!(war.rotation, Some(141.0));
        assert_eq!(war.x, 0.043130986);
        assert_eq!(war.y, 0.03563851);
    }

    fn attr(definition_index: u32, raw_value: f32) -> StickerAttribute {
        StickerAttribute {
            definition_index,
            raw_value,
        }
    }
}

#[derive(Debug, PartialEq)]
pub enum PropCollectionError {
    PlayerSpecialIDCellXMissing,
    PlayerSpecialIDCellYMissing,
    PlayerSpecialIDCellZMissing,
    PlayerSpecialIDOffsetXMissing,
    PlayerSpecialIDOffsetYMissing,
    PlayerSpecialIDOffsetZMissing,
    GrenadeSpecialIDCellXMissing,
    GrenadeSpecialIDCellYMissing,
    GrenadeSpecialIDCellZMissing,
    GrenadeSpecialIDOffsetXMissing,
    GrenadeSpecialIDOffsetYMissing,
    GrenadeSpecialIDOffsetZMissing,
    CoordinateOffsetNone,
    CoordinateCellNone,
    CoordinateIncorrectTypes,
    CoordinateBothNone,
    GrenadeOffsetVariantNone,
    PlayerMetaDataNameNone,
    ButtonsSpecialIDNone,
    ButtonsMapNoEntryFound,
    GetPropFromEntEntityNotFound,
    GetPropFromEntPropNotFound,
    ButtonMaskNotU64Variant,
    RulesEntityIdNotSet,
    ControllerEntityIdNotSet,
    SpecialidsEyeAnglesNotSet,
    SpecialidsItemDefNotSet,
    EyeAnglesWrongVariant,
    WeaponIdxMappingNotFound,
    WeaponDefVariantWrongType,
    SpecialidsPlayerTeamPointerNotSet,
    TeamNumIncorrectVariant,
    IllegalTeamValue,
    TeamEntityIdNotSet,
    GrenadeOwnerIdNotSet,
    GrenadeOwnerIdPropIncorrectVariant,
    PlayerNotFound,
    SpecialidsActiveWeaponNotSet,
    WeaponHandleIncorrectVariant,
    UnknownCustomPropName,
    UnknownCoordinateAxis,
    WeaponEntityNotFound,
    WeaponEntityWantedPropNotFound,
    WeaponSkinFloatConvertionError,
    WeaponSkinNoSkinMapping,
    WeaponSkinIdxIncorrectVariant,
    OriginalOwnerXuidIdLowNotSet,
    OriginalOwnerXuidIdHighNotSet,
    OriginalOwnerXuidLowNotFound,
    OriginalOwnerXuidHighNotFound,
    OriginalOwnerXuidlowIncorrectVariant,
    OriginalOwnerXuidHighIncorrectVariant,
    SpottedIncorrectVariant,
    VelocityNotFound,
    AgentIdNotFound,
    AgentIncorrectVariant,
    AgentPropNotFound,
    AgentSpecialIdNotSet,
    UseridNotFound,
    InventoryMaxNotFound,
    GloveSkinFloatConvertionError,
    GloveSkinIdxIncorrectVariant,
    GloveSkinNoSkinMapping,
}
impl std::error::Error for PropCollectionError {}
impl fmt::Display for PropCollectionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}", self)
    }
}
