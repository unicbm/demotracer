use crate::first_pass::frameparser::StartEndOffset;
use crate::first_pass::parser::FirstPassOutput;
use crate::first_pass::prop_controller::PropController;
use crate::first_pass::read_bits::DemoParserError;
use crate::first_pass::sendtables::Serializer;
use crate::first_pass::stringtables::StringTable;
use crate::first_pass::stringtables::UserInfo;
use crate::parse_demo::DecodePlan;
use crate::second_pass::collect_data::{make_dense_column_slots, make_dense_player_columns, make_shared_string_sources, ProjectileRecord, SharedStringSource};
use crate::second_pass::decoder::QfMapper;
use crate::second_pass::entities::Entity;
use crate::second_pass::entities::PlayerMetaData;
use crate::second_pass::game_events::GameEvent;
use crate::second_pass::other_netmessages::Class;
use crate::second_pass::parser::SecondPassOutput;
use crate::second_pass::path_ops::FieldPath;
use crate::second_pass::variants::{InventoryWeaponCosmetic, PropColumn, Sticker, Variant};
use crate::second_pass::sparse_scalar::SparseScalarColumns;
use ahash::AHashMap;
use ahash::AHashSet;
use ahash::HashMap;
use ahash::RandomState;
use csgoproto::csvc_msg_game_event_list::DescriptorT;
use csgoproto::{CSubtickMoveStep, CsgoInputHistoryEntryPb};
use csgoproto::CsvcMsgVoiceData;
use std::cell::RefCell;
use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::env;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;
const HUF_LOOKUPTABLE_MAXVALUE: u32 = (1 << 17) - 1;
const DEFAULT_MAX_ENTITY_ID: usize = 1024;

