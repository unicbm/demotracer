/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use serde::{Deserialize, Serialize};
use std::fmt;
use std::str::FromStr;
use std::sync::Arc;

pub const DEMOTRACER_ABI: i32 = 17;
pub const DTR_FORMAT_VERSION: u32 = 9;

pub const COMMAND_FIELD_FORWARD_MOVE: u32 = 1 << 0;
pub const COMMAND_FIELD_LEFT_MOVE: u32 = 1 << 1;
pub const COMMAND_FIELD_UP_MOVE: u32 = 1 << 2;
pub const COMMAND_FIELD_VIEW_ANGLES: u32 = 1 << 3;
pub const COMMAND_FIELD_BUTTONS: u32 = 1 << 4;
pub const COMMAND_FIELD_MOUSE: u32 = 1 << 5;
pub const COMMAND_FIELD_WEAPON_SELECT: u32 = 1 << 6;
pub const COMMAND_FIELD_LEFT_HAND: u32 = 1 << 7;
pub const COMMAND_FIELDS_ALL: u32 = COMMAND_FIELD_FORWARD_MOVE
    | COMMAND_FIELD_LEFT_MOVE
    | COMMAND_FIELD_UP_MOVE
    | COMMAND_FIELD_VIEW_ANGLES
    | COMMAND_FIELD_BUTTONS
    | COMMAND_FIELD_MOUSE
    | COMMAND_FIELD_WEAPON_SELECT
    | COMMAND_FIELD_LEFT_HAND;

pub const INPUT_HISTORY_FIELD_VIEW_ANGLES: u32 = 1 << 0;
pub const INPUT_HISTORY_FIELD_RENDER_TICK_COUNT: u32 = 1 << 1;
pub const INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION: u32 = 1 << 2;
pub const INPUT_HISTORY_FIELD_PLAYER_TICK_COUNT: u32 = 1 << 3;
pub const INPUT_HISTORY_FIELD_PLAYER_TICK_FRACTION: u32 = 1 << 4;
pub const INPUT_HISTORY_FIELD_CL_INTERP_FRACTION: u32 = 1 << 5;
pub const INPUT_HISTORY_FIELD_SV_INTERP0_SRC_TICK: u32 = 1 << 6;
pub const INPUT_HISTORY_FIELD_SV_INTERP0_DST_TICK: u32 = 1 << 7;
pub const INPUT_HISTORY_FIELD_SV_INTERP0_FRACTION: u32 = 1 << 8;
pub const INPUT_HISTORY_FIELD_SV_INTERP1_SRC_TICK: u32 = 1 << 9;
pub const INPUT_HISTORY_FIELD_SV_INTERP1_DST_TICK: u32 = 1 << 10;
pub const INPUT_HISTORY_FIELD_SV_INTERP1_FRACTION: u32 = 1 << 11;
pub const INPUT_HISTORY_FIELD_PLAYER_INTERP_SRC_TICK: u32 = 1 << 12;
pub const INPUT_HISTORY_FIELD_PLAYER_INTERP_DST_TICK: u32 = 1 << 13;
pub const INPUT_HISTORY_FIELD_PLAYER_INTERP_FRACTION: u32 = 1 << 14;
pub const INPUT_HISTORY_FIELD_FRAME_NUMBER: u32 = 1 << 15;
pub const INPUT_HISTORY_FIELD_TARGET_ENT_INDEX: u32 = 1 << 16;
pub const INPUT_HISTORY_FIELD_SHOOT_POSITION: u32 = 1 << 17;
pub const INPUT_HISTORY_FIELD_TARGET_HEAD_POS_CHECK: u32 = 1 << 18;
pub const INPUT_HISTORY_FIELD_TARGET_ABS_POS_CHECK: u32 = 1 << 19;
pub const INPUT_HISTORY_FIELD_TARGET_ABS_ANG_CHECK: u32 = 1 << 20;
pub const INPUT_HISTORY_FIELDS_ALL: u32 = (1 << 21) - 1;
pub const INPUT_HISTORY_CLIENT_TICK_UNKNOWN: i32 = i32::MIN;

pub fn public_demo_path(path: &str) -> String {
    let normalized = path.replace('\\', "/");
    normalized
        .rsplit('/')
        .find(|part| !part.is_empty())
        .unwrap_or("demo.dem")
        .to_string()
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Side {
    Both,
    T,
    Ct,
}

impl Side {
    pub fn matches_team(self, team_num: u8) -> bool {
        match self {
            Side::Both => team_num == 2 || team_num == 3,
            Side::T => team_num == 2,
            Side::Ct => team_num == 3,
        }
    }

    pub fn team_dir(team_num: u8) -> &'static str {
        match team_num {
            2 => "t",
            3 => "ct",
            _ => "unknown",
        }
    }
}

impl Default for Side {
    fn default() -> Self {
        Side::Both
    }
}

