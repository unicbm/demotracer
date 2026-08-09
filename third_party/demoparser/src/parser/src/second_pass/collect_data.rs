use super::entities::PlayerMetaData;
use super::variants::Variant;
use super::variants::{InventoryWeaponAttribute, InventoryWeaponCosmetic, Sticker};
use crate::demo_network_handle::{
    demo_network_ehandle_index, DEMO_NETWORK_EHANDLE_INVALID_INDEX,
};
use crate::first_pass::prop_controller::*;
use crate::first_pass::read_bits::DemoParserError;
use crate::maps::BUTTONMAP;
use crate::maps::PLAYER_COLOR;
use crate::second_pass::entities::EntityType;
use crate::second_pass::parser_settings::{PlayerInventorySnapshot, SecondPassParser};
use crate::second_pass::variants::PropColumn;
use crate::second_pass::variants::VarVec;
use csgoproto::maps::AGENTSMAP;
use csgoproto::maps::PAINTKITS;
use csgoproto::maps::STICKER_ID_TO_NAME;
use csgoproto::maps::WEAPINDICIES;
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

// DONT KNOW IF THESE ARE CORRECT. SEEMS TO GIVE CORRECT VALUES
const CELL_BITS: i32 = 9;
const MAX_COORD: f32 = (1 << 14) as f32;
// https://github.com/markus-wa/demoinfocs-golang/blob/master/pkg/demoinfocs/constants/constants.go#L11
const IS_AIRBORNE_CONST: u32 = 0xFFFFFF;
const ECON_ATTR_SET_ITEM_TEXTURE_PREFAB: u32 = 6;
const ECON_ATTR_SET_ITEM_TEXTURE_SEED: u32 = 7;
const ECON_ATTR_SET_ITEM_TEXTURE_WEAR: u32 = 8;
const STEAM_ID64_BASE: u64 = 76_561_197_960_265_728;

#[derive(Debug, Clone)]
pub struct ProjectileRecord {
    pub steamid: Option<u64>,
    pub name: Option<String>,
    pub x: Option<f32>,
    pub y: Option<f32>,
    pub z: Option<f32>,
    pub tick: Option<i32>,
    pub grenade_type: Option<String>,
    pub entity_id: Option<i32>,
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
            PropType::GameTime => return Ok(Variant::F32(self.net_tick as f32 / 64.0)),
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
            self.update_projectile_record_effects(index, projectile_entid);
            return;
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
        if let Some(cosmetic) = self.cached_weapon_cosmetic(weapon_entity_id) {
            return Ok(Variant::Stickers(cosmetic.stickers.clone()));
        }

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

