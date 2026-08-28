/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::model::{
    Cs2Rec, Cs2RecHeader, ParsedPlayerTick, ParsedProjectile, ReplayInputHistoryEntry,
    ReplayInputHistoryTick, ReplayProjectile, ReplayTick, SubtickMode, SubtickMove,
    INPUT_HISTORY_FIELDS_ALL,
};
use crate::{Error, Result};
use std::collections::BTreeMap;

pub const MAX_SUBTICKS_PER_TICK: usize = 36;
pub const MAX_INPUT_HISTORY_PER_TICK: usize = 64;
const MAX_PLAYER_VELOCITY_COMPONENT: f32 = 4096.0;

#[derive(Clone, Copy, Debug, Default)]
pub struct SynthesisOptions {
    pub subtick_mode: SubtickMode,
    pub play_start_tick_index: u32,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct SynthesisStats {
    pub source_subticks: usize,
    pub written_subticks: usize,
    pub ticks_with_source_subticks: usize,
    pub ticks_with_written_subticks: usize,
    pub dropped_invalid_subticks: usize,
    pub dropped_overflow_subticks: usize,
    pub truncated_button_subticks: usize,
}

impl SynthesisStats {
    pub fn add_assign(&mut self, other: &Self) {
        self.source_subticks += other.source_subticks;
        self.written_subticks += other.written_subticks;
        self.ticks_with_source_subticks += other.ticks_with_source_subticks;
        self.ticks_with_written_subticks += other.ticks_with_written_subticks;
        self.dropped_invalid_subticks += other.dropped_invalid_subticks;
        self.dropped_overflow_subticks += other.dropped_overflow_subticks;
        self.truncated_button_subticks += other.truncated_button_subticks;
    }
}

trait PlayerRow {
    fn row(&self) -> &ParsedPlayerTick;
}

impl PlayerRow for ParsedPlayerTick {
    fn row(&self) -> &ParsedPlayerTick {
        self
    }
}

impl PlayerRow for &ParsedPlayerTick {
    fn row(&self) -> &ParsedPlayerTick {
        *self
    }
}

pub fn synthesize_player_rec(
    rows: &[ParsedPlayerTick],
    map: &str,
    tick_rate: f32,
    round: u32,
) -> Result<Cs2Rec> {
    synthesize_player_rec_with_options(
        rows,
        &[],
        map,
        tick_rate,
        round,
        SynthesisOptions::default(),
    )
    .map(|(rec, _stats)| rec)
}

pub fn synthesize_player_rec_with_options(
    rows: &[ParsedPlayerTick],
    projectiles: &[ParsedProjectile],
    map: &str,
    tick_rate: f32,
    round: u32,
    options: SynthesisOptions,
) -> Result<(Cs2Rec, SynthesisStats)> {
    synthesize_player_rec_with_projectile_iter(
        rows,
        projectiles.iter(),
        map,
        tick_rate,
        round,
        options,
    )
}

pub fn synthesize_player_rec_with_projectile_refs(
    rows: &[ParsedPlayerTick],
    projectiles: &[&ParsedProjectile],
    map: &str,
    tick_rate: f32,
    round: u32,
    options: SynthesisOptions,
) -> Result<(Cs2Rec, SynthesisStats)> {
    synthesize_player_rec_with_projectile_iter(
        rows,
        projectiles.iter().copied(),
        map,
        tick_rate,
        round,
        options,
    )
}

pub fn synthesize_player_rec_with_row_refs(
    rows: &[&ParsedPlayerTick],
    projectiles: &[&ParsedProjectile],
    map: &str,
    tick_rate: f32,
    round: u32,
    options: SynthesisOptions,
) -> Result<(Cs2Rec, SynthesisStats)> {
    synthesize_player_rec_with_projectile_iter(
        rows,
        projectiles.iter().copied(),
        map,
        tick_rate,
        round,
        options,
    )
}

fn synthesize_player_rec_with_projectile_iter<'a>(
    rows: &[impl PlayerRow],
    projectiles: impl IntoIterator<Item = &'a ParsedProjectile>,
    map: &str,
    tick_rate: f32,
    round: u32,
    options: SynthesisOptions,
) -> Result<(Cs2Rec, SynthesisStats)> {
    if rows.len() < 2 {
        return Err(Error::InvalidDemo(
            "need at least two player rows to synthesize replay".to_string(),
        ));
    }
    for pair in rows.windows(2) {
        let current = pair[0].row();
        let next = pair[1].row();
        let expected_tick = i64::from(current.tick) + 1;
        if i64::from(next.tick) != expected_tick {
            return Err(Error::InvalidDemo(format!(
                "player {} replay source ticks are not consecutive: {} -> {} (expected {})",
                current.steam_id, current.tick, next.tick, expected_tick
            )));
        }
    }
    if options.play_start_tick_index as usize >= rows.len().saturating_sub(1) {
        return Err(Error::InvalidDemo(format!(
            "play start tick index {} is outside {} synthesized ticks",
            options.play_start_tick_index,
            rows.len().saturating_sub(1)
        )));
    }
    let first = rows[0].row();
    let mut ticks = Vec::with_capacity(rows.len().saturating_sub(1));
    let mut subticks = Vec::new();
    let mut command_frames = Vec::with_capacity(rows.len().saturating_sub(1));
    let mut input_history_ticks = Vec::with_capacity(rows.len().saturating_sub(1));
    let mut input_history_entries = Vec::new();
    let mut stats = SynthesisStats::default();
    for pair in rows.windows(2) {
        let pre_row = pair[0].row();
        let post_row = pair[1].row();
        let mut pre = pre_row.snapshot();
        let mut post = post_row.snapshot();
        normalize_impossible_player_velocity(&mut pre);
        normalize_impossible_player_velocity(&mut post);
        command_frames.push(pre_row.command_frame());
        let (input_history_tick, mut tick_input_history) =
            sanitize_input_history(pre_row, options.subtick_mode);
        input_history_ticks.push(input_history_tick);
        input_history_entries.append(&mut tick_input_history);
        let mut tick_subticks = sanitize_subticks(pre_row, options.subtick_mode, &mut stats);
        let num_subtick = tick_subticks.len() as u32;
        subticks.append(&mut tick_subticks);
        ticks.push(ReplayTick {
            pre,
            post,
            weapon_def_index: normalize_replay_weapon_def_index(pre_row.item_def_idx),
            num_subtick,
        });
    }
    let replay_projectiles = synthesize_projectiles(rows, projectiles, ticks.len());

    Ok((
        Cs2Rec {
            header: Cs2RecHeader {
                version: crate::model::DTR_FORMAT_VERSION,
                tick_rate,
                map: map.to_string(),
                round,
                side: first.team_num,
                steam_id: first.steam_id,
                player_name: first.name.clone(),
                flags: 0,
                play_start_tick_index: options.play_start_tick_index,
            },
            ticks,
            projectiles: replay_projectiles,
            high_fidelity: crate::model::HighFidelityMetadata::default(),
            subticks,
            command_frames,
            movement_extras: Vec::new(),
            input_history_ticks,
            input_history_entries,
        },
        stats,
    ))
}