pub struct SecondPassParser<'a> {
    pub(crate) entity_projection: Option<super::entity_projection::EntityProjection>,
    pub(crate) profile: crate::profile::PassProfile,
    pub(crate) property_profile: Option<crate::profile::PropertyProfile>,
    pub cancelled: Option<&'a AtomicBool>,
    pub start_end_offset: Option<StartEndOffset>,
    pub qf_mapper: &'a QfMapper,
    pub prop_controller: &'a PropController,
    pub cls_by_id: &'a Vec<Class>,
    pub(crate) entity_field_cache: Vec<AHashMap<u64, super::entities::ResolvedEntityField<'a>>>,
    pub stringtable_players: BTreeMap<i32, UserInfo>,
    pub net_tick: u32,
    pub tick_interval: Option<f32>,
    pub parse_inventory: bool,
    pub paths: Vec<FieldPath>,
    pub ptr: usize,
    pub parse_all_packets: bool,
    pub ge_list: &'a AHashMap<i32, DescriptorT>,
    pub serializers: AHashMap<String, Serializer, RandomState>,
    pub cls_bits: Option<u32>,
    pub entities: Vec<Option<Entity>>,
    pub weapon_econ_snapshot_cache:
        RefCell<AHashMap<i32, ((u32, u64), Option<Arc<InventoryWeaponCosmetic>>)>>,
    pub weapon_sticker_cache: RefCell<AHashMap<i32, ((u32, u64), Arc<[Sticker]>)>>,
    pub player_inventory_snapshot_cache: RefCell<AHashMap<i32, PlayerInventorySnapshot>>,
    pub inventory_generation: u64,
    pub stable_agent_skin_cache: RefCell<AHashMap<(u64, u32), u32>>,
    pub glove_attribute_cache: RefCell<AHashMap<i32, ((u32, u64), [Option<Variant>; 3])>>,
    pub tick: i32,
    pub players: BTreeMap<i32, PlayerMetaData>,
    pub teams: Teams,
    pub huffman_lookup_table: &'a [(u8, u8)],
    pub game_events: Vec<GameEvent>,
    pub string_tables: Vec<StringTable>,
    pub rules_entity_id: Option<i32>,
    pub c4_entity_id: Option<i32>,
    pub game_events_counter: AHashSet<String>,
    pub uniq_prop_names: AHashSet<String>,
    pub baselines: AHashMap<u32, Vec<u8>, RandomState>,
    pub projectiles: BTreeSet<i32>,
    pub projectile_record_indices: AHashMap<i32, usize>,
    pub fullpackets_parsed: u32,
    pub wanted_players: AHashSet<u64>,
    pub wanted_ticks: AHashSet<i32>,
    // Output from parsing
    pub projectile_records: Vec<ProjectileRecord>,
    pub voice_data: Vec<(i32, CsvcMsgVoiceData)>,
    pub output: AHashMap<u32, PropColumn, RandomState>,
    pub(crate) dense_player_columns: Option<Vec<PropColumn>>,
    pub(crate) dense_column_slots: Vec<usize>,
    pub(crate) shared_string_sources: Vec<Option<SharedStringSource>>,
    pub(crate) sparse_scalar_columns: Option<SparseScalarColumns>,
    pub header: HashMap<String, String>,
    pub skins: Vec<EconItem>,
    pub item_drops: Vec<EconItem>,
    pub convars: AHashMap<String, String>,
    pub chat_messages: Vec<ChatMessageRecord>,
    pub player_end_data: Vec<PlayerEndMetaData>,
    // Settings
    pub wanted_events: Vec<String>,
    pub parse_entities: bool,
    pub parse_projectiles: bool,
    pub collect_projectile_records: bool,
    pub parse_grenades: bool,
    pub is_debug_mode: bool,
    pub df_per_player: AHashMap<u64, AHashMap<u32, PropColumn>>,
    pub order_by_steamid: bool,
    pub last_tick: i32,
    pub parse_usercmd: bool,
    pub(crate) usercmd_command_scratch: Vec<super::parser::usercmd_wire::CommandRange>,
    pub(crate) usercmd_delta_payload_scratch: super::parser::usercmd_delta::BorrowedPayloads,
    pub(crate) usercmd_full_history_scratch: Vec<CsgoInputHistoryEntryPb>,
    pub(crate) usercmd_players: AHashMap<i32, UserCmdPlayerState>,
    pub(crate) usercmd_history_scratch: Vec<(usize, CsgoInputHistoryEntryPb)>,
    pub(crate) usercmd_subtick_scratch: Vec<(usize, CSubtickMoveStep)>,
    pub(crate) empty_usercmd_subticks: Arc<[super::variants::UserCmdSubtickMove]>,
    pub list_props: bool,
    pub decode_plan: DecodePlan,
}

#[derive(Default)]
pub(crate) struct UserCmdPlayerState {
    pub history: Vec<CsgoInputHistoryEntryPb>,
    pub subticks: Vec<CSubtickMoveStep>,
    pub history_output: Option<Arc<[super::variants::InputHistory]>>,
}
#[derive(Debug, Clone)]
pub struct Teams {
    pub team1_entid: Option<i32>,
    pub team2_entid: Option<i32>,
    pub team3_entid: Option<i32>,
}
impl Teams {
    pub fn new() -> Self {
        Teams {
            team1_entid: None,
            team2_entid: None,
            team3_entid: None,
        }
    }
}

#[derive(Debug, Clone)]
pub struct ChatMessageRecord {
    pub entity_idx: Option<i32>,
    pub param1: Option<String>,
    pub param2: Option<String>,
    pub param3: Option<String>,
    pub param4: Option<String>,
}
#[derive(Debug, Clone)]
pub struct EconItem {
    pub account_id: Option<u32>,
    pub item_id: Option<u64>,
    pub def_index: Option<u32>,
    pub paint_index: Option<u32>,
    pub rarity: Option<u32>,
    pub quality: Option<u32>,
    pub paint_wear: Option<u32>,
    pub paint_seed: Option<u32>,
    pub quest_id: Option<u32>,
    pub dropreason: Option<u32>,
    pub custom_name: Option<String>,
    pub inventory: Option<u32>,
    pub ent_idx: Option<i32>,
    pub steamid: Option<u64>,
    pub item_name: Option<String>,
    pub skin_name: Option<String>,
}

