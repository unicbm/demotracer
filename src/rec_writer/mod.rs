/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::model::{
    Cs2Rec, Cs2RecHeader, HighFidelityMetadata, MovementSnapshot, ProjectileKind,
    ReplayCommandFrame, ReplayInputHistoryEntry, ReplayInputHistoryTick, ReplayMovementExtra,
    ReplayProjectile, ReplayTick, SubtickMove, COMMAND_FIELDS_ALL, DTR_FORMAT_VERSION,
    INPUT_HISTORY_FIELDS_ALL,
};
use crate::{io_error, Error, Result};
use std::fs::File;
use std::io::{BufReader, BufWriter, Cursor, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 8] = b"CSDTRREC";
const CODEC_NONE: u8 = 0;
const CODEC_BROTLI: u8 = 1;
const BROTLI_BUFFER_SIZE: usize = 4096;
const BROTLI_QUALITY: u32 = 6;
const BROTLI_LGWIN: u32 = 22;
const SNAPSHOT_BYTE_SIZE: usize = 92;
const TICK_METADATA_BYTE_SIZE: usize = 8;
const PROJECTILE_BYTE_SIZE: usize = 48;
const SUBTICK_BYTE_SIZE: usize = 28;
const COMMAND_FRAME_BYTE_SIZE: usize = 68;
const MOVEMENT_EXTRA_BYTE_SIZE: usize = 48;
const INPUT_HISTORY_TICK_BYTE_SIZE: usize = 16;
const INPUT_HISTORY_ENTRY_BYTE_SIZE: usize = 128;
const SECTION_HEADER_BYTE_SIZE: u64 = 36;
const MAX_SUBTICKS_PER_TICK: u32 = 36;
const MAX_INPUT_HISTORY_PER_TICK: u32 = 64;

const SECTION_SNAPSHOTS: u32 = 1;
const SECTION_TICK_METADATA: u32 = 2;
const SECTION_PROJECTILES: u32 = 3;
const SECTION_HIGH_FIDELITY_JSON: u32 = 4;
const SECTION_SUBTICKS: u32 = 5;
const SECTION_COMMAND_FRAMES: u32 = 6;
const SECTION_MOVEMENT_EXTRAS: u32 = 7;
const SECTION_INPUT_HISTORY: u32 = 8;
const SECTION_VERSION_V1: u32 = 1;
const SECTION_VERSION_V2: u32 = 2;

const MIB: u64 = 1024 * 1024;

/// Resource limits applied while reading untrusted `.dtr` files.
///
/// The defaults are deliberately generous enough for unusually long individual
/// rounds while still preventing corrupt headers or compression bombs from
/// requesting excessive memory.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DtrReadLimits {
    pub max_file_bytes: u64,
    pub max_section_count: u32,
    pub max_compressed_section_bytes: u64,
    pub max_total_compressed_bytes: u64,
    pub max_decoded_section_bytes: u64,
    pub max_total_decoded_bytes: u64,
    pub max_tick_count: u32,
    pub max_subtick_count: u32,
    pub max_subticks_per_tick: u32,
    pub max_projectile_count: u32,
    pub max_metadata_json_bytes: u32,
}

impl Default for DtrReadLimits {
    fn default() -> Self {
        Self {
            max_file_bytes: 64 * MIB,
            max_section_count: 32,
            max_compressed_section_bytes: 48 * MIB,
            max_total_compressed_bytes: 64 * MIB,
            max_decoded_section_bytes: 48 * MIB,
            max_total_decoded_bytes: 64 * MIB,
            max_tick_count: 32_768,
            max_subtick_count: 1_179_648,
            max_subticks_per_tick: MAX_SUBTICKS_PER_TICK,
            max_projectile_count: 4_096,
            max_metadata_json_bytes: 8 * 1024 * 1024,
        }
    }
}

struct ReadBudget<R> {
    inner: R,
    remaining_budget: u64,
    known_remaining: Option<u64>,
}

impl<R> ReadBudget<R> {
    fn new(inner: R, max_bytes: u64, known_len: Option<u64>) -> Self {
        Self {
            inner,
            remaining_budget: max_bytes,
            known_remaining: known_len,
        }
    }

    fn ensure_available(&self, len: u64, name: &str) -> Result<()> {
        if len > self.remaining_budget {
            return Err(Error::InvalidRec(format!(
                "{name} length {len} exceeds remaining input budget {}",
                self.remaining_budget
            )));
        }
        if let Some(remaining) = self.known_remaining {
            if len > remaining {
                return Err(Error::InvalidRec(format!(
                    "{name} length {len} exceeds remaining file bytes {remaining}"
                )));
            }
        }
        Ok(())
    }
}

impl<R: Read> Read for ReadBudget<R> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let known_remaining = self.known_remaining.unwrap_or(u64::MAX);
        let allowed = self
            .remaining_budget
            .min(known_remaining)
            .min(buf.len() as u64) as usize;
        if allowed == 0 {
            return Ok(0);
        }
        let read = self.inner.read(&mut buf[..allowed])?;
        self.remaining_budget -= read as u64;
        if let Some(remaining) = &mut self.known_remaining {
            *remaining -= read as u64;
        }
        Ok(read)
    }
}

impl<R: Read> ReadBudget<R> {
    fn require_eof(&mut self) -> Result<()> {
        if self.known_remaining.is_some_and(|remaining| remaining != 0) {
            return Err(Error::InvalidRec(
                "trailing bytes after top-level .dtr payload".to_string(),
            ));
        }
        if self.known_remaining.is_none() {
            let mut extra = [0_u8; 1];
            if self
                .inner
                .read(&mut extra)
                .map_err(|e| Error::InvalidRec(e.to_string()))?
                != 0
            {
                return Err(Error::InvalidRec(
                    "trailing bytes after top-level .dtr payload".to_string(),
                ));
            }
        }
        Ok(())
    }
}

pub fn write_rec_file(path: &Path, rec: &Cs2Rec) -> Result<()> {
    let file = File::create(path).map_err(|e| io_error(path, e))?;
    let mut writer = BufWriter::new(file);
    write_rec(&mut writer, rec)
}

pub fn read_rec_file(path: &Path) -> Result<Cs2Rec> {
    read_rec_file_with_limits(path, DtrReadLimits::default())
}

pub fn read_rec_file_with_limits(path: &Path, limits: DtrReadLimits) -> Result<Cs2Rec> {
    let file = File::open(path).map_err(|e| io_error(path, e))?;
    let file_len = file.metadata().map_err(|e| io_error(path, e))?.len();
    if file_len > limits.max_file_bytes {
        return Err(Error::InvalidRec(format!(
            "file length {file_len} exceeds limit {}",
            limits.max_file_bytes
        )));
    }
    let mut reader = BufReader::new(file);
    read_rec_with_known_len(&mut reader, limits, Some(file_len))
}

pub fn write_rec<W: Write>(writer: &mut W, rec: &Cs2Rec) -> Result<()> {
    validate_rec_semantics(rec)?;
    validate_subtick_count(rec)?;
    validate_play_start_tick(rec.ticks.len(), rec.header.play_start_tick_index)?;
    validate_snapshot_chain(rec)?;
    validate_optional_tick_aligned_count(
        "command frame count",
        rec.command_frames.len(),
        rec.ticks.len(),
    )?;
    validate_optional_tick_aligned_count(
        "movement extra count",
        rec.movement_extras.len(),
        rec.ticks.len(),
    )?;
    validate_input_history_shape(rec)?;
    let tick_count = checked_u32_count("tick count", rec.ticks.len())?;
    let subtick_count = checked_u32_count("subtick count", rec.subticks.len())?;
    let projectile_count = checked_u32_count("projectile count", rec.projectiles.len())?;
    let metadata_json = optional_metadata_json_bytes(&rec.high_fidelity)?;
    let metadata_json_len = checked_u32_count("metadata json length", metadata_json.len())?;

    writer
        .write_all(MAGIC)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u32(writer, DTR_FORMAT_VERSION)?;
    write_f32(writer, rec.header.tick_rate)?;
    write_u32(writer, rec.header.round)?;
    write_u8(writer, rec.header.side)?;
    write_u32(writer, rec.header.flags)?;
    write_u64(writer, rec.header.steam_id)?;
    write_u32(writer, tick_count)?;
    write_u32(writer, subtick_count)?;
    write_u32(writer, projectile_count)?;
    write_u32(writer, rec.header.play_start_tick_index)?;
    write_u32(writer, metadata_json_len)?;
    write_string(writer, &rec.header.map)?;
    write_string(writer, &rec.header.player_name)?;
    write_sectioned_body(writer, rec, &metadata_json)?;

    Ok(())
}

pub fn read_rec<R: Read>(reader: &mut R) -> Result<Cs2Rec> {
    read_rec_with_limits(reader, DtrReadLimits::default())
}

pub fn read_rec_with_limits<R: Read>(reader: &mut R, limits: DtrReadLimits) -> Result<Cs2Rec> {
    read_rec_with_known_len(reader, limits, None)
}

fn read_rec_with_known_len<R: Read>(
    reader: &mut R,
    limits: DtrReadLimits,
    known_len: Option<u64>,
) -> Result<Cs2Rec> {
    let mut reader = ReadBudget::new(reader, limits.max_file_bytes, known_len);
    read_rec_bounded(&mut reader, limits)
}

fn read_rec_bounded<R: Read>(reader: &mut ReadBudget<R>, limits: DtrReadLimits) -> Result<Cs2Rec> {
    let mut magic = [0_u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if &magic != MAGIC {
        return Err(Error::InvalidRec("bad magic".to_string()));
    }

    let version = read_u32(reader)?;
    if !(3..=DTR_FORMAT_VERSION).contains(&version) {
        return Err(Error::InvalidRec(format!("unsupported version {version}")));
    }

    let tick_rate = read_f32(reader)?;
    let round = read_u32(reader)?;
    let side = read_u8(reader)?;
    let flags = read_u32(reader)?;
    let steam_id = read_u64(reader)?;
    let tick_count_raw = read_u32(reader)?;
    let subtick_count_raw = read_u32(reader)?;
    let projectile_count_raw = if version >= 4 { read_u32(reader)? } else { 0 };
    let play_start_tick_index = if version >= 5 { read_u32(reader)? } else { 0 };
    let metadata_json_len_raw = if version >= 6 { read_u32(reader)? } else { 0 };
    validate_header_counts(
        tick_count_raw,
        subtick_count_raw,
        projectile_count_raw,
        metadata_json_len_raw,
        limits,
    )?;
    let tick_count = tick_count_raw as usize;
    let subtick_count = subtick_count_raw as usize;
    let projectile_count = projectile_count_raw as usize;
    let metadata_json_len = metadata_json_len_raw as usize;
    validate_play_start_tick(tick_count, play_start_tick_index)?;
    let map = read_bounded_string(reader, "map string")?;
    let player_name = read_bounded_string(reader, "player name string")?;
    if version >= 7 {
        let (
            ticks,
            projectiles,
            high_fidelity,
            subticks,
            command_frames,
            movement_extras,
            input_history_ticks,
            input_history_entries,
        ) = read_sectioned_body(
            reader,
            version,
            tick_count,
            subtick_count,
            projectile_count,
            metadata_json_len,
            limits,
        )?;

        let rec = Cs2Rec {
            header: Cs2RecHeader {
                version,
                tick_rate,
                map,
                round,
                side,
                steam_id,
                player_name,
                flags,
                play_start_tick_index,
            },
            ticks,
            projectiles,
            high_fidelity,
            subticks,
            command_frames,
            movement_extras,
            input_history_ticks,
            input_history_entries,
        };
        return finish_read_rec(reader, rec);
    }

    let codec = read_u8(reader)?;
    if codec != CODEC_BROTLI {
        return Err(Error::InvalidRec(format!("unsupported codec {codec}")));
    }

    let body_uncompressed_len_raw = read_u64(reader)?;
    let body_compressed_len_raw = read_u64(reader)?;
    enforce_byte_limit(
        "compressed body",
        body_compressed_len_raw,
        limits.max_compressed_section_bytes,
    )?;
    enforce_byte_limit(
        "total compressed body",
        body_compressed_len_raw,
        limits.max_total_compressed_bytes,
    )?;
    enforce_byte_limit(
        "decoded body",
        body_uncompressed_len_raw,
        limits.max_decoded_section_bytes,
    )?;
    enforce_byte_limit(
        "total decoded body",
        body_uncompressed_len_raw,
        limits.max_total_decoded_bytes,
    )?;
    let body_uncompressed_len = checked_len(body_uncompressed_len_raw, "body_uncompressed_len")?;
    let body_compressed_len = checked_len(body_compressed_len_raw, "body_compressed_len")?;
    let expected_body_len = expected_body_len(
        tick_count,
        subtick_count,
        projectile_count,
        metadata_json_len,
    )?;
    if body_uncompressed_len != expected_body_len {
        return Err(Error::InvalidRec(format!(
            "body length {body_uncompressed_len} != expected {expected_body_len}"
        )));
    }

    let compressed = read_exact_vec_bounded(reader, body_compressed_len, "compressed body")?;
    let body = decompress_body(&compressed, body_uncompressed_len)?;
    let (ticks, projectiles, high_fidelity, subticks) = read_body(
        &body,
        tick_count,
        projectile_count,
        metadata_json_len,
        subtick_count,
        limits.max_subticks_per_tick,
    )?;

    let rec = Cs2Rec {
        header: Cs2RecHeader {
            version,
            tick_rate,
            map,
            round,
            side,
            steam_id,
            player_name,
            flags,
            play_start_tick_index,
        },
        ticks,
        projectiles,
        high_fidelity,
        subticks,
        command_frames: Vec::new(),
        movement_extras: Vec::new(),
        input_history_ticks: Vec::new(),
        input_history_entries: Vec::new(),
    };
    finish_read_rec(reader, rec)
}

fn finish_read_rec<R: Read>(reader: &mut ReadBudget<R>, rec: Cs2Rec) -> Result<Cs2Rec> {
    validate_rec_semantics(&rec)?;
    reader.require_eof()?;
    Ok(rec)
}

fn validate_rec_semantics(rec: &Cs2Rec) -> Result<()> {
    if !rec.header.tick_rate.is_finite() || rec.header.tick_rate <= 0.0 {
        return Err(Error::InvalidRec(
            "tick_rate must be finite and positive".to_string(),
        ));
    }
    if !matches!(rec.header.side, 0 | 2 | 3) {
        return Err(Error::InvalidRec(format!(
            "side {} is not 0, 2, or 3",
            rec.header.side
        )));
    }

    for (tick_index, tick) in rec.ticks.iter().enumerate() {
        validate_snapshot_semantics(&tick.pre, &format!("tick {tick_index} pre"))?;
        validate_snapshot_semantics(&tick.post, &format!("tick {tick_index} post"))?;
        if tick.weapon_def_index < -1 {
            return Err(Error::InvalidRec(format!(
                "tick {tick_index} weapon_def_index {} is below -1",
                tick.weapon_def_index
            )));
        }
    }

    for (index, subtick) in rec.subticks.iter().enumerate() {
        if !subtick.when.is_finite() || !(0.0..1.0).contains(&subtick.when) {
            return Err(Error::InvalidRec(format!(
                "subtick {index} when must be finite and in [0, 1)"
            )));
        }
        validate_finite_values(
            &[
                subtick.pressed,
                subtick.analog_forward,
                subtick.analog_left,
                subtick.pitch_delta,
                subtick.yaw_delta,
            ],
            &format!("subtick {index}"),
        )?;
    }

    for (index, frame) in rec.command_frames.iter().enumerate() {
        if frame.fields & !COMMAND_FIELDS_ALL != 0 {
            return Err(Error::InvalidRec(format!(
                "command frame {index} has unknown fields 0x{:08x}",
                frame.fields & !COMMAND_FIELDS_ALL
            )));
        }
        if frame.left_hand_desired > 1 {
            return Err(Error::InvalidRec(format!(
                "command frame {index} left_hand_desired must be 0 or 1"
            )));
        }
        if frame.weapon_select < -1 {
            return Err(Error::InvalidRec(format!(
                "command frame {index} weapon_select {} is below -1",
                frame.weapon_select
            )));
        }
        validate_finite_values(
            &[
                frame.forward_move,
                frame.left_move,
                frame.up_move,
                frame.pitch,
                frame.yaw,
                frame.roll,
            ],
            &format!("command frame {index}"),
        )?;
    }

    for (index, extra) in rec.movement_extras.iter().enumerate() {
        validate_finite_values(
            &[
                extra.jump_pressed_time,
                extra.last_duck_time,
                extra.last_actual_jump_press_frac,
                extra.last_usable_jump_press_frac,
                extra.last_landed_frac,
                extra.last_landed_velocity[0],
                extra.last_landed_velocity[1],
                extra.last_landed_velocity[2],
            ],
            &format!("movement extra {index}"),
        )?;
    }

    for (index, entry) in rec.input_history_entries.iter().enumerate() {
        if entry.fields & !INPUT_HISTORY_FIELDS_ALL != 0 {
            return Err(Error::InvalidRec(format!(
                "input history entry {index} has unknown fields 0x{:08x}",
                entry.fields & !INPUT_HISTORY_FIELDS_ALL
            )));
        }
        validate_finite_values(
            &[
                entry.view_angles[0],
                entry.view_angles[1],
                entry.view_angles[2],
                entry.render_tick_fraction,
                entry.player_tick_fraction,
                entry.cl_interp_fraction,
                entry.sv_interp0_fraction,
                entry.sv_interp1_fraction,
                entry.player_interp_fraction,
                entry.shoot_position[0],
                entry.shoot_position[1],
                entry.shoot_position[2],
                entry.target_head_pos_check[0],
                entry.target_head_pos_check[1],
                entry.target_head_pos_check[2],
                entry.target_abs_pos_check[0],
                entry.target_abs_pos_check[1],
                entry.target_abs_pos_check[2],
                entry.target_abs_ang_check[0],
                entry.target_abs_ang_check[1],
                entry.target_abs_ang_check[2],
            ],
            &format!("input history entry {index}"),
        )?;
    }

    for (index, projectile) in rec.projectiles.iter().enumerate() {
        if projectile.tick_index as usize >= rec.ticks.len() {
            return Err(Error::InvalidRec(format!(
                "projectile {index} tick_index {} out of range for {} ticks",
                projectile.tick_index,
                rec.ticks.len()
            )));
        }
        if projectile.weapon_def_index < -1 {
            return Err(Error::InvalidRec(format!(
                "projectile {index} weapon_def_index {} is below -1",
                projectile.weapon_def_index
            )));
        }
        validate_finite_values(
            &projectile.initial_position,
            &format!("projectile {index} initial position"),
        )?;
        validate_finite_values(
            &projectile.initial_velocity,
            &format!("projectile {index} initial velocity"),
        )?;
        validate_finite_values(
            &projectile.detonation_position,
            &format!("projectile {index} detonation position"),
        )?;
    }

    validate_input_history_shape(rec)
}

fn validate_snapshot_semantics(snapshot: &MovementSnapshot, name: &str) -> Result<()> {
    validate_finite_values(&snapshot.origin, &format!("{name} origin"))?;
    validate_finite_values(&snapshot.velocity, &format!("{name} velocity"))?;
    validate_finite_values(&snapshot.angles, &format!("{name} angles"))?;
    validate_finite_values(
        &[
            snapshot.duck_amount,
            snapshot.duck_speed,
            snapshot.ladder_normal[0],
            snapshot.ladder_normal[1],
            snapshot.ladder_normal[2],
        ],
        name,
    )?;
    for (field, value) in [
        ("ducked", snapshot.ducked),
        ("ducking", snapshot.ducking),
        ("desires_duck", snapshot.desires_duck),
    ] {
        if value > 1 {
            return Err(Error::InvalidRec(format!("{name} {field} must be 0 or 1")));
        }
    }
    Ok(())
}

fn validate_finite_values(values: &[f32], name: &str) -> Result<()> {
    if values.iter().any(|value| !value.is_finite()) {
        return Err(Error::InvalidRec(format!(
            "{name} contains a non-finite float"
        )));
    }
    Ok(())
}

fn validate_subtick_count(rec: &Cs2Rec) -> Result<()> {
    let mut expected_subticks = 0_usize;
    for (tick_index, tick) in rec.ticks.iter().enumerate() {
        accumulate_tick_subticks(
            &mut expected_subticks,
            tick.num_subtick,
            tick_index,
            MAX_SUBTICKS_PER_TICK,
        )?;
    }
    if expected_subticks != rec.subticks.len() {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {}",
            rec.subticks.len()
        )));
    }
    Ok(())
}