impl fmt::Display for Side {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Side::Both => f.write_str("both"),
            Side::T => f.write_str("t"),
            Side::Ct => f.write_str("ct"),
        }
    }
}

impl FromStr for Side {
    type Err = String;

    fn from_str(value: &str) -> std::result::Result<Self, Self::Err> {
        match value.to_ascii_lowercase().as_str() {
            "both" | "all" | "两边" | "全部" => Ok(Side::Both),
            "t" | "terrorist" | "terrorists" => Ok(Side::T),
            "ct" | "counter" | "counter-terrorist" | "counter-terrorists" => Ok(Side::Ct),
            _ => Err(format!("unknown side: {value}")),
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum SubtickMode {
    #[default]
    Auto,
    Off,
}

impl fmt::Display for SubtickMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SubtickMode::Auto => f.write_str("auto"),
            SubtickMode::Off => f.write_str("off"),
        }
    }
}

impl FromStr for SubtickMode {
    type Err = String;

    fn from_str(value: &str) -> std::result::Result<Self, Self::Err> {
        match value.to_ascii_lowercase().as_str() {
            "auto" | "on" | "1" | "true" => Ok(SubtickMode::Auto),
            "off" | "0" | "false" => Ok(SubtickMode::Off),
            _ => Err(format!("unknown subtick mode: {value}")),
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct MovementSnapshot {
    pub origin: [f32; 3],
    pub velocity: [f32; 3],
    pub angles: [f32; 3],
    pub entity_flags: u32,
    pub move_type: u8,
    pub buttons: u64,
    pub buttons1: u64,
    pub buttons2: u64,
    pub duck_amount: f32,
    pub duck_speed: f32,
    pub ladder_normal: [f32; 3],
    pub ducked: u8,
    pub ducking: u8,
    pub desires_duck: u8,
    pub actual_move_type: u8,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayTick {
    pub pre: MovementSnapshot,
    pub post: MovementSnapshot,
    pub weapon_def_index: i32,
    pub num_subtick: u32,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayCommandFrame {
    pub forward_move: f32,
    pub left_move: f32,
    pub up_move: f32,
    pub pitch: f32,
    pub yaw: f32,
    pub roll: f32,
    pub buttons: u64,
    pub buttons1: u64,
    pub buttons2: u64,
    pub mouse_dx: i32,
    pub mouse_dy: i32,
    pub weapon_select: i32,
    pub fields: u32,
    pub left_hand_desired: u8,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayMovementExtra {
    pub fields: u32,
    pub jump_pressed_time: f32,
    pub last_duck_time: f32,
    pub last_actual_jump_press_tick: i32,
    pub last_actual_jump_press_frac: f32,
    pub last_usable_jump_press_tick: i32,
    pub last_usable_jump_press_frac: f32,
    pub last_landed_tick: i32,
    pub last_landed_frac: f32,
    pub last_landed_velocity: [f32; 3],
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayInputHistoryTick {
    pub source_client_tick: i32,
    pub attack1_start_history_index: i32,
    pub attack2_start_history_index: i32,
    pub num_entries: u32,
}

impl Default for ReplayInputHistoryTick {
    fn default() -> Self {
        Self {
            source_client_tick: INPUT_HISTORY_CLIENT_TICK_UNKNOWN,
            attack1_start_history_index: -1,
            attack2_start_history_index: -1,
            num_entries: 0,
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayInputHistoryEntry {
    pub fields: u32,
    pub view_angles: [f32; 3],
    pub render_tick_count: i32,
    pub render_tick_fraction: f32,
    pub player_tick_count: i32,
    pub player_tick_fraction: f32,
    pub cl_interp_fraction: f32,
    pub sv_interp0_src_tick: i32,
    pub sv_interp0_dst_tick: i32,
    pub sv_interp0_fraction: f32,
    pub sv_interp1_src_tick: i32,
    pub sv_interp1_dst_tick: i32,
    pub sv_interp1_fraction: f32,
    pub player_interp_src_tick: i32,
    pub player_interp_dst_tick: i32,
    pub player_interp_fraction: f32,
    pub frame_number: i32,
    pub target_ent_index: i32,
    pub shoot_position: [f32; 3],
    pub target_head_pos_check: [f32; 3],
    pub target_abs_pos_check: [f32; 3],
    pub target_abs_ang_check: [f32; 3],
}

impl Default for ReplayInputHistoryEntry {
    fn default() -> Self {
        Self {
            fields: 0,
            view_angles: [0.0; 3],
            render_tick_count: 0,
            render_tick_fraction: 0.0,
            player_tick_count: 0,
            player_tick_fraction: 0.0,
            cl_interp_fraction: 0.0,
            sv_interp0_src_tick: -1,
            sv_interp0_dst_tick: -1,
            sv_interp0_fraction: 0.0,
            sv_interp1_src_tick: -1,
            sv_interp1_dst_tick: -1,
            sv_interp1_fraction: 0.0,
            player_interp_src_tick: -1,
            player_interp_dst_tick: -1,
            player_interp_fraction: 0.0,
            frame_number: 0,
            target_ent_index: -1,
            shoot_position: [0.0; 3],
            target_head_pos_check: [0.0; 3],
            target_abs_pos_check: [0.0; 3],
            target_abs_ang_check: [0.0; 3],
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ProjectileKind {
    #[default]
    Unknown,
    Smoke,
    Flash,
    He,
    Molotov,
    Decoy,
}

impl ProjectileKind {
    pub fn from_grenade_type(value: &str) -> Self {
        let lower = value.to_ascii_lowercase();
        if lower.contains("smoke") {
            Self::Smoke
        } else if lower.contains("flash") {
            Self::Flash
        } else if lower.contains("hegrenade") || lower.contains("he_grenade") {
            Self::He
        } else if lower.contains("molotov")
            || lower.contains("incgrenade")
            || lower.contains("inferno")
        {
            Self::Molotov
        } else if lower.contains("decoy") {
            Self::Decoy
        } else {
            Self::Unknown
        }
    }

    pub fn to_u8(self) -> u8 {
        match self {
            Self::Unknown => 0,
            Self::Smoke => 1,
            Self::Flash => 2,
            Self::He => 3,
            Self::Molotov => 4,
            Self::Decoy => 5,
        }
    }

    pub fn from_u8(value: u8) -> Self {
        match value {
            1 => Self::Smoke,
            2 => Self::Flash,
            3 => Self::He,
            4 => Self::Molotov,
            5 => Self::Decoy,
            _ => Self::Unknown,
        }
    }

    pub fn weapon_def_index(self) -> i32 {
        match self {
            Self::Flash => 43,
            Self::He => 44,
            Self::Smoke => 45,
            Self::Molotov => 46,
            Self::Decoy => 47,
            Self::Unknown => -1,
        }
    }

    pub fn weapon_def_index_from_grenade_type(value: &str) -> i32 {
        let lower = value.to_ascii_lowercase();
        if lower.contains("flash") {
            43
        } else if lower.contains("hegrenade") || lower.contains("he_grenade") {
            44
        } else if lower.contains("smoke") {
            45
        } else if lower.contains("incgrenade") {
            48
        } else if lower.contains("molotov") || lower.contains("inferno") {
            46
        } else if lower.contains("decoy") {
            47
        } else {
            -1
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ProjectileEffectSource {
    #[default]
    Unknown,
    SmokeDetonationProp,
    SmokeDetonationEvent,
    FlashDetonationEvent,
    HeDetonationEvent,
    MolotovDetonationEvent,
    InfernoStartBurnEvent,
    DecoyStartedEvent,
    DecoyDetonationEvent,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayProjectile {
    pub tick_index: u32,
    pub kind: ProjectileKind,
    pub weapon_def_index: i32,
    pub initial_position: [f32; 3],
    pub initial_velocity: [f32; 3],
    pub detonation_position: [f32; 3],
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayProjectileMetadata {
    pub tick_index: u32,
    pub tick: i32,
    pub kind: ProjectileKind,
    pub weapon_def_index: i32,
    pub effect_tick_index: Option<u32>,
    pub effect_tick: Option<i32>,
    pub effect_position: [f32; 3],
    pub effect_source: ProjectileEffectSource,
    pub effect_confidence: f32,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct HighFidelityMetadata {
    pub schema_version: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub round_start_balance: Option<u32>,
    pub events: Vec<ReplayHifiEvent>,
    pub inventory_snapshots: Vec<ReplayInventorySnapshot>,
    #[serde(default)]
    pub projectiles: Vec<ReplayProjectileMetadata>,
}

impl Default for HighFidelityMetadata {
    fn default() -> Self {
        Self {
            schema_version: 4,
            round_start_balance: None,
            events: Vec::new(),
            inventory_snapshots: Vec::new(),
            projectiles: Vec::new(),
        }
    }
}

impl HighFidelityMetadata {
    pub fn new(
        events: Vec<ReplayHifiEvent>,
        inventory_snapshots: Vec<ReplayInventorySnapshot>,
    ) -> Self {
        Self {
            schema_version: 4,
            round_start_balance: None,
            events,
            inventory_snapshots,
            projectiles: Vec::new(),
        }
    }

    pub fn with_projectiles(
        round_start_balance: Option<u32>,
        events: Vec<ReplayHifiEvent>,
        inventory_snapshots: Vec<ReplayInventorySnapshot>,
        projectiles: Vec<ReplayProjectileMetadata>,
    ) -> Self {
        Self {
            schema_version: 4,
            round_start_balance,
            events,
            inventory_snapshots,
            projectiles,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.round_start_balance.is_none()
            && self.events.is_empty()
            && self.inventory_snapshots.is_empty()
            && self.projectiles.is_empty()
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ReplayHifiEventKind {
    BombInitialOwner,
    ItemDrop,
    ItemPickup,
    ItemTransfer,
    BombDrop,
    BombPickup,
    BombBeginplant,
    BombPlanted,
    WeaponFire,
    PlayerHurt,
    PlayerDeath,
    RoundStart,
    RoundFreezeEnd,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayHifiEvent {
    pub tick_index: u32,
    pub tick: i32,
    pub kind: ReplayHifiEventKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub actor_steam_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_steam_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub weapon_def_index: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entity_id: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub actor_count_after: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_count_after: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub damage: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub health: Option<i32>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayInventorySnapshot {
    pub tick_index: u32,
    pub tick: i32,
    pub steam_id: u64,
    pub weapon_def_counts: Vec<ReplayInventoryItemCount>,
    pub active_weapon_def_index: i32,
    pub armor_value: u32,
    pub has_helmet: bool,
    pub has_defuser: bool,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct ReplayInventoryItemCount {
    pub weapon_def_index: i32,
    pub count: u32,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct SubtickMove {
    pub when: f32,
    pub button: u32,
    pub pressed: f32,
    pub analog_forward: f32,
    pub analog_left: f32,
    pub pitch_delta: f32,
    pub yaw_delta: f32,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct Cs2RecHeader {
    pub version: u32,
    pub tick_rate: f32,
    pub map: String,
    pub round: u32,
    pub side: u8,
    pub steam_id: u64,
    pub player_name: String,
    pub flags: u32,
    pub play_start_tick_index: u32,
}

impl Default for Cs2RecHeader {
    fn default() -> Self {
        Self {
            version: DTR_FORMAT_VERSION,
            tick_rate: 64.0,
            map: String::new(),
            round: 0,
            side: 0,
            steam_id: 0,
            player_name: String::new(),
            flags: 0,
            play_start_tick_index: 0,
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct Cs2Rec {
    pub header: Cs2RecHeader,
    pub ticks: Vec<ReplayTick>,
    pub projectiles: Vec<ReplayProjectile>,
    pub high_fidelity: HighFidelityMetadata,
    pub subticks: Vec<SubtickMove>,
    pub command_frames: Vec<ReplayCommandFrame>,
    pub movement_extras: Vec<ReplayMovementExtra>,
    pub input_history_ticks: Vec<ReplayInputHistoryTick>,
    pub input_history_entries: Vec<ReplayInputHistoryEntry>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedDemo {
    pub path: String,
    pub stem: String,
    pub demo_sha256: String,
    pub map: String,
    #[serde(default)]
    pub demo_patch_version: Option<i32>,
    #[serde(default)]
    pub demo_version_name: Option<String>,
    #[serde(default)]
    pub server_name: Option<String>,
    #[serde(default)]
    pub playback_time_seconds: Option<f32>,
    pub tick_rate: f32,
    pub round_freeze_end_ticks: Vec<i32>,
    pub bomb_beginplant_ticks: Vec<i32>,
    pub bomb_planted_ticks: Vec<i32>,
    pub rows: Vec<ParsedPlayerTick>,
    pub projectiles: Vec<ParsedProjectile>,
    pub voice_frames: Vec<ParsedVoiceFrame>,
    pub events: Vec<ParsedGameEvent>,
    pub avatar_overrides: Vec<ParsedAvatarOverride>,
    pub econ_items: Vec<ParsedEconItem>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedVoiceFrame {
    pub tick: i32,
    pub xuid: u64,
    pub client: i32,
    pub format: i32,
    pub sample_rate: Option<u32>,
    pub voice_level: Option<f32>,
    pub sequence_bytes: Option<i32>,
    pub section_number: Option<u32>,
    pub uncompressed_sample_offset: Option<u32>,
    pub num_packets: Option<u32>,
    pub packet_offsets: Vec<u32>,
    pub audio: Vec<u8>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedEconItem {
    pub steam_id: Option<u64>,
    pub item_def_index: Option<u32>,
    pub paint_kit: Option<u32>,
    pub paint_seed: Option<u32>,
    pub paint_wear_raw: Option<u32>,
    pub paint_wear: Option<f32>,
    #[serde(default)]
    pub custom_name: Option<String>,
    pub item_name: Option<String>,
    pub skin_name: Option<String>,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum AvatarImageFormat {
    Png,
    Jpeg,
    #[default]
    Binary,
}

impl AvatarImageFormat {
    pub fn extension(self) -> &'static str {
        match self {
            Self::Png => "png",
            Self::Jpeg => "jpg",
            Self::Binary => "bin",
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ParsedAvatarOverride {
    pub steam_id: u64,
    pub format: AvatarImageFormat,
    pub sha256: String,
    pub source: String,
    #[serde(skip)]
    pub bytes: Vec<u8>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedProjectile {
    pub tick: i32,
    pub steam_id: u64,
    pub name: String,
    pub grenade_type: String,
    pub kind: ProjectileKind,
    pub weapon_def_index: i32,
    pub initial_position: [f32; 3],
    pub initial_velocity: [f32; 3],
    pub detonation_position: [f32; 3],
    pub effect_position: [f32; 3],
    pub effect_tick: Option<i32>,
    pub effect_source: ProjectileEffectSource,
    pub effect_confidence: f32,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedGameEvent {
    pub tick: i32,
    pub name: String,
    pub user_steam_id: Option<u64>,
    pub attacker_steam_id: Option<u64>,
    pub victim_steam_id: Option<u64>,
    pub weapon_def_index: Option<i32>,
    pub item_name: Option<String>,
    pub entity_id: Option<i32>,
    pub damage: Option<i32>,
    pub health: Option<i32>,
    /// Winning engine side for `round_end` (`2` = T, `3` = CT).
    /// This is deliberately kept as a side, not a persistent team identity.
    pub winner_side: Option<u8>,
    pub chat_text: Option<String>,
    pub chat_scope: Option<String>,
    pub chat_message_name: Option<String>,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ParsedWeaponSticker {
    pub slot: u8,
    pub sticker_id: u32,
    pub wear: f32,
    pub offset_x: f32,
    pub offset_y: f32,
    pub scale: Option<f32>,
    pub rotation: Option<f32>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedInventoryWeaponCosmetic {
    pub item_def_index: i32,
    pub item_id_high: Option<u32>,
    pub item_id_low: Option<u32>,
    pub item_account_id: Option<u32>,
    pub original_owner_xuid: Option<u64>,
    pub paint_kit: u32,
    pub paint_seed: u32,
    pub paint_wear: f32,
    pub entity_quality: Option<i32>,
    pub stattrak_counter: Option<i32>,
    pub attributes: Vec<ParsedInventoryWeaponAttribute>,
    pub custom_name: Option<String>,
    pub stickers: Vec<ParsedWeaponSticker>,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, Ord, PartialEq, PartialOrd, Serialize)]
pub struct ParsedScoreboardFlair {
    pub item_def_index: u32,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ParsedInventoryWeaponAttribute {
    pub definition_index: u32,
    pub raw_value: f32,
    pub raw_value_bits: u32,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedPlayerTick {
    pub tick: i32,
    pub steam_id: u64,
    pub name: String,
    pub team_num: u8,
    pub is_alive: bool,
    pub round: u32,
    pub round_in_progress: bool,
    pub is_freeze_period: bool,
    pub game_time: Option<f32>,
    pub origin: [f32; 3],
    pub velocity: [f32; 3],
    pub pitch: f32,
    pub yaw: f32,
    pub buttons: u64,
    #[serde(default)]
    pub buttonstates_present: bool,
    pub buttonstate1: u64,
    pub buttonstate2: u64,
    pub buttonstate3: u64,
    pub usercmd_forward_move: Option<f32>,
    pub usercmd_left_move: Option<f32>,
    pub usercmd_up_move: Option<f32>,
    pub usercmd_pitch: Option<f32>,
    pub usercmd_yaw: Option<f32>,
    pub usercmd_roll: Option<f32>,
    pub usercmd_mouse_dx: Option<i32>,
    pub usercmd_mouse_dy: Option<i32>,
    pub usercmd_weapon_select: Option<i32>,
    pub usercmd_left_hand_desired: Option<bool>,
    pub usercmd_client_tick: Option<i32>,
    pub usercmd_attack1_start_history_index: i32,
    pub usercmd_attack2_start_history_index: i32,
    pub input_history: Vec<ReplayInputHistoryEntry>,
    pub item_def_idx: i32,
    pub inventory_as_ids: Vec<i32>,
    pub inventory_weapon_cosmetics: Arc<[ParsedInventoryWeaponCosmetic]>,
    pub music_kit_id: Option<u32>,
    pub scoreboard_flair: Option<ParsedScoreboardFlair>,
    pub agent_item_def_index: Option<u32>,
    pub agent_skin: Option<String>,
    pub active_weapon_paint_kit: Option<u32>,
    pub active_weapon_paint_seed: Option<u32>,
    pub active_weapon_paint_wear: Option<f32>,
    pub active_weapon_original_owner_steam_id: Option<u64>,
    pub active_weapon_item_account_id: Option<u32>,
    pub active_weapon_item_id: Option<u64>,
    pub active_weapon_custom_name: Option<String>,
    pub active_weapon_stickers: Vec<ParsedWeaponSticker>,
    pub glove_item_def_index: Option<i32>,
    pub glove_paint_kit: Option<u32>,
    pub glove_paint_seed: Option<u32>,
    pub glove_paint_wear: Option<f32>,
    pub crosshair_code: Option<String>,
    pub viewmodel_left_handed: Option<bool>,
    pub viewmodel_fov: Option<f32>,
    pub viewmodel_offset_x: Option<f32>,
    pub viewmodel_offset_y: Option<f32>,
    pub viewmodel_offset_z: Option<f32>,
    pub scoreboard_score: Option<i32>,
    pub scoreboard_mvps: Option<u32>,
    pub scoreboard_kills: Option<u32>,
    pub scoreboard_deaths: Option<u32>,
    pub scoreboard_assists: Option<u32>,
    pub scoreboard_headshot_kills: Option<u32>,
    pub scoreboard_damage: Option<u32>,
    pub armor_value: u32,
    pub has_helmet: bool,
    pub has_defuser: bool,
    pub round_start_equip_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub account_balance: Option<u32>,
    pub entity_flags: u32,
    pub move_type: u8,
    pub duck_amount: Option<f32>,
    pub duck_speed: Option<f32>,
    pub ladder_normal: Option<[f32; 3]>,
    pub ducked: Option<bool>,
    pub ducking: Option<bool>,
    pub desires_duck: Option<bool>,
    pub subtick_moves: Vec<SubtickMove>,
    pub subtick_button_truncated: usize,
    pub player_user_id: Option<i32>,
    pub player_entity_id: Option<i32>,
    pub player_color: Option<String>,
    pub team_rounds_total: Option<u32>,
    pub team_name: Option<String>,
    pub team_clan_name: Option<String>,
}

impl ParsedPlayerTick {
    pub fn snapshot(&self) -> MovementSnapshot {
        const FL_DUCKING: u32 = 1 << 1;
        const IN_DUCK: u64 = 1 << 2;

        let (buttons, buttons1, buttons2) = if self.buttonstates_present {
            (self.buttonstate1, self.buttonstate2, self.buttonstate3)
        } else {
            (self.buttons, 0, 0)
        };
        let physically_ducked = (self.entity_flags & FL_DUCKING) != 0;
        let wants_duck = ((buttons | buttons1) & IN_DUCK) != 0;
        let ducked = self.ducked.unwrap_or(physically_ducked);
        let ducking = self.ducking.unwrap_or(wants_duck && !physically_ducked);
        let desires_duck = self.desires_duck.unwrap_or(wants_duck);

        MovementSnapshot {
            origin: self.origin,
            velocity: self.velocity,
            angles: [self.pitch, self.yaw, 0.0],
            entity_flags: self.entity_flags,
            move_type: self.move_type,
            buttons,
            buttons1,
            buttons2,
            duck_amount: self
                .duck_amount
                .unwrap_or(if physically_ducked { 1.0 } else { 0.0 }),
            duck_speed: self
                .duck_speed
                .unwrap_or(if physically_ducked || wants_duck {
                    8.0
                } else {
                    0.0
                }),
            ladder_normal: self.ladder_normal.unwrap_or([0.0, 0.0, 0.0]),
            ducked: u8::from(ducked),
            ducking: u8::from(ducking),
            desires_duck: u8::from(desires_duck),
            actual_move_type: self.move_type,
        }
    }

    pub fn command_frame(&self) -> ReplayCommandFrame {
        let (buttons, buttons1, buttons2) = if self.buttonstates_present {
            (self.buttonstate1, self.buttonstate2, self.buttonstate3)
        } else {
            (self.buttons, 0, 0)
        };
        let mut fields = 0_u32;
        let mut frame = ReplayCommandFrame {
            buttons,
            buttons1,
            buttons2,
            weapon_select: -1,
            ..ReplayCommandFrame::default()
        };

        if let Some(value) = self.usercmd_forward_move {
            frame.forward_move = value;
            fields |= COMMAND_FIELD_FORWARD_MOVE;
        }
        if let Some(value) = self.usercmd_left_move {
            frame.left_move = value;
            fields |= COMMAND_FIELD_LEFT_MOVE;
        }
        if let Some(value) = self.usercmd_up_move {
            frame.up_move = value;
            fields |= COMMAND_FIELD_UP_MOVE;
        }
        let pitch = self.usercmd_pitch.unwrap_or(self.pitch);
        let yaw = self.usercmd_yaw.unwrap_or(self.yaw);
        let roll = self.usercmd_roll.unwrap_or(0.0);
        frame.pitch = pitch;
        frame.yaw = yaw;
        frame.roll = roll;
        if self.usercmd_pitch.is_some() || self.usercmd_yaw.is_some() || self.usercmd_roll.is_some()
        {
            fields |= COMMAND_FIELD_VIEW_ANGLES;
        }
        if self.buttonstates_present {
            fields |= COMMAND_FIELD_BUTTONS;
        }
        if let (Some(dx), Some(dy)) = (self.usercmd_mouse_dx, self.usercmd_mouse_dy) {
            frame.mouse_dx = dx;
            frame.mouse_dy = dy;
            fields |= COMMAND_FIELD_MOUSE;
        }
        if let Some(value) = self.usercmd_weapon_select {
            frame.weapon_select = value;
            fields |= COMMAND_FIELD_WEAPON_SELECT;
        }
        if let Some(value) = self.usercmd_left_hand_desired {
            frame.left_hand_desired = u8::from(value);
            fields |= COMMAND_FIELD_LEFT_HAND;
        }
        frame.fields = fields;
        frame
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn public_demo_path_strips_local_directories() {
        assert_eq!(public_demo_path(r"C:\demos\match.dem"), "match.dem");
        assert_eq!(public_demo_path("/home/user/demos/match.dem"), "match.dem");
        assert_eq!(public_demo_path("match.dem"), "match.dem");
        assert_eq!(public_demo_path(""), "demo.dem");
    }

    #[test]
    fn snapshot_preserves_real_duck_and_ladder_fields() {
        let row = ParsedPlayerTick {
            duck_amount: Some(0.375),
            duck_speed: Some(-0.0),
            ladder_normal: Some([1.0, -0.0, 0.5]),
            ducked: Some(false),
            ducking: Some(true),
            desires_duck: Some(true),
            ..ParsedPlayerTick::default()
        };

        let snapshot = row.snapshot();

        assert_eq!(snapshot.duck_amount.to_bits(), 0.375_f32.to_bits());
        assert_eq!(snapshot.duck_speed.to_bits(), (-0.0_f32).to_bits());
        assert_eq!(snapshot.ladder_normal[0].to_bits(), 1.0_f32.to_bits());
        assert_eq!(snapshot.ladder_normal[1].to_bits(), (-0.0_f32).to_bits());
        assert_eq!(snapshot.ladder_normal[2].to_bits(), 0.5_f32.to_bits());
        assert_eq!(snapshot.ducked, 0);
        assert_eq!(snapshot.ducking, 1);
        assert_eq!(snapshot.desires_duck, 1);
    }

    #[test]
    fn snapshot_duck_fallback_separates_desire_from_physical_duck() {
        const FL_DUCKING: u32 = 1 << 1;
        const IN_DUCK: u64 = 1 << 2;

        let wanting_duck = ParsedPlayerTick {
            buttons: IN_DUCK,
            ..ParsedPlayerTick::default()
        }
        .snapshot();

        assert_eq!(wanting_duck.duck_amount, 0.0);
        assert_eq!(wanting_duck.ducked, 0);
        assert_eq!(wanting_duck.ducking, 1);
        assert_eq!(wanting_duck.desires_duck, 1);

        let physically_ducked = ParsedPlayerTick {
            entity_flags: FL_DUCKING,
            ..ParsedPlayerTick::default()
        }
        .snapshot();

        assert_eq!(physically_ducked.duck_amount, 1.0);
        assert_eq!(physically_ducked.ducked, 1);
        assert_eq!(physically_ducked.ducking, 0);
        assert_eq!(physically_ducked.desires_duck, 0);
    }

    #[test]
    fn explicit_zero_buttonstates_do_not_fall_back_to_legacy_buttons() {
        let row = ParsedPlayerTick {
            buttons: 1,
            buttonstates_present: true,
            buttonstate1: 0,
            buttonstate2: 0,
            buttonstate3: 0,
            ..ParsedPlayerTick::default()
        };

        let snapshot = row.snapshot();
        let command = row.command_frame();

        assert_eq!(snapshot.buttons, 0);
        assert_eq!(snapshot.buttons1, 0);
        assert_eq!(snapshot.buttons2, 0);
        assert_eq!(command.buttons, 0);
        assert_eq!(command.buttons1, 0);
        assert_eq!(command.buttons2, 0);
        assert_ne!(command.fields & COMMAND_FIELD_BUTTONS, 0);
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum RoundStatus {
    Recommended,
    Partial,
    Suspicious,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RoundSummary {
    pub round: u32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub duration_seconds: f32,
    pub t_players: usize,
    pub ct_players: usize,
    pub total_players: usize,
    pub valid_rows: usize,
    pub status: RoundStatus,
    pub problems: Vec<String>,
}

impl RoundSummary {
    pub fn selected_by_default(&self) -> bool {
        matches!(self.status, RoundStatus::Recommended | RoundStatus::Partial)
    }

    pub fn requires_suspicious_opt_in(&self) -> bool {
        self.status == RoundStatus::Suspicious
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct DemoAnalysis {
    pub demo_path: String,
    pub demo_stem: String,
    pub map: String,
    pub tick_rate: f32,
    pub row_count: usize,
    pub rounds: Vec<RoundSummary>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionManifest {
    pub demo_path: String,
    pub demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub avatar_overrides: Vec<ManifestAvatarOverride>,
    pub rounds: Vec<ConvertedRound>,
    pub files: Vec<ConvertedFile>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ManifestAvatarOverride {
    pub steam_id: u64,
    pub format: AvatarImageFormat,
    pub sha256: String,
    pub path: String,
    pub source: String,
    pub bytes: usize,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConvertedRound {
    pub round: u32,
    pub recording_start_tick: i32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub original_end_tick: i32,
    pub bomb_planted_tick: Option<i32>,
    pub bomb_planted_seconds_after_live: Option<f32>,
    pub freeze_preroll_ticks: i32,
    pub duration_seconds: f32,
    pub pistol_round: bool,
    pub cut_reason: Option<String>,
    pub t_economy: TeamEconomy,
    pub ct_economy: TeamEconomy,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scoreboard: Option<ReplayRoundScoreboard>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub chat_messages: Vec<ReplayChatMessage>,
    pub files: usize,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct TeamEconomy {
    pub side: String,
    pub players: usize,
    pub round_start_equipment_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub class: EconomyClass,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum EconomyClass {
    Pistol,
    Eco,
    Force,
    #[default]
    Full,
    Unknown,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConvertedFile {
    pub path: String,
    pub round: u32,
    pub side: String,
    pub steam_id: u64,
    pub player_name: String,
    pub ticks: usize,
    pub subticks: usize,
    pub play_start_tick_index: u32,
    pub first_weapon_def_index: i32,
    pub preload_weapon_def_indices: Vec<i32>,
    #[serde(default)]
    pub hifi_event_count: usize,
    #[serde(default)]
    pub inventory_snapshot_count: usize,
    #[serde(default)]
    pub loadout: ReplayLoadout,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub music_kit_id: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scoreboard_flair: Option<ReplayScoreboardFlair>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cosmetics: Option<ReplayCosmetics>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub view: Option<ReplayView>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scoreboard: Option<ReplayPlayerScoreboard>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayLoadout {
    pub weapon_def_indices: Vec<i32>,
    pub armor_value: u32,
    pub has_helmet: bool,
    pub has_defuser: bool,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayView {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crosshair_code: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub viewmodel: Option<ReplayViewmodel>,
}

impl ReplayView {
    pub fn is_empty(&self) -> bool {
        self.crosshair_code.is_none() && self.viewmodel.is_none()
    }
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayViewmodel {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub left_handed: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fov: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_x: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_y: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offset_z: Option<f32>,
}

impl ReplayViewmodel {
    pub fn is_empty(&self) -> bool {
        self.left_handed.is_none()
            && self.fov.is_none()
            && self.offset_x.is_none()
            && self.offset_y.is_none()
            && self.offset_z.is_none()
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, Ord, PartialEq, PartialOrd, Serialize)]
pub struct ReplayScoreboardFlair {
    pub item_def_index: u32,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayRoundScoreboard {
    pub t_score: u32,
    pub ct_score: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub t_team_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ct_team_name: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayPlayerScoreboard {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_user_id: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_entity_id: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub score: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kills: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deaths: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub assists: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mvps: Option<u32>,
}

impl ReplayPlayerScoreboard {
    pub fn is_empty(&self) -> bool {
        self.score.is_none()
            && self.player_user_id.is_none()
            && self.player_entity_id.is_none()
            && self.player_color.is_none()
            && self.kills.is_none()
            && self.deaths.is_none()
            && self.assists.is_none()
            && self.mvps.is_none()
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ReplayChatMessage {
    pub tick: i32,
    pub sender_steam_id: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sender_name: Option<String>,
    pub scope: String,
    pub text: String,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayCosmetics {
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub weapons: Vec<ReplayWeaponCosmetic>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub knife: Option<ReplayItemCosmetic>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub glove: Option<ReplayItemCosmetic>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent: Option<ReplayAgentCosmetic>,
}

impl ReplayCosmetics {
    pub fn is_empty(&self) -> bool {
        self.weapons.is_empty()
            && self.knife.is_none()
            && self.glove.is_none()
            && self.agent.is_none()
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ReplayWeaponCosmetic {
    pub weapon_def_index: i32,
    pub paint_kit: u32,
    pub seed: u32,
    pub wear: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quality: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stattrak_counter: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub original_owner_steam_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_account_id: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_name: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stickers: Vec<ReplayWeaponSticker>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub charms: Vec<ReplayWeaponCharm>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inspect: Option<ReplayCosmeticInspect>,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayWeaponSticker {
    pub slot: u8,
    pub sticker_id: u32,
    pub wear: f32,
    pub offset_x: f32,
    pub offset_y: f32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scale: Option<f32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rotation: Option<f32>,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ReplayWeaponCharm {
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

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ReplayItemCosmetic {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_def_index: Option<i32>,
    pub paint_kit: u32,
    pub seed: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seed_known: Option<bool>,
    pub wear: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inspect: Option<ReplayCosmeticInspect>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct ReplayCosmeticInspect {
    pub command: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub steam_url: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ReplayAgentCosmetic {
    pub item_def_index: u32,
    pub model_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
}