#[derive(Debug, Clone)]
pub struct PlayerInventorySnapshot {
    pub generation: u64,
    pub player_signature: (u32, Option<u64>, Option<u32>),
    pub weapon_eids: Vec<i32>,
    pub weapon_signature: Vec<(i32, u32, u64)>,
    pub ids: Vec<u32>,
    pub cosmetics: Option<Arc<[InventoryWeaponCosmetic]>>,
}
#[derive(Debug, Clone)]
pub struct PlayerEndMetaData {
    pub steamid: Option<u64>,
    pub name: Option<String>,
    pub team_number: Option<i32>,
}

impl<'a> SecondPassParser<'a> {
    pub fn create_output(mut self) -> SecondPassOutput {
        if let Some(profile) = self.property_profile.take() {
            profile.report(&self.prop_controller.prop_infos);
        }
        if let Some(mut columns) = self.dense_player_columns.take() {
            if let Some(sparse) = self.sparse_scalar_columns.take() {
                sparse.finish(&mut columns);
            }
            for (prop, column) in self.prop_controller.prop_infos.iter().zip(columns) {
                // The legacy collector creates a column only after its first row.
                if column.len() != 0 {
                    self.output.insert(prop.id, column);
                }
            }
        }
        SecondPassOutput {
            voice_data: self.voice_data,
            chat_messages: self.chat_messages,
            convars: self.convars,
            df: self.output,
            game_events: self.game_events,
            skins: self.skins,
            item_drops: self.item_drops,
            header: None,
            player_md: self.player_end_data,
            roster: self
                .players
                .values()
                .map(|p| PlayerEndMetaData {
                    steamid: p.steamid,
                    name: p.name.clone(),
                    team_number: p.team_num.map(|t| t as i32),
                })
                .collect(),
            game_events_counter: self.game_events_counter,
            uniq_prop_names: self.uniq_prop_names,
            prop_info: PropController::new(vec![], vec![], AHashMap::default(), AHashMap::default(), false, &["none".to_string()], false),
            projectiles: self.projectile_records,
            ptr: self.ptr,
            df_per_player: self.df_per_player,
            entities: self.entities,
            last_tick: self.tick,
        }
    }
    pub fn new(
        first_pass_output: FirstPassOutput<'a>,
        offset: usize,
        parse_all_packets: bool,
        start_end_offset: Option<StartEndOffset>,
        decode_plan: DecodePlan,
    ) -> Result<Self, DemoParserError> {
        first_pass_output
            .settings
            .wanted_player_props
            .clone()
            .extend(vec!["tick".to_owned(), "steamid".to_owned(), "name".to_owned()]);
        let args: Vec<String> = env::args().collect();
        let debug = if args.len() > 2 { args[2] == "true" } else { false };
        let dense_player_columns = make_dense_player_columns(
            first_pass_output.prop_controller,
            first_pass_output.order_by_steamid,
            first_pass_output.settings.parse_projectiles,
        );
        let sparse_scalar_columns = dense_player_columns.as_ref().and_then(|_| {
            SparseScalarColumns::new(first_pass_output.prop_controller, first_pass_output.cls_by_id)
        });
        let dense_column_slots = make_dense_column_slots(
            first_pass_output.prop_controller.prop_infos.len(),
            |slot| sparse_scalar_columns.as_ref().is_some_and(|sparse| sparse.is_deferred(slot)),
        );
        let shared_string_sources = make_shared_string_sources(first_pass_output.prop_controller);
        let property_profile = dense_player_columns.as_ref().and_then(|_| {
            let count = first_pass_output.prop_controller.prop_infos.len();
            let deferred = sparse_scalar_columns.as_ref()
                .map_or(0, |sparse| (0..count).filter(|&slot| sparse.is_deferred(slot)).count());
            crate::profile::PropertyProfile::from_env(count, deferred)
        });

        Ok(SecondPassParser {
            entity_projection: (decode_plan.project_entity_state && !debug && !first_pass_output.list_props)
                .then(|| super::entity_projection::EntityProjection::new(first_pass_output.prop_controller,
                    !decode_plan.game_events && !decode_plan.voice_data && !decode_plan.item_drops && !decode_plan.end_of_match
                    && !first_pass_output.settings.parse_projectiles && !first_pass_output.settings.collect_projectile_records
                    && !first_pass_output.settings.parse_grenades)),
            profile: crate::profile::PassProfile::new(),
            property_profile,
            cancelled: first_pass_output.settings.cancelled,
            uniq_prop_names: AHashSet::default(),
            parse_usercmd: contains_usercmd_prop(&first_pass_output.settings.wanted_player_props),
            usercmd_command_scratch: Vec::new(),
            usercmd_delta_payload_scratch: Default::default(),
            usercmd_full_history_scratch: Vec::new(),
            usercmd_players: AHashMap::default(),
            usercmd_history_scratch: Vec::new(),
            usercmd_subtick_scratch: Vec::new(),
            empty_usercmd_subticks: Arc::default(),
            last_tick: 0,
            start_end_offset: start_end_offset,
            order_by_steamid: first_pass_output.order_by_steamid,
            df_per_player: AHashMap::default(),
            voice_data: vec![],
            paths: vec![
                FieldPath {
                    last: 0,
                    path: [0, 0, 0, 0, 0, 0, 0],
                };
                8192
            ],
            parse_inventory: first_pass_output.prop_controller.wanted_player_props.contains(&"inventory".to_string()),
            net_tick: 0,
            tick_interval: first_pass_output.header.get("server_tick_interval")
                .and_then(|value| value.parse::<f32>().ok())
                .filter(|value| value.is_finite() && *value > 0.0),
            c4_entity_id: None,
            stringtable_players: first_pass_output.stringtable_players,
            is_debug_mode: debug,
            projectile_records: vec![],
            parse_all_packets: parse_all_packets,
            wanted_players: first_pass_output.wanted_players.clone(),
            wanted_ticks: first_pass_output.wanted_ticks.clone(),
            prop_controller: &first_pass_output.prop_controller,
            qf_mapper: &first_pass_output.qfmap,
            fullpackets_parsed: 0,
            serializers: AHashMap::default(),
            ptr: offset,
            ge_list: first_pass_output.ge_list,
            cls_by_id: &first_pass_output.cls_by_id,
            entity_field_cache: (0..first_pass_output.cls_by_id.len()).map(|_| AHashMap::default()).collect(),
            entities: vec![None; DEFAULT_MAX_ENTITY_ID],
            weapon_econ_snapshot_cache: RefCell::new(AHashMap::default()),
            weapon_sticker_cache: RefCell::new(AHashMap::default()),
            player_inventory_snapshot_cache: RefCell::new(AHashMap::default()),
            inventory_generation: 0,
            stable_agent_skin_cache: RefCell::new(AHashMap::default()),
            glove_attribute_cache: RefCell::new(AHashMap::default()),
            cls_bits: None,
            tick: -99999,
            players: BTreeMap::default(),
            output: AHashMap::default(),
            dense_player_columns,
            dense_column_slots,
            shared_string_sources,
            sparse_scalar_columns,
            game_events: vec![],
            wanted_events: first_pass_output.settings.wanted_events.clone(),
            parse_entities: first_pass_output.settings.parse_ents,
            projectiles: BTreeSet::default(),
            projectile_record_indices: AHashMap::default(),
            baselines: first_pass_output.baselines.clone(),
            string_tables: first_pass_output.string_tables.clone(),
            teams: Teams::new(),
            game_events_counter: AHashSet::default(),
            parse_projectiles: first_pass_output.settings.parse_projectiles,
            collect_projectile_records: first_pass_output.settings.collect_projectile_records,
            parse_grenades: first_pass_output.settings.parse_grenades,
            rules_entity_id: None,
            convars: AHashMap::default(),
            chat_messages: vec![],
            item_drops: vec![],
            skins: vec![],
            player_end_data: vec![],
            huffman_lookup_table: &first_pass_output.settings.huffman_lookup_table,
            header: HashMap::default(),
            list_props: first_pass_output.list_props,
            decode_plan,
        })
    }
}

