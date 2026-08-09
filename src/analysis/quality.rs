/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::model::ParsedPlayerTick;
use crate::model::{public_demo_path, DemoAnalysis, ParsedDemo, RoundStatus, RoundSummary};
use std::collections::{BTreeMap, BTreeSet};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AnalysisOptions {
    pub min_round_seconds: f32,
    pub max_round_seconds: f32,
}

impl Default for AnalysisOptions {
    fn default() -> Self {
        Self {
            min_round_seconds: 10.0,
            max_round_seconds: 240.0,
        }
    }
}

pub fn analyze_demo(parsed: &ParsedDemo, options: AnalysisOptions) -> DemoAnalysis {
    let mut by_round: BTreeMap<u32, Vec<_>> = BTreeMap::new();
    for (idx, row) in parsed.rows.iter().enumerate() {
        by_round.entry(row.round).or_default().push(idx);
    }

    let mut rounds = Vec::new();
    for (round, indices) in by_round {
        let round_min_tick = indices.iter().map(|idx| parsed.rows[*idx].tick).min();
        let round_max_tick = indices.iter().map(|idx| parsed.rows[*idx].tick).max();
        let freeze_end_ticks = match (round_min_tick, round_max_tick) {
            (Some(min_tick), Some(max_tick)) => parsed
                .round_freeze_end_ticks
                .iter()
                .copied()
                .filter(|tick| *tick >= min_tick && *tick <= max_tick)
                .collect::<Vec<_>>(),
            _ => Vec::new(),
        };
        let round_floor_tick = choose_round_floor(parsed, round, &indices, &freeze_end_ticks);
        let active: Vec<_> = indices
            .iter()
            .copied()
            .filter(|idx| {
                let row = &parsed.rows[*idx];
                row.round_in_progress && !row.is_freeze_period
            })
            .collect();
        let has_active_window = !active.is_empty();
        let source = if has_active_window {
            active
        } else {
            indices.clone()
        };
        let source = apply_round_start_floor(parsed, &source, round_floor_tick);
        let Some(window) = select_stable_window(parsed, &source) else {
            continue;
        };

        let start_tick = window.start_tick;
        let end_tick = window.end_tick;
        let tick_span = (end_tick - start_tick).max(0) as f32;
        let duration_seconds = if parsed.tick_rate > 0.0 {
            tick_span / parsed.tick_rate
        } else {
            0.0
        };

        let t_count = window.t_players;
        let ct_count = window.ct_players;
        let total = t_count + ct_count;
        let partial_evidence = evidence_backed_partial_round(
            parsed, &indices, &source, start_tick, end_tick, t_count, ct_count,
        );
        let mut problems = Vec::new();
        if total != 10 && !partial_evidence {
            problems.push(format!("available players {total} != 10"));
        }
        if t_count != 5 && !partial_evidence {
            problems.push(format!("T players {t_count} != 5"));
        }
        if ct_count != 5 && !partial_evidence {
            problems.push(format!("CT players {ct_count} != 5"));
        }
        if duration_seconds < options.min_round_seconds {
            problems.push(format!(
                "round window too short: {:.1}s < {:.1}s",
                duration_seconds, options.min_round_seconds
            ));
        }
        if duration_seconds > options.max_round_seconds {
            problems.push(format!(
                "round window too long: {:.1}s > {:.1}s",
                duration_seconds, options.max_round_seconds
            ));
        }
        if window.valid_rows < 2 {
            problems.push(format!("valid player rows {} < 2", window.valid_rows));
        }
        if !has_active_window && round_floor_tick.is_none() {
            problems.push("missing active/freeze-end window; used raw round rows".to_string());
        }

        let status = match (problems.is_empty(), partial_evidence) {
            (true, true) => RoundStatus::Partial,
            (true, false) => RoundStatus::Recommended,
            (false, _) => RoundStatus::Suspicious,
        };
        rounds.push(RoundSummary {
            round,
            start_tick,
            end_tick,
            duration_seconds,
            t_players: t_count,
            ct_players: ct_count,
            total_players: total,
            valid_rows: window.valid_rows,
            status,
            problems,
        });
    }

    DemoAnalysis {
        demo_path: public_demo_path(&parsed.path),
        demo_stem: parsed.stem.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        row_count: parsed.rows.len(),
        rounds,
    }
}