fn sanitize_input_history(
    row: &ParsedPlayerTick,
    subtick_mode: SubtickMode,
) -> (ReplayInputHistoryTick, Vec<ReplayInputHistoryEntry>) {
    if subtick_mode == SubtickMode::Off {
        return (ReplayInputHistoryTick::default(), Vec::new());
    }

    // Only attack start indexes consume replay input history. Keeping every
    // command entry would duplicate several high-entropy snapshots on nearly
    // every player tick even though no shot references them.
    let source_attack_indexes = [
        row.usercmd_attack1_start_history_index,
        row.usercmd_attack2_start_history_index,
    ];
    let mut referenced_source_indexes = source_attack_indexes
        .iter()
        .filter_map(|source_index| usize::try_from(*source_index).ok())
        .collect::<Vec<_>>();
    referenced_source_indexes.sort_unstable();
    referenced_source_indexes.dedup();

    let mut retained_source_indexes = Vec::with_capacity(referenced_source_indexes.len());
    let mut entries = Vec::with_capacity(referenced_source_indexes.len());
    for source_index in referenced_source_indexes {
        let Some(entry) = row.input_history.get(source_index) else {
            continue;
        };
        if !input_history_entry_is_valid(entry) {
            continue;
        }
        retained_source_indexes.push(source_index);
        entries.push(*entry);
    }

    if entries.is_empty() {
        return (ReplayInputHistoryTick::default(), entries);
    }

    let remap_attack_index = |source_index: i32| -> i32 {
        usize::try_from(source_index)
            .ok()
            .and_then(|index| {
                retained_source_indexes
                    .iter()
                    .position(|retained| *retained == index)
            })
            .and_then(|index| i32::try_from(index).ok())
            .unwrap_or(-1)
    };
    (
        ReplayInputHistoryTick {
            // Modern demos normally provide base.client_tick. Demo tick is the safest
            // same-clock fallback for older delta commands and is only used as an offset anchor.
            source_client_tick: row.usercmd_client_tick.unwrap_or(row.tick),
            attack1_start_history_index: remap_attack_index(
                row.usercmd_attack1_start_history_index,
            ),
            attack2_start_history_index: remap_attack_index(
                row.usercmd_attack2_start_history_index,
            ),
            num_entries: entries.len() as u32,
        },
        entries,
    )
}

