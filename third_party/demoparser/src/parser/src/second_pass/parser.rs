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
use csgoproto::CSubtickMoveStep;
use csgoproto::CnetMsgTick;
use csgoproto::CsgoInputHistoryEntryPb;
use csgoproto::CsgoUserCmdPb;
use csgoproto::CsvcMsgServerInfo;
use csgoproto::CsvcMsgUserCommands;
use csgoproto::CsvcMsgVoiceData;
use csgoproto::EDemoCommands::*;
use prost::Message;
use snap::raw::decompress_len;
use snap::raw::Decoder as SnapDecoder;
use std::sync::atomic::Ordering;

use super::variants::{InputHistory, InputHistoryInterpolation, UserCmdSubtickMove};

const OUTER_BUF_DEFAULT_LEN: usize = 400_000;
const INNER_BUF_DEFAULT_LEN: usize = 8192 * 15;

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

    fn explicit_defaults(self) -> Vec<u8> {
        let mut out = Vec::new();
        let fields: &[(u64, u8)] = match self {
            Self::Buttons => &[(1, 0), (2, 0), (3, 0)],
            Self::QAngle => &[(1, 5), (2, 5), (3, 5)],
            Self::BaseUserCmd => &[(3, 2), (4, 2), (5, 5), (6, 5), (7, 5), (8, 0), (9, 0), (11, 0), (12, 0), (20, 0)],
            Self::CsgoUserCmd => &[(1, 2), (9, 0)],
            Self::InputHistory => &[(2, 2), (4, 0), (5, 5), (6, 0), (7, 5)],
            Self::Interpolation => &[(1, 0), (2, 0), (3, 5)],
            Self::InterpolationCl => &[(3, 5)],
            Self::Vector => &[(1, 5), (2, 5), (3, 5)],
            Self::SubtickMove => &[(1, 0), (2, 0), (3, 5), (4, 5), (5, 5), (8, 5), (9, 5)],
        };
        for (field, wire_type) in fields {
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

fn decode_codegen_delta_repeated<M>(payloads: &[prost::bytes::Bytes], schema: DeltaMessageSchema) -> Option<Vec<M>>
where
    M: Message + Default,
{
    let mut messages = Vec::new();
    for payload in payloads {
        let mut bytes = payload.as_ref();
        // The generated delta encoder prefixes a replaced repeated field with
        // this marker before its zero-based, length-delimited element entries.
        if bytes.first() == Some(&0x0F) {
            bytes = &bytes[1..];
        }
        while !bytes.is_empty() {
            let key = read_delta_varint(&mut bytes)?;
            if key & 0x07 != 2 {
                return None;
            }
            let index = usize::try_from(key >> 3).ok()?;
            if index != messages.len() {
                return None;
            }
            let length = usize::try_from(read_delta_varint(&mut bytes)?).ok()?;
            if bytes.len() < length {
                return None;
            }
            let (message, rest) = bytes.split_at(length);
            let message = sanitize_codegen_delta_message(message, schema)?;
            messages.push(M::decode(message.as_slice()).ok()?);
            bytes = rest;
        }
    }
    Some(messages)
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
            if frame.demo_cmd == DemAnimationData || frame.demo_cmd == DemSendTables || frame.demo_cmd == DemStringTables {
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
                FirstPassParser::resize_if_needed(buf, decompress_len(possibly_uncompressed_bytes))?;
                match SnapDecoder::new().decompress(possibly_uncompressed_bytes, buf) {
                    Ok(idx) => Ok(&buf[..idx]),
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
        let msg = match CDemoPacket::decode(bytes) {
            Err(_) => return Err(DemoParserError::MalformedMessage),
            Ok(msg) => msg,
        };
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
            let msg_type = bitreader.read_u_bit_var()?;
            let size = bitreader.read_varint()?;
            if buf.len() < size as usize {
                buf.resize(size as usize, 0)
            }
            bitreader.read_n_bytes_mut(size as usize, buf)?;
            let msg_bytes = &buf[..size as usize];
            let ok = match NetMessageType::from(msg_type as i32) {
                svc_PacketEntities => {
                    if should_parse_entities {
                        self.parse_packet_ents(&msg_bytes, is_fullpacket)?;
                        if !is_fullpacket {
                            self.collect_entities();
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

        let msg = match CsvcMsgUserCommands::decode(bytes) {
            Ok(m) => m,
            _ => return Ok(()),
        };
        for cmd in msg.commands {
            if let Some(delta_data) = cmd.delta_data.as_ref().filter(|data| !data.is_empty()) {
                let sanitized = match sanitize_codegen_delta_message(delta_data.as_ref(), DeltaMessageSchema::CsgoUserCmd) {
                    Some(value) => value,
                    None => continue,
                };
                let user_cmd = match DeltaCsgoUserCmdPb::decode(sanitized.as_slice()) {
                    Ok(value) => value,
                    Err(_) => continue,
                };
                self.apply_delta_user_cmd(user_cmd, cmd.player_slot());
                continue;
            }
            let user_cmd = match CsgoUserCmdPb::decode(cmd.data()) {
                Ok(m) => m,
                _ => return Ok(()),
            };

            let left_hand_desired = user_cmd.left_hand_desired();
            let attack1_start_history_index =
                user_cmd.attack1_start_history_index.unwrap_or(-1);
            let attack2_start_history_index =
                user_cmd.attack2_start_history_index.unwrap_or(-1);
            if let Some(base) = user_cmd.base {
                let entity_id = demo_network_ehandle_index(base.pawn_entity_handle());
                if let Some(Some(ent)) = self.entities.get_mut(entity_id as usize) {
                    let history = user_cmd
                        .input_history
                        .into_iter()
                        .map(parse_input_history)
                        .collect();
                    ent.props.insert(USERCMD_INPUT_HISTORY_BASEID, Variant::InputHistory(history));
                    ent.props.insert(
                        USERCMD_ATTACK_START_HISTORY_INDEX_1,
                        Variant::I32(attack1_start_history_index),
                    );
                    ent.props.insert(
                        USERCMD_ATTACK_START_HISTORY_INDEX_2,
                        Variant::I32(attack2_start_history_index),
                    );
                    if let Some(client_tick) = base.client_tick {
                        ent.props.insert(USERCMD_CLIENT_TICK, Variant::I32(client_tick));
                    }
                    let mut subtick_moves = vec![];
                    for subtick in &base.subtick_moves {
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
                    ent.props.insert(USERCMD_SUBTICK_MOVES_BASEID, Variant::UserCmdSubtickMoves(subtick_moves));
                    ent.props.insert(USERCMD_LEFTMOVE, Variant::F32(base.leftmove()));
                    ent.props.insert(USERCMD_FORWARDMOVE, Variant::F32(base.forwardmove()));
                    ent.props.insert(USERCMD_UPMOVE, Variant::F32(base.upmove()));
                    ent.props.insert(USERCMD_IMPULSE, Variant::I32(base.impulse()));
                    ent.props.insert(USERCMD_MOUSE_DX, Variant::I32(base.mousedx()));
                    ent.props.insert(USERCMD_MOUSE_DY, Variant::I32(base.mousedy()));
                    ent.props.insert(USERCMD_WEAPON_SELECT, Variant::I32(base.weaponselect()));
                    ent.props.insert(USERCMD_SUBTICK_LEFT_HAND_DESIRED, Variant::Bool(left_hand_desired));
                    if let Some(viewangles) = base.viewangles {
                        ent.props.insert(USERCMD_VIEWANGLE_X, Variant::F32(viewangles.x()));
                        ent.props.insert(USERCMD_VIEWANGLE_Y, Variant::F32(viewangles.y()));
                        ent.props.insert(USERCMD_VIEWANGLE_Z, Variant::F32(viewangles.z()));
                    }
                    if let Some(buttons_pb) = base.buttons_pb {
                        ent.props.insert(USERCMD_BUTTONSTATE_1, Variant::U64(buttons_pb.buttonstate1()));
                        ent.props.insert(USERCMD_BUTTONSTATE_2, Variant::U64(buttons_pb.buttonstate2()));
                        ent.props.insert(USERCMD_BUTTONSTATE_3, Variant::U64(buttons_pb.buttonstate3()));
                    }
                    ent.props
                        .insert(USERCMD_CONSUMED_SERVER_ANGLE_CHANGES, Variant::U32(base.consumed_server_angle_changes()));
                }
            }
        }
        Ok(())
    }

    fn apply_delta_user_cmd(&mut self, user_cmd: DeltaCsgoUserCmdPb, player_slot: i32) {
        let input_history = decode_codegen_delta_repeated::<CsgoInputHistoryEntryPb>(&user_cmd.input_history_delta, DeltaMessageSchema::InputHistory)
            .unwrap_or_default()
            .into_iter()
            .map(parse_input_history)
            .collect();
        let left_hand_desired = user_cmd.left_hand_desired;
        let attack1_start_history_index = user_cmd.attack1_start_history_index.unwrap_or(-1);
        let attack2_start_history_index = user_cmd.attack2_start_history_index.unwrap_or(-1);
        let Some(base) = user_cmd.base else {
            return;
        };
        let subtick_moves = decode_codegen_delta_repeated::<CSubtickMoveStep>(&base.subtick_moves_delta, DeltaMessageSchema::SubtickMove)
            .unwrap_or_default()
            .into_iter()
            .map(|subtick| UserCmdSubtickMove {
                when: subtick.when(),
                button: subtick.button(),
                pressed: subtick.pressed(),
                analog_forward: subtick.analog_forward_delta(),
                analog_left: subtick.analog_left_delta(),
                pitch_delta: subtick.pitch_delta(),
                yaw_delta: subtick.yaw_delta(),
            })
            .collect();

        let explicit_pawn = base
            .pawn_entity_handle
            .filter(|handle| *handle != 0x00FF_FFFF)
            .map(demo_network_ehandle_index);
        let controller_entid = player_slot.checked_add(1);
        let controller_pawn = controller_entid.and_then(|controller| {
            self.prop_controller
                .special_ids
                .player_pawn
                .and_then(|id| match self.get_prop_from_ent(&id, &controller) {
                    Ok(Variant::U32(handle)) => Some(demo_network_ehandle_index(handle)),
                    _ => None,
                })
        });
        let metadata_pawn = controller_entid.and_then(|controller| {
            self.players
                .values()
                .find(|player| player.controller_entid == Some(controller))
                .and_then(|player| player.player_entity_id)
        });
        let Some(entity_id) = explicit_pawn.or(controller_pawn).or(metadata_pawn) else {
            return;
        };
        let Some(Some(ent)) = self.entities.get_mut(entity_id as usize) else {
            return;
        };

        ent.props.insert(USERCMD_INPUT_HISTORY_BASEID, Variant::InputHistory(input_history));
        ent.props.insert(USERCMD_SUBTICK_MOVES_BASEID, Variant::UserCmdSubtickMoves(subtick_moves));
        ent.props.insert(
            USERCMD_ATTACK_START_HISTORY_INDEX_1,
            Variant::I32(attack1_start_history_index),
        );
        ent.props.insert(
            USERCMD_ATTACK_START_HISTORY_INDEX_2,
            Variant::I32(attack2_start_history_index),
        );
        if let Some(client_tick) = base.client_tick {
            ent.props.insert(USERCMD_CLIENT_TICK, Variant::I32(client_tick));
        }
        if let Some(value) = left_hand_desired {
            ent.props.insert(USERCMD_SUBTICK_LEFT_HAND_DESIRED, Variant::Bool(value));
        }
        if let Some(value) = base.leftmove {
            ent.props.insert(USERCMD_LEFTMOVE, Variant::F32(value));
        }
        if let Some(value) = base.forwardmove {
            ent.props.insert(USERCMD_FORWARDMOVE, Variant::F32(value));
        }
        if let Some(value) = base.upmove {
            ent.props.insert(USERCMD_UPMOVE, Variant::F32(value));
        }
        if let Some(value) = base.impulse {
            ent.props.insert(USERCMD_IMPULSE, Variant::I32(value));
        }
        if let Some(value) = base.mousedx {
            ent.props.insert(USERCMD_MOUSE_DX, Variant::I32(value));
        }
        if let Some(value) = base.mousedy {
            ent.props.insert(USERCMD_MOUSE_DY, Variant::I32(value));
        }
        if let Some(value) = base.weaponselect {
            ent.props.insert(USERCMD_WEAPON_SELECT, Variant::I32(value));
        }
        if let Some(value) = base.consumed_server_angle_changes {
            ent.props.insert(USERCMD_CONSUMED_SERVER_ANGLE_CHANGES, Variant::U32(value));
        }
        if let Some(viewangles) = base.viewangles {
            if let Some(value) = viewangles.x {
                ent.props.insert(USERCMD_VIEWANGLE_X, Variant::F32(value));
            }
            if let Some(value) = viewangles.y {
                ent.props.insert(USERCMD_VIEWANGLE_Y, Variant::F32(value));
            }
            if let Some(value) = viewangles.z {
                ent.props.insert(USERCMD_VIEWANGLE_Z, Variant::F32(value));
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
        Ok(())
    }
    pub fn parse_user_command_cmd(&mut self, _data: &[u8]) -> Result<(), DemoParserError> {
        // Only in pov demos. Maybe implement sometime. Includes buttons etc.
        Ok(())
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

        let subticks = decode_codegen_delta_repeated::<CSubtickMoveStep>(&base.subtick_moves_delta, DeltaMessageSchema::SubtickMove).unwrap();
        assert_eq!(subticks.len(), 1);
        assert_eq!(subticks[0].button(), 0x400);
        assert!(subticks[0].pressed());
        assert!((subticks[0].when() - 0.421875).abs() < f32::EPSILON);
    }

    #[test]
    fn rejects_nonsequential_codegen_delta_repeated_entries() {
        let payload = prost::bytes::Bytes::from_static(&[0x0A, 0x00]);
        assert!(decode_codegen_delta_repeated::<CSubtickMoveStep>(&[payload], DeltaMessageSchema::SubtickMove,).is_none());
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