fn validate_play_start_tick(tick_count: usize, play_start_tick_index: u32) -> Result<()> {
    if tick_count == 0 {
        if play_start_tick_index == 0 {
            return Ok(());
        }
        return Err(Error::InvalidRec(format!(
            "play_start_tick_index {play_start_tick_index} requires at least one tick"
        )));
    }
    if play_start_tick_index as usize >= tick_count {
        return Err(Error::InvalidRec(format!(
            "play_start_tick_index {play_start_tick_index} out of range for {tick_count} ticks"
        )));
    }
    Ok(())
}

fn validate_snapshot_chain(rec: &Cs2Rec) -> Result<()> {
    for (index, pair) in rec.ticks.windows(2).enumerate() {
        if !snapshot_bit_eq(&pair[0].post, &pair[1].pre) {
            return Err(Error::InvalidRec(format!(
                "discontinuous snapshot chain between ticks {index} and {}",
                index + 1
            )));
        }
    }
    Ok(())
}

fn validate_optional_tick_aligned_count(name: &str, count: usize, tick_count: usize) -> Result<()> {
    if count != 0 && count != tick_count {
        return Err(Error::InvalidRec(format!(
            "{name} {count} must be 0 or match tick count {tick_count}"
        )));
    }
    Ok(())
}

fn validate_input_history_shape(rec: &Cs2Rec) -> Result<()> {
    validate_optional_tick_aligned_count(
        "input history tick count",
        rec.input_history_ticks.len(),
        rec.ticks.len(),
    )?;
    if rec.input_history_ticks.is_empty() {
        if rec.input_history_entries.is_empty() {
            return Ok(());
        }
        return Err(Error::InvalidRec(
            "input history entries require tick descriptors".to_string(),
        ));
    }
    let mut expected_entries = 0_usize;
    for (tick_index, tick) in rec.input_history_ticks.iter().enumerate() {
        if tick.num_entries > MAX_INPUT_HISTORY_PER_TICK {
            return Err(Error::InvalidRec(format!(
                "input history tick {tick_index} count {} exceeds limit {MAX_INPUT_HISTORY_PER_TICK}",
                tick.num_entries
            )));
        }
        for (name, index) in [
            ("attack1", tick.attack1_start_history_index),
            ("attack2", tick.attack2_start_history_index),
        ] {
            if index < -1 || index >= tick.num_entries as i32 {
                return Err(Error::InvalidRec(format!(
                    "input history tick {tick_index} {name} index {index} is outside {} entries",
                    tick.num_entries
                )));
            }
        }
        expected_entries = expected_entries
            .checked_add(tick.num_entries as usize)
            .ok_or_else(|| Error::InvalidRec("input history entry sum overflow".to_string()))?;
    }
    if expected_entries != rec.input_history_entries.len() {
        return Err(Error::InvalidRec(format!(
            "input history tick sum {expected_entries} != entry count {}",
            rec.input_history_entries.len()
        )));
    }
    Ok(())
}

fn write_sectioned_body<W: Write>(
    writer: &mut W,
    rec: &Cs2Rec,
    metadata_json: &[u8],
) -> Result<()> {
    let mut sections = Vec::new();
    sections.push((
        SECTION_SNAPSHOTS,
        SECTION_VERSION_V2,
        if rec.ticks.is_empty() {
            0
        } else {
            rec.ticks.len() + 1
        },
        build_snapshot_section_v2(rec)?,
    ));
    sections.push((
        SECTION_TICK_METADATA,
        SECTION_VERSION_V1,
        rec.ticks.len(),
        build_tick_metadata_section(rec)?,
    ));
    sections.push((
        SECTION_SUBTICKS,
        SECTION_VERSION_V1,
        rec.subticks.len(),
        build_subtick_section(rec)?,
    ));
    if !rec.projectiles.is_empty() {
        sections.push((
            SECTION_PROJECTILES,
            SECTION_VERSION_V1,
            rec.projectiles.len(),
            build_projectile_section(rec)?,
        ));
    }
    if !metadata_json.is_empty() {
        sections.push((
            SECTION_HIGH_FIDELITY_JSON,
            SECTION_VERSION_V1,
            1,
            metadata_json.to_vec(),
        ));
    }
    if !rec.command_frames.is_empty() {
        sections.push((
            SECTION_COMMAND_FRAMES,
            SECTION_VERSION_V2,
            rec.command_frames.len(),
            build_command_frame_section_v2(rec)?,
        ));
    }
    if !rec.movement_extras.is_empty() {
        sections.push((
            SECTION_MOVEMENT_EXTRAS,
            SECTION_VERSION_V1,
            rec.movement_extras.len(),
            build_movement_extra_section(rec)?,
        ));
    }
    sections.push((
        SECTION_INPUT_HISTORY,
        SECTION_VERSION_V1,
        rec.ticks.len(),
        build_input_history_section(rec)?,
    ));

    write_u32(writer, checked_u32_count("section count", sections.len())?)?;
    for (section_id, section_version, element_count, payload) in sections {
        write_section(writer, section_id, section_version, element_count, &payload)?;
    }
    Ok(())
}

fn write_section<W: Write>(
    writer: &mut W,
    section_id: u32,
    section_version: u32,
    element_count: usize,
    payload: &[u8],
) -> Result<()> {
    let compressed = compress_body(payload)?;
    write_u32(writer, section_id)?;
    write_u32(writer, section_version)?;
    write_u8(writer, CODEC_BROTLI)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u32(writer, 0)?;
    write_u32(
        writer,
        checked_u32_count("section element count", element_count)?,
    )?;
    write_u64(writer, payload.len() as u64)?;
    write_u64(writer, compressed.len() as u64)?;
    writer
        .write_all(&compressed)
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn build_tick_metadata_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.ticks.len() * TICK_METADATA_BYTE_SIZE);
    for tick in &rec.ticks {
        write_i32(&mut body, tick.weapon_def_index)?;
        write_u32(&mut body, tick.num_subtick)?;
    }
    Ok(body)
}

#[cfg(test)]
fn build_snapshot_section_v1(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let snapshot_count = if rec.ticks.is_empty() {
        0
    } else {
        rec.ticks.len() + 1
    };
    let mut body = Vec::with_capacity(snapshot_count * SNAPSHOT_BYTE_SIZE);
    if let Some(first) = rec.ticks.first() {
        write_snapshot(&mut body, &first.pre)?;
        for tick in &rec.ticks {
            write_snapshot(&mut body, &tick.post)?;
        }
    }
    Ok(body)
}

fn build_projectile_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.projectiles.len() * PROJECTILE_BYTE_SIZE);
    for projectile in &rec.projectiles {
        write_projectile(&mut body, projectile)?;
    }
    Ok(body)
}

fn build_subtick_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.subticks.len() * SUBTICK_BYTE_SIZE);
    for subtick in &rec.subticks {
        write_subtick(&mut body, subtick)?;
    }
    Ok(body)
}

fn build_input_history_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let capacity = rec
        .ticks
        .len()
        .checked_mul(INPUT_HISTORY_TICK_BYTE_SIZE)
        .and_then(|value| {
            rec.input_history_entries
                .len()
                .checked_mul(INPUT_HISTORY_ENTRY_BYTE_SIZE)
                .and_then(|entry_bytes| value.checked_add(entry_bytes))
        })
        .ok_or_else(|| Error::InvalidRec("input history section too large".to_string()))?;
    let mut body = Vec::with_capacity(capacity);
    let mut entry_index = 0_usize;
    for tick_index in 0..rec.ticks.len() {
        let tick = rec
            .input_history_ticks
            .get(tick_index)
            .copied()
            .unwrap_or_default();
        write_i32(&mut body, tick.source_client_tick)?;
        write_i32(&mut body, tick.attack1_start_history_index)?;
        write_i32(&mut body, tick.attack2_start_history_index)?;
        write_u32(&mut body, tick.num_entries)?;
        let end = entry_index + tick.num_entries as usize;
        for entry in &rec.input_history_entries[entry_index..end] {
            write_input_history_entry(&mut body, entry)?;
        }
        entry_index = end;
    }
    Ok(body)
}

fn write_input_history_entry<W: Write>(
    writer: &mut W,
    entry: &ReplayInputHistoryEntry,
) -> Result<()> {
    write_u32(writer, entry.fields)?;
    for value in entry.view_angles {
        write_f32(writer, value)?;
    }
    write_i32(writer, entry.render_tick_count)?;
    write_f32(writer, entry.render_tick_fraction)?;
    write_i32(writer, entry.player_tick_count)?;
    write_f32(writer, entry.player_tick_fraction)?;
    write_f32(writer, entry.cl_interp_fraction)?;
    write_i32(writer, entry.sv_interp0_src_tick)?;
    write_i32(writer, entry.sv_interp0_dst_tick)?;
    write_f32(writer, entry.sv_interp0_fraction)?;
    write_i32(writer, entry.sv_interp1_src_tick)?;
    write_i32(writer, entry.sv_interp1_dst_tick)?;
    write_f32(writer, entry.sv_interp1_fraction)?;
    write_i32(writer, entry.player_interp_src_tick)?;
    write_i32(writer, entry.player_interp_dst_tick)?;
    write_f32(writer, entry.player_interp_fraction)?;
    write_i32(writer, entry.frame_number)?;
    write_i32(writer, entry.target_ent_index)?;
    for values in [
        entry.shoot_position,
        entry.target_head_pos_check,
        entry.target_abs_pos_check,
        entry.target_abs_ang_check,
    ] {
        for value in values {
            write_f32(writer, value)?;
        }
    }
    Ok(())
}

#[cfg(test)]
fn build_command_frame_section_v1(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.command_frames.len() * COMMAND_FRAME_BYTE_SIZE);
    for frame in &rec.command_frames {
        write_command_frame(&mut body, frame)?;
    }
    Ok(body)
}

fn build_snapshot_section_v2(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::new();
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.origin[0].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.origin[1].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.origin[2].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.velocity[0].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.velocity[1].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.velocity[2].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.angles[0].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.angles[1].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.angles[2].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.entity_flags),
    )?;
    write_delta_u8_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.move_type),
    )?;
    write_delta_u64_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.buttons),
    )?;
    write_delta_u64_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.buttons1),
    )?;
    write_delta_u64_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.buttons2),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.duck_amount.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.duck_speed.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.ladder_normal[0].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.ladder_normal[1].to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.ladder_normal[2].to_bits()),
    )?;
    write_delta_u8_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.ducked),
    )?;
    write_delta_u8_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.ducking),
    )?;
    write_delta_u8_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.desires_duck),
    )?;
    write_delta_u8_column(
        &mut body,
        snapshot_chain(rec).map(|snapshot| snapshot.actual_move_type),
    )?;
    Ok(body)
}