fn input_history_entry_is_valid(entry: &ReplayInputHistoryEntry) -> bool {
    entry.fields & !INPUT_HISTORY_FIELDS_ALL == 0
        && entry.view_angles.iter().all(|value| value.is_finite())
        && entry.render_tick_fraction.is_finite()
        && entry.player_tick_fraction.is_finite()
        && entry.cl_interp_fraction.is_finite()
        && entry.sv_interp0_fraction.is_finite()
        && entry.sv_interp1_fraction.is_finite()
        && entry.player_interp_fraction.is_finite()
        && entry.shoot_position.iter().all(|value| value.is_finite())
        && entry
            .target_head_pos_check
            .iter()
            .all(|value| value.is_finite())
        && entry
            .target_abs_pos_check
            .iter()
            .all(|value| value.is_finite())
        && entry
            .target_abs_ang_check
            .iter()
            .all(|value| value.is_finite())
}

fn synthesize_projectiles<'a>(
    rows: &[impl PlayerRow],
    projectiles: impl IntoIterator<Item = &'a ParsedProjectile>,
    tick_count: usize,
) -> Vec<ReplayProjectile> {
    if rows.is_empty() || tick_count == 0 {
        return Vec::new();
    }

    let steam_id = rows[0].row().steam_id;
    let mut tick_to_index = BTreeMap::new();
    for (index, row) in rows.iter().take(tick_count).enumerate() {
        tick_to_index.entry(row.row().tick).or_insert(index as u32);
    }

    let mut out = projectiles
        .into_iter()
        .filter(|projectile| projectile.steam_id == steam_id)
        .filter_map(|projectile| {
            let tick_index = *tick_to_index.get(&projectile.tick)?;
            Some(ReplayProjectile {
                tick_index,
                kind: projectile.kind,
                weapon_def_index: projectile.weapon_def_index,
                initial_position: projectile.initial_position,
                initial_velocity: projectile.initial_velocity,
                detonation_position: projectile.detonation_position,
            })
        })
        .collect::<Vec<_>>();
    out.sort_by_key(|projectile| projectile.tick_index);
    out
}

fn sanitize_subticks(
    row: &ParsedPlayerTick,
    subtick_mode: SubtickMode,
    stats: &mut SynthesisStats,
) -> Vec<SubtickMove> {
    if subtick_mode == SubtickMode::Off {
        return Vec::new();
    }

    stats.source_subticks += row.subtick_moves.len();
    stats.truncated_button_subticks += row.subtick_button_truncated;
    if !row.subtick_moves.is_empty() {
        stats.ticks_with_source_subticks += 1;
    }

    let mut valid = Vec::with_capacity(row.subtick_moves.len().min(MAX_SUBTICKS_PER_TICK));
    for subtick in &row.subtick_moves {
        if subtick_is_valid(subtick) {
            valid.push(*subtick);
        } else {
            stats.dropped_invalid_subticks += 1;
        }
    }

    valid.sort_by(|a, b| a.when.total_cmp(&b.when));
    if valid.len() > MAX_SUBTICKS_PER_TICK {
        stats.dropped_overflow_subticks += valid.len() - MAX_SUBTICKS_PER_TICK;
        valid.truncate(MAX_SUBTICKS_PER_TICK);
    }
    if !valid.is_empty() {
        stats.ticks_with_written_subticks += 1;
        stats.written_subticks += valid.len();
    }
    valid
}

