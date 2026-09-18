use crate::demo_network_handle::demo_network_ehandle_index;
use crate::first_pass::parser::Frame;
use crate::first_pass::parser::HEADER_ENDS_AT_BYTE;
use crate::first_pass::parser_settings::FirstPassParser;
use crate::first_pass::prop_controller::PropController;
use crate::first_pass::prop_controller::*;
use crate::first_pass::read_bits::read_varint;
use crate::first_pass::read_bits::Bitreader;
use crate::first_pass::read_bits::DemoParserError;
use crate::first_pass::stringtables::parse_userinfo;
use crate::maps::demo_cmd_type_from_int;
use crate::second_pass::collect_data::ProjectileRecord;
use crate::second_pass::entities::Entity;
use crate::second_pass::game_events::GameEvent;
use crate::second_pass::parser_settings::SecondPassParser;
use crate::second_pass::parser_settings::*;
use crate::second_pass::variants::PropColumn;
use crate::second_pass::variants::Variant;
use ahash::AHashMap;
use ahash::AHashSet;
use csgoproto::message_type::NetMessageType::{self, *};
use csgoproto::CDemoFullPacket;
use csgoproto::CDemoPacket;
use csgoproto::CInButtonStatePb;
use csgoproto::CMsgQAngle;
#[cfg(test)]
use csgoproto::CSubtickMoveStep;
use csgoproto::CnetMsgTick;
use csgoproto::CsgoInputHistoryEntryPb;
use csgoproto::CsgoUserCmdPb;
use csgoproto::CsvcMsgServerInfo;
#[cfg(test)]
use csgoproto::CsvcMsgUserCommands;
use csgoproto::CsvcMsgVoiceData;
use csgoproto::EDemoCommands::*;
use prost::Message;
use snap::raw::decompress_len;
use snap::raw::Decoder as SnapDecoder;
use std::sync::atomic::Ordering;

use super::variants::{into_shared_slice, InputHistory, InputHistoryInterpolation, UserCmdSubtickMove};

#[path = "usercmd_delta.rs"]
pub(crate) mod usercmd_delta;

#[path = "usercmd_wire.rs"]
pub(crate) mod usercmd_wire;

const OUTER_BUF_DEFAULT_LEN: usize = 400_000;
const INNER_BUF_DEFAULT_LEN: usize = 8192 * 15;

#[inline]
fn commit_usercmd_scalar(
    entity: &mut Entity,
    sparse: Option<&mut super::sparse_scalar::SparseScalarColumns>,
    property_id: u32,
    value: Variant,
) {
    use std::collections::hash_map::Entry;
    let slot = if property_id == USERCMD_CLIENT_TICK { Some(22) }
        else { property_id.checked_sub(USERCMD_VIEWANGLE_X).filter(|slot| *slot < 22).map(|slot| slot as usize) };
    let bits = match &value {
        Variant::Bool(value) => Some(u64::from(*value)),
        Variant::I32(value) => Some((1_u64 << 32) | u64::from(*value as u32)),
        Variant::U32(value) => Some((2_u64 << 32) | u64::from(*value)),
        Variant::F32(value) => Some((3_u64 << 32) | u64::from(value.to_bits())),
        _ => None,
    };
    if let (Some(slot), Some(bits)) = (slot, bits) {
        let cache = entity.usercmd_scalar_cache.get_or_insert_with(|| Box::new([u64::MAX; 23]));
        if cache[slot] == bits { return; }
        cache[slot] = bits;
    } else if let Some(slot) = slot {
        if let Some(cache) = entity.usercmd_scalar_cache.as_mut() { cache[slot] = u64::MAX; }
    }
    match entity.props.entry(property_id) {
        Entry::Occupied(mut entry) => {
            let unchanged = match (entry.get(), &value) {
                (Variant::Bool(a), Variant::Bool(b)) => a == b,
                (Variant::I32(a), Variant::I32(b)) => a == b,
                (Variant::U32(a), Variant::U32(b)) => a == b,
                (Variant::F32(a), Variant::F32(b)) => a.to_bits() == b.to_bits(),
                _ => false,
            };
            if unchanged { return; }
            if let Some(sparse) = sparse { sparse.record(entity.entity_id, property_id, &value); }
            entry.insert(value);
        }
        Entry::Vacant(entry) => {
            if let Some(sparse) = sparse { sparse.record(entity.entity_id, property_id, &value); }
            entry.insert(value);
        }
    }
}

// Both full and delta writers invoke this only where they actually commit a scalar.
macro_rules! commit_usercmd_scalar {
    ($parser:ident, $entity:ident, $property:expr, $value:expr) => {{
        commit_usercmd_scalar($entity, $parser.sparse_scalar_columns.as_mut(), $property, $value);
    }};
}

#[inline]
fn packet_message_is_needed(
    msg_type: &NetMessageType,
    decode_plan: crate::parse_demo::DecodePlan,
    parse_usercmd: bool,
    should_parse_entities: bool,
) -> bool {
    match msg_type {
        svc_PacketEntities => should_parse_entities,
        svc_UserCmds => parse_usercmd,
        svc_CreateStringTable | svc_UpdateStringTable | svc_ServerInfo
        | CS_UM_PlayerStatsUpdate | net_Tick | svc_ClearAllStringTables => true,
        CS_UM_SendPlayerItemDrops => decode_plan.item_drops,
        CS_UM_EndOfMatchAllPlayersData => decode_plan.end_of_match,
        svc_VoiceData => decode_plan.voice_data,
        UM_SayText2 | UM_SayText | net_SetConVar | CS_UM_ServerRankUpdate
        | GE_Source1LegacyGameEvent | GE_FireBulletsId | GE_PlayerBulletHitId => decode_plan.game_events,
        _ => false,
    }
}

// July 2026 CS2 demos carry server user commands in CMsgServerUserCmd.delta_data.
// The outer command and its singular children still use protobuf wire fields, while
// codegen-delta repeated children use a custom payload that prost cannot decode as
// their declared message type. Treat those repeated children as opaque bytes so the
// command-level input fields remain readable.
#[derive(Clone, PartialEq, Message)]
struct DeltaBaseUserCmdPb {
    #[prost(int32, optional, tag = "1")]
    legacy_command_number: Option<i32>,
    #[prost(int32, optional, tag = "2")]
    client_tick: Option<i32>,
    #[prost(uint32, optional, tag = "17")]
    prediction_offset_ticks_x256: Option<u32>,
    #[prost(message, optional, tag = "3")]
    buttons_pb: Option<CInButtonStatePb>,
    #[prost(message, optional, tag = "4")]
    viewangles: Option<CMsgQAngle>,
    #[prost(float, optional, tag = "5")]
    forwardmove: Option<f32>,
    #[prost(float, optional, tag = "6")]
    leftmove: Option<f32>,
    #[prost(float, optional, tag = "7")]
    upmove: Option<f32>,
    #[prost(int32, optional, tag = "8")]
    impulse: Option<i32>,
    #[prost(int32, optional, tag = "9")]
    weaponselect: Option<i32>,
    #[prost(int32, optional, tag = "10")]
    random_seed: Option<i32>,
    #[prost(int32, optional, tag = "11")]
    mousedx: Option<i32>,
    #[prost(int32, optional, tag = "12")]
    mousedy: Option<i32>,
    #[prost(uint32, optional, tag = "14")]
    pawn_entity_handle: Option<u32>,
    #[prost(bytes = "bytes", repeated, tag = "18")]
    subtick_moves_delta: Vec<prost::bytes::Bytes>,
    #[prost(bytes = "bytes", optional, tag = "19")]
    move_crc: Option<prost::bytes::Bytes>,
    #[prost(uint32, optional, tag = "20")]
    consumed_server_angle_changes: Option<u32>,
    #[prost(int32, optional, tag = "21")]
    cmd_flags: Option<i32>,
    #[prost(bytes = "bytes", optional, tag = "22")]
    execution_notes: Option<prost::bytes::Bytes>,
}