        let stickers = stickers_from_attributes(attributes);
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
        let cache_key = player.steamid.zip(player.team_num);
        if let Some(key) = cache_key {
            if let Some(agent) = self.stable_agent_skin_cache.borrow().get(&key) {
                return Ok(Variant::String(agent.clone()));
            }
        }
        let id = match self.prop_controller.special_ids.agent_skin_idx {
            Some(i) => i,
            None => return Err(PropCollectionError::AgentSpecialIdNotSet),
        };
        match self.get_controller_prop(&id, player) {
            Ok(Variant::U32(agent_id)) => match AGENTSMAP.get(&agent_id) {
                Some(agent) => {
                    let agent = agent.to_string();
                    if let Some(key) = cache_key.filter(|_| !is_map_based_default_agent(&agent)) {
                        self.stable_agent_skin_cache
                            .borrow_mut()
                            .insert(key, agent.clone());
                    }
                    return Ok(Variant::String(agent));
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
        Ok(Variant::U32Vec(snapshot.ids))
    }
    pub fn find_my_inventory_weapon_cosmetics(&self, entity_id: &i32) -> Result<Variant, PropCollectionError> {
        let snapshot = self.player_inventory_snapshot(entity_id, true)?;
        let cosmetics = snapshot.cosmetics.unwrap_or_else(|| Arc::from([]));
        Ok(Variant::InventoryWeaponCosmetics(cosmetics))
    }

    fn player_inventory_snapshot(
        &self,
        entity_id: &i32,
        include_cosmetics: bool,
    ) -> Result<PlayerInventorySnapshot, PropCollectionError> {
        if let Some(mut snapshot) = clone_current_inventory_snapshot(
            &self.player_inventory_snapshot_cache,
            *entity_id,
            self.inventory_generation,
        ) {
            if include_cosmetics && snapshot.cosmetics.is_none() {
                snapshot.cosmetics = Some(self.collect_inventory_cosmetics(
                    entity_id,
                    &snapshot.weapon_eids,
                ));
                self.player_inventory_snapshot_cache
                    .borrow_mut()
                    .insert(*entity_id, snapshot.clone());
            }
            return Ok(snapshot);
        }

        if !matches!(self.find_is_alive(entity_id), Ok(Variant::Bool(true))) {
            let snapshot = PlayerInventorySnapshot {
                generation: self.inventory_generation,
                player_signature: self.inventory_player_signature(entity_id),
                weapon_eids: Vec::new(),
                weapon_signature: Vec::new(),
                ids: Vec::new(),
                cosmetics: include_cosmetics.then(|| Arc::from([])),
            };
            self.player_inventory_snapshot_cache
                .borrow_mut()
                .insert(*entity_id, snapshot.clone());
            return Ok(snapshot);
        }

        let inventory_max_len = match self.get_prop_from_ent(&(MY_WEAPONS_OFFSET as u32), entity_id) {
            Ok(Variant::U32(value)) => value,
            _ => return Err(PropCollectionError::InventoryMaxNotFound),
        };
        let mut weapon_eids = Vec::new();
        for index in 1..=inventory_max_len {
            let prop_id = MY_WEAPONS_OFFSET + index;
            let eid = match self.get_prop_from_ent(&(prop_id as u32), entity_id) {
                Ok(Variant::U32(handle)) => demo_network_ehandle_index(handle),
                _ => continue,
            };
            if !weapon_eids.contains(&eid) {
                weapon_eids.push(eid);
            }
        }

        let weapon_signature = weapon_eids
            .iter()
            .map(|eid| {
                let entity = self.entities.get(*eid as usize).and_then(Option::as_ref);
                (
                    *eid,
                    entity.map(|entity| entity.serial).unwrap_or(u32::MAX),
                    entity
                        .map(|entity| entity.cosmetic_revision)
                        .unwrap_or(u64::MAX),
                )
            })
            .collect::<Vec<_>>();
        let player_signature = self.inventory_player_signature(entity_id);
        let mut ids = Vec::new();
        if let Some(item_def_id) = self.prop_controller.special_ids.item_def {
            for eid in &weapon_eids {
                if let Ok(item_def) = self.get_prop_from_ent(&item_def_id, eid) {
                    self.insert_equipment_id(&mut ids, item_def, entity_id);
                }
            }
        }

        let reusable_cosmetics = self
            .player_inventory_snapshot_cache
            .borrow()
            .get(entity_id)
            .filter(|snapshot| {
                inventory_cosmetics_are_reusable(
                    snapshot.player_signature,
                    &snapshot.weapon_signature,
                    player_signature,
                    &weapon_signature,
                )
            })
            .and_then(|snapshot| snapshot.cosmetics.as_ref().map(Arc::clone));
        let cosmetics = if include_cosmetics {
            reusable_cosmetics.or_else(|| {
                Some(self.collect_inventory_cosmetics(entity_id, &weapon_eids))
            })
        } else {
            reusable_cosmetics
        };
        let snapshot = PlayerInventorySnapshot {
            generation: self.inventory_generation,
            player_signature,
            weapon_eids,
            weapon_signature,
            ids,
            cosmetics,
        };
        self.player_inventory_snapshot_cache
            .borrow_mut()
            .insert(*entity_id, snapshot.clone());
        Ok(snapshot)
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
        player_entity_id: &i32,
        weapon_eids: &[i32],
    ) -> Arc<[InventoryWeaponCosmetic]> {
        weapon_eids
            .iter()
            .filter_map(|eid| self.cached_inventory_weapon_cosmetic(player_entity_id, eid))
            .collect()
    }

    fn cached_inventory_weapon_cosmetic(
        &self,
        player_entity_id: &i32,
        weapon_entity_id: &i32,
    ) -> Option<InventoryWeaponCosmetic> {
        let current = self.cached_weapon_cosmetic(weapon_entity_id)?;
        let slot_key = self.owned_weapon_slot_key(player_entity_id, &current);
        if let Some(key) = slot_key {
            if let Some(cached) = self.stable_owned_weapon_cosmetic_cache.borrow().get(&key) {
                return Some(refresh_owned_weapon_dynamic_fields(cached, &current));
            }
        }

        if let Some(key) = slot_key {
            if current.paint_kit != 0
                && current.paint_wear.is_finite()
                && (0.0..=1.0).contains(&current.paint_wear)
            {
                self.stable_owned_weapon_cosmetic_cache
                    .borrow_mut()
                    .insert(key, current.as_ref().clone());
            }
        }
        Some(current.as_ref().clone())
    }

    fn owned_weapon_slot_key(
        &self,
        player_entity_id: &i32,
        cosmetic: &InventoryWeaponCosmetic,
    ) -> Option<(u64, u32, u32)> {
        let player = self.players.get(player_entity_id)?;
        let steam_id = player.steamid?;
        let team_num = player.team_num.filter(|team| matches!(team, 2 | 3))?;
        stable_owned_weapon_slot_key(
            steam_id,
            team_num,
            cosmetic.item_def_index,
            cosmetic.item_account_id,
            cosmetic.original_owner_xuid,
        )
    }

    fn cached_weapon_cosmetic(
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
            Ok(Variant::F32(f)) if f.is_finite() && f.fract() == 0.0 && f >= 0.0 => {
                Ok(Variant::U32(f as u32))
            }
            Ok(Variant::U32(value)) => Ok(Variant::U32(value)),
            Ok(Variant::I32(value)) if value >= 0 => Ok(Variant::U32(value as u32)),
            Ok(_) => Err(PropCollectionError::GloveSkinIdxIncorrectVariant),
            Err(e) => return Err(e),
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
                Some(id) => match self.get_prop_from_ent(&id, entity_id) {
                    Ok(Variant::String(name)) => Some(name),
                    Ok(_) => return Err(DemoParserError::IncorrectMetaDataProp),
                    Err(_) => None,
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
                    match self.should_remove(steamid) {
                        Some(eid) => {
                            self.players.remove(&eid);
                        }
                        None => {}
                    }
                    self.players.insert(
                        e,
                        PlayerMetaData {
                            name,
                            team_num,
                            player_entity_id: player_entid,
                            steamid,
                            controller_entid: Some(*entity_id),
                        },
                    );
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

fn stable_owned_weapon_slot_key(
    steam_id: u64,
    team_num: u32,
    item_def_index: u32,
    item_account_id: Option<u32>,
    original_owner_xuid: Option<u64>,
) -> Option<(u64, u32, u32)> {
    let expected_account_id = steam_id
        .checked_sub(STEAM_ID64_BASE)
        .and_then(|value| u32::try_from(value).ok());
    let owned = item_account_id
        .zip(expected_account_id)
        .is_some_and(|(actual, expected)| actual == expected)
        || original_owner_xuid == Some(steam_id);
    owned.then_some((steam_id, team_num, item_def_index))
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

fn clone_current_inventory_snapshot(
    cache: &std::cell::RefCell<ahash::AHashMap<i32, PlayerInventorySnapshot>>,
    entity_id: i32,
    generation: u64,
) -> Option<PlayerInventorySnapshot> {
    let cache = cache.borrow();
    cache
        .get(&entity_id)
        .filter(|snapshot| snapshot.generation == generation)
        .cloned()
}

fn refresh_owned_weapon_dynamic_fields(
    stable: &InventoryWeaponCosmetic,
    current: &InventoryWeaponCosmetic,
) -> InventoryWeaponCosmetic {
    let mut cosmetic = stable.clone();
    cosmetic.item_id_high = current.item_id_high;
    cosmetic.item_id_low = current.item_id_low;
    cosmetic.item_account_id = current.item_account_id;
    cosmetic.original_owner_xuid = current.original_owner_xuid;
    cosmetic.stattrak_counter = current.stattrak_counter;
    cosmetic
}

#[cfg(test)]
mod tests {
    use super::{
        clone_current_inventory_snapshot, inventory_cosmetics_are_reusable,
        is_map_based_default_agent,
        refresh_owned_weapon_dynamic_fields, stable_owned_weapon_slot_key,
        stickers_from_attributes, should_collect_player_rows, StickerAttribute, STEAM_ID64_BASE,
    };
    use crate::second_pass::parser_settings::PlayerInventorySnapshot;
    use crate::second_pass::variants::InventoryWeaponCosmetic;
    use ahash::AHashMap;
    use std::cell::RefCell;
    use std::sync::Arc;

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
    fn stable_weapon_slots_require_matching_ownership_and_keep_sides_separate() {
        let account_id = 123;
        let steam_id = STEAM_ID64_BASE + u64::from(account_id);

        assert_eq!(
            stable_owned_weapon_slot_key(steam_id, 2, 7, Some(account_id), None),
            Some((steam_id, 2, 7))
        );
        assert_eq!(
            stable_owned_weapon_slot_key(steam_id, 3, 7, None, Some(steam_id)),
            Some((steam_id, 3, 7))
        );
        assert_eq!(
            stable_owned_weapon_slot_key(steam_id, 2, 7, Some(account_id + 1), None),
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
    fn cloned_inventory_snapshot_releases_cache_borrow_before_upgrade() {
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

        let mut snapshot = clone_current_inventory_snapshot(&cache, 7, 3).unwrap();
        snapshot.cosmetics = Some(Arc::from([]));
        cache.borrow_mut().insert(7, snapshot);

        assert!(cache.borrow().get(&7).unwrap().cosmetics.is_some());
    }

    #[test]
    fn stable_weapon_snapshot_refreshes_only_dynamic_identity_fields() {
        let stable = cosmetic(7, 600, 0.01, 1, 10);
        let current = cosmetic(7, 999, 0.42, 2, 25);
        let refreshed = refresh_owned_weapon_dynamic_fields(&stable, &current);

        assert_eq!(refreshed.paint_kit, stable.paint_kit);
        assert_eq!(refreshed.paint_wear.to_bits(), stable.paint_wear.to_bits());
        assert_eq!(refreshed.custom_name, stable.custom_name);
        assert_eq!(refreshed.item_id_low, current.item_id_low);
        assert_eq!(refreshed.item_account_id, current.item_account_id);
        assert_eq!(refreshed.original_owner_xuid, current.original_owner_xuid);
        assert_eq!(refreshed.stattrak_counter, current.stattrak_counter);
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

    fn cosmetic(
        item_def_index: u32,
        paint_kit: u32,
        paint_wear: f32,
        identity: u32,
        stattrak_counter: i32,
    ) -> InventoryWeaponCosmetic {
        InventoryWeaponCosmetic {
            item_def_index,
            item_id_high: Some(identity + 100),
            item_id_low: Some(identity),
            item_account_id: Some(identity + 200),
            original_owner_xuid: Some(76561198000000000 + u64::from(identity)),
            paint_kit,
            paint_seed: 17,
            paint_wear,
            entity_quality: Some(3),
            stattrak_counter: Some(stattrak_counter),
            attributes: Vec::new(),
            custom_name: Some("stable".to_string()),
            stickers: Vec::new(),
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