fn subtick_is_valid(subtick: &SubtickMove) -> bool {
    subtick.when.is_finite()
        && (0.0..1.0).contains(&subtick.when)
        && subtick.pressed.is_finite()
        && subtick.analog_forward.is_finite()
        && subtick.analog_left.is_finite()
        && subtick.pitch_delta.is_finite()
        && subtick.yaw_delta.is_finite()
}

fn normalize_replay_weapon_def_index(def: i32) -> i32 {
    if is_cs2_knife_def_index(def) {
        42
    } else {
        def
    }
}

fn normalize_impossible_player_velocity(snapshot: &mut crate::model::MovementSnapshot) {
    if snapshot
        .velocity
        .iter()
        .any(|component| component.abs() > MAX_PLAYER_VELOCITY_COMPONENT)
    {
        snapshot.velocity = [0.0, 0.0, 0.0];
    }
}

fn is_cs2_knife_def_index(def: i32) -> bool {
    // CS2 demos can report the active knife as the equipped cosmetic item
    // definition. BotController treats canonical def 42 as "the bot's own
    // knife", so the file format stores every knife variant as 42.
    crate::export::valid_knife_item_def_index(def)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(tick: i32, weapon: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id: 42,
            name: "p".to_string(),
            team_num: 2,
            is_alive: true,
            round: 1,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: Some(tick as f32 / 64.0),
            origin: [tick as f32, 0.0, 64.0],
            velocity: [1.0, 2.0, 3.0],
            pitch: 4.0,
            yaw: 5.0,
            buttons: 1,
            buttonstates_present: true,
            buttonstate1: 1,
            buttonstate2: 1,
            buttonstate3: 0,
            usercmd_forward_move: Some(12.5),
            usercmd_left_move: Some(-4.0),
            usercmd_up_move: Some(0.25),
            usercmd_pitch: Some(40.0),
            usercmd_yaw: Some(50.0),
            usercmd_roll: Some(0.0),
            usercmd_mouse_dx: Some(3),
            usercmd_mouse_dy: Some(-2),
            usercmd_weapon_select: Some(123),
            usercmd_left_hand_desired: Some(true),
            usercmd_client_tick: Some(tick),
            usercmd_attack1_start_history_index: -1,
            usercmd_attack2_start_history_index: -1,
            input_history: Vec::new(),
            item_def_idx: weapon,
            inventory_as_ids: Vec::new(),
            inventory_weapon_cosmetics: Vec::new().into(),
            music_kit_id: None,
            scoreboard_flair: None,
            agent_item_def_index: None,
            agent_skin: None,
            active_weapon_paint_kit: None,
            active_weapon_paint_seed: None,
            active_weapon_paint_wear: None,
            active_weapon_original_owner_steam_id: None,
            active_weapon_item_account_id: None,
            active_weapon_item_id: None,
            active_weapon_custom_name: None,
            active_weapon_stickers: Vec::new(),
            glove_item_def_index: None,
            glove_paint_kit: None,
            glove_paint_seed: None,
            glove_paint_wear: None,
            crosshair_code: None,
            viewmodel_left_handed: None,
            viewmodel_fov: None,
            viewmodel_offset_x: None,
            viewmodel_offset_y: None,
            viewmodel_offset_z: None,
            scoreboard_score: None,
            scoreboard_mvps: None,
            scoreboard_kills: None,
            scoreboard_deaths: None,
            scoreboard_assists: None,
            scoreboard_headshot_kills: None,
            scoreboard_damage: None,
            armor_value: 0,
            has_helmet: false,
            has_defuser: false,
            round_start_equip_value: 0,
            equipment_value_total: 0,
            money_saved_total: 0,
            cash_spent_this_round: 0,
            account_balance: None,
            entity_flags: 1,
            move_type: 2,
            duck_amount: None,
            duck_speed: None,
            ladder_normal: None,
            ducked: None,
            ducking: None,
            desires_duck: None,
            subtick_moves: Vec::new(),
            subtick_button_truncated: 0,
            player_user_id: None,
            player_entity_id: None,
            player_color: None,
            team_rounds_total: None,
            team_name: None,
            team_clan_name: None,
        }
    }

    fn subtick(when: f32, button: u32) -> SubtickMove {
        SubtickMove {
            when,
            button,
            pressed: 1.0,
            analog_forward: 0.25,
            analog_left: 0.5,
            pitch_delta: 0.75,
            yaw_delta: 1.0,
        }
    }

    fn projectile(
        tick: i32,
        steam_id: u64,
        kind: crate::model::ProjectileKind,
    ) -> ParsedProjectile {
        ParsedProjectile {
            tick,
            steam_id,
            name: "p".to_string(),
            grenade_type: format!("{kind:?}"),
            kind,
            weapon_def_index: kind.weapon_def_index(),
            initial_position: [tick as f32, 1.0, 2.0],
            initial_velocity: [3.0, tick as f32, 4.0],
            detonation_position: [5.0, 6.0, tick as f32],
            ..ParsedProjectile::default()
        }
    }

    #[test]
    fn synthesis_uses_adjacent_rows_as_pre_post() {
        let rec = synthesize_player_rec(&[row(10, 7), row(11, 7), row(12, 9)], "de_nuke", 64.0, 1)
            .unwrap();
        assert_eq!(rec.ticks.len(), 2);
        assert_eq!(rec.ticks[0].pre.origin[0], 10.0);
        assert_eq!(rec.ticks[0].post.origin[0], 11.0);
        assert_eq!(rec.ticks[1].weapon_def_index, 7);
        assert_eq!(rec.command_frames.len(), 2);
        assert_eq!(rec.command_frames[0].forward_move, 12.5);
        assert_eq!(rec.command_frames[0].left_move, -4.0);
        assert_eq!(rec.command_frames[0].up_move, 0.25);
        assert_eq!(rec.command_frames[0].pitch, 40.0);
        assert_eq!(rec.command_frames[0].yaw, 50.0);
        assert_eq!(rec.command_frames[0].mouse_dx, 3);
        assert_eq!(rec.command_frames[0].mouse_dy, -2);
        assert_eq!(rec.command_frames[0].weapon_select, 123);
        assert_eq!(rec.command_frames[0].left_hand_desired, 1);
        assert_ne!(
            rec.command_frames[0].fields & crate::model::COMMAND_FIELD_FORWARD_MOVE,
            0
        );
        assert!(rec.subticks.is_empty());
    }

    #[test]
    fn synthesis_clears_impossible_spawn_transition_velocity() {
        let first = row(10, 7);
        let mut artifact = row(11, 7);
        artifact.velocity = [126_715.7, 91_870.14, 256.0];
        let last = row(12, 7);

        let rec = synthesize_player_rec(&[first, artifact, last], "de_nuke", 64.0, 1).unwrap();

        assert_eq!(rec.ticks[0].post.velocity, [0.0, 0.0, 0.0]);
        assert_eq!(rec.ticks[1].pre.velocity, [0.0, 0.0, 0.0]);
    }

    #[test]
    fn synthesis_rejects_non_consecutive_source_ticks() {
        let error =
            synthesize_player_rec(&[row(10, 7), row(12, 7)], "de_nuke", 64.0, 1).unwrap_err();

        assert!(matches!(
            error,
            Error::InvalidDemo(message)
                if message.contains("player 42")
                    && message.contains("10 -> 12")
                    && message.contains("expected 11")
        ));
    }

    #[test]
    fn synthesis_canonicalizes_cs2_lib_knife_def_indices() {
        let rec = synthesize_player_rec(
            &[
                row(10, 42),
                row(11, 59),
                row(12, 526),
                row(13, 508),
                row(14, 7),
            ],
            "de_nuke",
            64.0,
            1,
        )
        .unwrap();
        assert_eq!(rec.ticks[0].weapon_def_index, 42);
        assert_eq!(rec.ticks[1].weapon_def_index, 42);
        assert_eq!(rec.ticks[2].weapon_def_index, 42);
        assert_eq!(rec.ticks[3].weapon_def_index, 42);
    }

    #[test]
    fn synthesis_writes_sorted_and_bounded_subticks() {
        let mut r0 = row(10, 7);
        r0.subtick_moves = vec![subtick(0.7, 2), subtick(1.0, 3), subtick(0.1, 1)];
        r0.subtick_button_truncated = 1;
        let mut r1 = row(11, 7);
        r1.subtick_moves = (0..40).map(|i| subtick(i as f32 / 80.0, i)).collect();
        let r2 = row(12, 7);

        let (rec, stats) = synthesize_player_rec_with_options(
            &[r0, r1, r2],
            &[],
            "de_nuke",
            64.0,
            1,
            SynthesisOptions::default(),
        )
        .unwrap();

        assert_eq!(rec.ticks[0].num_subtick, 2);
        assert_eq!(rec.ticks[1].num_subtick, MAX_SUBTICKS_PER_TICK as u32);
        assert_eq!(rec.subticks[0].button, 1);
        assert_eq!(rec.subticks[1].button, 2);
        assert_eq!(stats.source_subticks, 43);
        assert_eq!(stats.written_subticks, 38);
        assert_eq!(stats.ticks_with_source_subticks, 2);
        assert_eq!(stats.ticks_with_written_subticks, 2);
        assert_eq!(stats.dropped_invalid_subticks, 1);
        assert_eq!(stats.dropped_overflow_subticks, 4);
        assert_eq!(stats.truncated_button_subticks, 1);
    }

    #[test]
    fn synthesis_preserves_grenade_release_subtick_phase() {
        let mut release = subtick(0.125, 1);
        release.pressed = 0.0;
        let mut first = row(10, 46);
        first.subtick_moves = vec![release];

        let rec = synthesize_player_rec(&[first, row(11, 46)], "de_mirage", 64.0, 1).unwrap();

        assert_eq!(rec.ticks[0].num_subtick, 1);
        assert_eq!(rec.subticks[0].when, 0.125);
        assert_eq!(rec.subticks[0].button, 1);
        assert_eq!(rec.subticks[0].pressed, 0.0);
    }

    #[test]
    fn synthesis_can_disable_subticks() {
        let mut r0 = row(10, 7);
        r0.subtick_moves = vec![subtick(0.25, 1)];
        let r1 = row(11, 7);
        let (rec, stats) = synthesize_player_rec_with_options(
            &[r0, r1],
            &[],
            "de_nuke",
            64.0,
            1,
            SynthesisOptions {
                subtick_mode: SubtickMode::Off,
                ..SynthesisOptions::default()
            },
        )
        .unwrap();

        assert_eq!(rec.ticks[0].num_subtick, 0);
        assert!(rec.subticks.is_empty());
        assert_eq!(stats, SynthesisStats::default());
    }

    #[test]
    fn synthesis_keeps_only_referenced_shooting_history_and_remaps_attack_indexes() {
        let mut first = row(500, 7);
        first.usercmd_client_tick = Some(700);
        first.usercmd_attack1_start_history_index = 2;
        first.usercmd_attack2_start_history_index = 1;
        first.input_history = vec![
            ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_COUNT,
                render_tick_count: 650,
                ..ReplayInputHistoryEntry::default()
            },
            ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_COUNT
                    | crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION,
                render_tick_count: 698,
                render_tick_fraction: 0.5,
                ..ReplayInputHistoryEntry::default()
            },
            ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_COUNT
                    | crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION,
                render_tick_count: 699,
                render_tick_fraction: 0.75,
                ..ReplayInputHistoryEntry::default()
            },
        ];

        let rec = synthesize_player_rec(&[first, row(501, 7)], "de_nuke", 64.0, 1).unwrap();

        assert_eq!(rec.input_history_ticks.len(), 1);
        assert_eq!(rec.input_history_ticks[0].source_client_tick, 700);
        assert_eq!(rec.input_history_ticks[0].attack1_start_history_index, 1);
        assert_eq!(rec.input_history_ticks[0].attack2_start_history_index, 0);
        assert_eq!(rec.input_history_ticks[0].num_entries, 2);
        assert_eq!(rec.input_history_entries.len(), 2);
        assert_eq!(rec.input_history_entries[0].render_tick_count, 698);
        assert_eq!(rec.input_history_entries[1].render_tick_count, 699);
    }

    #[test]
    fn synthesis_deduplicates_shared_attack_history_entry() {
        let mut first = row(500, 7);
        first.usercmd_attack1_start_history_index = 1;
        first.usercmd_attack2_start_history_index = 1;
        first.input_history = vec![
            ReplayInputHistoryEntry::default(),
            ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION,
                render_tick_fraction: 0.25,
                ..ReplayInputHistoryEntry::default()
            },
        ];

        let rec = synthesize_player_rec(&[first, row(501, 7)], "de_nuke", 64.0, 1).unwrap();

        assert_eq!(rec.input_history_ticks[0].attack1_start_history_index, 0);
        assert_eq!(rec.input_history_ticks[0].attack2_start_history_index, 0);
        assert_eq!(rec.input_history_ticks[0].num_entries, 1);
        assert_eq!(rec.input_history_entries.len(), 1);
    }

    #[test]
    fn synthesis_omits_history_without_valid_attack_reference() {
        let mut first = row(500, 7);
        first.usercmd_client_tick = Some(700);
        first.usercmd_attack1_start_history_index = 1;
        first.input_history = vec![
            ReplayInputHistoryEntry {
                fields: crate::model::INPUT_HISTORY_FIELD_RENDER_TICK_COUNT,
                render_tick_count: 699,
                ..ReplayInputHistoryEntry::default()
            },
            ReplayInputHistoryEntry {
                render_tick_fraction: f32::NAN,
                ..ReplayInputHistoryEntry::default()
            },
        ];

        let rec = synthesize_player_rec(&[first, row(501, 7)], "de_nuke", 64.0, 1).unwrap();

        assert_eq!(
            rec.input_history_ticks[0],
            ReplayInputHistoryTick::default()
        );
        assert!(rec.input_history_entries.is_empty());
    }

    #[test]
    fn synthesis_projectile_refs_match_owned_projectiles() {
        let rows = [row(10, 7), row(11, 7), row(12, 7), row(13, 7)];
        let projectiles = [
            projectile(12, 42, crate::model::ProjectileKind::Smoke),
            projectile(10, 42, crate::model::ProjectileKind::Flash),
            projectile(11, 99, crate::model::ProjectileKind::He),
            projectile(13, 42, crate::model::ProjectileKind::Molotov),
        ];
        let projectile_refs = projectiles.iter().collect::<Vec<_>>();

        let (owned_rec, owned_stats) = synthesize_player_rec_with_options(
            &rows,
            &projectiles,
            "de_nuke",
            64.0,
            1,
            SynthesisOptions::default(),
        )
        .unwrap();
        let (borrowed_rec, borrowed_stats) = synthesize_player_rec_with_projectile_refs(
            &rows,
            &projectile_refs,
            "de_nuke",
            64.0,
            1,
            SynthesisOptions::default(),
        )
        .unwrap();

        assert_eq!(borrowed_rec.projectiles, owned_rec.projectiles);
        assert_eq!(borrowed_stats, owned_stats);
        assert_eq!(borrowed_rec.projectiles.len(), 2);
        assert_eq!(borrowed_rec.projectiles[0].tick_index, 0);
        assert_eq!(borrowed_rec.projectiles[1].tick_index, 2);
    }

    #[test]
    fn synthesis_row_refs_match_owned_rows() {
        let mut r0 = row(10, 7);
        r0.subtick_moves = vec![subtick(0.25, 1)];
        let rows = [r0, row(11, 7), row(12, 9)];
        let row_refs = rows.iter().collect::<Vec<_>>();
        let projectiles = [projectile(11, 42, crate::model::ProjectileKind::Smoke)];
        let projectile_refs = projectiles.iter().collect::<Vec<_>>();

        let (owned_rec, owned_stats) = synthesize_player_rec_with_projectile_refs(
            &rows,
            &projectile_refs,
            "de_nuke",
            64.0,
            1,
            SynthesisOptions::default(),
        )
        .unwrap();
        let (borrowed_rec, borrowed_stats) = synthesize_player_rec_with_row_refs(
            &row_refs,
            &projectile_refs,
            "de_nuke",
            64.0,
            1,
            SynthesisOptions::default(),
        )
        .unwrap();

        assert_eq!(borrowed_rec, owned_rec);
        assert_eq!(borrowed_stats, owned_stats);
    }
}