#[derive(Clone, PartialEq, Message)]
struct DeltaCsgoUserCmdPb {
    #[prost(message, optional, tag = "1")]
    base: Option<DeltaBaseUserCmdPb>,
    #[prost(bytes = "bytes", repeated, tag = "2")]
    input_history_delta: Vec<prost::bytes::Bytes>,
    #[prost(int32, optional, tag = "6")]
    attack1_start_history_index: Option<i32>,
    #[prost(int32, optional, tag = "7")]
    attack2_start_history_index: Option<i32>,
    #[prost(bool, optional, tag = "9")]
    left_hand_desired: Option<bool>,
    #[prost(bool, optional, tag = "11")]
    is_predicting_body_shot_fx: Option<bool>,
    #[prost(bool, optional, tag = "12")]
    is_predicting_head_shot_fx: Option<bool>,
    #[prost(bool, optional, tag = "13")]
    is_predicting_kill_ragdolls: Option<bool>,
}

fn read_delta_varint(bytes: &mut &[u8]) -> Option<u64> {
    let mut value = 0_u64;
    for shift in (0..70).step_by(7) {
        let (&byte, rest) = bytes.split_first()?;
        *bytes = rest;
        value |= u64::from(byte & 0x7F) << shift;
        if byte & 0x80 == 0 {
            return Some(value);
        }
    }
    None
}

#[cfg(test)]
fn write_delta_varint(mut value: u64, out: &mut Vec<u8>) {
    while value >= 0x80 {
        out.push((value as u8 & 0x7F) | 0x80);
        value >>= 7;
    }
    out.push(value as u8);
}

#[derive(Clone, Copy)]
enum DeltaMessageSchema {
    CsgoUserCmd,
    BaseUserCmd,
    Buttons,
    QAngle,
    InputHistory,
    Interpolation,
    InterpolationCl,
    Vector,
    SubtickMove,
}

impl DeltaMessageSchema {
    fn field_wire_type(self, field: u64) -> Option<u8> {
        match self {
            Self::CsgoUserCmd => match field {
                1 | 2 => Some(2),
                6 | 7 | 9 | 11 | 12 | 13 => Some(0),
                _ => None,
            },
            Self::BaseUserCmd => match field {
                1 | 2 | 8 | 9 | 10 | 11 | 12 | 14 | 17 | 20 | 21 => Some(0),
                3 | 4 | 18 | 19 | 22 => Some(2),
                5 | 6 | 7 => Some(5),
                _ => None,
            },
            Self::Buttons => match field {
                1..=3 => Some(0),
                _ => None,
            },
            Self::QAngle => match field {
                1..=3 => Some(5),
                _ => None,
            },
            Self::InputHistory => match field {
                2 | 12..=15 | 66..=69 => Some(2),
                4 | 6 | 64 | 65 => Some(0),
                5 | 7 => Some(5),
                _ => None,
            },
            Self::Interpolation => match field {
                1 | 2 => Some(0),
                3 => Some(5),
                _ => None,
            },
            Self::InterpolationCl => match field {
                3 => Some(5),
                _ => None,
            },
            Self::Vector => match field {
                1..=3 => Some(5),
                _ => None,
            },
            Self::SubtickMove => match field {
                1 | 2 => Some(0),
                3 | 4 | 5 | 8 | 9 => Some(5),
                _ => None,
            },
        }
    }

    fn child(self, field: u64) -> Option<Self> {
        match (self, field) {
            (Self::CsgoUserCmd, 1) => Some(Self::BaseUserCmd),
            (Self::BaseUserCmd, 3) => Some(Self::Buttons),
            (Self::BaseUserCmd, 4) => Some(Self::QAngle),
            (Self::InputHistory, 2 | 69) => Some(Self::QAngle),
            (Self::InputHistory, 12) => Some(Self::InterpolationCl),
            (Self::InputHistory, 13..=15) => Some(Self::Interpolation),
            (Self::InputHistory, 66..=68) => Some(Self::Vector),
            _ => None,
        }
    }

    fn default_fields(self) -> &'static [(u64, u8)] {
        match self {
            Self::Buttons => &[(1, 0), (2, 0), (3, 0)],
            Self::QAngle => &[(1, 5), (2, 5), (3, 5)],
            Self::BaseUserCmd => &[(3, 2), (4, 2), (5, 5), (6, 5), (7, 5), (8, 0), (9, 0), (11, 0), (12, 0), (20, 0)],
            Self::CsgoUserCmd => &[(1, 2), (9, 0)],
            Self::InputHistory => &[(2, 2), (4, 0), (5, 5), (6, 0), (7, 5)],
            Self::Interpolation => &[(1, 0), (2, 0), (3, 5)],
            Self::InterpolationCl => &[(3, 5)],
            Self::Vector => &[(1, 5), (2, 5), (3, 5)],
            Self::SubtickMove => &[(1, 0), (2, 0), (3, 5), (4, 5), (5, 5), (8, 5), (9, 5)],
        }
    }

    #[cfg(test)]
    fn explicit_defaults(self) -> Vec<u8> {
        let mut out = Vec::new();
        for (field, wire_type) in self.default_fields() {
            write_delta_varint((field << 3) | u64::from(*wire_type), &mut out);
            match wire_type {
                0 => out.push(0),
                2 => {
                    let nested = self.child(*field).map(Self::explicit_defaults).unwrap_or_default();
                    write_delta_varint(nested.len() as u64, &mut out);
                    out.extend_from_slice(&nested);
                }
                5 => out.extend_from_slice(&[0; 4]),
                _ => unreachable!(),
            }
        }
        out
    }
}

fn shared_input_history(history: &[CsgoInputHistoryEntryPb]) -> std::sync::Arc<[InputHistory]> {
    let mut output = std::sync::Arc::<[InputHistory]>::new_uninit_slice(history.len());
    let target = std::sync::Arc::get_mut(&mut output).unwrap();
    for (slot, entry) in target.iter_mut().zip(history) {
        slot.write(parse_input_history(*entry));
    }
    // SAFETY: the allocation has exactly history.len() elements, and the loop
    // initializes every element before publishing the immutable slice.
    unsafe { output.assume_init() }
}

fn parse_input_history(input: CsgoInputHistoryEntryPb) -> InputHistory {
    InputHistory {
        view_angles: input.view_angles.map(|value| [value.x(), value.y(), value.z()]),
        render_tick_count: input.render_tick_count,
        render_tick_fraction: input.render_tick_fraction,
        player_tick_count: input.player_tick_count,
        player_tick_fraction: input.player_tick_fraction,
        cl_interp_fraction: input.cl_interp.and_then(|value| value.frac),
        sv_interp0: input.sv_interp0.map(|value| InputHistoryInterpolation {
            src_tick: value.src_tick,
            dst_tick: value.dst_tick,
            fraction: value.frac,
        }),
        sv_interp1: input.sv_interp1.map(|value| InputHistoryInterpolation {
            src_tick: value.src_tick,
            dst_tick: value.dst_tick,
            fraction: value.frac,
        }),
        player_interp: input.player_interp.map(|value| InputHistoryInterpolation {
            src_tick: value.src_tick,
            dst_tick: value.dst_tick,
            fraction: value.frac,
        }),
        frame_number: input.frame_number,
        target_ent_index: input.target_ent_index,
        shoot_position: input.shoot_position.map(|value| [value.x(), value.y(), value.z()]),
        target_head_pos_check: input
            .target_head_pos_check
            .map(|value| [value.x(), value.y(), value.z()]),
        target_abs_pos_check: input
            .target_abs_pos_check
            .map(|value| [value.x(), value.y(), value.z()]),
        target_abs_ang_check: input
            .target_abs_ang_check
            .map(|value| [value.x(), value.y(), value.z()]),
    }
}