fn build_command_frame_section_v2(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::new();
    write_delta_u32_column(
        &mut body,
        rec.command_frames
            .iter()
            .map(|frame| frame.forward_move.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames
            .iter()
            .map(|frame| frame.left_move.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames
            .iter()
            .map(|frame| frame.up_move.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.pitch.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.yaw.to_bits()),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.roll.to_bits()),
    )?;
    write_delta_u64_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.buttons),
    )?;
    write_delta_u64_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.buttons1),
    )?;
    write_delta_u64_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.buttons2),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.mouse_dx as u32),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.mouse_dy as u32),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames
            .iter()
            .map(|frame| frame.weapon_select as u32),
    )?;
    write_delta_u32_column(
        &mut body,
        rec.command_frames.iter().map(|frame| frame.fields),
    )?;
    write_delta_u8_column(
        &mut body,
        rec.command_frames
            .iter()
            .map(|frame| frame.left_hand_desired),
    )?;
    Ok(body)
}

fn snapshot_chain(rec: &Cs2Rec) -> impl Iterator<Item = &MovementSnapshot> {
    rec.ticks
        .first()
        .map(|tick| &tick.pre)
        .into_iter()
        .chain(rec.ticks.iter().map(|tick| &tick.post))
}

fn write_delta_u32_column<W, I>(writer: &mut W, values: I) -> Result<()>
where
    W: Write,
    I: IntoIterator<Item = u32>,
{
    let mut values = values.into_iter();
    let Some(mut previous) = values.next() else {
        return Ok(());
    };
    write_u32(writer, previous)?;
    for current in values {
        let delta = current.wrapping_sub(previous) as i32;
        write_uleb128_u32(writer, zigzag_i32(delta))?;
        previous = current;
    }
    Ok(())
}

fn write_delta_u64_column<W, I>(writer: &mut W, values: I) -> Result<()>
where
    W: Write,
    I: IntoIterator<Item = u64>,
{
    let mut values = values.into_iter();
    let Some(mut previous) = values.next() else {
        return Ok(());
    };
    write_u64(writer, previous)?;
    for current in values {
        let delta = current.wrapping_sub(previous) as i64;
        write_uleb128_u64(writer, zigzag_i64(delta))?;
        previous = current;
    }
    Ok(())
}

fn write_delta_u8_column<W, I>(writer: &mut W, values: I) -> Result<()>
where
    W: Write,
    I: IntoIterator<Item = u8>,
{
    let mut values = values.into_iter();
    let Some(mut previous) = values.next() else {
        return Ok(());
    };
    write_u8(writer, previous)?;
    for current in values {
        let delta = current.wrapping_sub(previous) as i8;
        write_uleb128_u32(writer, zigzag_i8(delta) as u32)?;
        previous = current;
    }
    Ok(())
}

fn zigzag_i8(value: i8) -> u8 {
    ((value as u8) << 1) ^ ((value >> 7) as u8)
}

fn zigzag_i32(value: i32) -> u32 {
    ((value as u32) << 1) ^ ((value >> 31) as u32)
}

fn zigzag_i64(value: i64) -> u64 {
    ((value as u64) << 1) ^ ((value >> 63) as u64)
}

fn write_uleb128_u32<W: Write>(writer: &mut W, mut value: u32) -> Result<()> {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value != 0 {
            byte |= 0x80;
        }
        write_u8(writer, byte)?;
        if value == 0 {
            return Ok(());
        }
    }
}

fn write_uleb128_u64<W: Write>(writer: &mut W, mut value: u64) -> Result<()> {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value != 0 {
            byte |= 0x80;
        }
        write_u8(writer, byte)?;
        if value == 0 {
            return Ok(());
        }
    }
}

fn build_movement_extra_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.movement_extras.len() * MOVEMENT_EXTRA_BYTE_SIZE);
    for extra in &rec.movement_extras {
        write_movement_extra(&mut body, extra)?;
    }
    Ok(body)
}

#[cfg(test)]
fn build_body(rec: &Cs2Rec, metadata_json: &[u8]) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(expected_body_len(
        rec.ticks.len(),
        rec.subticks.len(),
        rec.projectiles.len(),
        metadata_json.len(),
    )?);
    if let Some(first) = rec.ticks.first() {
        write_snapshot(&mut body, &first.pre)?;
        for tick in &rec.ticks {
            write_snapshot(&mut body, &tick.post)?;
        }
    }
    for tick in &rec.ticks {
        write_i32(&mut body, tick.weapon_def_index)?;
        write_u32(&mut body, tick.num_subtick)?;
    }
    for projectile in &rec.projectiles {
        write_projectile(&mut body, projectile)?;
    }
    body.write_all(metadata_json)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    for subtick in &rec.subticks {
        write_subtick(&mut body, subtick)?;
    }
    Ok(body)
}

fn read_body(
    body: &[u8],
    tick_count: usize,
    projectile_count: usize,
    metadata_json_len: usize,
    subtick_count: usize,
    max_subticks_per_tick: u32,
) -> Result<(
    Vec<ReplayTick>,
    Vec<ReplayProjectile>,
    HighFidelityMetadata,
    Vec<SubtickMove>,
)> {
    let mut reader = Cursor::new(body);
    let snapshot_count = snapshot_count(tick_count)?;
    let mut snapshots = reserved_vec(snapshot_count, "snapshot count")?;
    for _ in 0..snapshot_count {
        snapshots.push(read_snapshot(&mut reader)?);
    }

    let mut ticks = reserved_vec(tick_count, "tick count")?;
    let mut expected_subticks = 0_usize;
    for i in 0..tick_count {
        let weapon_def_index = read_i32(&mut reader)?;
        let num_subtick = read_u32(&mut reader)?;
        accumulate_tick_subticks(
            &mut expected_subticks,
            num_subtick,
            i,
            max_subticks_per_tick,
        )?;
        ticks.push(ReplayTick {
            pre: snapshots[i].clone(),
            post: snapshots[i + 1].clone(),
            weapon_def_index,
            num_subtick,
        });
    }

    if expected_subticks != subtick_count {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {subtick_count}"
        )));
    }

    let mut projectiles = reserved_vec(projectile_count, "projectile count")?;
    for _ in 0..projectile_count {
        projectiles.push(read_projectile(&mut reader)?);
    }

    let high_fidelity = if metadata_json_len > 0 {
        let mut metadata_json = zeroed_vec(metadata_json_len, "metadata JSON")?;
        reader
            .read_exact(&mut metadata_json)
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        serde_json::from_slice(&metadata_json)
            .map_err(|e| Error::InvalidRec(format!("invalid high_fidelity metadata JSON: {e}")))?
    } else {
        HighFidelityMetadata::default()
    };

    let mut subticks = reserved_vec(subtick_count, "subtick count")?;
    for _ in 0..subtick_count {
        subticks.push(read_subtick(&mut reader)?);
    }
    if reader.position() != body.len() as u64 {
        return Err(Error::InvalidRec("trailing bytes in .dtr body".to_string()));
    }
    Ok((ticks, projectiles, high_fidelity, subticks))
}

type V7Sections = (
    Vec<ReplayTick>,
    Vec<ReplayProjectile>,
    HighFidelityMetadata,
    Vec<SubtickMove>,
    Vec<ReplayCommandFrame>,
    Vec<ReplayMovementExtra>,
    Vec<ReplayInputHistoryTick>,
    Vec<ReplayInputHistoryEntry>,
);

fn read_sectioned_body<R: Read>(
    reader: &mut ReadBudget<R>,
    format_version: u32,
    tick_count: usize,
    subtick_count: usize,
    projectile_count: usize,
    metadata_json_len: usize,
    limits: DtrReadLimits,
) -> Result<V7Sections> {
    let section_count_raw = read_u32(reader)?;
    enforce_count_limit("section count", section_count_raw, limits.max_section_count)?;
    let section_count = section_count_raw as usize;
    let snapshot_count = snapshot_count(tick_count)?;

    let mut snapshots: Option<Vec<MovementSnapshot>> = None;
    let mut tick_metadata: Option<Vec<(i32, u32)>> = None;
    let mut subticks: Option<Vec<SubtickMove>> = None;
    let mut projectiles: Option<Vec<ReplayProjectile>> = None;
    let mut high_fidelity = HighFidelityMetadata::default();
    let mut saw_high_fidelity = false;
    let mut saw_command_frames = false;
    let mut saw_movement_extras = false;
    let mut command_frames = Vec::new();
    let mut movement_extras = Vec::new();
    let mut saw_input_history = false;
    let mut input_history_ticks = Vec::new();
    let mut input_history_entries = Vec::new();
    let mut total_compressed = 0_u64;
    let mut total_decoded = 0_u64;

    for section_index in 0..section_count {
        let header = read_section_header(reader)?;
        enforce_byte_limit(
            "compressed section",
            header.compressed_len,
            limits.max_compressed_section_bytes,
        )?;
        enforce_byte_limit(
            "decoded section",
            header.uncompressed_len,
            limits.max_decoded_section_bytes,
        )?;
        total_compressed = add_budgeted_bytes(
            "total compressed sections",
            total_compressed,
            header.compressed_len,
            limits.max_total_compressed_bytes,
        )?;
        total_decoded = add_budgeted_bytes(
            "total decoded sections",
            total_decoded,
            header.uncompressed_len,
            limits.max_total_decoded_bytes,
        )?;
        let remaining_section_count = section_count - section_index - 1;
        let remaining_header_bytes = (remaining_section_count as u64)
            .checked_mul(SECTION_HEADER_BYTE_SIZE)
            .ok_or_else(|| Error::InvalidRec("remaining section headers too large".to_string()))?;
        let required_remaining = header
            .compressed_len
            .checked_add(remaining_header_bytes)
            .ok_or_else(|| Error::InvalidRec("section payload length overflow".to_string()))?;
        reader.ensure_available(required_remaining, "section payload and remaining headers")?;

        if !is_known_section(header.section_id) {
            let compressed_len = checked_len(header.compressed_len, "section compressed length")?;
            skip_exact_bounded(reader, compressed_len, "unknown section")?;
            continue;
        }

        match header.section_id {
            SECTION_SNAPSHOTS => {
                reject_duplicate(snapshots.is_some(), "snapshots")?;
                require_versioned_section_header_shape(
                    "snapshots",
                    format_version,
                    header.section_version,
                    header.element_count,
                    snapshot_count,
                    header.uncompressed_len,
                    checked_product(snapshot_count, SNAPSHOT_BYTE_SIZE, "snapshot section")?,
                )?;
            }
            SECTION_TICK_METADATA => {
                reject_duplicate(tick_metadata.is_some(), "tick metadata")?;
                require_section_header_shape(
                    "tick metadata",
                    header.section_version,
                    header.element_count,
                    tick_count,
                    header.uncompressed_len,
                    checked_product(tick_count, TICK_METADATA_BYTE_SIZE, "tick metadata section")?,
                )?;
            }
            SECTION_SUBTICKS => {
                reject_duplicate(subticks.is_some(), "subticks")?;
                require_section_header_shape(
                    "subticks",
                    header.section_version,
                    header.element_count,
                    subtick_count,
                    header.uncompressed_len,
                    checked_product(subtick_count, SUBTICK_BYTE_SIZE, "subtick section")?,
                )?;
            }
            SECTION_PROJECTILES => {
                reject_duplicate(projectiles.is_some(), "projectiles")?;
                require_section_header_shape(
                    "projectiles",
                    header.section_version,
                    header.element_count,
                    projectile_count,
                    header.uncompressed_len,
                    checked_product(projectile_count, PROJECTILE_BYTE_SIZE, "projectile section")?,
                )?;
            }
            SECTION_HIGH_FIDELITY_JSON => {
                reject_duplicate(saw_high_fidelity, "high fidelity metadata")?;
                require_section_header_shape(
                    "high fidelity metadata",
                    header.section_version,
                    header.element_count,
                    if metadata_json_len == 0 { 0 } else { 1 },
                    header.uncompressed_len,
                    metadata_json_len,
                )?;
            }
            SECTION_COMMAND_FRAMES => {
                reject_duplicate(saw_command_frames, "command frames")?;
                require_versioned_section_header_shape(
                    "command frames",
                    format_version,
                    header.section_version,
                    header.element_count,
                    tick_count,
                    header.uncompressed_len,
                    checked_product(tick_count, COMMAND_FRAME_BYTE_SIZE, "command frame section")?,
                )?;
            }
            SECTION_MOVEMENT_EXTRAS => {
                reject_duplicate(saw_movement_extras, "movement extras")?;
                require_section_header_shape(
                    "movement extras",
                    header.section_version,
                    header.element_count,
                    tick_count,
                    header.uncompressed_len,
                    checked_product(
                        tick_count,
                        MOVEMENT_EXTRA_BYTE_SIZE,
                        "movement extra section",
                    )?,
                )?;
            }
            SECTION_INPUT_HISTORY => {
                reject_duplicate(saw_input_history, "input history")?;
                require_input_history_section_header_shape(
                    header.section_version,
                    header.element_count,
                    tick_count,
                    header.uncompressed_len,
                )?;
            }
            _ => unreachable!(),
        }

        if !matches!(header.codec, CODEC_NONE | CODEC_BROTLI) {
            return Err(Error::InvalidRec(format!(
                "unsupported section codec {}",
                header.codec
            )));
        }
        if header.codec == CODEC_NONE && header.compressed_len != header.uncompressed_len {
            return Err(Error::InvalidRec(format!(
                "uncompressed section compressed length {} != decoded length {}",
                header.compressed_len, header.uncompressed_len
            )));
        }
        let compressed_len = checked_len(header.compressed_len, "section compressed length")?;
        let decoded_len = checked_len(header.uncompressed_len, "section uncompressed length")?;
        let compressed = read_exact_vec_bounded(reader, compressed_len, "compressed section")?;
        let body = decode_section_body(compressed, header.codec, decoded_len)?;

        match header.section_id {
            SECTION_SNAPSHOTS => {
                snapshots = Some(read_snapshots_from_section(
                    &body,
                    snapshot_count,
                    header.section_version,
                )?);
            }
            SECTION_TICK_METADATA => {
                tick_metadata = Some(read_tick_metadata_from_section(&body, tick_count)?);
            }
            SECTION_SUBTICKS => {
                subticks = Some(read_subticks_from_section(&body, subtick_count)?);
            }
            SECTION_PROJECTILES => {
                projectiles = Some(read_projectiles_from_section(&body, projectile_count)?);
            }
            SECTION_HIGH_FIDELITY_JSON => {
                high_fidelity = serde_json::from_slice(&body).map_err(|e| {
                    Error::InvalidRec(format!("invalid high_fidelity metadata JSON: {e}"))
                })?;
                saw_high_fidelity = true;
            }
            SECTION_COMMAND_FRAMES => {
                command_frames =
                    read_command_frames_from_section(&body, tick_count, header.section_version)?;
                saw_command_frames = true;
            }
            SECTION_MOVEMENT_EXTRAS => {
                movement_extras = read_movement_extras_from_section(&body, tick_count)?;
                saw_movement_extras = true;
            }
            SECTION_INPUT_HISTORY => {
                (input_history_ticks, input_history_entries) =
                    read_input_history_from_section(&body, tick_count)?;
                saw_input_history = true;
            }
            _ => unreachable!(),
        }
    }

    let snapshots = snapshots
        .ok_or_else(|| Error::InvalidRec("missing required section snapshots".to_string()))?;
    let tick_metadata = tick_metadata
        .ok_or_else(|| Error::InvalidRec("missing required section tick metadata".to_string()))?;
    let subticks = subticks
        .ok_or_else(|| Error::InvalidRec("missing required section subticks".to_string()))?;
    let projectiles = projectiles.unwrap_or_default();
    if projectiles.len() != projectile_count {
        return Err(Error::InvalidRec(format!(
            "projectile section count {} != header projectile count {projectile_count}",
            projectiles.len()
        )));
    }
    if metadata_json_len > 0 && !saw_high_fidelity {
        return Err(Error::InvalidRec(
            "missing high fidelity metadata section".to_string(),
        ));
    }
    if format_version >= 9 && !saw_input_history {
        return Err(Error::InvalidRec(
            "missing required section input history".to_string(),
        ));
    }
    if !saw_input_history {
        input_history_ticks = vec![ReplayInputHistoryTick::default(); tick_count];
    }

    let mut expected_subticks = 0_usize;
    let mut ticks = reserved_vec(tick_count, "tick count")?;
    for i in 0..tick_count {
        let (weapon_def_index, num_subtick) = tick_metadata[i];
        accumulate_tick_subticks(
            &mut expected_subticks,
            num_subtick,
            i,
            limits.max_subticks_per_tick,
        )?;
        ticks.push(ReplayTick {
            pre: snapshots[i].clone(),
            post: snapshots[i + 1].clone(),
            weapon_def_index,
            num_subtick,
        });
    }
    if expected_subticks != subtick_count {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {subtick_count}"
        )));
    }

    Ok((
        ticks,
        projectiles,
        high_fidelity,
        subticks,
        command_frames,
        movement_extras,
        input_history_ticks,
        input_history_entries,
    ))
}