fn evidence_backed_partial_round(
    parsed: &ParsedDemo,
    round_indices: &[usize],
    active_indices: &[usize],
    start_tick: i32,
    end_tick: i32,
    t_count: usize,
    ct_count: usize,
) -> bool {
    if !matches!((t_count, ct_count), (4, 5) | (5, 4)) {
        return false;
    }

    let mut freeze_players = BTreeMap::<u64, (u8, i32)>::new();
    for idx in round_indices {
        let row = &parsed.rows[*idx];
        if row.tick >= start_tick
            || !row.is_freeze_period
            || !row.is_alive
            || row.steam_id == 0
            || !matches!(row.team_num, 2 | 3)
        {
            continue;
        }
        freeze_players
            .entry(row.steam_id)
            .and_modify(|entry| {
                if row.tick > entry.1 {
                    *entry = (row.team_num, row.tick);
                }
            })
            .or_insert((row.team_num, row.tick));
    }
    let freeze_t = freeze_players
        .values()
        .filter(|(side, _)| *side == 2)
        .count();
    let freeze_ct = freeze_players
        .values()
        .filter(|(side, _)| *side == 3)
        .count();
    if freeze_t != 5 || freeze_ct != 5 {
        return false;
    }

    let active_players = active_indices
        .iter()
        .filter_map(|idx| {
            let row = &parsed.rows[*idx];
            (row.tick >= start_tick
                && row.tick <= end_tick
                && row.is_alive
                && row.steam_id != 0
                && matches!(row.team_num, 2 | 3))
            .then_some(row.steam_id)
        })
        .collect::<BTreeSet<_>>();
    if active_players.len() != 9 {
        return false;
    }

    let missing = freeze_players
        .keys()
        .filter(|steam_id| !active_players.contains(steam_id))
        .copied()
        .collect::<Vec<_>>();
    let [missing_steam_id] = missing.as_slice() else {
        return false;
    };
    let Some(&(side, last_alive_freeze_tick)) = freeze_players.get(missing_steam_id) else {
        return false;
    };
    if (side == 2 && t_count != 4) || (side == 3 && ct_count != 4) {
        return false;
    }

    let death_tick = parsed
        .events
        .iter()
        .filter(|event| {
            event.name == "player_death"
                && event.tick >= last_alive_freeze_tick
                && event.tick < start_tick
                && (event.user_steam_id == Some(*missing_steam_id)
                    || event.victim_steam_id == Some(*missing_steam_id))
        })
        .map(|event| event.tick)
        .min();
    let Some(death_tick) = death_tick else {
        return false;
    };

    let has_dead_freeze_evidence = round_indices.iter().any(|idx| {
        let row = &parsed.rows[*idx];
        row.steam_id == *missing_steam_id
            && row.tick >= death_tick
            && row.tick < start_tick
            && row.is_freeze_period
            && !row.is_alive
    });
    let revived_during_live = round_indices.iter().any(|idx| {
        let row = &parsed.rows[*idx];
        row.steam_id == *missing_steam_id
            && row.tick >= start_tick
            && row.tick <= end_tick
            && row.round_in_progress
            && !row.is_freeze_period
            && row.is_alive
    });
    if !has_dead_freeze_evidence || revived_during_live {
        return false;
    }

    true
}