#[cfg(test)]
fn sanitize_codegen_delta_message(mut bytes: &[u8], schema: DeltaMessageSchema) -> Option<Vec<u8>> {
    let mut out = Vec::with_capacity(bytes.len());
    while !bytes.is_empty() {
        let key = read_delta_varint(&mut bytes)?;
        let field = key >> 3;
        let wire_type = (key & 0x07) as u8;
        if field == 0 {
            return None;
        }

        if wire_type == 7 {
            let normal_wire_type = schema.field_wire_type(field)?;
            write_delta_varint((field << 3) | u64::from(normal_wire_type), &mut out);
            match normal_wire_type {
                0 => out.push(0),
                1 => out.extend_from_slice(&[0; 8]),
                2 => {
                    let nested = schema.child(field).map(DeltaMessageSchema::explicit_defaults).unwrap_or_default();
                    write_delta_varint(nested.len() as u64, &mut out);
                    out.extend_from_slice(&nested);
                }
                5 => out.extend_from_slice(&[0; 4]),
                _ => return None,
            }
            continue;
        }

        write_delta_varint(key, &mut out);
        match wire_type {
            0 => {
                let value = read_delta_varint(&mut bytes)?;
                write_delta_varint(value, &mut out);
            }
            1 => {
                let (value, rest) = bytes.split_at_checked(8)?;
                out.extend_from_slice(value);
                bytes = rest;
            }
            2 => {
                let length = usize::try_from(read_delta_varint(&mut bytes)?).ok()?;
                let (value, rest) = bytes.split_at_checked(length)?;
                let value = if let Some(child) = schema.child(field) {
                    sanitize_codegen_delta_message(value, child)?
                } else {
                    value.to_vec()
                };
                write_delta_varint(value.len() as u64, &mut out);
                out.extend_from_slice(&value);
                bytes = rest;
            }
            5 => {
                let (value, rest) = bytes.split_at_checked(4)?;
                out.extend_from_slice(value);
                bytes = rest;
            }
            _ => return None,
        }
    }
    Some(out)
}

#[cfg(test)]
struct DecodedRepeatedDelta<M> {
    state: Vec<M>,
    updates: Vec<M>,
}

/// Reconstruct a stateful codegen-delta repeated field while returning only
/// the elements updated by this command. The leading wire-type 7 key declares
/// the target list length in its field-number bits. Following length-delimited
/// fields use their field number as a zero-based element index and may be sparse.
#[cfg(test)]
fn decode_codegen_delta_repeated<M>(
    payloads: &[prost::bytes::Bytes],
    schema: DeltaMessageSchema,
    previous: &[M],
) -> Option<DecodedRepeatedDelta<M>>
where
    M: Message + Default + Clone,
{
    let mut messages = previous.to_vec();
    let mut updated_indices = Vec::new();
    let mut declared_count = None;
    for payload in payloads {
        let mut bytes = payload.as_ref();
        if !bytes.is_empty() {
            let mut after_marker = bytes;
            let marker = read_delta_varint(&mut after_marker)?;
            if marker & 0x07 == 7 {
                if declared_count.is_some() {
                    return None;
                }
                let count = usize::try_from(marker >> 3).ok()?;
                messages.resize(count, M::default());
                declared_count = Some(count);
                bytes = after_marker;
            }
        }
        while !bytes.is_empty() {
            let key = read_delta_varint(&mut bytes)?;
            if key & 0x07 != 2 {
                return None;
            }
            let index = usize::try_from(key >> 3).ok()?;
            let length = usize::try_from(read_delta_varint(&mut bytes)?).ok()?;
            if bytes.len() < length {
                return None;
            }
            let (message, rest) = bytes.split_at(length);
            let message = sanitize_codegen_delta_message(message, schema)?;
            messages.get_mut(index)?.merge(message.as_slice()).ok()?;
            if !updated_indices.contains(&index) {
                updated_indices.push(index);
            }
            bytes = rest;
        }
    }
    let updates = updated_indices
        .into_iter()
        .map(|index| messages[index].clone())
        .collect();
    Some(DecodedRepeatedDelta {
        state: messages,
        updates,
    })
}