#[derive(Debug, Clone)]
pub struct SpecialIDs {
    pub teamnum: Option<u32>,
    pub player_name: Option<u32>,
    pub steamid: Option<u32>,
    pub player_pawn: Option<u32>,

    pub player_team_pointer: Option<u32>,
    pub weapon_owner_pointer: Option<u32>,
    pub team_team_num: Option<u32>,

    pub cell_x_player: Option<u32>,
    pub cell_y_player: Option<u32>,
    pub cell_z_player: Option<u32>,

    pub cell_x_offset_player: Option<u32>,
    pub cell_y_offset_player: Option<u32>,
    pub cell_z_offset_player: Option<u32>,
    pub active_weapon: Option<u32>,
    pub item_def: Option<u32>,
    pub item_id_high: Option<u32>,
    pub item_id_low: Option<u32>,
    pub item_account_id: Option<u32>,
    pub entity_quality: Option<u32>,
    pub fallback_stattrak: Option<u32>,

    pub m_vec_x_grenade: Option<u32>,
    pub m_vec_y_grenade: Option<u32>,
    pub m_vec_z_grenade: Option<u32>,

    pub m_cell_x_grenade: Option<u32>,
    pub m_cell_y_grenade: Option<u32>,
    pub m_cell_z_grenade: Option<u32>,