struct SectionHeader {
    section_id: u32,
    section_version: u32,
    codec: u8,
    element_count: u32,
    uncompressed_len: u64,
    compressed_len: u64,
}

fn read_section_header<R: Read>(reader: &mut R) -> Result<SectionHeader> {
    let section_id = read_u32(reader)?;
    let section_version = read_u32(reader)?;
    let codec = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if pad != [0, 0, 0] {
        return Err(Error::InvalidRec(
            "section header padding must be zero".to_string(),
        ));
    }
    let flags = read_u32(reader)?;
    if flags != 0 {
        return Err(Error::InvalidRec(format!(
            "unsupported section flags 0x{flags:08x}"
        )));
    }
    let element_count = read_u32(reader)?;
    let uncompressed_len = read_u64(reader)?;
    let compressed_len = read_u64(reader)?;
    Ok(SectionHeader {
        section_id,
        section_version,
        codec,
        element_count,
        uncompressed_len,
        compressed_len,
    })
}

fn is_known_section(section_id: u32) -> bool {
    matches!(
        section_id,
        SECTION_SNAPSHOTS
            | SECTION_TICK_METADATA
            | SECTION_PROJECTILES
            | SECTION_HIGH_FIDELITY_JSON
            | SECTION_SUBTICKS
            | SECTION_COMMAND_FRAMES
            | SECTION_MOVEMENT_EXTRAS
            | SECTION_INPUT_HISTORY
    )
}

fn decode_section_body(compressed: Vec<u8>, codec: u8, expected_len: usize) -> Result<Vec<u8>> {
    match codec {
        CODEC_NONE => {
            if compressed.len() != expected_len {
                return Err(Error::InvalidRec(format!(
                    "uncompressed section length {} != expected {expected_len}",
                    compressed.len()
                )));
            }
            Ok(compressed)
        }
        CODEC_BROTLI => decompress_body(&compressed, expected_len),
        _ => Err(Error::InvalidRec(format!(
            "unsupported section codec {codec}"
        ))),
    }
}

fn require_section_header_shape(
    name: &str,
    section_version: u32,
    element_count: u32,
    expected_elements: usize,
    byte_len: u64,
    expected_byte_len: usize,
) -> Result<()> {
    if section_version != SECTION_VERSION_V1 {
        return Err(Error::InvalidRec(format!(
            "unsupported {name} section version {section_version}"
        )));
    }
    if u64::from(element_count) != expected_elements as u64 {
        return Err(Error::InvalidRec(format!(
            "{name} element count {element_count} != expected {expected_elements}"
        )));
    }
    if byte_len != expected_byte_len as u64 {
        return Err(Error::InvalidRec(format!(
            "{name} byte length {byte_len} != expected {expected_byte_len}"
        )));
    }
    Ok(())
}

fn require_versioned_section_header_shape(
    name: &str,
    format_version: u32,
    section_version: u32,
    element_count: u32,
    expected_elements: usize,
    byte_len: u64,
    v1_byte_len: usize,
) -> Result<()> {
    if u64::from(element_count) != expected_elements as u64 {
        return Err(Error::InvalidRec(format!(
            "{name} element count {element_count} != expected {expected_elements}"
        )));
    }
    match (format_version, section_version) {
        (7, SECTION_VERSION_V1) => {
            if byte_len != v1_byte_len as u64 {
                return Err(Error::InvalidRec(format!(
                    "{name} byte length {byte_len} != expected {v1_byte_len}"
                )));
            }
        }
        (8 | 9, SECTION_VERSION_V2) => {
            if expected_elements == 0 && byte_len != 0 {
                return Err(Error::InvalidRec(format!(
                    "empty {name} section has non-zero byte length {byte_len}"
                )));
            }
        }
        _ => {
            return Err(Error::InvalidRec(format!(
                "unsupported {name} section version {section_version} for .dtr v{format_version}"
            )));
        }
    }
    Ok(())
}

fn require_input_history_section_header_shape(
    section_version: u32,
    element_count: u32,
    tick_count: usize,
    byte_len: u64,
) -> Result<()> {
    if section_version != SECTION_VERSION_V1 {
        return Err(Error::InvalidRec(format!(
            "unsupported input history section version {section_version}"
        )));
    }
    if u64::from(element_count) != tick_count as u64 {
        return Err(Error::InvalidRec(format!(
            "input history element count {element_count} != expected {tick_count}"
        )));
    }
    let minimum = checked_product(
        tick_count,
        INPUT_HISTORY_TICK_BYTE_SIZE,
        "input history tick descriptors",
    )? as u64;
    let maximum_entries = tick_count
        .checked_mul(MAX_INPUT_HISTORY_PER_TICK as usize)
        .ok_or_else(|| Error::InvalidRec("input history entry limit overflow".to_string()))?;
    let maximum = minimum
        .checked_add(checked_product(
            maximum_entries,
            INPUT_HISTORY_ENTRY_BYTE_SIZE,
            "input history entries",
        )? as u64)
        .ok_or_else(|| Error::InvalidRec("input history section limit overflow".to_string()))?;
    if !(minimum..=maximum).contains(&byte_len) {
        return Err(Error::InvalidRec(format!(
            "input history byte length {byte_len} is outside {minimum}..={maximum}"
        )));
    }
    Ok(())
}

fn reject_duplicate(duplicate: bool, name: &str) -> Result<()> {
    if duplicate {
        return Err(Error::InvalidRec(format!("duplicate section {name}")));
    }
    Ok(())
}

fn read_snapshots_from_section(
    body: &[u8],
    count: usize,
    section_version: u32,
) -> Result<Vec<MovementSnapshot>> {
    match section_version {
        SECTION_VERSION_V1 => read_snapshots_from_section_v1(body, count),
        SECTION_VERSION_V2 => read_snapshots_from_section_v2(body, count),
        _ => Err(Error::InvalidRec(format!(
            "unsupported snapshots section version {section_version}"
        ))),
    }
}

fn read_snapshots_from_section_v1(body: &[u8], count: usize) -> Result<Vec<MovementSnapshot>> {
    let mut reader = Cursor::new(body);
    let mut snapshots = reserved_vec(count, "snapshot section elements")?;
    for _ in 0..count {
        snapshots.push(read_snapshot(&mut reader)?);
    }
    require_no_trailing(&reader, body, "snapshots")?;
    Ok(snapshots)
}

fn read_tick_metadata_from_section(body: &[u8], count: usize) -> Result<Vec<(i32, u32)>> {
    let mut reader = Cursor::new(body);
    let mut metadata = reserved_vec(count, "tick metadata section elements")?;
    for _ in 0..count {
        metadata.push((read_i32(&mut reader)?, read_u32(&mut reader)?));
    }
    require_no_trailing(&reader, body, "tick metadata")?;
    Ok(metadata)
}

fn read_projectiles_from_section(body: &[u8], count: usize) -> Result<Vec<ReplayProjectile>> {
    let mut reader = Cursor::new(body);
    let mut projectiles = reserved_vec(count, "projectile section elements")?;
    for _ in 0..count {
        projectiles.push(read_projectile(&mut reader)?);
    }
    require_no_trailing(&reader, body, "projectiles")?;
    Ok(projectiles)
}

fn read_subticks_from_section(body: &[u8], count: usize) -> Result<Vec<SubtickMove>> {
    let mut reader = Cursor::new(body);
    let mut subticks = reserved_vec(count, "subtick section elements")?;
    for _ in 0..count {
        subticks.push(read_subtick(&mut reader)?);
    }
    require_no_trailing(&reader, body, "subticks")?;
    Ok(subticks)
}

fn read_command_frames_from_section(
    body: &[u8],
    count: usize,
    section_version: u32,
) -> Result<Vec<ReplayCommandFrame>> {
    match section_version {
        SECTION_VERSION_V1 => read_command_frames_from_section_v1(body, count),
        SECTION_VERSION_V2 => read_command_frames_from_section_v2(body, count),
        _ => Err(Error::InvalidRec(format!(
            "unsupported command frames section version {section_version}"
        ))),
    }
}

fn read_command_frames_from_section_v1(
    body: &[u8],
    count: usize,
) -> Result<Vec<ReplayCommandFrame>> {
    let mut reader = Cursor::new(body);
    let mut frames = reserved_vec(count, "command frame section elements")?;
    for _ in 0..count {
        frames.push(read_command_frame(&mut reader)?);
    }
    require_no_trailing(&reader, body, "command frames")?;
    Ok(frames)
}