#[derive(Debug)]
pub struct SecondPassOutput {
    pub df: AHashMap<u32, PropColumn>,
    pub game_events: Vec<GameEvent>,
    pub skins: Vec<EconItem>,
    pub item_drops: Vec<EconItem>,
    pub chat_messages: Vec<ChatMessageRecord>,
    pub convars: AHashMap<String, String>,
    pub header: Option<AHashMap<String, String>>,
    pub player_md: Vec<PlayerEndMetaData>,
    /// Live player roster from CCSPlayerController entities (final per-player state).
    /// Populated even when CCSUsrMsg_EndOfMatchAllPlayersData is absent (community/casual
    /// demos), where `player_md` ends up empty. Use as a fallback when `player_md` is empty.
    pub roster: Vec<PlayerEndMetaData>,
    pub game_events_counter: AHashSet<String>,
    pub uniq_prop_names: AHashSet<String>,
    pub prop_info: PropController,
    pub projectiles: Vec<ProjectileRecord>,
    pub ptr: usize,
    pub voice_data: Vec<(i32, CsvcMsgVoiceData)>,
    pub df_per_player: AHashMap<u64, AHashMap<u32, PropColumn>>,
    pub entities: Vec<Option<Entity>>,
    pub last_tick: i32,
}
impl<'a> SecondPassParser<'a> {
    pub fn start(&mut self, demo_bytes: &'a [u8]) -> Result<(), DemoParserError> {
        let started_at = self.ptr;
        let profile_started = self.profile.start();
        // re-use these to avoid allocation
        let mut buf = vec![0_u8; INNER_BUF_DEFAULT_LEN];
        let mut buf2 = vec![0_u8; OUTER_BUF_DEFAULT_LEN];

        loop {
            if self.cancelled.is_some_and(|flag| flag.load(Ordering::Relaxed)) {
                return Err(DemoParserError::Cancelled);
            }
            // Need at least a few bytes to read frame header (3 varints, minimum 1 byte each)
            if self.ptr + 3 > demo_bytes.len() {
                break;
            }
            let frame = match self.read_frame(demo_bytes) {
                Ok(f) => f,
                Err(DemoParserError::OutOfBytesError) => break,
                Err(e) => return Err(e),
            };
            let skipped = frame.demo_cmd == DemAnimationData || frame.demo_cmd == DemSendTables || frame.demo_cmd == DemStringTables;
            self.profile.frame(skipped);
            if skipped {
                self.ptr += frame.size as usize;
                continue;
            }
            let bytes = match self.slice_packet_bytes(demo_bytes, frame.size) {
                Ok(b) => b,
                Err(_) => {
                    self.ptr += frame.size;
                    continue;
                }
            };
            let bytes = self.decompress_if_needed(&mut buf, bytes, &frame)?;
            self.ptr += frame.size;

            let ok = match frame.demo_cmd {
                DemSignonPacket => self.parse_packet(&bytes, &mut buf2),
                DemPacket => self.parse_packet(&bytes, &mut buf2),
                DemStop => break,
                DemUserCmd => Ok(()),
                DemFullPacket => {
                    if self.parse_full_packet_and_break_if_needed(&bytes, &mut buf2, started_at)? {
                        break;
                    }
                    Ok(())
                }
                _ => Ok(()),
            };
            ok?;
        }
        self.profile.report("second", started_at, profile_started);
        Ok(())
    }
    fn parse_full_packet_and_break_if_needed(&mut self, bytes: &[u8], buf: &mut Vec<u8>, started_at: usize) -> Result<bool, DemoParserError> {
        if let Some(start_end_offset) = self.start_end_offset {
            if self.ptr > start_end_offset.end {
                return Ok(true);
            } else {
                self.parse_full_packet(&bytes, true, buf)?;
                return Ok(false);
            }
        }
        match self.parse_all_packets {
            true => {
                self.parse_full_packet(&bytes, false, buf)?;
            }
            false => {
                if self.fullpackets_parsed == 0 && started_at != HEADER_ENDS_AT_BYTE {
                    self.parse_full_packet(&bytes, true, buf)?;
                    self.fullpackets_parsed += 1;
                } else {
                    return Ok(true);
                }
            }
        }
        return Ok(false);
    }
    fn read_frame(&mut self, demo_bytes: &[u8]) -> Result<Frame, DemoParserError> {
        let frame_starts_at = self.ptr;
        let cmd = read_varint(demo_bytes, &mut self.ptr)?;
        let tick = read_varint(demo_bytes, &mut self.ptr)?;
        let size = read_varint(demo_bytes, &mut self.ptr)?;
        self.tick = tick as i32;

        let msg_type = cmd & !64;
        let is_compressed = (cmd & 64) == 64;
        let demo_cmd = demo_cmd_type_from_int(msg_type as i32)?;

        Ok(Frame {
            size: size as usize,
            frame_starts_at,
            is_compressed,
            demo_cmd,
            tick: self.tick,
        })
    }
    fn slice_packet_bytes(&mut self, demo_bytes: &'a [u8], frame_size: usize) -> Result<&'a [u8], DemoParserError> {
        if self.ptr + frame_size as usize >= demo_bytes.len() {
            return Err(DemoParserError::MalformedMessage);
        }
        Ok(&demo_bytes[self.ptr..self.ptr + frame_size])
    }
    fn decompress_if_needed<'b>(&mut self, buf: &'b mut Vec<u8>, possibly_uncompressed_bytes: &'b [u8], frame: &Frame) -> Result<&'b [u8], DemoParserError> {
        match frame.is_compressed {
            true => {
                let profile_started = self.profile.start();
                FirstPassParser::resize_if_needed(buf, decompress_len(possibly_uncompressed_bytes))?;
                match SnapDecoder::new().decompress(possibly_uncompressed_bytes, buf) {
                    Ok(idx) => {
                        self.profile.decompressed(profile_started, possibly_uncompressed_bytes.len(), idx);
                        Ok(&buf[..idx])
                    }
                    Err(e) => return Err(DemoParserError::DecompressionFailure(format!("{}", e))),
                }
            }
            false => Ok(possibly_uncompressed_bytes),
        }
    }
    pub fn resize_if_needed(buf: &mut Vec<u8>, needed_len: Result<usize, snap::Error>) -> Result<(), DemoParserError> {
        match needed_len {
            Ok(len) => {
                if buf.len() < len {
                    buf.resize(len, 0)
                }
            }
            Err(e) => return Err(DemoParserError::DecompressionFailure(e.to_string())),
        };
        Ok(())
    }

    pub fn parse_packet(&mut self, bytes: &[u8], buf: &mut Vec<u8>) -> Result<(), DemoParserError> {
        let started = self.profile.start();
        let msg = match CDemoPacket::decode(bytes) {
            Err(_) => return Err(DemoParserError::MalformedMessage),
            Ok(msg) => msg,
        };
        self.profile.phase(0, started);
        let mut bitreader = Bitreader::new(msg.data());
        self.parse_packet_from_bitreader(&mut bitreader, buf, true, false)?;
        Ok(())
    }

    pub fn parse_packet_from_bitreader(
        &mut self,
        bitreader: &mut Bitreader,
        buf: &mut Vec<u8>,
        should_parse_entities: bool,
        is_fullpacket: bool,
    ) -> Result<(), DemoParserError> {
        let mut wrong_order_events = vec![];

        while bitreader.bits_remaining().unwrap_or(0) > 8 {
            let msg_type = NetMessageType::from(bitreader.read_u_bit_var()? as i32);
            let size = bitreader.read_varint()?;
            // Ignored payloads can be large (sounds, cosmetics, voice). Preserve
            // their exact bit extent without resizing or filling the scratch buffer.
            if !packet_message_is_needed(&msg_type, self.decode_plan, self.parse_usercmd, should_parse_entities) {
                bitreader.skip_n_bytes(size as usize)?;
                continue;
            }
            if buf.len() < size as usize {
                buf.resize(size as usize, 0)
            }
            let started = self.profile.start();
            bitreader.read_n_bytes_mut(size as usize, buf)?;
            self.profile.phase(1, started);
            let msg_bytes = &buf[..size as usize];
            let phase = match msg_type { svc_PacketEntities => None, svc_UserCmds => Some(2), _ => Some(3) };
            let started = phase.and_then(|_| self.profile.start());
            let ok = match msg_type {
                svc_PacketEntities => {
                    if should_parse_entities {
                        let profile_started = self.profile.start();
                        self.parse_packet_ents(&msg_bytes, is_fullpacket)?;
                        self.profile.parsed_entities(profile_started);
                        if !is_fullpacket {
                            let profile_started = self.profile.start();
                            self.collect_entities();
                            self.profile.collected_entities(profile_started);
                        }
                    }
                    Ok(())
                }
                svc_CreateStringTable => self.parse_create_stringtable(msg_bytes),
                svc_UpdateStringTable => self.update_string_table(msg_bytes),
                svc_ServerInfo => self.parse_server_info(msg_bytes),
                CS_UM_SendPlayerItemDrops if self.decode_plan.item_drops => {
                    self.parse_item_drops(msg_bytes)
                }
                CS_UM_EndOfMatchAllPlayersData if self.decode_plan.end_of_match => {
                    self.parse_player_end_msg(msg_bytes)
                }
                UM_SayText2 if self.decode_plan.game_events => {
                    self.create_custom_event_chat_message(msg_bytes)
                }
                UM_SayText if self.decode_plan.game_events => {
                    self.create_custom_event_server_message(msg_bytes)
                }
                net_SetConVar if self.decode_plan.game_events => {
                    self.create_custom_event_parse_convars(msg_bytes)
                }
                CS_UM_PlayerStatsUpdate => self.parse_player_stats_update(msg_bytes),
                CS_UM_ServerRankUpdate if self.decode_plan.game_events => {
                    self.create_custom_event_rank_update(msg_bytes)
                }
                net_Tick => self.parse_net_tick(msg_bytes),
                svc_ClearAllStringTables => self.clear_stringtables(),
                svc_VoiceData if self.decode_plan.voice_data => self.parse_voice_data(msg_bytes),
                GE_Source1LegacyGameEvent if self.decode_plan.game_events => {
                    self.parse_game_event(msg_bytes, &mut wrong_order_events)
                }
                svc_UserCmds => self.parse_user_cmd(msg_bytes),
                GE_FireBulletsId if self.decode_plan.game_events => {
                    self.create_custom_event_fire_bullets(msg_bytes)
                }
                GE_PlayerBulletHitId if self.decode_plan.game_events => {
                    self.create_custom_event_player_bullet_hit(msg_bytes)
                }
                _ => Ok(()),
            };
            if let Some(phase) = phase { self.profile.phase(phase, started); }
            ok?
        }
        if !wrong_order_events.is_empty() {
            self.resolve_wrong_order_event(&mut wrong_order_events)?;
        }
        Ok(())
    }
    pub fn parse_user_cmd(&mut self, bytes: &[u8]) -> Result<(), DemoParserError> {
        // We simply inject the values into the entities as if they came from packet_ents like any other val.

        // This method is quite expensive so early exit it if not needed.
        if !self.parse_usercmd {
            return Ok(());
        }

        if usercmd_wire::decode_commands(bytes, &mut self.usercmd_command_scratch).is_err() {
            return Ok(());
        }
        for index in 0..self.usercmd_command_scratch.len() {
            let cmd = self.usercmd_command_scratch[index];
            let player_slot = cmd.player_slot;
            if cmd.delta.1 != 0 {
                let delta_data = &bytes[cmd.delta.0..cmd.delta.0 + cmd.delta.1];
                let decode_started = self.profile.start();
                let mut payloads = std::mem::take(&mut self.usercmd_delta_payload_scratch);
                let decoded = usercmd_delta::decode_borrowed_command(delta_data, &mut payloads);
                self.profile.phase(4, decode_started);
                if let Some(user_cmd) = decoded {
                    let apply_started = self.profile.start();
                    self.apply_delta_user_cmd_payloads(user_cmd, player_slot,
                        payloads.history.iter().map(|&(start, len)| &delta_data[start..start + len]),
                        payloads.subticks.iter().map(|&(start, len)| &delta_data[start..start + len]));
                    self.profile.phase(7, apply_started);
                }
                self.usercmd_delta_payload_scratch = payloads;
                continue;
            }
            let decode_started = self.profile.start();
            self.usercmd_full_history_scratch.clear();
            let mut user_cmd = CsgoUserCmdPb {
                input_history: std::mem::take(&mut self.usercmd_full_history_scratch),
                ..Default::default()
            };
            if user_cmd.merge(&bytes[cmd.data.0..cmd.data.0 + cmd.data.1]).is_err() {
                self.usercmd_full_history_scratch = user_cmd.input_history;
                return Ok(());
            }
            self.profile.phase(5, decode_started);
            let apply_started = self.profile.start();
            let left_hand_desired = user_cmd.left_hand_desired();
            let attack1_start_history_index =
                user_cmd.attack1_start_history_index.unwrap_or(-1);
            let attack2_start_history_index =
                user_cmd.attack2_start_history_index.unwrap_or(-1);
            let UserCmdPlayerState { history: input_history_baseline, subticks: subtick_baseline, history_output } =
                self.usercmd_players.entry(player_slot).or_default();
            self.usercmd_full_history_scratch = std::mem::replace(input_history_baseline, user_cmd.input_history);
            *history_output = None;
            *subtick_baseline = user_cmd.base.as_mut().map(|base| std::mem::take(&mut base.subtick_moves)).unwrap_or_default();
            if let Some(base) = user_cmd.base {
                let entity_id = demo_network_ehandle_index(base.pawn_entity_handle());
                if let Some(Some(ent)) = self.entities.get_mut(entity_id as usize) {
                    let history = history_output.get_or_insert_with(|| shared_input_history(input_history_baseline)).clone();
                    ent.props.insert(USERCMD_INPUT_HISTORY_BASEID, Variant::InputHistory(history));
                    commit_usercmd_scalar!(self, ent,
                        USERCMD_ATTACK_START_HISTORY_INDEX_1,
                        Variant::I32(attack1_start_history_index)
                    );
                    commit_usercmd_scalar!(self, ent,
                        USERCMD_ATTACK_START_HISTORY_INDEX_2,
                        Variant::I32(attack2_start_history_index)
                    );
                    if let Some(client_tick) = base.client_tick {
                        commit_usercmd_scalar!(self, ent, USERCMD_CLIENT_TICK, Variant::I32(client_tick));
                    }
                    let mut subtick_moves = vec![];
                    for subtick in subtick_baseline.iter() {
                        subtick_moves.push(UserCmdSubtickMove {
                            when: subtick.when(),
                            button: subtick.button(),
                            pressed: subtick.pressed(),
                            analog_forward: subtick.analog_forward_delta(),
                            analog_left: subtick.analog_left_delta(),
                            pitch_delta: subtick.pitch_delta(),
                            yaw_delta: subtick.yaw_delta(),
                        });
                    }
                    let subtick_moves = if subtick_moves.is_empty() { self.empty_usercmd_subticks.clone() } else { into_shared_slice(subtick_moves) };
                    ent.props.insert(USERCMD_SUBTICK_MOVES_BASEID, Variant::UserCmdSubtickMoves(subtick_moves));
                    commit_usercmd_scalar!(self, ent, USERCMD_LEFTMOVE, Variant::F32(base.leftmove()));
                    commit_usercmd_scalar!(self, ent, USERCMD_FORWARDMOVE, Variant::F32(base.forwardmove()));
                    commit_usercmd_scalar!(self, ent, USERCMD_UPMOVE, Variant::F32(base.upmove()));
                    commit_usercmd_scalar!(self, ent, USERCMD_IMPULSE, Variant::I32(base.impulse()));
                    commit_usercmd_scalar!(self, ent, USERCMD_MOUSE_DX, Variant::I32(base.mousedx()));
                    commit_usercmd_scalar!(self, ent, USERCMD_MOUSE_DY, Variant::I32(base.mousedy()));
                    commit_usercmd_scalar!(self, ent, USERCMD_WEAPON_SELECT, Variant::I32(base.weaponselect()));
                    commit_usercmd_scalar!(self, ent, USERCMD_SUBTICK_LEFT_HAND_DESIRED, Variant::Bool(left_hand_desired));
                    if let Some(viewangles) = base.viewangles {
                        commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_X, Variant::F32(viewangles.x()));
                        commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_Y, Variant::F32(viewangles.y()));
                        commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_Z, Variant::F32(viewangles.z()));
                    }
                    if let Some(buttons_pb) = base.buttons_pb {
                        ent.props.insert(USERCMD_BUTTONSTATE_1, Variant::U64(buttons_pb.buttonstate1()));
                        ent.props.insert(USERCMD_BUTTONSTATE_2, Variant::U64(buttons_pb.buttonstate2()));
                        ent.props.insert(USERCMD_BUTTONSTATE_3, Variant::U64(buttons_pb.buttonstate3()));
                    }
                    commit_usercmd_scalar!(self, ent,
                        USERCMD_CONSUMED_SERVER_ANGLE_CHANGES, Variant::U32(base.consumed_server_angle_changes()));
                }
            }
            self.profile.phase(6, apply_started);
        }
        Ok(())
    }

    #[cfg(test)]
    fn apply_delta_user_cmd(&mut self, mut user_cmd: DeltaCsgoUserCmdPb, player_slot: i32) {
        let history = std::mem::take(&mut user_cmd.input_history_delta);
        let subticks = user_cmd.base.as_mut().map(|base| std::mem::take(&mut base.subtick_moves_delta)).unwrap_or_default();
        self.apply_delta_user_cmd_payloads(user_cmd, player_slot,
            history.iter().map(AsRef::as_ref), subticks.iter().map(AsRef::as_ref));
    }

    fn apply_delta_user_cmd_payloads<'b>(
        &mut self, user_cmd: DeltaCsgoUserCmdPb, player_slot: i32,
        history_payloads: impl IntoIterator<Item = &'b [u8]>,
        subtick_payloads: impl IntoIterator<Item = &'b [u8]>,
    ) {
        let UserCmdPlayerState { history: input_history_baseline, subticks: subtick_baseline, history_output } =
            self.usercmd_players.entry(player_slot).or_default();
        // Invalid repeated deltas retain the preceding baseline, as before.
        if usercmd_delta::apply_repeated_iter(history_payloads, input_history_baseline,
            &mut self.usercmd_history_scratch, |_, _| {}).unwrap_or(false) {
            *history_output = None;
        }
        let input_history = history_output.get_or_insert_with(|| shared_input_history(input_history_baseline)).clone();
        let left_hand_desired = user_cmd.left_hand_desired;
        let attack1_start_history_index = user_cmd.attack1_start_history_index.unwrap_or(-1);
        let attack2_start_history_index = user_cmd.attack2_start_history_index.unwrap_or(-1);
        let Some(base) = user_cmd.base else {
            return;
        };
        let mut subtick_moves = Vec::new();
        let _ = usercmd_delta::apply_repeated_iter(subtick_payloads, subtick_baseline,
            &mut self.usercmd_subtick_scratch, |_, subtick| subtick_moves.push(UserCmdSubtickMove {
                when: subtick.when(),
                button: subtick.button(),
                pressed: subtick.pressed(),
                analog_forward: subtick.analog_forward_delta(),
                analog_left: subtick.analog_left_delta(),
                pitch_delta: subtick.pitch_delta(),
                yaw_delta: subtick.yaw_delta(),
            }));
        let subtick_moves = if subtick_moves.is_empty() { self.empty_usercmd_subticks.clone() } else { into_shared_slice(subtick_moves) };

        let explicit_pawn = base
            .pawn_entity_handle
            .filter(|handle| *handle != 0x00FF_FFFF)
            .map(demo_network_ehandle_index);
        let controller_entid = player_slot.checked_add(1);
        let entity_id = explicit_pawn.or_else(|| controller_entid.and_then(|controller| {
            self.prop_controller
                .special_ids
                .player_pawn
                .and_then(|id| match self.get_prop_from_ent(&id, &controller) {
                    Ok(Variant::U32(handle)) => Some(demo_network_ehandle_index(handle)),
                    _ => None,
                })
        })).or_else(|| controller_entid.and_then(|controller| {
            self.players
                .values()
                .find(|player| player.controller_entid == Some(controller))
                .and_then(|player| player.player_entity_id)
        }));
        let Some(entity_id) = entity_id else {
            return;
        };
        let Some(Some(ent)) = self.entities.get_mut(entity_id as usize) else {
            return;
        };

        ent.props.insert(USERCMD_INPUT_HISTORY_BASEID, Variant::InputHistory(input_history));
        ent.props.insert(USERCMD_SUBTICK_MOVES_BASEID, Variant::UserCmdSubtickMoves(subtick_moves));
        commit_usercmd_scalar!(self, ent,
            USERCMD_ATTACK_START_HISTORY_INDEX_1,
            Variant::I32(attack1_start_history_index)
        );
        commit_usercmd_scalar!(self, ent,
            USERCMD_ATTACK_START_HISTORY_INDEX_2,
            Variant::I32(attack2_start_history_index)
        );
        if let Some(client_tick) = base.client_tick {
            commit_usercmd_scalar!(self, ent, USERCMD_CLIENT_TICK, Variant::I32(client_tick));
        }
        if let Some(value) = left_hand_desired {
            commit_usercmd_scalar!(self, ent, USERCMD_SUBTICK_LEFT_HAND_DESIRED, Variant::Bool(value));
        }
        if let Some(value) = base.leftmove {
            commit_usercmd_scalar!(self, ent, USERCMD_LEFTMOVE, Variant::F32(value));
        }
        if let Some(value) = base.forwardmove {
            commit_usercmd_scalar!(self, ent, USERCMD_FORWARDMOVE, Variant::F32(value));
        }
        if let Some(value) = base.upmove {
            commit_usercmd_scalar!(self, ent, USERCMD_UPMOVE, Variant::F32(value));
        }
        if let Some(value) = base.impulse {
            commit_usercmd_scalar!(self, ent, USERCMD_IMPULSE, Variant::I32(value));
        }
        if let Some(value) = base.mousedx {
            commit_usercmd_scalar!(self, ent, USERCMD_MOUSE_DX, Variant::I32(value));
        }
        if let Some(value) = base.mousedy {
            commit_usercmd_scalar!(self, ent, USERCMD_MOUSE_DY, Variant::I32(value));
        }
        if let Some(value) = base.weaponselect {
            commit_usercmd_scalar!(self, ent, USERCMD_WEAPON_SELECT, Variant::I32(value));
        }
        if let Some(value) = base.consumed_server_angle_changes {
            commit_usercmd_scalar!(self, ent, USERCMD_CONSUMED_SERVER_ANGLE_CHANGES, Variant::U32(value));
        }
        if let Some(viewangles) = base.viewangles {
            if let Some(value) = viewangles.x {
                commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_X, Variant::F32(value));
            }
            if let Some(value) = viewangles.y {
                commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_Y, Variant::F32(value));
            }
            if let Some(value) = viewangles.z {
                commit_usercmd_scalar!(self, ent, USERCMD_VIEWANGLE_Z, Variant::F32(value));
            }
        }
        if let Some(buttons) = base.buttons_pb {
            if let Some(value) = buttons.buttonstate1 {
                ent.props.insert(USERCMD_BUTTONSTATE_1, Variant::U64(value));
            }
            if let Some(value) = buttons.buttonstate2 {
                ent.props.insert(USERCMD_BUTTONSTATE_2, Variant::U64(value));
            }
            if let Some(value) = buttons.buttonstate3 {
                ent.props.insert(USERCMD_BUTTONSTATE_3, Variant::U64(value));
            }
        }
    }

    pub fn parse_voice_data(&mut self, bytes: &[u8]) -> Result<(), DemoParserError> {
        if let Ok(m) = CsvcMsgVoiceData::decode(bytes) {
            self.voice_data.push((self.tick, m));
        }
        Ok(())
    }
    pub fn parse_game_event(&mut self, bytes: &[u8], wrong_order_events: &mut Vec<GameEvent>) -> Result<(), DemoParserError> {
        match self.parse_event(bytes) {
            Ok(Some(event)) => {
                wrong_order_events.push(event);
                Ok(())
            }
            Ok(None) => Ok(()),
            Err(e) => return Err(e),
        }
    }

    pub fn parse_net_tick(&mut self, bytes: &[u8]) -> Result<(), DemoParserError> {
        let message = match CnetMsgTick::decode(bytes) {
            Ok(message) => message,
            Err(_) => return Err(DemoParserError::MalformedMessage),
        };
        self.net_tick = message.tick();
        Ok(())
    }

    pub fn parse_full_packet(&mut self, bytes: &[u8], should_parse_entities: bool, buf: &mut Vec<u8>) -> Result<(), DemoParserError> {
        self.string_tables = vec![];
        let full_packet = match CDemoFullPacket::decode(bytes) {
            Err(_e) => return Err(DemoParserError::MalformedMessage),
            Ok(p) => p,
        };
        self.parse_full_packet_stringtables(&full_packet);
        if let Some(packet) = full_packet.packet {
            let mut bitreader = Bitreader::new(packet.data());
            self.parse_packet_from_bitreader(&mut bitreader, buf, should_parse_entities, true)
        } else {
            Ok(())
        }
    }

    pub fn parse_full_packet_stringtables(&mut self, full_packet: &CDemoFullPacket) {
        if let Some(string_table) = &full_packet.string_table {
            for item in &string_table.tables {
                if item.table_name == Some("instancebaseline".to_string()) {
                    for i in &item.items {
                        let k = i.str().parse::<u32>().unwrap_or(u32::MAX);
                        self.baselines.insert(k, i.data().to_vec());
                    }
                }
                if item.table_name == Some("userinfo".to_string()) {
                    for i in &item.items {
                        if let Ok(player) = parse_userinfo(&i.data()) {
                            if player.steamid != 0 {
                                self.stringtable_players.insert(player.userid, player);
                            }
                        }
                    }
                }
            }
        }
    }
    fn clear_stringtables(&mut self) -> Result<(), DemoParserError> {
        self.string_tables = vec![];
        Ok(())
    }
    pub fn parse_server_info(&mut self, bytes: &[u8]) -> Result<(), DemoParserError> {
        let server_info = match CsvcMsgServerInfo::decode(bytes) {
            Err(_e) => return Err(DemoParserError::MalformedMessage),
            Ok(p) => p,
        };
        let class_count = server_info.max_classes();
        self.cls_bits = Some((class_count as f32 + 1.).log2().ceil() as u32);
        if let Some(interval) = server_info.tick_interval.filter(|v| v.is_finite() && *v > 0.0) {
            self.tick_interval = Some(interval);
        }
        Ok(())
    }
    pub fn parse_user_command_cmd(&mut self, _data: &[u8]) -> Result<(), DemoParserError> {
        // Only in pov demos. Maybe implement sometime. Includes buttons etc.
        Ok(())
    }
}