fn apply_round_start_floor(
    parsed: &ParsedDemo,
    indices: &[usize],
    start_floor_tick: Option<i32>,
) -> Vec<usize> {
    let Some(start_tick) = start_floor_tick else {
        return indices.to_vec();
    };
    let filtered = indices
        .iter()
        .copied()
        .filter(|idx| parsed.rows[*idx].tick >= start_tick)
        .collect::<Vec<_>>();
    if filtered.is_empty() {
        indices.to_vec()
    } else {
        filtered
    }
}

fn choose_round_floor(
    parsed: &ParsedDemo,
    round: u32,
    indices: &[usize],
    freeze_end_ticks: &[i32],
) -> Option<i32> {
    let event_floor = if is_pistol_round(round) {
        choose_pistol_freeze_end(parsed, indices, freeze_end_ticks)
    } else {
        freeze_end_ticks.first().copied()
    };

    event_floor
        .or_else(|| infer_latest_freeze_transition_floor(parsed, indices))
        .or_else(|| infer_round_start_from_movement(parsed, indices))
}

fn choose_pistol_freeze_end(
    parsed: &ParsedDemo,
    indices: &[usize],
    freeze_end_ticks: &[i32],
) -> Option<i32> {
    freeze_end_ticks
        .iter()
        .copied()
        .max_by_key(|tick| (freeze_end_quality(parsed, indices, *tick), *tick))
}

fn freeze_end_quality(parsed: &ParsedDemo, indices: &[usize], tick: i32) -> i32 {
    let pre_window = seconds_to_ticks(parsed, 35.0);
    let post_window = seconds_to_ticks(parsed, 12.0);
    let before = count_players_in_window(
        parsed,
        indices,
        tick.saturating_sub(pre_window),
        tick,
        PlayerWindowKind::Freeze,
    );
    let after = count_players_in_window(
        parsed,
        indices,
        tick,
        tick.saturating_add(post_window),
        PlayerWindowKind::Active,
    );

    (after.total().min(10) as i32 * 1_000)
        + (after.balanced_score() as i32 * 100)
        + (before.total().min(10) as i32 * 10)
}

fn infer_latest_freeze_transition_floor(parsed: &ParsedDemo, indices: &[usize]) -> Option<i32> {
    let max_gap = seconds_to_ticks(parsed, 45.0);
    let freeze_by_tick = count_players_by_tick(parsed, indices, PlayerWindowKind::Freeze);
    let active_by_tick = count_players_by_tick(parsed, indices, PlayerWindowKind::Active);
    let mut latest = None;

    for (freeze_tick, freeze_players) in freeze_by_tick {
        if freeze_players.total() < 6 {
            continue;
        }
        let active_tick = active_by_tick
            .range((freeze_tick + 1)..=freeze_tick.saturating_add(max_gap))
            .find_map(|(tick, players)| (players.total() >= 6).then_some(*tick));
        if let Some(active_tick) = active_tick {
            latest = Some(active_tick);
        }
    }

    latest
}

fn infer_round_start_from_movement(parsed: &ParsedDemo, indices: &[usize]) -> Option<i32> {
    let mut by_tick: BTreeMap<i32, usize> = BTreeMap::new();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if !row.is_alive || row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let speed2d =
            (row.velocity[0] * row.velocity[0] + row.velocity[1] * row.velocity[1]).sqrt();
        if speed2d >= 30.0 {
            *by_tick.entry(row.tick).or_default() += 1;
        }
    }
    by_tick
        .into_iter()
        .find_map(|(tick, moving_players)| (moving_players >= 3).then_some(tick))
}

#[derive(Clone, Copy, Debug)]
enum PlayerWindowKind {
    Freeze,
    Active,
}

#[derive(Clone, Debug, Default)]
struct TeamPlayerSet {
    t: BTreeSet<u64>,
    ct: BTreeSet<u64>,
}

impl TeamPlayerSet {
    fn add(&mut self, row: &ParsedPlayerTick) {
        match row.team_num {
            2 => {
                self.t.insert(row.steam_id);
            }
            3 => {
                self.ct.insert(row.steam_id);
            }
            _ => {}
        }
    }