    pub grenade_owner_id: Option<u32>,
    pub buttons: Option<u32>,
    pub eye_angles: Option<u32>,

    pub orig_own_low: Option<u32>,
    pub orig_own_high: Option<u32>,
    pub life_state: Option<u32>,

    pub h_owner_entity: Option<u32>,
    pub agent_skin_idx: Option<u32>,
    pub total_rounds_played: Option<u32>,

    pub round_win_reason: Option<u32>,
    pub round_start_count: Option<u32>,
    pub round_end_count: Option<u32>,
    pub match_end_count: Option<u32>,

    pub is_incendiary_grenade: Option<u32>,
    pub sellback_entry_def_idx: Option<u32>,
    pub sellback_entry_n_cost: Option<u32>,
    pub sellback_entry_prev_armor: Option<u32>,
    pub sellback_entry_prev_helmet: Option<u32>,
    pub sellback_entry_h_item: Option<u32>,

    pub weapon_purchase_count: Option<u32>,
    pub in_buy_zone: Option<u32>,
    pub custom_name: Option<u32>,

    pub is_airborn: Option<u32>,
    pub grenade_initial_position: Option<u32>,
    pub initial_velocity: Option<u32>,
    pub grenade_smoke_detonation_position: Option<u32>,
    pub grenade_bounces: Option<u32>,
}
impl SpecialIDs {
    /// Exhaustive destructuring makes a new dependency require updating this list.
    pub(crate) fn iter(&self) -> impl Iterator<Item = u32> {
        let Self {
            teamnum, player_name, steamid,
            player_pawn, player_team_pointer, weapon_owner_pointer,
            team_team_num, cell_x_player, cell_y_player,
            cell_z_player, cell_x_offset_player, cell_y_offset_player,
            cell_z_offset_player, active_weapon, item_def,
            item_id_high, item_id_low, item_account_id,
            entity_quality, fallback_stattrak, m_vec_x_grenade,
            m_vec_y_grenade, m_vec_z_grenade, m_cell_x_grenade,
            m_cell_y_grenade, m_cell_z_grenade, grenade_owner_id,
            buttons, eye_angles, orig_own_low,
            orig_own_high, life_state, h_owner_entity,
            agent_skin_idx, total_rounds_played, round_win_reason,
            round_start_count, round_end_count, match_end_count,
            is_incendiary_grenade, sellback_entry_def_idx, sellback_entry_n_cost,
            sellback_entry_prev_armor, sellback_entry_prev_helmet, sellback_entry_h_item,
            weapon_purchase_count, in_buy_zone, custom_name,
            is_airborn, grenade_initial_position, initial_velocity,
            grenade_smoke_detonation_position, grenade_bounces,
        } = self;
        [
            *teamnum, *player_name, *steamid,
            *player_pawn, *player_team_pointer, *weapon_owner_pointer,
            *team_team_num, *cell_x_player, *cell_y_player,
            *cell_z_player, *cell_x_offset_player, *cell_y_offset_player,
            *cell_z_offset_player, *active_weapon, *item_def,
            *item_id_high, *item_id_low, *item_account_id,
            *entity_quality, *fallback_stattrak, *m_vec_x_grenade,
            *m_vec_y_grenade, *m_vec_z_grenade, *m_cell_x_grenade,
            *m_cell_y_grenade, *m_cell_z_grenade, *grenade_owner_id,
            *buttons, *eye_angles, *orig_own_low,
            *orig_own_high, *life_state, *h_owner_entity,
            *agent_skin_idx, *total_rounds_played, *round_win_reason,
            *round_start_count, *round_end_count, *match_end_count,
            *is_incendiary_grenade, *sellback_entry_def_idx, *sellback_entry_n_cost,
            *sellback_entry_prev_armor, *sellback_entry_prev_helmet, *sellback_entry_h_item,
            *weapon_purchase_count, *in_buy_zone, *custom_name,
            *is_airborn, *grenade_initial_position, *initial_velocity,
            *grenade_smoke_detonation_position, *grenade_bounces,
        ].into_iter().flatten()
    }