#[cfg(test)]
mod sparse_usercmd_tests {
    use super::*;

    #[test]
    fn scalar_cache_preserves_bits_types_and_network_overwrites() {
        let mut entity = Entity {
            cls_id: 0, entity_id: 1, serial: 1, props: AHashMap::default(),
            entity_type: EntityType::Normal, cosmetic_revision: 0, usercmd_scalar_cache: None,
        };
        for bits in [0_u32, 0x80000000, 0x7fc00001, 0x7fc00002, 0x7fc00002] {
            commit_usercmd_scalar(&mut entity, None, USERCMD_VIEWANGLE_X, Variant::F32(f32::from_bits(bits)));
            let Variant::F32(value) = entity.props[&USERCMD_VIEWANGLE_X] else { panic!("wrong scalar type") };
            assert_eq!(value.to_bits(), bits);
        }
        SecondPassParser::insert_field(&mut entity, Variant::F32(19.0), Some(crate::first_pass::sendtables::FieldInfo {
            decoder: super::super::decoder::Decoder::NoscaleDecoder,
            should_parse: true, prop_id: USERCMD_VIEWANGLE_X,
        }), false);
        commit_usercmd_scalar(&mut entity, None, USERCMD_VIEWANGLE_X, Variant::F32(f32::from_bits(0x7fc00002)));
        let Variant::F32(value) = entity.props[&USERCMD_VIEWANGLE_X] else { panic!("wrong scalar type") };
        assert_eq!(value.to_bits(), 0x7fc00002);
        commit_usercmd_scalar(&mut entity, None, USERCMD_VIEWANGLE_X, Variant::U64(8));
        commit_usercmd_scalar(&mut entity, None, USERCMD_VIEWANGLE_X, Variant::F32(f32::from_bits(0x7fc00002)));
        assert!(matches!(entity.props[&USERCMD_VIEWANGLE_X], Variant::F32(_)));
    }
    use crate::first_pass::parser_settings::ParserInputs;
    use crate::first_pass::prop_controller::PropInfo;
    use crate::parse_demo::DecodePlan;
    use crate::second_pass::collect_data::PropType;
    use crate::second_pass::entities::EntityType;
    use crate::second_pass::sparse_scalar::SparseScalarColumns;
    use crate::second_pass::variants::VarVec;
    use std::sync::Arc;