    fn total(&self) -> usize {
        self.t.len() + self.ct.len()
    }

    fn balanced_score(&self) -> usize {
        self.t.len().min(5) + self.ct.len().min(5)
    }
}

fn count_players_in_window(
    parsed: &ParsedDemo,
    indices: &[usize],
    start_tick: i32,
    end_tick: i32,
    kind: PlayerWindowKind,
) -> TeamPlayerSet {
    let mut players = TeamPlayerSet::default();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if row.tick < start_tick || row.tick > end_tick || !row_qualifies_for_window(row, kind) {
            continue;
        }
        players.add(row);
    }
    players
}

fn count_players_by_tick(
    parsed: &ParsedDemo,
    indices: &[usize],
    kind: PlayerWindowKind,
) -> BTreeMap<i32, TeamPlayerSet> {
    let mut by_tick = BTreeMap::new();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if !row_qualifies_for_window(row, kind) {
            continue;
        }
        by_tick
            .entry(row.tick)
            .or_insert_with(TeamPlayerSet::default)
            .add(row);
    }
    by_tick
}

fn row_qualifies_for_window(row: &ParsedPlayerTick, kind: PlayerWindowKind) -> bool {
    if !row.is_alive || row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
        return false;
    }
    match kind {
        PlayerWindowKind::Freeze => row.is_freeze_period,
        PlayerWindowKind::Active => row.round_in_progress && !row.is_freeze_period,
    }
}

fn seconds_to_ticks(parsed: &ParsedDemo, seconds: f32) -> i32 {
    (parsed.tick_rate.max(1.0) * seconds).round() as i32
}

fn is_pistol_round(round: u32) -> bool {
    round == 0 || round == 12
}

#[derive(Clone, Copy, Debug, Default)]
struct TeamSpan {
    rows: usize,
    first_tick: i32,
    last_tick: i32,
}

#[derive(Clone, Debug, Default)]
struct PlayerSpans {
    by_team: BTreeMap<u8, TeamSpan>,
}

#[derive(Clone, Copy, Debug)]
struct StableWindow {
    start_tick: i32,
    end_tick: i32,
    t_players: usize,
    ct_players: usize,
    valid_rows: usize,
}