    pub fn new() -> Self {
        SpecialIDs {
            round_start_count: None,
            round_end_count: None,
            match_end_count: None,
            round_win_reason: None,
            total_rounds_played: None,
            h_owner_entity: None,
            teamnum: None,
            player_name: None,
            steamid: None,
            player_pawn: None,
            player_team_pointer: None,
            weapon_owner_pointer: None,
            team_team_num: None,
            cell_x_player: None,
            cell_y_player: None,
            cell_z_player: None,
            cell_x_offset_player: None,
            cell_y_offset_player: None,
            cell_z_offset_player: None,
            active_weapon: None,
            item_def: None,
            item_id_high: None,
            item_id_low: None,
            item_account_id: None,
            entity_quality: None,
            fallback_stattrak: None,
            m_cell_x_grenade: None,
            m_cell_y_grenade: None,
            m_cell_z_grenade: None,
            m_vec_x_grenade: None,
            m_vec_y_grenade: None,
            m_vec_z_grenade: None,
            grenade_owner_id: None,
            buttons: None,
            eye_angles: None,
            orig_own_high: None,
            orig_own_low: None,
            life_state: None,
            agent_skin_idx: None,
            is_incendiary_grenade: None,
            sellback_entry_def_idx: None,
            sellback_entry_h_item: None,
            sellback_entry_n_cost: None,
            sellback_entry_prev_armor: None,
            sellback_entry_prev_helmet: None,
            weapon_purchase_count: None,
            in_buy_zone: None,
            custom_name: None,
            is_airborn: None,
            grenade_initial_position: None,
            initial_velocity: None,
            grenade_smoke_detonation_position: None,
            grenade_bounces: None,
        }
    }
}

pub fn create_huffman_lookup_table() -> Vec<(u8, u8)> {
    let buf = include_bytes!("huf.b");
    let mut huf2 = Vec::with_capacity(HUF_LOOKUPTABLE_MAXVALUE as usize);
    for chunk in buf.chunks_exact(2) {
        huf2.push((chunk[0], chunk[1]));
    }
    huf2.push((0, 0));
    return huf2;
}

fn contains_usercmd_prop(names: &[String]) -> bool {
    names.iter().any(|name| name.contains("usercmd"))
}