    fn sample(parser: &mut SecondPassParser<'_>, ids: &[u32], oracle: &mut [PropColumn]) {
        for (&id, column) in ids.iter().zip(oracle) {
            column.push(parser.entities[1].as_ref().unwrap().props.get(&id).cloned());
        }
        parser.sparse_scalar_columns.as_mut().unwrap().record_row(1, true);
    }

    #[test]
    fn full_and_delta_scalar_writers_match_live_entity_state_with_missing_fields() {
        let ids = [USERCMD_VIEWANGLE_X, USERCMD_VIEWANGLE_Y, USERCMD_VIEWANGLE_Z,
            USERCMD_FORWARDMOVE, USERCMD_LEFTMOVE, USERCMD_UPMOVE, USERCMD_IMPULSE,
            USERCMD_MOUSE_DX, USERCMD_MOUSE_DY, USERCMD_WEAPON_SELECT, USERCMD_CLIENT_TICK,
            USERCMD_ATTACK_START_HISTORY_INDEX_1, USERCMD_ATTACK_START_HISTORY_INDEX_2,
            USERCMD_SUBTICK_LEFT_HAND_DESIRED, USERCMD_CONSUMED_SERVER_ANGLE_CHANGES];
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
        first.prop_controller.prop_infos = ids.iter().map(|&id| PropInfo {
            id, prop_type: PropType::Player, prop_name: format!("p{id}"),
            prop_friendly_name: format!("p{id}"), is_player_prop: true,
        }).collect();
        let mut parser = SecondPassParser::new(first.create_first_pass_output().unwrap(), 0,
            true, None, DecodePlan::FULL).unwrap();
        parser.sparse_scalar_columns = SparseScalarColumns::from_schema(&first.prop_controller, &[]);
        assert_eq!(parser.sparse_scalar_columns.as_ref().unwrap().column_count(), ids.len());
        parser.entities[1] = Some(Entity {
            cls_id: 0, entity_id: 1, serial: 0, props: Default::default(),
            entity_type: EntityType::Normal, cosmetic_revision: 0, usercmd_scalar_cache: None,
        });
        parser.parse_usercmd = true;
        let mut oracle: Vec<_> = ids.iter().map(|_| PropColumn::new()).collect();
        sample(&mut parser, &ids, &mut oracle);
        parser.apply_delta_user_cmd(DeltaCsgoUserCmdPb {
            attack1_start_history_index: Some(2), left_hand_desired: Some(true),
            base: Some(DeltaBaseUserCmdPb {
                pawn_entity_handle: Some(1), client_tick: Some(10), forwardmove: Some(0.5),
                viewangles: Some(CMsgQAngle { x: Some(90.0), ..Default::default() }),
                ..Default::default()
            }), ..Default::default()
        }, 0);
        sample(&mut parser, &ids, &mut oracle);
        // Missing delta scalars retain the previous entity value; attack
        // history indices are intentionally written as -1 by the real writer.
        parser.apply_delta_user_cmd(DeltaCsgoUserCmdPb {
            base: Some(DeltaBaseUserCmdPb { pawn_entity_handle: Some(1), ..Default::default() }),
            ..Default::default()
        }, 0);
        sample(&mut parser, &ids, &mut oracle);
        let full = CsgoUserCmdPb {
            base: Some(csgoproto::CBaseUserCmdPb {
                pawn_entity_handle: Some(1), viewangles: Some(CMsgQAngle::default()),
                ..Default::default()
            }), ..Default::default()
        };
        let message = CsvcMsgUserCommands {
            commands: vec![csgoproto::CMsgServerUserCmd {
                data: Some(full.encode_to_vec().into()), player_slot: Some(0), ..Default::default()
            }],
        };
        parser.parse_user_cmd(&message.encode_to_vec()).unwrap();
        sample(&mut parser, &ids, &mut oracle);
        parser.apply_delta_user_cmd(DeltaCsgoUserCmdPb {
            left_hand_desired: Some(true),
            base: Some(DeltaBaseUserCmdPb {
                pawn_entity_handle: Some(1), forwardmove: Some(-0.0), client_tick: Some(0),
                ..Default::default()
            }), ..Default::default()
        }, 0);
        sample(&mut parser, &ids, &mut oracle);
        let mut actual: Vec<_> = ids.iter().map(|_| PropColumn::new()).collect();
        parser.sparse_scalar_columns.take().unwrap().finish(&mut actual);
        for (actual, expected) in actual.iter().zip(&oracle) {
            assert_eq!(actual.num_nones, expected.num_nones);
            match (&actual.data, &expected.data) {
                (Some(VarVec::F32(actual)), Some(VarVec::F32(expected))) => {
                    assert_eq!(actual.iter().map(|v| v.map(f32::to_bits)).collect::<Vec<_>>(),
                        expected.iter().map(|v| v.map(f32::to_bits)).collect::<Vec<_>>());
                }
                _ => assert_eq!(actual, expected),
            }
        }
    }
}