fn select_stable_window(parsed: &ParsedDemo, indices: &[usize]) -> Option<StableWindow> {
    if indices.is_empty() {
        return None;
    }

    let mut players: BTreeMap<u64, PlayerSpans> = BTreeMap::new();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if !row.is_alive || row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let spans = players.entry(row.steam_id).or_default();
        let span = spans.by_team.entry(row.team_num).or_insert(TeamSpan {
            rows: 0,
            first_tick: row.tick,
            last_tick: row.tick,
        });
        span.rows += 1;
        span.first_tick = span.first_tick.min(row.tick);
        span.last_tick = span.last_tick.max(row.tick);
    }

    let mut assignments: BTreeMap<u64, (u8, TeamSpan)> = BTreeMap::new();
    for (steam_id, spans) in players {
        let Some((&team, &span)) = spans
            .by_team
            .iter()
            .max_by_key(|(_, span)| (span.last_tick, span.rows))
        else {
            continue;
        };
        assignments.insert(steam_id, (team, span));
    }

    let mut start_tick = None::<i32>;
    let mut end_tick = None::<i32>;
    let mut t_players = 0_usize;
    let mut ct_players = 0_usize;
    for (team, span) in assignments.values() {
        start_tick = Some(start_tick.map_or(span.first_tick, |tick| tick.max(span.first_tick)));
        end_tick = Some(end_tick.map_or(span.last_tick, |tick| tick.max(span.last_tick)));
        match team {
            2 => t_players += 1,
            3 => ct_players += 1,
            _ => {}
        }
    }
    let start_tick = start_tick?;
    let end_tick = end_tick?;

    let mut valid_rows = 0_usize;
    for idx in indices {
        let row = &parsed.rows[*idx];
        if row.tick < start_tick || row.tick > end_tick || !row.is_alive {
            continue;
        }
        let Some((assigned_team, _)) = assignments.get(&row.steam_id) else {
            continue;
        };
        if row.team_num == *assigned_team {
            valid_rows += 1;
        }
    }

    Some(StableWindow {
        start_tick,
        end_tick,
        t_players,
        ct_players,
        valid_rows,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{ParsedGameEvent, ParsedPlayerTick};

    fn row(round: u32, tick: i32, team_num: u8, steam_id: u64) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id,
            name: steam_id.to_string(),
            team_num,
            is_alive: true,
            round,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: None,
            origin: [0.0, 0.0, 0.0],
            velocity: [0.0, 0.0, 0.0],
            pitch: 0.0,
            yaw: 0.0,
            buttons: 0,
            buttonstates_present: false,
            buttonstate1: 0,
            buttonstate2: 0,
            buttonstate3: 0,
            usercmd_forward_move: None,
            usercmd_left_move: None,
            usercmd_up_move: None,
            usercmd_pitch: None,
            usercmd_yaw: None,
            usercmd_roll: None,
            usercmd_mouse_dx: None,
            usercmd_mouse_dy: None,
            usercmd_weapon_select: None,
            usercmd_left_hand_desired: None,
            usercmd_client_tick: None,
            usercmd_attack1_start_history_index: -1,
            usercmd_attack2_start_history_index: -1,
            input_history: Vec::new(),
            item_def_idx: -1,
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

    #[test]
    fn flags_underfilled_round_as_suspicious() {
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows: vec![row(1, 0, 2, 1), row(1, 640, 3, 2)],
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };
        let analysis = analyze_demo(&parsed, AnalysisOptions::default());
        assert_eq!(analysis.rounds[0].status, RoundStatus::Suspicious);
        assert!(analysis.rounds[0]
            .problems
            .iter()
            .any(|p| p.contains("available players")));
    }

    fn four_v_five_disconnect_demo(include_death_event: bool) -> ParsedDemo {
        let mut rows = Vec::new();
        for steam_id in 1..=10 {
            let team_num = if steam_id <= 5 { 2 } else { 3 };
            let mut frozen = row(11, 100, team_num, steam_id);
            frozen.round_in_progress = false;
            frozen.is_freeze_period = true;
            rows.push(frozen);
        }

        let mut dead = row(11, 160, 3, 10);
        dead.round_in_progress = false;
        dead.is_freeze_period = true;
        dead.is_alive = false;
        rows.push(dead);

        for steam_id in 1..=9 {
            let team_num = if steam_id <= 5 { 2 } else { 3 };
            rows.push(row(11, 200, team_num, steam_id));
            rows.push(row(11, 1_000, team_num, steam_id));
        }

        ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![180],
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows,
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: include_death_event
                .then(|| ParsedGameEvent {
                    tick: 150,
                    name: "player_death".to_string(),
                    user_steam_id: Some(10),
                    ..ParsedGameEvent::default()
                })
                .into_iter()
                .collect(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        }
    }

    #[test]
    fn selects_evidence_backed_four_v_five_round_as_partial() {
        let analysis = analyze_demo(
            &four_v_five_disconnect_demo(true),
            AnalysisOptions::default(),
        );
        let round = &analysis.rounds[0];

        assert_eq!(round.status, RoundStatus::Partial);
        assert_eq!((round.t_players, round.ct_players), (5, 4));
        assert!(round.selected_by_default());
        assert!(round.problems.is_empty());
    }

    #[test]
    fn keeps_four_v_five_round_suspicious_without_explicit_death_evidence() {
        let analysis = analyze_demo(
            &four_v_five_disconnect_demo(false),
            AnalysisOptions::default(),
        );
        let round = &analysis.rounds[0];

        assert_eq!(round.status, RoundStatus::Suspicious);
        assert!(!round.selected_by_default());
        assert!(round
            .problems
            .iter()
            .any(|problem| problem.contains("available players 9 != 10")));
    }

    #[test]
    fn flags_round_without_active_window_as_suspicious() {
        let mut frozen = row(1, 0, 2, 1);
        frozen.round_in_progress = false;
        frozen.is_freeze_period = true;
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows: vec![frozen],
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].status, RoundStatus::Suspicious);
        assert!(analysis.rounds[0]
            .problems
            .iter()
            .any(|p| p.contains("available players")));
    }

    #[test]
    fn recovers_halftime_side_swap_window_from_raw_rows() {
        let mut rows = Vec::new();
        let mut old_side = row(12, 0, 2, 1);
        old_side.round_in_progress = false;
        old_side.is_freeze_period = true;
        rows.push(old_side);

        for steam_id in 1..=5 {
            for tick in [100, 800] {
                let mut r = row(12, tick, 3, steam_id);
                r.round_in_progress = false;
                r.is_freeze_period = true;
                rows.push(r);
            }
        }
        for steam_id in 6..=10 {
            for tick in [100, 800] {
                let mut r = row(12, tick, 2, steam_id);
                r.round_in_progress = false;
                r.is_freeze_period = true;
                rows.push(r);
            }
        }

        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows,
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());
        let summary = &analysis.rounds[0];

        assert_eq!(summary.status, RoundStatus::Suspicious);
        assert_eq!(summary.start_tick, 100);
        assert_eq!(summary.t_players, 5);
        assert_eq!(summary.ct_players, 5);
    }

    #[test]
    fn uses_round_freeze_end_event_as_window_floor() {
        let rows = vec![
            row(1, 100, 2, 1),
            row(1, 100, 3, 2),
            row(1, 200, 2, 1),
            row(1, 200, 3, 2),
        ];
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![150],
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows,
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].start_tick, 200);
    }

    #[test]
    fn pistol_round_uses_late_freeze_end_to_skip_warmup_activity() {
        let mut rows = Vec::new();
        for steam_id in 1..=5 {
            rows.push(row(0, 100, 2, steam_id));
            let mut frozen = row(0, 1_900, 2, steam_id);
            frozen.is_freeze_period = true;
            frozen.round_in_progress = false;
            rows.push(frozen);
            rows.push(row(0, 2_100, 2, steam_id));
        }
        for steam_id in 6..=10 {
            rows.push(row(0, 100, 3, steam_id));
            let mut frozen = row(0, 1_900, 3, steam_id);
            frozen.is_freeze_period = true;
            frozen.round_in_progress = false;
            rows.push(frozen);
            rows.push(row(0, 2_100, 3, steam_id));
        }
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![500, 2_000],
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows,
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].start_tick, 2_100);
    }

    #[test]
    fn pistol_round_infers_freeze_transition_when_event_is_missing() {
        let mut rows = Vec::new();
        for steam_id in 1..=5 {
            rows.push(row(12, 100, 2, steam_id));
            let mut frozen = row(12, 1_000, 3, steam_id);
            frozen.is_freeze_period = true;
            frozen.round_in_progress = false;
            rows.push(frozen);
            rows.push(row(12, 1_300, 3, steam_id));
        }
        for steam_id in 6..=10 {
            rows.push(row(12, 100, 3, steam_id));
            let mut frozen = row(12, 1_000, 2, steam_id);
            frozen.is_freeze_period = true;
            frozen.round_in_progress = false;
            rows.push(frozen);
            rows.push(row(12, 1_300, 2, steam_id));
        }
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_test".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows,
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].start_tick, 1_300);
        assert_eq!(analysis.rounds[0].t_players, 5);
        assert_eq!(analysis.rounds[0].ct_players, 5);
    }
}