fn read_snapshots_from_section_v2(body: &[u8], count: usize) -> Result<Vec<MovementSnapshot>> {
    let mut reader = Cursor::new(body);
    let mut snapshots = reserved_vec(count, "snapshot section elements")?;
    snapshots.resize_with(count, MovementSnapshot::default);
    read_delta_u32_column(&mut reader, count, "snapshot origin x", |index, value| {
        snapshots[index].origin[0] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot origin y", |index, value| {
        snapshots[index].origin[1] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot origin z", |index, value| {
        snapshots[index].origin[2] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot velocity x", |index, value| {
        snapshots[index].velocity[0] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot velocity y", |index, value| {
        snapshots[index].velocity[1] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot velocity z", |index, value| {
        snapshots[index].velocity[2] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot pitch", |index, value| {
        snapshots[index].angles[0] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot yaw", |index, value| {
        snapshots[index].angles[1] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot roll", |index, value| {
        snapshots[index].angles[2] = f32::from_bits(value);
    })?;
    read_delta_u32_column(
        &mut reader,
        count,
        "snapshot entity flags",
        |index, value| snapshots[index].entity_flags = value,
    )?;
    read_delta_u8_column(&mut reader, count, "snapshot move type", |index, value| {
        snapshots[index].move_type = value;
    })?;
    read_delta_u64_column(&mut reader, count, "snapshot buttons", |index, value| {
        snapshots[index].buttons = value;
    })?;
    read_delta_u64_column(&mut reader, count, "snapshot buttons1", |index, value| {
        snapshots[index].buttons1 = value;
    })?;
    read_delta_u64_column(&mut reader, count, "snapshot buttons2", |index, value| {
        snapshots[index].buttons2 = value;
    })?;
    read_delta_u32_column(
        &mut reader,
        count,
        "snapshot duck amount",
        |index, value| snapshots[index].duck_amount = f32::from_bits(value),
    )?;
    read_delta_u32_column(&mut reader, count, "snapshot duck speed", |index, value| {
        snapshots[index].duck_speed = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot ladder x", |index, value| {
        snapshots[index].ladder_normal[0] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot ladder y", |index, value| {
        snapshots[index].ladder_normal[1] = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "snapshot ladder z", |index, value| {
        snapshots[index].ladder_normal[2] = f32::from_bits(value);
    })?;
    read_delta_u8_column(&mut reader, count, "snapshot ducked", |index, value| {
        snapshots[index].ducked = value;
    })?;
    read_delta_u8_column(&mut reader, count, "snapshot ducking", |index, value| {
        snapshots[index].ducking = value;
    })?;
    read_delta_u8_column(
        &mut reader,
        count,
        "snapshot desires duck",
        |index, value| snapshots[index].desires_duck = value,
    )?;
    read_delta_u8_column(
        &mut reader,
        count,
        "snapshot actual move type",
        |index, value| snapshots[index].actual_move_type = value,
    )?;
    require_no_trailing(&reader, body, "snapshots")?;
    Ok(snapshots)
}

fn read_command_frames_from_section_v2(
    body: &[u8],
    count: usize,
) -> Result<Vec<ReplayCommandFrame>> {
    let mut reader = Cursor::new(body);
    let mut frames = reserved_vec(count, "command frame section elements")?;
    frames.resize_with(count, ReplayCommandFrame::default);
    read_delta_u32_column(
        &mut reader,
        count,
        "command forward move",
        |index, value| {
            frames[index].forward_move = f32::from_bits(value);
        },
    )?;
    read_delta_u32_column(&mut reader, count, "command left move", |index, value| {
        frames[index].left_move = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "command up move", |index, value| {
        frames[index].up_move = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "command pitch", |index, value| {
        frames[index].pitch = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "command yaw", |index, value| {
        frames[index].yaw = f32::from_bits(value);
    })?;
    read_delta_u32_column(&mut reader, count, "command roll", |index, value| {
        frames[index].roll = f32::from_bits(value);
    })?;
    read_delta_u64_column(&mut reader, count, "command buttons", |index, value| {
        frames[index].buttons = value;
    })?;
    read_delta_u64_column(&mut reader, count, "command buttons1", |index, value| {
        frames[index].buttons1 = value;
    })?;
    read_delta_u64_column(&mut reader, count, "command buttons2", |index, value| {
        frames[index].buttons2 = value;
    })?;
    read_delta_u32_column(&mut reader, count, "command mouse dx", |index, value| {
        frames[index].mouse_dx = value as i32;
    })?;
    read_delta_u32_column(&mut reader, count, "command mouse dy", |index, value| {
        frames[index].mouse_dy = value as i32;
    })?;
    read_delta_u32_column(
        &mut reader,
        count,
        "command weapon select",
        |index, value| frames[index].weapon_select = value as i32,
    )?;
    read_delta_u32_column(&mut reader, count, "command fields", |index, value| {
        frames[index].fields = value;
    })?;
    read_delta_u8_column(
        &mut reader,
        count,
        "command left hand desired",
        |index, value| frames[index].left_hand_desired = value,
    )?;
    require_no_trailing(&reader, body, "command frames")?;
    Ok(frames)
}

fn read_delta_u32_column<R, F>(reader: &mut R, count: usize, name: &str, mut set: F) -> Result<()>
where
    R: Read,
    F: FnMut(usize, u32),
{
    if count == 0 {
        return Ok(());
    }
    let mut previous = read_u32(reader)?;
    set(0, previous);
    for index in 1..count {
        let delta = unzigzag_u32(read_uleb128_u32(reader, name)?) as u32;
        let current = previous.wrapping_add(delta);
        set(index, current);
        previous = current;
    }
    Ok(())
}

fn read_delta_u64_column<R, F>(reader: &mut R, count: usize, name: &str, mut set: F) -> Result<()>
where
    R: Read,
    F: FnMut(usize, u64),
{
    if count == 0 {
        return Ok(());
    }
    let mut previous = read_u64(reader)?;
    set(0, previous);
    for index in 1..count {
        let delta = unzigzag_u64(read_uleb128_u64(reader, name)?) as u64;
        let current = previous.wrapping_add(delta);
        set(index, current);
        previous = current;
    }
    Ok(())
}

fn read_delta_u8_column<R, F>(reader: &mut R, count: usize, name: &str, mut set: F) -> Result<()>
where
    R: Read,
    F: FnMut(usize, u8),
{
    if count == 0 {
        return Ok(());
    }
    let mut previous = read_u8(reader)?;
    set(0, previous);
    for index in 1..count {
        let encoded = read_uleb128_u32(reader, name)?;
        if encoded > u8::MAX as u32 {
            return Err(Error::InvalidRec(format!(
                "{name} delta {encoded} exceeds u8"
            )));
        }
        let delta = unzigzag_u8(encoded as u8) as u8;
        let current = previous.wrapping_add(delta);
        set(index, current);
        previous = current;
    }
    Ok(())
}

fn unzigzag_u8(value: u8) -> i8 {
    ((value >> 1) as i8) ^ -((value & 1) as i8)
}

fn unzigzag_u32(value: u32) -> i32 {
    ((value >> 1) as i32) ^ -((value & 1) as i32)
}

fn unzigzag_u64(value: u64) -> i64 {
    ((value >> 1) as i64) ^ -((value & 1) as i64)
}

fn read_uleb128_u32<R: Read>(reader: &mut R, name: &str) -> Result<u32> {
    let mut value = 0_u32;
    for index in 0..5 {
        let byte = read_u8(reader)?;
        if index == 4 && byte & 0xf0 != 0 {
            return Err(Error::InvalidRec(format!("{name} varint overflows u32")));
        }
        value |= u32::from(byte & 0x7f) << (index * 7);
        if byte & 0x80 == 0 {
            if index > 0 && byte == 0 {
                return Err(Error::InvalidRec(format!("{name} varint is not canonical")));
            }
            return Ok(value);
        }
    }
    Err(Error::InvalidRec(format!("{name} varint is too long")))
}

fn read_uleb128_u64<R: Read>(reader: &mut R, name: &str) -> Result<u64> {
    let mut value = 0_u64;
    for index in 0..10 {
        let byte = read_u8(reader)?;
        if index == 9 && byte & 0xfe != 0 {
            return Err(Error::InvalidRec(format!("{name} varint overflows u64")));
        }
        value |= u64::from(byte & 0x7f) << (index * 7);
        if byte & 0x80 == 0 {
            if index > 0 && byte == 0 {
                return Err(Error::InvalidRec(format!("{name} varint is not canonical")));
            }
            return Ok(value);
        }
    }
    Err(Error::InvalidRec(format!("{name} varint is too long")))
}

fn read_movement_extras_from_section(
    body: &[u8],
    count: usize,
) -> Result<Vec<ReplayMovementExtra>> {
    let mut reader = Cursor::new(body);
    let mut extras = reserved_vec(count, "movement extra section elements")?;
    for _ in 0..count {
        extras.push(read_movement_extra(&mut reader)?);
    }
    require_no_trailing(&reader, body, "movement extras")?;
    Ok(extras)
}

fn read_input_history_from_section(
    body: &[u8],
    tick_count: usize,
) -> Result<(Vec<ReplayInputHistoryTick>, Vec<ReplayInputHistoryEntry>)> {
    let mut reader = Cursor::new(body);
    let mut ticks = reserved_vec(tick_count, "input history tick descriptors")?;
    let mut entries = Vec::new();
    for tick_index in 0..tick_count {
        let tick = ReplayInputHistoryTick {
            source_client_tick: read_i32(&mut reader)?,
            attack1_start_history_index: read_i32(&mut reader)?,
            attack2_start_history_index: read_i32(&mut reader)?,
            num_entries: read_u32(&mut reader)?,
        };
        if tick.num_entries > MAX_INPUT_HISTORY_PER_TICK {
            return Err(Error::InvalidRec(format!(
                "input history tick {tick_index} count {} exceeds limit {MAX_INPUT_HISTORY_PER_TICK}",
                tick.num_entries
            )));
        }
        entries
            .try_reserve(tick.num_entries as usize)
            .map_err(|error| {
                Error::InvalidRec(format!("unable to allocate input history: {error}"))
            })?;
        for _ in 0..tick.num_entries {
            entries.push(read_input_history_entry(&mut reader)?);
        }
        ticks.push(tick);
    }
    require_no_trailing(&reader, body, "input history")?;
    Ok((ticks, entries))
}

fn read_input_history_entry<R: Read>(reader: &mut R) -> Result<ReplayInputHistoryEntry> {
    let fields = read_u32(reader)?;
    let view_angles = [read_f32(reader)?, read_f32(reader)?, read_f32(reader)?];
    let render_tick_count = read_i32(reader)?;
    let render_tick_fraction = read_f32(reader)?;
    let player_tick_count = read_i32(reader)?;
    let player_tick_fraction = read_f32(reader)?;
    let cl_interp_fraction = read_f32(reader)?;
    let sv_interp0_src_tick = read_i32(reader)?;
    let sv_interp0_dst_tick = read_i32(reader)?;
    let sv_interp0_fraction = read_f32(reader)?;
    let sv_interp1_src_tick = read_i32(reader)?;
    let sv_interp1_dst_tick = read_i32(reader)?;
    let sv_interp1_fraction = read_f32(reader)?;
    let player_interp_src_tick = read_i32(reader)?;
    let player_interp_dst_tick = read_i32(reader)?;
    let player_interp_fraction = read_f32(reader)?;
    let frame_number = read_i32(reader)?;
    let target_ent_index = read_i32(reader)?;
    let mut read_vec3 =
        || -> Result<[f32; 3]> { Ok([read_f32(reader)?, read_f32(reader)?, read_f32(reader)?]) };
    Ok(ReplayInputHistoryEntry {
        fields,
        view_angles,
        render_tick_count,
        render_tick_fraction,
        player_tick_count,
        player_tick_fraction,
        cl_interp_fraction,
        sv_interp0_src_tick,
        sv_interp0_dst_tick,
        sv_interp0_fraction,
        sv_interp1_src_tick,
        sv_interp1_dst_tick,
        sv_interp1_fraction,
        player_interp_src_tick,
        player_interp_dst_tick,
        player_interp_fraction,
        frame_number,
        target_ent_index,
        shoot_position: read_vec3()?,
        target_head_pos_check: read_vec3()?,
        target_abs_pos_check: read_vec3()?,
        target_abs_ang_check: read_vec3()?,
    })
}

fn require_no_trailing(reader: &Cursor<&[u8]>, body: &[u8], name: &str) -> Result<()> {
    if reader.position() != body.len() as u64 {
        return Err(Error::InvalidRec(format!(
            "trailing bytes in {name} section"
        )));
    }
    Ok(())
}

fn read_exact_vec_bounded<R: Read>(
    reader: &mut ReadBudget<R>,
    len: usize,
    name: &str,
) -> Result<Vec<u8>> {
    reader.ensure_available(len as u64, name)?;
    let mut bytes = zeroed_vec(len, name)?;
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(bytes)
}

fn skip_exact_bounded<R: Read>(reader: &mut ReadBudget<R>, len: usize, name: &str) -> Result<()> {
    reader.ensure_available(len as u64, name)?;
    let mut remaining = len;
    let mut buffer = [0_u8; 4096];
    while remaining > 0 {
        let take = remaining.min(buffer.len());
        reader
            .read_exact(&mut buffer[..take])
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        remaining -= take;
    }
    Ok(())
}

fn metadata_json_bytes(metadata: &HighFidelityMetadata) -> Result<Vec<u8>> {
    serde_json::to_vec(metadata)
        .map_err(|e| Error::InvalidRec(format!("invalid high_fidelity metadata: {e}")))
}

fn optional_metadata_json_bytes(metadata: &HighFidelityMetadata) -> Result<Vec<u8>> {
    if metadata.is_empty() {
        Ok(Vec::new())
    } else {
        metadata_json_bytes(metadata)
    }
}

fn compress_body(body: &[u8]) -> Result<Vec<u8>> {
    let mut compressed = Vec::new();
    {
        let mut compressor = brotli::CompressorWriter::new(
            &mut compressed,
            BROTLI_BUFFER_SIZE,
            BROTLI_QUALITY,
            BROTLI_LGWIN,
        );
        compressor
            .write_all(body)
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        compressor
            .flush()
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
    }
    Ok(compressed)
}

fn decompress_body(compressed: &[u8], expected_len: usize) -> Result<Vec<u8>> {
    let mut decompressor = brotli::Decompressor::new(compressed, BROTLI_BUFFER_SIZE);
    let mut body = zeroed_vec(expected_len, "decoded body")?;
    decompressor
        .read_exact(&mut body)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    let mut probe = [0_u8; 1];
    match decompressor.read(&mut probe) {
        Ok(0) => {}
        Ok(_) => {
            return Err(Error::InvalidRec(format!(
                "decompressed body exceeds expected length {expected_len}"
            )));
        }
        Err(error) => return Err(Error::InvalidRec(error.to_string())),
    }
    Ok(body)
}

fn expected_body_len(
    tick_count: usize,
    subtick_count: usize,
    projectile_count: usize,
    metadata_json_len: usize,
) -> Result<usize> {
    let snapshot_count = snapshot_count(tick_count)?;
    let snapshot_bytes = snapshot_count
        .checked_mul(SNAPSHOT_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("snapshot body too large".to_string()))?;
    let tick_bytes = tick_count
        .checked_mul(TICK_METADATA_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("tick body too large".to_string()))?;
    let subtick_bytes = subtick_count
        .checked_mul(SUBTICK_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("subtick body too large".to_string()))?;
    let projectile_bytes = projectile_count
        .checked_mul(PROJECTILE_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("projectile body too large".to_string()))?;
    snapshot_bytes
        .checked_add(tick_bytes)
        .and_then(|value| value.checked_add(projectile_bytes))
        .and_then(|value| value.checked_add(metadata_json_len))
        .and_then(|value| value.checked_add(subtick_bytes))
        .ok_or_else(|| Error::InvalidRec("body too large".to_string()))
}

fn checked_len(value: u64, name: &str) -> Result<usize> {
    usize::try_from(value).map_err(|_| Error::InvalidRec(format!("{name} too large: {value}")))
}

fn checked_u32_count(name: &str, value: usize) -> Result<u32> {
    u32::try_from(value).map_err(|_| Error::InvalidRec(format!("{name} too large: {value}")))
}

fn validate_header_counts(
    tick_count: u32,
    subtick_count: u32,
    projectile_count: u32,
    metadata_json_len: u32,
    limits: DtrReadLimits,
) -> Result<()> {
    enforce_count_limit("tick count", tick_count, limits.max_tick_count)?;
    enforce_count_limit("subtick count", subtick_count, limits.max_subtick_count)?;
    enforce_count_limit(
        "projectile count",
        projectile_count,
        limits.max_projectile_count,
    )?;
    enforce_count_limit(
        "metadata JSON length",
        metadata_json_len,
        limits.max_metadata_json_bytes,
    )?;
    let maximum_for_ticks = u64::from(tick_count)
        .checked_mul(u64::from(limits.max_subticks_per_tick))
        .ok_or_else(|| Error::InvalidRec("subtick count limit overflow".to_string()))?;
    if u64::from(subtick_count) > maximum_for_ticks {
        return Err(Error::InvalidRec(format!(
            "subtick count {subtick_count} exceeds {tick_count} ticks x {} subticks per tick",
            limits.max_subticks_per_tick
        )));
    }
    Ok(())
}

fn enforce_count_limit(name: &str, value: u32, limit: u32) -> Result<()> {
    if value > limit {
        return Err(Error::InvalidRec(format!(
            "{name} {value} exceeds limit {limit}"
        )));
    }
    Ok(())
}

fn enforce_byte_limit(name: &str, value: u64, limit: u64) -> Result<()> {
    if value > limit {
        return Err(Error::InvalidRec(format!(
            "{name} length {value} exceeds limit {limit}"
        )));
    }
    Ok(())
}

fn add_budgeted_bytes(name: &str, current: u64, value: u64, limit: u64) -> Result<u64> {
    let total = current
        .checked_add(value)
        .ok_or_else(|| Error::InvalidRec(format!("{name} length overflow")))?;
    enforce_byte_limit(name, total, limit)?;
    Ok(total)
}

fn snapshot_count(tick_count: usize) -> Result<usize> {
    if tick_count == 0 {
        Ok(0)
    } else {
        tick_count
            .checked_add(1)
            .ok_or_else(|| Error::InvalidRec("snapshot count too large".to_string()))
    }
}

fn checked_product(count: usize, width: usize, name: &str) -> Result<usize> {
    count
        .checked_mul(width)
        .ok_or_else(|| Error::InvalidRec(format!("{name} too large")))
}

fn accumulate_tick_subticks(
    total: &mut usize,
    count: u32,
    tick_index: usize,
    max_per_tick: u32,
) -> Result<()> {
    if count > max_per_tick {
        return Err(Error::InvalidRec(format!(
            "tick {tick_index} subtick count {count} exceeds limit {max_per_tick}"
        )));
    }
    *total = total
        .checked_add(count as usize)
        .ok_or_else(|| Error::InvalidRec("tick subtick sum overflow".to_string()))?;
    Ok(())
}

fn reserved_vec<T>(len: usize, name: &str) -> Result<Vec<T>> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(len)
        .map_err(|error| Error::InvalidRec(format!("unable to allocate {name} {len}: {error}")))?;
    Ok(values)
}

fn zeroed_vec(len: usize, name: &str) -> Result<Vec<u8>> {
    let mut bytes = reserved_vec(len, name)?;
    bytes.resize(len, 0);
    Ok(bytes)
}

#[cfg(test)]
fn write_snapshot<W: Write>(writer: &mut W, snapshot: &MovementSnapshot) -> Result<()> {
    for value in snapshot.origin {
        write_f32(writer, value)?;
    }
    for value in snapshot.velocity {
        write_f32(writer, value)?;
    }
    for value in snapshot.angles {
        write_f32(writer, value)?;
    }
    write_u32(writer, snapshot.entity_flags)?;
    write_u8(writer, snapshot.move_type)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u64(writer, snapshot.buttons)?;
    write_u64(writer, snapshot.buttons1)?;
    write_u64(writer, snapshot.buttons2)?;
    write_f32(writer, snapshot.duck_amount)?;
    write_f32(writer, snapshot.duck_speed)?;
    for value in snapshot.ladder_normal {
        write_f32(writer, value)?;
    }
    write_u8(writer, snapshot.ducked)?;
    write_u8(writer, snapshot.ducking)?;
    write_u8(writer, snapshot.desires_duck)?;
    write_u8(writer, snapshot.actual_move_type)?;
    Ok(())
}

fn read_snapshot<R: Read>(reader: &mut R) -> Result<MovementSnapshot> {
    let mut origin = [0.0_f32; 3];
    let mut velocity = [0.0_f32; 3];
    let mut angles = [0.0_f32; 3];
    for value in &mut origin {
        *value = read_f32(reader)?;
    }
    for value in &mut velocity {
        *value = read_f32(reader)?;
    }
    for value in &mut angles {
        *value = read_f32(reader)?;
    }
    let entity_flags = read_u32(reader)?;
    let move_type = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if pad != [0, 0, 0] {
        return Err(Error::InvalidRec(
            "snapshot padding must be zero".to_string(),
        ));
    }
    let buttons = read_u64(reader)?;
    let buttons1 = read_u64(reader)?;
    let buttons2 = read_u64(reader)?;
    let duck_amount = read_f32(reader)?;
    let duck_speed = read_f32(reader)?;
    let mut ladder_normal = [0.0_f32; 3];
    for value in &mut ladder_normal {
        *value = read_f32(reader)?;
    }
    let ducked = read_u8(reader)?;
    let ducking = read_u8(reader)?;
    let desires_duck = read_u8(reader)?;
    let actual_move_type = read_u8(reader)?;
    Ok(MovementSnapshot {
        origin,
        velocity,
        angles,
        entity_flags,
        move_type,
        buttons,
        buttons1,
        buttons2,
        duck_amount,
        duck_speed,
        ladder_normal,
        ducked,
        ducking,
        desires_duck,
        actual_move_type,
    })
}

fn write_subtick<W: Write>(writer: &mut W, subtick: &SubtickMove) -> Result<()> {
    write_f32(writer, subtick.when)?;
    write_u32(writer, subtick.button)?;
    write_f32(writer, subtick.pressed)?;
    write_f32(writer, subtick.analog_forward)?;
    write_f32(writer, subtick.analog_left)?;
    write_f32(writer, subtick.pitch_delta)?;
    write_f32(writer, subtick.yaw_delta)?;
    Ok(())
}

fn read_subtick<R: Read>(reader: &mut R) -> Result<SubtickMove> {
    Ok(SubtickMove {
        when: read_f32(reader)?,
        button: read_u32(reader)?,
        pressed: read_f32(reader)?,
        analog_forward: read_f32(reader)?,
        analog_left: read_f32(reader)?,
        pitch_delta: read_f32(reader)?,
        yaw_delta: read_f32(reader)?,
    })
}

#[cfg(test)]
fn write_command_frame<W: Write>(writer: &mut W, frame: &ReplayCommandFrame) -> Result<()> {
    write_f32(writer, frame.forward_move)?;
    write_f32(writer, frame.left_move)?;
    write_f32(writer, frame.up_move)?;
    write_f32(writer, frame.pitch)?;
    write_f32(writer, frame.yaw)?;
    write_f32(writer, frame.roll)?;
    write_u64(writer, frame.buttons)?;
    write_u64(writer, frame.buttons1)?;
    write_u64(writer, frame.buttons2)?;
    write_i32(writer, frame.mouse_dx)?;
    write_i32(writer, frame.mouse_dy)?;
    write_i32(writer, frame.weapon_select)?;
    write_u32(writer, frame.fields)?;
    write_u8(writer, frame.left_hand_desired)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(())
}

fn read_command_frame<R: Read>(reader: &mut R) -> Result<ReplayCommandFrame> {
    let forward_move = read_f32(reader)?;
    let left_move = read_f32(reader)?;
    let up_move = read_f32(reader)?;
    let pitch = read_f32(reader)?;
    let yaw = read_f32(reader)?;
    let roll = read_f32(reader)?;
    let buttons = read_u64(reader)?;
    let buttons1 = read_u64(reader)?;
    let buttons2 = read_u64(reader)?;
    let mouse_dx = read_i32(reader)?;
    let mouse_dy = read_i32(reader)?;
    let weapon_select = read_i32(reader)?;
    let fields = read_u32(reader)?;
    let left_hand_desired = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if pad != [0, 0, 0] {
        return Err(Error::InvalidRec(
            "command frame padding must be zero".to_string(),
        ));
    }
    Ok(ReplayCommandFrame {
        forward_move,
        left_move,
        up_move,
        pitch,
        yaw,
        roll,
        buttons,
        buttons1,
        buttons2,
        mouse_dx,
        mouse_dy,
        weapon_select,
        fields,
        left_hand_desired,
    })
}

fn write_movement_extra<W: Write>(writer: &mut W, extra: &ReplayMovementExtra) -> Result<()> {
    write_u32(writer, extra.fields)?;
    write_f32(writer, extra.jump_pressed_time)?;
    write_f32(writer, extra.last_duck_time)?;
    write_i32(writer, extra.last_actual_jump_press_tick)?;
    write_f32(writer, extra.last_actual_jump_press_frac)?;
    write_i32(writer, extra.last_usable_jump_press_tick)?;
    write_f32(writer, extra.last_usable_jump_press_frac)?;
    write_i32(writer, extra.last_landed_tick)?;
    write_f32(writer, extra.last_landed_frac)?;
    for value in extra.last_landed_velocity {
        write_f32(writer, value)?;
    }
    Ok(())
}

fn read_movement_extra<R: Read>(reader: &mut R) -> Result<ReplayMovementExtra> {
    let fields = read_u32(reader)?;
    let jump_pressed_time = read_f32(reader)?;
    let last_duck_time = read_f32(reader)?;
    let last_actual_jump_press_tick = read_i32(reader)?;
    let last_actual_jump_press_frac = read_f32(reader)?;
    let last_usable_jump_press_tick = read_i32(reader)?;
    let last_usable_jump_press_frac = read_f32(reader)?;
    let last_landed_tick = read_i32(reader)?;
    let last_landed_frac = read_f32(reader)?;
    let mut last_landed_velocity = [0.0_f32; 3];
    for value in &mut last_landed_velocity {
        *value = read_f32(reader)?;
    }
    Ok(ReplayMovementExtra {
        fields,
        jump_pressed_time,
        last_duck_time,
        last_actual_jump_press_tick,
        last_actual_jump_press_frac,
        last_usable_jump_press_tick,
        last_usable_jump_press_frac,
        last_landed_tick,
        last_landed_frac,
        last_landed_velocity,
    })
}

fn write_projectile<W: Write>(writer: &mut W, projectile: &ReplayProjectile) -> Result<()> {
    write_u32(writer, projectile.tick_index)?;
    write_i32(writer, projectile.weapon_def_index)?;
    write_u8(writer, projectile.kind.to_u8())?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    for value in projectile.initial_position {
        write_f32(writer, value)?;
    }
    for value in projectile.initial_velocity {
        write_f32(writer, value)?;
    }
    for value in projectile.detonation_position {
        write_f32(writer, value)?;
    }
    Ok(())
}

fn read_projectile<R: Read>(reader: &mut R) -> Result<ReplayProjectile> {
    let tick_index = read_u32(reader)?;
    let weapon_def_index = read_i32(reader)?;
    let kind_raw = read_u8(reader)?;
    if kind_raw > ProjectileKind::Decoy.to_u8() {
        return Err(Error::InvalidRec(format!(
            "unsupported projectile kind {kind_raw}"
        )));
    }
    let kind = ProjectileKind::from_u8(kind_raw);
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if pad != [0, 0, 0] {
        return Err(Error::InvalidRec(
            "projectile padding must be zero".to_string(),
        ));
    }
    let mut initial_position = [0.0_f32; 3];
    let mut initial_velocity = [0.0_f32; 3];
    let mut detonation_position = [0.0_f32; 3];
    for value in &mut initial_position {
        *value = read_f32(reader)?;
    }
    for value in &mut initial_velocity {
        *value = read_f32(reader)?;
    }
    for value in &mut detonation_position {
        *value = read_f32(reader)?;
    }
    Ok(ReplayProjectile {
        tick_index,
        kind,
        weapon_def_index,
        initial_position,
        initial_velocity,
        detonation_position,
    })
}

fn snapshot_bit_eq(a: &MovementSnapshot, b: &MovementSnapshot) -> bool {
    f32_array_bit_eq(&a.origin, &b.origin)
        && f32_array_bit_eq(&a.velocity, &b.velocity)
        && f32_array_bit_eq(&a.angles, &b.angles)
        && a.entity_flags == b.entity_flags
        && a.move_type == b.move_type
        && a.buttons == b.buttons
        && a.buttons1 == b.buttons1
        && a.buttons2 == b.buttons2
        && a.duck_amount.to_bits() == b.duck_amount.to_bits()
        && a.duck_speed.to_bits() == b.duck_speed.to_bits()
        && f32_array_bit_eq(&a.ladder_normal, &b.ladder_normal)
        && a.ducked == b.ducked
        && a.ducking == b.ducking
        && a.desires_duck == b.desires_duck
        && a.actual_move_type == b.actual_move_type
}

fn f32_array_bit_eq<const N: usize>(a: &[f32; N], b: &[f32; N]) -> bool {
    a.iter()
        .zip(b.iter())
        .all(|(lhs, rhs)| lhs.to_bits() == rhs.to_bits())
}

fn write_string<W: Write>(writer: &mut W, value: &str) -> Result<()> {
    let bytes = value.as_bytes();
    if bytes.len() > u16::MAX as usize {
        return Err(Error::InvalidRec(format!(
            "string too long: {} bytes",
            bytes.len()
        )));
    }
    write_u16(writer, bytes.len() as u16)?;
    writer
        .write_all(bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn read_bounded_string<R: Read>(reader: &mut ReadBudget<R>, name: &str) -> Result<String> {
    let len = read_u16(reader)? as usize;
    let bytes = read_exact_vec_bounded(reader, len, name)?;
    String::from_utf8(bytes).map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u8<W: Write>(writer: &mut W, value: u8) -> Result<()> {
    writer
        .write_all(&[value])
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u16<W: Write>(writer: &mut W, value: u16) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u32<W: Write>(writer: &mut W, value: u32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_i32<W: Write>(writer: &mut W, value: i32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u64<W: Write>(writer: &mut W, value: u64) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_f32<W: Write>(writer: &mut W, value: f32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn read_u8<R: Read>(reader: &mut R) -> Result<u8> {
    let mut bytes = [0_u8; 1];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(bytes[0])
}

fn read_u16<R: Read>(reader: &mut R) -> Result<u16> {
    let mut bytes = [0_u8; 2];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u16::from_le_bytes(bytes))
}

fn read_u32<R: Read>(reader: &mut R) -> Result<u32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u32::from_le_bytes(bytes))
}

fn read_i32<R: Read>(reader: &mut R) -> Result<i32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(i32::from_le_bytes(bytes))
}

fn read_u64<R: Read>(reader: &mut R) -> Result<u64> {
    let mut bytes = [0_u8; 8];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u64::from_le_bytes(bytes))
}

fn read_f32<R: Read>(reader: &mut R) -> Result<f32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(f32::from_le_bytes(bytes))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{
        Cs2RecHeader, HighFidelityMetadata, MovementSnapshot, ProjectileKind, ReplayCommandFrame,
        ReplayMovementExtra, ReplayProjectile, ReplayTick, SubtickMove, COMMAND_FIELD_FORWARD_MOVE,
        COMMAND_FIELD_LEFT_MOVE, COMMAND_FIELD_MOUSE, COMMAND_FIELD_VIEW_ANGLES,
    };

    #[test]
    fn rec_writer_rejects_mismatched_subtick_count() {
        let mut rec = sample_rec();
        rec.ticks[0].num_subtick = 2;
        let mut bytes = Vec::new();
        let err = write_rec(&mut bytes, &rec).unwrap_err();
        assert!(err
            .to_string()
            .contains("tick subtick sum 2 != header subtick count 1"));
    }

    #[test]
    fn rec_writer_rejects_more_than_36_subticks_on_one_tick() {
        let mut rec = sample_rec();
        rec.ticks[0].num_subtick = 37;
        rec.subticks = vec![rec.subticks[0].clone(); 37];
        let mut bytes = Vec::new();

        let err = write_rec(&mut bytes, &rec).unwrap_err();

        assert!(err
            .to_string()
            .contains("tick 0 subtick count 37 exceeds limit 36"));
    }

    #[test]
    fn rec_writer_rejects_non_finite_replay_values() {
        let mut rec = sample_rec();
        rec.ticks[0].pre.origin[0] = f32::NAN;
        let mut bytes = Vec::new();

        let err = write_rec(&mut bytes, &rec).unwrap_err();

        assert!(err.to_string().contains("contains a non-finite float"));
        assert!(bytes.is_empty());
    }

    #[test]
    fn rec_writer_rejects_unknown_command_fields() {
        let mut rec = sample_rec();
        rec.command_frames[0].fields |= 1 << 31;
        let mut bytes = Vec::new();

        let err = write_rec(&mut bytes, &rec).unwrap_err();

        assert!(err.to_string().contains("unknown fields 0x80000000"));
        assert!(bytes.is_empty());
    }

    #[test]
    fn rec_reader_rejects_mismatched_subtick_count() {
        let rec = sample_rec();
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let mut body = build_body(&rec, &metadata_json).unwrap();
        let metadata_offset = SNAPSHOT_BYTE_SIZE * (rec.ticks.len() + 1);
        body[metadata_offset + 4..metadata_offset + 8].copy_from_slice(&2_u32.to_le_bytes());
        let bytes = test_file_bytes(
            &body,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            metadata_json.len(),
            CODEC_BROTLI,
            None,
        );
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err
            .to_string()
            .contains("tick subtick sum 2 != header subtick count 1"));
    }

    #[test]
    fn rec_roundtrip_is_bit_stable() {
        let mut rec = sample_rec();
        rec.header.play_start_tick_index = 1;

        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        assert_eq!(&bytes[0..8], MAGIC);
        assert_eq!(
            u32::from_le_bytes(bytes[8..12].try_into().unwrap()),
            DTR_FORMAT_VERSION
        );
        let parsed = read_rec(&mut &bytes[..]).unwrap();
        assert_eq!(parsed.header, rec.header);
        assert_eq!(parsed.ticks.len(), rec.ticks.len());
        assert_eq!(parsed.projectiles, rec.projectiles);
        assert_eq!(parsed.high_fidelity, rec.high_fidelity);
        assert_eq!(parsed.subticks.len(), rec.subticks.len());
        assert_eq!(parsed.command_frames, rec.command_frames);
        assert_eq!(parsed.movement_extras, rec.movement_extras);
        for (parsed_tick, tick) in parsed.ticks.iter().zip(rec.ticks.iter()) {
            assert!(snapshot_bit_eq(&parsed_tick.pre, &tick.pre));
            assert!(snapshot_bit_eq(&parsed_tick.post, &tick.post));
            assert_eq!(parsed_tick.weapon_def_index, tick.weapon_def_index);
            assert_eq!(parsed_tick.num_subtick, tick.num_subtick);
        }
        assert!(subtick_bit_eq(&parsed.subticks[0], &rec.subticks[0]));
        assert_eq!(
            parsed.ticks[0].pre.origin[1].to_bits(),
            (-0.0_f32).to_bits()
        );
        assert_eq!(
            parsed.subticks[0].analog_forward.to_bits(),
            (-0.0_f32).to_bits()
        );
    }

    #[test]
    fn rec_reader_rejects_top_level_trailing_bytes() {
        let mut bytes = encoded_sample_rec();
        bytes.push(0x42);

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("trailing bytes after top-level .dtr payload"));
    }

    #[test]
    fn rec_reader_rejects_non_finite_replay_values() {
        let mut rec = sample_rec();
        rec.ticks[0].pre.origin[0] = f32::INFINITY;
        let bytes = encoded_v7_rec(&rec);

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err.to_string().contains("contains a non-finite float"));
    }

    #[test]
    fn rec_reader_keeps_v7_section_layout_readable() {
        let rec = sample_rec();
        let bytes = encoded_v7_rec(&rec);

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(parsed.header.version, 7);
        assert_eq!(parsed.projectiles, rec.projectiles);
        assert_eq!(parsed.high_fidelity, rec.high_fidelity);
        assert_eq!(parsed.command_frames, rec.command_frames);
        for (parsed_tick, tick) in parsed.ticks.iter().zip(&rec.ticks) {
            assert!(snapshot_bit_eq(&parsed_tick.pre, &tick.pre));
            assert!(snapshot_bit_eq(&parsed_tick.post, &tick.post));
        }
    }

    #[test]
    fn delta_columns_roundtrip_wrapping_bit_patterns() {
        let values32 = [0, u32::MAX, 0x8000_0000, 1, 1, 0x7fff_ffff];
        let values64 = [0, u64::MAX, 0x8000_0000_0000_0000, 1, 1];
        let values8 = [0, u8::MAX, 0x80, 1, 1];
        let mut body = Vec::new();
        write_delta_u32_column(&mut body, values32).unwrap();
        write_delta_u64_column(&mut body, values64).unwrap();
        write_delta_u8_column(&mut body, values8).unwrap();
        let mut reader = Cursor::new(body.as_slice());
        let mut decoded32 = [0; 6];
        let mut decoded64 = [0; 5];
        let mut decoded8 = [0; 5];
        read_delta_u32_column(&mut reader, decoded32.len(), "u32", |index, value| {
            decoded32[index] = value;
        })
        .unwrap();
        read_delta_u64_column(&mut reader, decoded64.len(), "u64", |index, value| {
            decoded64[index] = value;
        })
        .unwrap();
        read_delta_u8_column(&mut reader, decoded8.len(), "u8", |index, value| {
            decoded8[index] = value;
        })
        .unwrap();

        assert_eq!(decoded32, values32);
        assert_eq!(decoded64, values64);
        assert_eq!(decoded8, values8);
        assert_eq!(reader.position(), body.len() as u64);
    }

    #[test]
    fn delta_column_reader_rejects_overflowing_varint() {
        let bytes = [0x80, 0x80, 0x80, 0x80, 0x10];
        let error = read_uleb128_u32(&mut &bytes[..], "snapshot origin x").unwrap_err();

        assert!(error
            .to_string()
            .contains("snapshot origin x varint overflows u32"));
    }

    #[test]
    fn rec_v7_reader_skips_unknown_sections() {
        let mut bytes = encoded_sample_rec();
        let section_count_offset = v7_section_count_offset(&bytes);
        let section_count = u32::from_le_bytes(
            bytes[section_count_offset..section_count_offset + 4]
                .try_into()
                .unwrap(),
        );
        bytes[section_count_offset..section_count_offset + 4]
            .copy_from_slice(&(section_count + 1).to_le_bytes());
        let mut unknown = Vec::new();
        write_u32(&mut unknown, 999).unwrap();
        write_u32(&mut unknown, SECTION_VERSION_V1).unwrap();
        write_u8(&mut unknown, CODEC_NONE).unwrap();
        unknown.write_all(&[0, 0, 0]).unwrap();
        write_u32(&mut unknown, 0).unwrap();
        write_u32(&mut unknown, 1).unwrap();
        write_u64(&mut unknown, 4).unwrap();
        write_u64(&mut unknown, 4).unwrap();
        unknown.write_all(&[1, 2, 3, 4]).unwrap();
        let insert_at = section_count_offset + 4;
        bytes.splice(insert_at..insert_at, unknown);

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(
            parsed.command_frames.len(),
            sample_rec().command_frames.len()
        );
    }

    #[test]
    fn rec_v7_reader_rejects_missing_required_section() {
        let mut bytes = encoded_sample_rec();
        let section_count_offset = v7_section_count_offset(&bytes);
        bytes[section_count_offset..section_count_offset + 4].copy_from_slice(&0_u32.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("missing required section snapshots"));
    }

    #[test]
    fn rec_v7_reader_rejects_duplicate_command_frames_section() {
        let rec = sample_rec();
        let mut bytes = encoded_sample_rec();
        append_duplicate_v7_section(
            &mut bytes,
            SECTION_COMMAND_FRAMES,
            rec.command_frames.len(),
            &build_command_frame_section_v1(&rec).unwrap(),
        );

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err.to_string().contains("duplicate section command frames"));
    }

    #[test]
    fn rec_v7_reader_rejects_duplicate_movement_extras_section() {
        let mut rec = sample_rec();
        rec.movement_extras = vec![ReplayMovementExtra::default(); rec.ticks.len()];
        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        append_duplicate_v7_section(
            &mut bytes,
            SECTION_MOVEMENT_EXTRAS,
            rec.movement_extras.len(),
            &build_movement_extra_section(&rec).unwrap(),
        );

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("duplicate section movement extras"));
    }

    #[test]
    fn rec_reader_defaults_v4_play_start_to_zero() {
        let rec = sample_rec();
        let body = build_body(&rec, &[]).unwrap();
        let bytes = test_file_bytes_for_version(
            &body,
            4,
            0,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            0,
            CODEC_BROTLI,
            None,
        );

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(parsed.header.version, 4);
        assert_eq!(parsed.header.play_start_tick_index, 0);
        assert_eq!(parsed.projectiles.len(), rec.projectiles.len());
    }

    #[test]
    fn rec_reader_keeps_v3_legacy_layout_readable() {
        let mut rec = sample_rec();
        rec.projectiles.clear();
        let body = build_body(&rec, &[]).unwrap();
        let bytes = test_file_bytes_for_version(
            &body,
            3,
            0,
            rec.ticks.len(),
            rec.subticks.len(),
            0,
            0,
            CODEC_BROTLI,
            None,
        );

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(parsed.header.version, 3);
        assert_eq!(parsed.header.play_start_tick_index, 0);
        assert_eq!(parsed.ticks.len(), rec.ticks.len());
        assert_eq!(parsed.subticks.len(), rec.subticks.len());
        assert!(parsed.projectiles.is_empty());
    }

    #[test]
    fn rec_reader_keeps_v5_legacy_layout_readable() {
        let mut rec = sample_rec();
        rec.header.play_start_tick_index = 1;
        let body = build_body(&rec, &[]).unwrap();
        let bytes = test_file_bytes_for_version(
            &body,
            5,
            rec.header.play_start_tick_index,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            0,
            CODEC_BROTLI,
            None,
        );

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(parsed.header.version, 5);
        assert_eq!(parsed.header.play_start_tick_index, 1);
        assert_eq!(parsed.ticks.len(), rec.ticks.len());
        assert_eq!(parsed.subticks.len(), rec.subticks.len());
        assert_eq!(parsed.projectiles.len(), rec.projectiles.len());
    }

    #[test]
    fn rec_reader_rejects_out_of_range_play_start() {
        let mut bytes = encoded_sample_rec();
        let offset = play_start_offset();
        bytes[offset..offset + 4].copy_from_slice(&99_u32.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("play_start_tick_index 99 out of range for 2 ticks"));
    }

    #[test]
    fn rec_v5_header_does_not_change_body_length() {
        let rec = sample_rec();
        let body = build_body(&rec, &metadata_json_bytes(&rec.high_fidelity).unwrap()).unwrap();
        let bytes = encoded_legacy_sample_rec();
        let (_, body_len_offset, _) = rec_header_offsets(&bytes);
        let body_uncompressed_len = u64::from_le_bytes(
            bytes[body_len_offset..body_len_offset + 8]
                .try_into()
                .unwrap(),
        );

        assert_eq!(body_uncompressed_len, body.len() as u64);
        assert_eq!(
            body.len(),
            expected_body_len(
                rec.ticks.len(),
                rec.subticks.len(),
                rec.projectiles.len(),
                metadata_json_bytes(&rec.high_fidelity).unwrap().len(),
            )
            .unwrap()
        );
    }

    #[test]
    fn rec_writer_rejects_discontinuous_snapshot_chain() {
        let mut rec = sample_rec();
        rec.ticks[1].pre.origin[0] += 1.0;
        let mut bytes = Vec::new();
        let err = write_rec(&mut bytes, &rec).unwrap_err();
        assert!(err
            .to_string()
            .contains("discontinuous snapshot chain between ticks 0 and 1"));
    }

    #[test]
    fn rec_writer_rejects_count_overflow() {
        assert_eq!(
            checked_u32_count("tick count", u32::MAX as usize).unwrap(),
            u32::MAX
        );

        let Ok(too_large) = usize::try_from(u64::from(u32::MAX) + 1) else {
            return;
        };
        let err = checked_u32_count("tick count", too_large).unwrap_err();
        assert!(err.to_string().contains("tick count too large"));
    }

    #[test]
    fn rec_reader_rejects_bad_magic() {
        let err = read_rec(&mut &b"BAD"[..]).unwrap_err();
        assert!(err.to_string().contains("failed to fill whole buffer"));
    }

    #[test]
    fn rec_reader_rejects_unsupported_version() {
        let mut bytes = encoded_sample_rec();
        bytes[8..12].copy_from_slice(&2_u32.to_le_bytes());
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("unsupported version 2"));
    }

    #[test]
    fn rec_reader_rejects_unknown_codec() {
        let mut bytes = encoded_legacy_sample_rec();
        let (codec_offset, _, _) = rec_header_offsets(&bytes);
        bytes[codec_offset] = 9;
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("unsupported codec 9"));
    }

    #[test]
    fn rec_reader_rejects_body_length_mismatch() {
        let mut bytes = encoded_legacy_sample_rec();
        let (_, body_len_offset, _) = rec_header_offsets(&bytes);
        bytes[body_len_offset..body_len_offset + 8].copy_from_slice(&999_u64.to_le_bytes());
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("body length 999 != expected"));
    }

    #[test]
    fn default_read_limits_leave_room_for_long_local_replays() {
        let limits = DtrReadLimits::default();

        assert_eq!(limits.max_file_bytes, 64 * MIB);
        assert_eq!(limits.max_section_count, 32);
        assert_eq!(limits.max_compressed_section_bytes, 48 * MIB);
        assert_eq!(limits.max_total_compressed_bytes, 64 * MIB);
        assert_eq!(limits.max_decoded_section_bytes, 48 * MIB);
        assert_eq!(limits.max_total_decoded_bytes, 64 * MIB);
        assert_eq!(limits.max_tick_count, 32_768);
        assert_eq!(limits.max_subtick_count, 32_768 * 36);
        assert_eq!(limits.max_subticks_per_tick, 36);
        assert_eq!(limits.max_projectile_count, 4_096);
        assert_eq!(limits.max_metadata_json_bytes, 8 * 1024 * 1024);
    }

    #[test]
    fn rec_reader_rejects_header_count_over_limit() {
        let mut bytes = encoded_sample_rec();
        let tick_offset = tick_count_offset();
        bytes[tick_offset..tick_offset + 4].copy_from_slice(&3_u32.to_le_bytes());
        let limits = DtrReadLimits {
            max_tick_count: 2,
            ..DtrReadLimits::default()
        };

        let err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();

        assert!(err.to_string().contains("tick count 3 exceeds limit 2"));
    }

    #[test]
    fn rec_reader_rejects_header_subticks_impossible_for_ticks() {
        let mut bytes = encoded_sample_rec();
        let subtick_offset = tick_count_offset() + 4;
        bytes[subtick_offset..subtick_offset + 4].copy_from_slice(&73_u32.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("subtick count 73 exceeds 2 ticks x 36 subticks per tick"));
    }

    #[test]
    fn legacy_reader_rejects_more_than_36_subticks_on_one_tick() {
        let mut rec = sample_rec();
        rec.ticks[0].num_subtick = 37;
        rec.subticks = vec![rec.subticks[0].clone(); 37];
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let body = build_body(&rec, &metadata_json).unwrap();
        let bytes = test_file_bytes(
            &body,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            metadata_json.len(),
            CODEC_BROTLI,
            None,
        );

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("tick 0 subtick count 37 exceeds limit 36"));
    }

    #[test]
    fn legacy_reader_enforces_decoded_and_compressed_budgets() {
        let bytes = encoded_legacy_sample_rec();
        let limits = DtrReadLimits {
            max_decoded_section_bytes: 1,
            ..DtrReadLimits::default()
        };
        let decoded_err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();
        assert!(decoded_err.to_string().contains("decoded body length"));

        let limits = DtrReadLimits {
            max_compressed_section_bytes: 1,
            ..DtrReadLimits::default()
        };
        let compressed_err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();
        assert!(compressed_err
            .to_string()
            .contains("compressed body length"));
    }

    #[test]
    fn v7_reader_enforces_section_count_and_total_budgets() {
        let bytes = encoded_sample_rec();
        let limits = DtrReadLimits {
            max_section_count: 1,
            ..DtrReadLimits::default()
        };
        let count_err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();
        assert!(count_err.to_string().contains("section count"));

        let limits = DtrReadLimits {
            max_total_decoded_bytes: 1,
            ..DtrReadLimits::default()
        };
        let decoded_err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();
        assert!(decoded_err
            .to_string()
            .contains("total decoded sections length"));

        let limits = DtrReadLimits {
            max_total_compressed_bytes: 1,
            ..DtrReadLimits::default()
        };
        let compressed_err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();
        assert!(compressed_err
            .to_string()
            .contains("total compressed sections length"));
    }

    #[test]
    fn v7_unknown_section_claims_count_toward_decoded_budget() {
        let mut bytes = encoded_sample_rec();
        insert_unknown_v7_section(&mut bytes, 100, &[]);
        let limits = DtrReadLimits {
            max_decoded_section_bytes: 99,
            ..DtrReadLimits::default()
        };

        let err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();

        assert!(err
            .to_string()
            .contains("decoded section length 100 exceeds limit 99"));
    }

    #[test]
    fn v8_variable_section_length_is_enforced_during_decode() {
        let mut bytes = encoded_sample_rec();
        let first_section = v7_section_count_offset(&bytes) + 4;
        let uncompressed_len_offset = first_section + 20;
        bytes[uncompressed_len_offset..uncompressed_len_offset + 8]
            .copy_from_slice(&1_u64.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("decompressed body exceeds expected length 1"));
    }

    #[test]
    fn v7_uncompressed_section_length_mismatch_is_rejected_before_payload_read() {
        let mut bytes = encoded_sample_rec();
        let first_section = v7_section_count_offset(&bytes) + 4;
        bytes[first_section + 8] = CODEC_NONE;

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("uncompressed section compressed length"));
    }

    #[test]
    fn bounded_brotli_rejects_output_beyond_declared_length() {
        let compressed = compress_body(&[1, 2]).unwrap();

        let err = decompress_body(&compressed, 1).unwrap_err();

        assert!(err
            .to_string()
            .contains("decompressed body exceeds expected length 1"));
    }

    #[test]
    fn file_reader_rejects_oversized_file_from_metadata() {
        let bytes = encoded_sample_rec();
        let path = write_temp_dtr("file-limit", &bytes);
        let limits = DtrReadLimits {
            max_file_bytes: bytes.len() as u64 - 1,
            ..DtrReadLimits::default()
        };

        let result = read_rec_file_with_limits(&path, limits);
        let _ = std::fs::remove_file(&path);
        let err = result.unwrap_err();

        assert!(err.to_string().contains("file length"));
    }

    #[test]
    fn file_reader_rejects_truncated_declared_string_before_allocation() {
        let bytes = truncated_map_header();
        let path = write_temp_dtr("truncated-map", &bytes);

        let result = read_rec_file(&path);
        let _ = std::fs::remove_file(&path);
        let err = result.unwrap_err();

        assert!(err
            .to_string()
            .contains("map string length 65535 exceeds remaining file bytes 0"));
    }

    #[test]
    fn file_reader_reserves_bytes_for_remaining_v7_headers_before_allocation() {
        let mut bytes = encoded_sample_rec();
        let first_section = v7_section_count_offset(&bytes) + 4;
        let first_payload = first_section + SECTION_HEADER_BYTE_SIZE as usize;
        let claimed_payload_len = (bytes.len() - first_payload) as u64;
        bytes[first_section + 28..first_section + 36]
            .copy_from_slice(&claimed_payload_len.to_le_bytes());
        let path = write_temp_dtr("remaining-headers", &bytes);

        let result = read_rec_file(&path);
        let _ = std::fs::remove_file(&path);
        let err = result.unwrap_err();

        assert!(err
            .to_string()
            .contains("section payload and remaining headers length"));
    }

    #[test]
    fn generic_reader_counts_header_strings_against_input_budget() {
        let bytes = truncated_map_header();
        let limits = DtrReadLimits {
            max_file_bytes: bytes.len() as u64 + 8,
            ..DtrReadLimits::default()
        };

        let err = read_rec_with_limits(&mut &bytes[..], limits).unwrap_err();

        assert!(err
            .to_string()
            .contains("map string length 65535 exceeds remaining input budget 8"));
    }

    fn sample_rec() -> Cs2Rec {
        let s0 = MovementSnapshot {
            origin: [1.0, -0.0, 3.0],
            velocity: [10.0, 20.0, 30.0],
            angles: [4.0, 90.0, -0.0],
            entity_flags: 1,
            move_type: 2,
            buttons: 33,
            buttons1: 1,
            buttons2: 2,
            duck_amount: 1.0,
            duck_speed: 8.0,
            ladder_normal: [-0.0, 0.0, 1.0],
            ducked: 1,
            ducking: 1,
            desires_duck: 1,
            actual_move_type: 2,
        };
        let s1 = MovementSnapshot {
            origin: [2.0, -0.0, 4.0],
            velocity: [11.0, 21.0, 31.0],
            angles: [5.0, 91.0, -0.0],
            buttons: 65,
            duck_amount: 0.5,
            duck_speed: 7.0,
            ..s0.clone()
        };
        let s2 = MovementSnapshot {
            origin: [3.0, -0.0, 5.0],
            velocity: [12.0, 22.0, 32.0],
            angles: [6.0, 92.0, -0.0],
            buttons: 129,
            duck_amount: 0.0,
            duck_speed: 0.0,
            ducked: 0,
            ducking: 0,
            desires_duck: 0,
            ..s1.clone()
        };
        Cs2Rec {
            header: Cs2RecHeader {
                version: DTR_FORMAT_VERSION,
                tick_rate: 64.0,
                map: "de_mirage".to_string(),
                round: 7,
                side: 2,
                steam_id: 76561198000000000,
                player_name: "player".to_string(),
                flags: 0,
                play_start_tick_index: 0,
            },
            ticks: vec![
                ReplayTick {
                    pre: s0,
                    post: s1.clone(),
                    weapon_def_index: 7,
                    num_subtick: 1,
                },
                ReplayTick {
                    pre: s1,
                    post: s2,
                    weapon_def_index: 9,
                    num_subtick: 0,
                },
            ],
            projectiles: vec![ReplayProjectile {
                tick_index: 1,
                kind: ProjectileKind::Smoke,
                weapon_def_index: 45,
                initial_position: [10.0, 20.0, 30.0],
                initial_velocity: [100.0, 200.0, 300.0],
                detonation_position: [40.0, 50.0, 60.0],
            }],
            high_fidelity: HighFidelityMetadata::new(
                vec![crate::model::ReplayHifiEvent {
                    tick_index: 1,
                    tick: 101,
                    kind: crate::model::ReplayHifiEventKind::ItemPickup,
                    actor_steam_id: Some(76561198000000000),
                    target_steam_id: None,
                    weapon_def_index: Some(45),
                    item_name: Some("smokegrenade".to_string()),
                    entity_id: None,
                    actor_count_after: None,
                    target_count_after: Some(2),
                    damage: None,
                    health: None,
                }],
                Vec::new(),
            ),
            subticks: vec![SubtickMove {
                when: 0.5,
                button: 1,
                pressed: 1.0,
                analog_forward: -0.0,
                analog_left: 0.0,
                pitch_delta: 0.0,
                yaw_delta: 1.25,
            }],
            command_frames: vec![
                ReplayCommandFrame {
                    forward_move: 1.0,
                    left_move: -1.0,
                    pitch: 4.0,
                    yaw: 90.0,
                    buttons: 33,
                    buttons1: 1,
                    mouse_dx: 3,
                    mouse_dy: -2,
                    fields: COMMAND_FIELD_FORWARD_MOVE
                        | COMMAND_FIELD_LEFT_MOVE
                        | COMMAND_FIELD_VIEW_ANGLES
                        | COMMAND_FIELD_MOUSE,
                    weapon_select: -1,
                    ..ReplayCommandFrame::default()
                },
                ReplayCommandFrame {
                    forward_move: 0.5,
                    left_move: 0.25,
                    pitch: 5.0,
                    yaw: 91.0,
                    buttons: 65,
                    fields: COMMAND_FIELD_FORWARD_MOVE | COMMAND_FIELD_LEFT_MOVE,
                    weapon_select: -1,
                    ..ReplayCommandFrame::default()
                },
            ],
            movement_extras: Vec::new(),
            input_history_ticks: vec![
                ReplayInputHistoryTick {
                    source_client_tick: 100,
                    attack1_start_history_index: 0,
                    attack2_start_history_index: -1,
                    num_entries: 1,
                },
                ReplayInputHistoryTick::default(),
            ],
            input_history_entries: vec![ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_VIEW_ANGLES
                    | crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_COUNT
                    | crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION
                    | crate::model::INPUT_HISTORY_FIELD_SHOOT_POSITION,
                view_angles: [4.0, 90.0, 0.0],
                render_tick_count: 99,
                render_tick_fraction: 0.75,
                shoot_position: [10.0, 20.0, 30.0],
                ..ReplayInputHistoryEntry::default()
            }],
        }
    }

    fn encoded_sample_rec() -> Vec<u8> {
        let mut bytes = Vec::new();
        write_rec(&mut bytes, &sample_rec()).unwrap();
        bytes
    }

    fn encoded_v7_rec(rec: &Cs2Rec) -> Vec<u8> {
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let mut sections = vec![
            (
                SECTION_SNAPSHOTS,
                if rec.ticks.is_empty() {
                    0
                } else {
                    rec.ticks.len() + 1
                },
                build_snapshot_section_v1(rec).unwrap(),
            ),
            (
                SECTION_TICK_METADATA,
                rec.ticks.len(),
                build_tick_metadata_section(rec).unwrap(),
            ),
            (
                SECTION_SUBTICKS,
                rec.subticks.len(),
                build_subtick_section(rec).unwrap(),
            ),
        ];
        if !rec.projectiles.is_empty() {
            sections.push((
                SECTION_PROJECTILES,
                rec.projectiles.len(),
                build_projectile_section(rec).unwrap(),
            ));
        }
        if !metadata_json.is_empty() {
            sections.push((SECTION_HIGH_FIDELITY_JSON, 1, metadata_json.clone()));
        }
        if !rec.command_frames.is_empty() {
            sections.push((
                SECTION_COMMAND_FRAMES,
                rec.command_frames.len(),
                build_command_frame_section_v1(rec).unwrap(),
            ));
        }
        if !rec.movement_extras.is_empty() {
            sections.push((
                SECTION_MOVEMENT_EXTRAS,
                rec.movement_extras.len(),
                build_movement_extra_section(rec).unwrap(),
            ));
        }

        let mut bytes = Vec::new();
        bytes.write_all(MAGIC).unwrap();
        write_u32(&mut bytes, 7).unwrap();
        write_f32(&mut bytes, rec.header.tick_rate).unwrap();
        write_u32(&mut bytes, rec.header.round).unwrap();
        write_u8(&mut bytes, rec.header.side).unwrap();
        write_u32(&mut bytes, rec.header.flags).unwrap();
        write_u64(&mut bytes, rec.header.steam_id).unwrap();
        write_u32(&mut bytes, rec.ticks.len() as u32).unwrap();
        write_u32(&mut bytes, rec.subticks.len() as u32).unwrap();
        write_u32(&mut bytes, rec.projectiles.len() as u32).unwrap();
        write_u32(&mut bytes, rec.header.play_start_tick_index).unwrap();
        write_u32(&mut bytes, metadata_json.len() as u32).unwrap();
        write_string(&mut bytes, &rec.header.map).unwrap();
        write_string(&mut bytes, &rec.header.player_name).unwrap();
        write_u32(&mut bytes, sections.len() as u32).unwrap();
        for (section_id, element_count, payload) in sections {
            write_section(
                &mut bytes,
                section_id,
                SECTION_VERSION_V1,
                element_count,
                &payload,
            )
            .unwrap();
        }
        bytes
    }

    fn encoded_legacy_sample_rec() -> Vec<u8> {
        let rec = sample_rec();
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let body = build_body(&rec, &metadata_json).unwrap();
        test_file_bytes_for_version(
            &body,
            6,
            rec.header.play_start_tick_index,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            metadata_json.len(),
            CODEC_BROTLI,
            None,
        )
    }

    fn test_file_bytes(
        body: &[u8],
        tick_count: usize,
        subtick_count: usize,
        projectile_count: usize,
        metadata_json_len: usize,
        codec: u8,
        body_len: Option<u64>,
    ) -> Vec<u8> {
        test_file_bytes_for_version(
            body,
            6,
            0,
            tick_count,
            subtick_count,
            projectile_count,
            metadata_json_len,
            codec,
            body_len,
        )
    }

    fn test_file_bytes_for_version(
        body: &[u8],
        version: u32,
        play_start_tick_index: u32,
        tick_count: usize,
        subtick_count: usize,
        projectile_count: usize,
        metadata_json_len: usize,
        codec: u8,
        body_len: Option<u64>,
    ) -> Vec<u8> {
        let compressed = compress_body(body).unwrap();
        let mut bytes = Vec::new();
        bytes.write_all(MAGIC).unwrap();
        write_u32(&mut bytes, version).unwrap();
        write_f32(&mut bytes, 64.0).unwrap();
        write_u32(&mut bytes, 7).unwrap();
        write_u8(&mut bytes, 2).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u64(&mut bytes, 76561198000000000).unwrap();
        write_u32(&mut bytes, tick_count as u32).unwrap();
        write_u32(&mut bytes, subtick_count as u32).unwrap();
        if version >= 4 {
            write_u32(&mut bytes, projectile_count as u32).unwrap();
        }
        if version >= 5 {
            write_u32(&mut bytes, play_start_tick_index).unwrap();
        }
        if version >= 6 {
            write_u32(&mut bytes, metadata_json_len as u32).unwrap();
        }
        write_string(&mut bytes, "de_mirage").unwrap();
        write_string(&mut bytes, "player").unwrap();
        write_u8(&mut bytes, codec).unwrap();
        write_u64(&mut bytes, body_len.unwrap_or(body.len() as u64)).unwrap();
        write_u64(&mut bytes, compressed.len() as u64).unwrap();
        bytes.write_all(&compressed).unwrap();
        bytes
    }

    fn play_start_offset() -> usize {
        8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4 + 4
    }

    fn rec_header_offsets(bytes: &[u8]) -> (usize, usize, usize) {
        let version = u32::from_le_bytes(bytes[8..12].try_into().unwrap());
        let mut offset = 8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4;
        if version >= 4 {
            offset += 4;
        }
        if version >= 5 {
            offset += 4;
        }
        if version >= 6 {
            offset += 4;
        }
        let map_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + map_len;
        let player_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + player_len;
        (offset, offset + 1, offset + 9)
    }

    fn v7_section_count_offset(bytes: &[u8]) -> usize {
        let mut offset = 8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4 + 4 + 4 + 4;
        let map_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + map_len;
        let player_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + player_len;
        offset
    }

    fn append_duplicate_v7_section(
        bytes: &mut Vec<u8>,
        section_id: u32,
        element_count: usize,
        payload: &[u8],
    ) {
        let section_count_offset = v7_section_count_offset(bytes);
        let section_count = u32::from_le_bytes(
            bytes[section_count_offset..section_count_offset + 4]
                .try_into()
                .unwrap(),
        );
        bytes[section_count_offset..section_count_offset + 4]
            .copy_from_slice(&(section_count + 1).to_le_bytes());
        write_section(
            bytes,
            section_id,
            SECTION_VERSION_V1,
            element_count,
            payload,
        )
        .unwrap();
    }

    fn insert_unknown_v7_section(bytes: &mut Vec<u8>, uncompressed_len: u64, payload: &[u8]) {
        let section_count_offset = v7_section_count_offset(bytes);
        let section_count = u32::from_le_bytes(
            bytes[section_count_offset..section_count_offset + 4]
                .try_into()
                .unwrap(),
        );
        bytes[section_count_offset..section_count_offset + 4]
            .copy_from_slice(&(section_count + 1).to_le_bytes());
        let mut section = Vec::new();
        write_u32(&mut section, 999).unwrap();
        write_u32(&mut section, SECTION_VERSION_V1).unwrap();
        write_u8(&mut section, CODEC_NONE).unwrap();
        section.write_all(&[0, 0, 0]).unwrap();
        write_u32(&mut section, 0).unwrap();
        write_u32(&mut section, 0).unwrap();
        write_u64(&mut section, uncompressed_len).unwrap();
        write_u64(&mut section, payload.len() as u64).unwrap();
        section.write_all(payload).unwrap();
        bytes.splice(section_count_offset + 4..section_count_offset + 4, section);
    }

    fn tick_count_offset() -> usize {
        8 + 4 + 4 + 4 + 1 + 4 + 8
    }

    fn truncated_map_header() -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.write_all(MAGIC).unwrap();
        write_u32(&mut bytes, DTR_FORMAT_VERSION).unwrap();
        write_f32(&mut bytes, 64.0).unwrap();
        write_u32(&mut bytes, 1).unwrap();
        write_u8(&mut bytes, 2).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u64(&mut bytes, 0).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u16(&mut bytes, u16::MAX).unwrap();
        bytes
    }

    fn write_temp_dtr(name: &str, bytes: &[u8]) -> std::path::PathBuf {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "cs2-demotracer-{name}-{}-{unique}.dtr",
            std::process::id()
        ));
        std::fs::write(&path, bytes).unwrap();
        path
    }

    fn subtick_bit_eq(a: &SubtickMove, b: &SubtickMove) -> bool {
        a.when.to_bits() == b.when.to_bits()
            && a.button == b.button
            && a.pressed.to_bits() == b.pressed.to_bits()
            && a.analog_forward.to_bits() == b.analog_forward.to_bits()
            && a.analog_left.to_bits() == b.analog_left.to_bits()
            && a.pitch_delta.to_bits() == b.pitch_delta.to_bits()
            && a.yaw_delta.to_bits() == b.yaw_delta.to_bits()
    }
}