#[cfg(test)]
mod packet_dispatch_tests {
    use super::*;
    use crate::parse_demo::DecodePlan;

    #[test]
    fn player_row_plan_keeps_stateful_metadata_and_only_requested_payloads() {
        for message in [
            svc_CreateStringTable, svc_UpdateStringTable, svc_ServerInfo,
            CS_UM_PlayerStatsUpdate, net_Tick, svc_ClearAllStringTables,
            svc_PacketEntities, svc_UserCmds,
        ] {
            assert!(packet_message_is_needed(&message, DecodePlan::PLAYER_ROWS_ONLY, true, true));
        }
        for message in [
            CS_UM_SendPlayerItemDrops, CS_UM_EndOfMatchAllPlayersData,
            svc_VoiceData, UM_SayText2, UM_SayText, net_SetConVar,
            CS_UM_ServerRankUpdate, GE_Source1LegacyGameEvent,
            GE_FireBulletsId, GE_PlayerBulletHitId,
        ] {
            assert!(!packet_message_is_needed(&message, DecodePlan::PLAYER_ROWS_ONLY, true, true));
            assert!(packet_message_is_needed(&message, DecodePlan::FULL, true, true));
        }
        assert!(!packet_message_is_needed(&svc_PacketEntities, DecodePlan::FULL, true, false));
        assert!(!packet_message_is_needed(&svc_UserCmds, DecodePlan::FULL, false, true));
        for message in [Unknown, svc_Sounds, net_NOP] {
            assert!(!packet_message_is_needed(&message, DecodePlan::FULL, true, true));
        }
    }
}

#[cfg(test)]
mod delta_usercmd_tests {
    use super::*;

    #[test]
    fn decodes_july_usercmd_fields_around_codegen_delta_subticks() {
        let bytes = [
            0x0A, 0x40, 0x10, 0xA5, 0x54, 0x1A, 0x06, 0x08, 0x90, 0x08, 0x10, 0x80, 0x08, 0x22, 0x0A, 0x0D, 0x87, 0x85, 0x29, 0x40, 0x15, 0x36, 0x07, 0xC7,
            0x42, 0x35, 0x00, 0x00, 0x80, 0xBF, 0x50, 0xF8, 0xFB, 0xA7, 0xF7, 0x07, 0x58, 0x51, 0x60, 0x06, 0x92, 0x01, 0x17, 0x0F, 0x02, 0x14, 0x08, 0x80,
            0x08, 0x10, 0x01, 0x1D, 0x00, 0x00, 0xD8, 0x3E, 0x45, 0x3C, 0x4E, 0x11, 0xBF, 0x4D, 0xF0, 0x6A, 0xD5, 0x40,
        ];

        let sanitized = sanitize_codegen_delta_message(bytes.as_slice(), DeltaMessageSchema::CsgoUserCmd).unwrap();
        let command = DeltaCsgoUserCmdPb::decode(sanitized.as_slice()).unwrap();
        let base = command.base.unwrap();
        let buttons = base.buttons_pb.unwrap();
        assert_eq!(buttons.buttonstate1, Some(0x410));
        assert_eq!(buttons.buttonstate2, Some(0x400));
        assert_eq!(base.leftmove, Some(-1.0));

        let subticks = decode_codegen_delta_repeated::<CSubtickMoveStep>(
            &base.subtick_moves_delta,
            DeltaMessageSchema::SubtickMove,
            &[],
        )
        .unwrap()
        .updates;
        assert_eq!(subticks.len(), 1);
        assert_eq!(subticks[0].button(), 0x400);
        assert!(subticks[0].pressed());
        assert!((subticks[0].when() - 0.421875).abs() < f32::EPSILON);
    }

    #[test]
    fn repeated_delta_marker_declares_more_than_one_subtick() {
        let payload = prost::bytes::Bytes::from_static(&[
            0x17, 0x02, 0x09, 0x08, 0x01, 0x10, 0x01, 0x1d, 0x00, 0x00, 0xb0, 0x3e, 0x0a,
            0x0a, 0x08, 0x80, 0x10, 0x10, 0x01, 0x1d, 0x00, 0x00, 0xb0, 0x3e,
        ]);
        let decoded = decode_codegen_delta_repeated::<CSubtickMoveStep>(
            &[payload],
            DeltaMessageSchema::SubtickMove,
            &[],
        )
        .unwrap();
        assert_eq!(decoded.state.len(), 2);
        assert_eq!(decoded.updates.len(), 2);
        assert_eq!(decoded.state[0].button, Some(1));
        assert_eq!(decoded.state[1].button, Some(2048));
    }

    #[test]
    fn repeated_delta_allows_sparse_later_indices_and_keeps_baseline_fields() {
        let previous = vec![
            CSubtickMoveStep::default(),
            CSubtickMoveStep {
                button: Some(2),
                pressed: Some(true),
                ..CSubtickMoveStep::default()
            },
        ];
        let payload = prost::bytes::Bytes::from_static(&[0x1f, 0x12, 0x02, 0x08, 0x04]);
        let decoded = decode_codegen_delta_repeated::<CSubtickMoveStep>(
            &[payload],
            DeltaMessageSchema::SubtickMove,
            &previous,
        )
        .unwrap();
        assert_eq!(decoded.state.len(), 3);
        assert_eq!(decoded.updates.len(), 1);
        assert_eq!(decoded.state[1].button, Some(2));
        assert_eq!(decoded.state[1].pressed, Some(true));
        assert_eq!(decoded.state[2].button, Some(4));
        assert_eq!(decoded.updates[0].button, Some(4));
    }

    #[test]
    fn repeated_delta_merges_partial_updates_into_existing_elements() {
        let previous = vec![CSubtickMoveStep {
            button: Some(2),
            pressed: Some(true),
            when: Some(0.25),
            ..CSubtickMoveStep::default()
        }];
        let payload = prost::bytes::Bytes::from_static(&[0x0f, 0x02, 0x02, 0x08, 0x04]);
        let decoded = decode_codegen_delta_repeated::<CSubtickMoveStep>(
            &[payload],
            DeltaMessageSchema::SubtickMove,
            &previous,
        )
        .unwrap();
        assert_eq!(decoded.state[0].button, Some(4));
        assert_eq!(decoded.state[0].pressed, Some(true));
        assert_eq!(decoded.state[0].when, Some(0.25));
        assert_eq!(decoded.updates, decoded.state);
    }

    #[test]
    fn expands_codegen_delta_clear_markers_to_explicit_zero_values() {
        let bytes = [
            0x0A, 0x12, 0x10, 0xA6, 0x54, 0x1A, 0x01, 0x17, 0x50, 0xED, 0xF1, 0xC9, 0xDD, 0x03, 0x97, 0x01, 0xA8, 0x01, 0x80, 0x01,
        ];
        let sanitized = sanitize_codegen_delta_message(bytes.as_slice(), DeltaMessageSchema::CsgoUserCmd).unwrap();
        let command = DeltaCsgoUserCmdPb::decode(sanitized.as_slice()).unwrap();
        let buttons = command.base.unwrap().buttons_pb.unwrap();
        assert_eq!(buttons.buttonstate2, Some(0));
    }
}
