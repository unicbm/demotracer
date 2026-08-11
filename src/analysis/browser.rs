/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use super::player_evidence::summarize_player_details;
use super::quality::{analyze_demo, AnalysisOptions};
use crate::model::{DemoAnalysis, ParsedDemo};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

pub use super::player_evidence::{
    BrowserCharmEvidence, BrowserCosmeticEvidence, BrowserPlayerDetails, BrowserStickerEvidence,
    BrowserViewmodelEvidence,
};

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserDemoAnalysis {
    #[serde(flatten)]
    pub analysis: DemoAnalysis,
    pub duration_seconds: f32,
    pub demo_patch_version: Option<i32>,
    pub demo_version_name: Option<String>,
    pub server_name: Option<String>,
    pub demo_source: Option<BrowserDemoSource>,
    pub players: Vec<BrowserPlayerSummary>,
    pub score: Option<BrowserScoreSummary>,
    #[serde(default)]
    pub round_outcomes: Vec<BrowserRoundOutcome>,
    #[serde(default)]
    pub friendly_fire: BrowserFriendlyFireSummary,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserFriendlyFireSummary {
    pub enabled: Option<bool>,
    pub evidence: String,
    pub damage_events: u32,
    pub damage: u32,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserRoundOutcome {
    pub round: u32,
    pub tick: i32,
    pub winner_side: u8,
    pub winner_team: String,
    pub reason: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserDemoSource {
    pub name: String,
    /// `serverName` is header-backed; `fileName` is only a filename inference.
    pub evidence: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserPlayerSummary {
    pub name: String,
    pub steam_id: String,
    pub side: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub player_color: Option<String>,
    /// Persistent match identity (`a` / `b`), independent from T/CT side swaps.
    pub team: String,
    pub team_name: Option<String>,
    pub score: Option<i32>,
    pub kills: Option<u32>,
    pub deaths: Option<u32>,
    pub assists: Option<u32>,
    pub mvps: Option<u32>,
    pub rounds: usize,
    pub rows: usize,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub details: Option<BrowserPlayerDetails>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserScoreSummary {
    pub team_a: BrowserTeamScore,
    pub team_b: BrowserTeamScore,
    /// `final` has an explicit match-end event; `completed` is exact only through
    /// the last observed completed round.
    pub status: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTeamScore {
    pub score: u32,
    pub name: Option<String>,
}

pub fn analyze_browser_demo(parsed: &ParsedDemo, options: AnalysisOptions) -> BrowserDemoAnalysis {
    let analysis = analyze_demo(parsed, options);
    let match_summary = summarize_match(parsed);
    let stats_rounds = match_summary.score.as_ref().and_then(|score| {
        score
            .team_a
            .score
            .checked_add(score.team_b.score)
            .filter(|rounds| *rounds > 0)
    });
    let combat_stats = summarize_combat_stats(
        parsed,
        &match_summary.team_by_steam_id,
        match_summary.match_window,
        &match_summary.round_outcomes,
    );
    let friendly_fire = summarize_friendly_fire(
        parsed,
        &match_summary.team_by_steam_id,
        match_summary.match_window,
    );
    BrowserDemoAnalysis {
        duration_seconds: summarize_duration(parsed, &analysis),
        demo_patch_version: parsed.demo_patch_version,
        demo_version_name: parsed.demo_version_name.clone(),
        server_name: parsed.server_name.clone(),
        demo_source: infer_demo_source(&parsed.stem, parsed.server_name.as_deref()),
        analysis,
        players: summarize_players_in_window(
            parsed,
            &match_summary.team_by_steam_id,
            match_summary.match_window,
            stats_rounds,
            combat_stats.as_ref(),
        ),
        score: match_summary.score,
        round_outcomes: match_summary.round_outcomes,
        friendly_fire,
    }
}

fn summarize_duration(parsed: &ParsedDemo, analysis: &DemoAnalysis) -> f32 {
    if !analysis.tick_rate.is_finite() || analysis.tick_rate <= 0.0 {
        return 0.0;
    }
    let observed_ticks = parsed
        .rows
        .iter()
        .map(|row| row.tick)
        .chain(parsed.events.iter().map(|event| event.tick))
        .chain(parsed.projectiles.iter().flat_map(|projectile| {
            [Some(projectile.tick), projectile.effect_tick]
                .into_iter()
                .flatten()
        }))
        .chain(parsed.voice_frames.iter().map(|frame| frame.tick))
        .chain(parsed.round_freeze_end_ticks.iter().copied())
        .chain(parsed.bomb_beginplant_ticks.iter().copied())
        .chain(parsed.bomb_planted_ticks.iter().copied());
    let (min_tick, max_tick) = observed_ticks.fold((None::<i32>, None::<i32>), |range, tick| {
        (
            Some(range.0.map_or(tick, |current| current.min(tick))),
            Some(range.1.map_or(tick, |current| current.max(tick))),
        )
    });
    let tick_span = match (min_tick, max_tick) {
        (Some(first), Some(last)) => last.saturating_sub(first),
        _ => analysis
            .rounds
            .first()
            .zip(analysis.rounds.last())
            .map(|(first, last)| last.end_tick.saturating_sub(first.start_tick))
            .unwrap_or_default(),
    };
    let observed_duration = if tick_span <= 0 {
        0.0
    } else {
        tick_span as f32 / analysis.tick_rate
    };
    parsed
        .playback_time_seconds
        .filter(|value| value.is_finite() && *value >= 0.0)
        .unwrap_or_default()
        .max(observed_duration)
}

#[cfg(test)]
fn summarize_players(
    parsed: &ParsedDemo,
    team_by_steam_id: &BTreeMap<u64, PersistentTeam>,
) -> Vec<BrowserPlayerSummary> {
    summarize_players_in_window(parsed, team_by_steam_id, None, None, None)
}

fn summarize_players_in_window(
    parsed: &ParsedDemo,
    team_by_steam_id: &BTreeMap<u64, PersistentTeam>,
    match_window: Option<(i32, i32)>,
    stats_rounds: Option<u32>,
    combat_stats: Option<&BTreeMap<u64, CombatPlayerStats>>,
) -> Vec<BrowserPlayerSummary> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    let mut details = summarize_player_details(parsed, match_window, stats_rounds);
    if let Some(combat_stats) = combat_stats {
        for (&steam_id, stats) in combat_stats {
            let details = details.entry(steam_id).or_default();
            details.first_kills = Some(stats.first_kills);
            details.first_deaths = Some(stats.first_deaths);
            details.two_k_rounds = Some(stats.two_k_rounds);
            details.three_k_rounds = Some(stats.three_k_rounds);
            details.four_k_rounds = Some(stats.four_k_rounds);
            details.five_k_rounds = Some(stats.five_k_rounds);
        }
    }
    for row in parsed.rows.iter().filter(|row| {
        match_window
            .is_none_or(|(start_tick, end_tick)| row.tick >= start_tick && row.tick <= end_tick)
    }) {
        if row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let player = players.entry(row.steam_id).or_default();
        player.rows += 1;
        player.rounds.insert(row.round);
        player.update_identity(row);
        player.update_scoreboard(row);
    }

    players
        .into_iter()
        .map(|(steam_id, player)| {
            let side = side_label(player.side.unwrap_or_default());
            let name = if player.name.is_empty() {
                steam_id.to_string()
            } else {
                player.name
            };
            let scoreboard = player.scoreboard.unwrap_or_default();
            BrowserPlayerSummary {
                name,
                steam_id: steam_id.to_string(),
                side,
                player_color: player.player_color,
                team: team_by_steam_id
                    .get(&steam_id)
                    .map(|team| team.as_str())
                    .unwrap_or("unknown")
                    .to_string(),
                team_name: player.team_name,
                score: scoreboard.score,
                kills: scoreboard.kills,
                deaths: scoreboard.deaths,
                assists: scoreboard.assists,
                mvps: scoreboard.mvps,
                rounds: player.rounds.len(),
                rows: player.rows,
                details: details.remove(&steam_id),
            }
        })
        .collect()
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PersistentTeam {
    A,
    B,
}

impl PersistentTeam {
    fn as_str(self) -> &'static str {
        match self {
            Self::A => "a",
            Self::B => "b",
        }
    }
}

#[derive(Default)]
struct MatchSummary {
    score: Option<BrowserScoreSummary>,
    team_by_steam_id: BTreeMap<u64, PersistentTeam>,
    match_window: Option<(i32, i32)>,
    round_outcomes: Vec<BrowserRoundOutcome>,
}

#[derive(Clone)]
struct CompletedRound {
    tick: i32,
    winner_side: u8,
    reason: Option<String>,
}

#[derive(Clone, Copy, Debug, Default)]
struct CombatPlayerStats {
    first_kills: u32,
    first_deaths: u32,
    two_k_rounds: u32,
    three_k_rounds: u32,
    four_k_rounds: u32,
    five_k_rounds: u32,
}

fn summarize_match(parsed: &ParsedDemo) -> MatchSummary {
    let Some(match_start_tick) = parsed
        .events
        .iter()
        .filter(|event| event.name == "round_announce_match_start")
        .map(|event| event.tick)
        .max()
    else {
        // Without a formal match-start marker we cannot distinguish warmup or
        // knife-round results from regulation rounds conservatively.
        return MatchSummary::default();
    };

    let match_end_tick = parsed
        .events
        .iter()
        .filter(|event| event.name == "cs_win_panel_match" && event.tick >= match_start_tick)
        .map(|event| event.tick)
        .min();

    let mut winners_by_tick = BTreeMap::<i32, (u8, Option<String>)>::new();
    for event in parsed.events.iter().filter(|event| {
        event.name == "round_end"
            && event.tick >= match_start_tick
            && match_end_tick.is_none_or(|end_tick| event.tick <= end_tick)
    }) {
        // Tournament demos can emit a side-less `round_end` while resetting
        // the server between halves. It is not a completed scoring round and
        // must not invalidate every otherwise exact winner observation.
        let Some(winner_side) = event.winner_side else {
            continue;
        };
        match winners_by_tick.entry(event.tick) {
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert((winner_side, event.round_end_reason.clone()));
            }
            std::collections::btree_map::Entry::Occupied(mut entry) => {
                if entry.get().0 != winner_side {
                    return MatchSummary::default();
                }
                if entry.get().1.is_none() && event.round_end_reason.is_some() {
                    entry.get_mut().1 = event.round_end_reason.clone();
                }
            }
        }
    }
    if winners_by_tick.is_empty() {
        return MatchSummary::default();
    }
    let rounds = winners_by_tick
        .into_iter()
        .map(|(tick, (winner_side, reason))| CompletedRound {
            tick,
            winner_side,
            reason,
        })
        .collect::<Vec<_>>();
    let last_round_tick = rounds.last().map(|round| round.tick).unwrap_or_default();

    // Players still present in the last completed round anchor two stable
    // rosters. Earlier players are assigned later by comparing their observed
    // side with those rosters across rounds, so a player who leaves before the
    // side swap is not silently moved to the opponent.
    let previous_round_tick = rounds
        .iter()
        .rev()
        .nth(1)
        .map(|round| round.tick)
        .unwrap_or(match_start_tick.saturating_sub(1));
    let mut anchor_identity = BTreeMap::<u64, (i32, u8)>::new();
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && row.tick > previous_round_tick
            && row.tick <= last_round_tick
    }) {
        let entry = anchor_identity
            .entry(row.steam_id)
            .or_insert((row.tick, row.team_num));
        if row.tick >= entry.0 {
            *entry = (row.tick, row.team_num);
        }
    }
    if !anchor_identity.values().any(|(_, identity)| *identity == 2)
        || !anchor_identity.values().any(|(_, identity)| *identity == 3)
    {
        return MatchSummary {
            match_window: Some((match_start_tick, match_end_tick.unwrap_or(last_round_tick))),
            ..MatchSummary::default()
        };
    }

    // A round-end interval can straddle a halftime/overtime side swap. Using
    // all rows in that interval lets the long pre-round transition outvote the
    // actual round, which moves one win to the wrong persistent team. Anchor
    // each round at its freeze-end event and sample the first roster state
    // after that event instead.
    let round_live_start_ticks = round_live_start_ticks(parsed, &rounds, match_start_tick);
    let mut identity_sides =
        identity_sides_at_round_start(parsed, &rounds, &round_live_start_ticks, &anchor_identity);
    fill_unambiguous_identity_side_gaps(&mut identity_sides);
    let Some(first_sides) = identity_sides.first().copied().flatten() else {
        return MatchSummary {
            match_window: Some((match_start_tick, match_end_tick.unwrap_or(last_round_tick))),
            ..MatchSummary::default()
        };
    };
    let team_a_identity: u8 = if first_sides[0] == 2 { 2 } else { 3 };

    let mut player_team_votes = BTreeMap::<u64, [usize; 2]>::new();
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && row.tick >= match_start_tick
            && row.tick <= last_round_tick
    }) {
        let round_index = rounds.partition_point(|round| round.tick < row.tick);
        if round_index >= rounds.len()
            || (round_index > 0 && row.tick <= rounds[round_index - 1].tick)
        {
            continue;
        }
        let Some(live_start_tick) = round_live_start_ticks[round_index] else {
            continue;
        };
        if row.tick < live_start_tick {
            continue;
        }
        let Some(sides) = identity_sides[round_index] else {
            continue;
        };
        let team_a_side = sides[usize::from(team_a_identity - 2)];
        let votes = player_team_votes.entry(row.steam_id).or_default();
        if row.team_num == team_a_side {
            votes[0] += 1;
        } else if row.team_num == opposite_side(team_a_side) {
            votes[1] += 1;
        }
    }
    let mut team_by_steam_id = player_team_votes
        .into_iter()
        .filter_map(|(steam_id, votes)| match votes {
            [team_a, team_b] if team_a > team_b => Some((steam_id, PersistentTeam::A)),
            [team_a, team_b] if team_b > team_a => Some((steam_id, PersistentTeam::B)),
            _ => None,
        })
        .collect::<BTreeMap<_, _>>();
    for (&steam_id, &(_, identity)) in &anchor_identity {
        team_by_steam_id.insert(
            steam_id,
            if identity == team_a_identity {
                PersistentTeam::A
            } else {
                PersistentTeam::B
            },
        );
    }

    let mut team_a_score = 0_u32;
    let mut team_b_score = 0_u32;
    let mut round_outcomes = Vec::with_capacity(rounds.len());
    for (round_index, (round, sides)) in rounds.iter().zip(&identity_sides).enumerate() {
        let Some(sides) = sides else {
            return MatchSummary {
                score: None,
                team_by_steam_id,
                match_window: Some((match_start_tick, match_end_tick.unwrap_or(last_round_tick))),
                round_outcomes: Vec::new(),
            };
        };
        let winning_identity = if sides[0] == round.winner_side { 2 } else { 3 };
        let winning_team = if winning_identity == team_a_identity {
            team_a_score += 1;
            PersistentTeam::A
        } else {
            team_b_score += 1;
            PersistentTeam::B
        };
        round_outcomes.push(BrowserRoundOutcome {
            round: u32::try_from(round_index + 1).unwrap_or(u32::MAX),
            tick: round.tick,
            winner_side: round.winner_side,
            winner_team: winning_team.as_str().to_string(),
            reason: round.reason.clone(),
        });
    }

    let is_final = match_end_tick.is_some_and(|end_tick| end_tick >= last_round_tick);
    MatchSummary {
        score: Some(BrowserScoreSummary {
            team_a: BrowserTeamScore {
                score: team_a_score,
                name: team_name_for_team(
                    parsed,
                    &team_by_steam_id,
                    PersistentTeam::A,
                    match_start_tick,
                    match_end_tick.unwrap_or(last_round_tick),
                ),
            },
            team_b: BrowserTeamScore {
                score: team_b_score,
                name: team_name_for_team(
                    parsed,
                    &team_by_steam_id,
                    PersistentTeam::B,
                    match_start_tick,
                    match_end_tick.unwrap_or(last_round_tick),
                ),
            },
            status: if is_final { "final" } else { "completed" }.to_string(),
        }),
        team_by_steam_id,
        match_window: Some((match_start_tick, match_end_tick.unwrap_or(last_round_tick))),
        round_outcomes,
    }
}

fn summarize_combat_stats(
    parsed: &ParsedDemo,
    team_by_steam_id: &BTreeMap<u64, PersistentTeam>,
    match_window: Option<(i32, i32)>,
    round_outcomes: &[BrowserRoundOutcome],
) -> Option<BTreeMap<u64, CombatPlayerStats>> {
    let (match_start_tick, match_end_tick) = match_window?;
    if team_by_steam_id.is_empty() || round_outcomes.is_empty() {
        return None;
    }

    let mut stats = team_by_steam_id
        .keys()
        .copied()
        .map(|steam_id| (steam_id, CombatPlayerStats::default()))
        .collect::<BTreeMap<_, _>>();
    let mut kills_by_player_round = BTreeMap::<(u64, usize), u32>::new();
    let mut opened_rounds = BTreeSet::<usize>::new();
    let mut deaths = parsed
        .events
        .iter()
        .filter(|event| {
            event.name == "player_death"
                && event.tick >= match_start_tick
                && event.tick <= match_end_tick
        })
        .collect::<Vec<_>>();
    deaths.sort_by_key(|event| event.tick);
    let has_death_events = !deaths.is_empty();
    let mut attributed_enemy_deaths = 0_u32;

    for event in deaths {
        let (Some(attacker), Some(victim)) = (event.attacker_steam_id, event.victim_steam_id)
        else {
            continue;
        };
        if attacker == 0 || victim == 0 || attacker == victim {
            continue;
        }
        let (Some(attacker_team), Some(victim_team)) = (
            team_by_steam_id.get(&attacker),
            team_by_steam_id.get(&victim),
        ) else {
            // A missing persistent identity means zero-valued multi-kill and
            // opening-duel totals would be misleading, so omit the metric set.
            return None;
        };
        if attacker_team == victim_team {
            continue;
        }
        let round_index = round_outcomes.partition_point(|round| round.tick < event.tick);
        if round_index >= round_outcomes.len() {
            continue;
        }

        attributed_enemy_deaths += 1;
        *kills_by_player_round
            .entry((attacker, round_index))
            .or_default() += 1;
        if opened_rounds.insert(round_index) {
            stats.entry(attacker).or_default().first_kills += 1;
            stats.entry(victim).or_default().first_deaths += 1;
        }
    }

    // If the demo exposes death events but their player identities cannot be
    // attributed, reporting a full table of zeroes is materially misleading.
    // Keep the metric unavailable until there is at least one usable enemy kill.
    if has_death_events && attributed_enemy_deaths == 0 {
        return None;
    }

    for ((steam_id, _), kills) in kills_by_player_round {
        let player = stats.entry(steam_id).or_default();
        match kills {
            2 => player.two_k_rounds += 1,
            3 => player.three_k_rounds += 1,
            4 => player.four_k_rounds += 1,
            5.. => player.five_k_rounds += 1,
            _ => {}
        }
    }
    Some(stats)
}

fn summarize_friendly_fire(
    parsed: &ParsedDemo,
    team_by_steam_id: &BTreeMap<u64, PersistentTeam>,
    match_window: Option<(i32, i32)>,
) -> BrowserFriendlyFireSummary {
    let mut damage_events = 0_u32;
    let mut damage = 0_u32;
    if let Some((match_start_tick, match_end_tick)) = match_window {
        for event in parsed.events.iter().filter(|event| {
            event.name == "player_hurt"
                && event.tick >= match_start_tick
                && event.tick <= match_end_tick
        }) {
            let (Some(attacker), Some(victim), Some(event_damage)) =
                (event.attacker_steam_id, event.victim_steam_id, event.damage)
            else {
                continue;
            };
            if attacker == 0 || victim == 0 || attacker == victim || event_damage <= 0 {
                continue;
            }
            if team_by_steam_id.get(&attacker).is_some_and(|team| {
                team_by_steam_id
                    .get(&victim)
                    .is_some_and(|victim_team| victim_team == team)
            }) {
                damage_events = damage_events.saturating_add(1);
                damage = damage.saturating_add(u32::try_from(event_damage).unwrap_or(u32::MAX));
            }
        }
    }

    let convar = friendly_fire_convar_evidence(parsed, match_window);
    match convar {
        FriendlyFireConVarEvidence::Stable(enabled) if !enabled && damage_events > 0 => {
            BrowserFriendlyFireSummary {
                enabled: None,
                evidence: "conflictingEvidence".to_string(),
                damage_events,
                damage,
            }
        }
        FriendlyFireConVarEvidence::Stable(enabled) => BrowserFriendlyFireSummary {
            enabled: Some(enabled),
            evidence: "serverConVar".to_string(),
            damage_events,
            damage,
        },
        FriendlyFireConVarEvidence::Changed => BrowserFriendlyFireSummary {
            enabled: None,
            evidence: "serverConVarChanged".to_string(),
            damage_events,
            damage,
        },
        FriendlyFireConVarEvidence::Missing if damage_events > 0 => BrowserFriendlyFireSummary {
            enabled: Some(true),
            evidence: "observedDamage".to_string(),
            damage_events,
            damage,
        },
        FriendlyFireConVarEvidence::Missing => BrowserFriendlyFireSummary {
            enabled: None,
            evidence: "unavailable".to_string(),
            damage_events,
            damage,
        },
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum FriendlyFireConVarEvidence {
    Stable(bool),
    Changed,
    Missing,
}

fn friendly_fire_convar_evidence(
    parsed: &ParsedDemo,
    match_window: Option<(i32, i32)>,
) -> FriendlyFireConVarEvidence {
    let mut observations = parsed
        .server_convars
        .iter()
        .filter(|value| value.name.trim().eq_ignore_ascii_case("mp_friendlyfire"))
        .filter_map(|value| parse_convar_bool(&value.value).map(|enabled| (value.tick, enabled)))
        .collect::<Vec<_>>();
    observations.sort_by_key(|(tick, _)| *tick);
    if observations.is_empty() {
        return FriendlyFireConVarEvidence::Missing;
    }

    let relevant = if let Some((start_tick, end_tick)) = match_window {
        let baseline = observations
            .iter()
            .rev()
            .find(|(tick, _)| *tick <= start_tick)
            .copied();
        baseline
            .into_iter()
            .chain(
                observations
                    .iter()
                    .copied()
                    .filter(|(tick, _)| *tick > start_tick && *tick <= end_tick),
            )
            .collect::<Vec<_>>()
    } else {
        observations
    };
    let Some((_, first)) = relevant.first().copied() else {
        return FriendlyFireConVarEvidence::Missing;
    };
    if relevant.iter().all(|(_, enabled)| *enabled == first) {
        FriendlyFireConVarEvidence::Stable(first)
    } else {
        FriendlyFireConVarEvidence::Changed
    }
}

fn parse_convar_bool(value: &str) -> Option<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "1" | "true" | "on" | "yes" => Some(true),
        "0" | "false" | "off" | "no" => Some(false),
        _ => None,
    }
}

fn round_live_start_ticks(
    parsed: &ParsedDemo,
    rounds: &[CompletedRound],
    match_start_tick: i32,
) -> Vec<Option<i32>> {
    let Some(last_round_tick) = rounds.last().map(|round| round.tick) else {
        return Vec::new();
    };
    let mut freeze_ticks = parsed
        .round_freeze_end_ticks
        .iter()
        .copied()
        .filter(|tick| *tick >= match_start_tick && *tick <= last_round_tick)
        .collect::<Vec<_>>();
    freeze_ticks.sort_unstable();
    freeze_ticks.dedup();

    let mut result = vec![None; rounds.len()];
    let mut freeze_index = 0_usize;
    let mut previous_round_tick = match_start_tick.saturating_sub(1);
    for (round_index, round) in rounds.iter().enumerate() {
        while freeze_index < freeze_ticks.len() && freeze_ticks[freeze_index] <= previous_round_tick
        {
            freeze_index += 1;
        }
        while freeze_index < freeze_ticks.len() && freeze_ticks[freeze_index] <= round.tick {
            result[round_index] = Some(freeze_ticks[freeze_index]);
            freeze_index += 1;
        }
        previous_round_tick = round.tick;
    }
    result
}

fn identity_sides_at_round_start(
    parsed: &ParsedDemo,
    rounds: &[CompletedRound],
    round_live_start_ticks: &[Option<i32>],
    anchor_identity: &BTreeMap<u64, (i32, u8)>,
) -> Vec<Option<[u8; 2]>> {
    let mut sample_ticks = vec![None::<i32>; rounds.len()];
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && anchor_identity.contains_key(&row.steam_id)
    }) {
        let round_index = rounds.partition_point(|round| round.tick < row.tick);
        if round_index >= rounds.len() {
            continue;
        }
        let Some(live_start_tick) = round_live_start_ticks[round_index] else {
            continue;
        };
        if row.tick < live_start_tick {
            continue;
        }
        sample_ticks[round_index] = Some(
            sample_ticks[round_index]
                .map(|current| current.min(row.tick))
                .unwrap_or(row.tick),
        );
    }

    let mut observations = vec![[[0_usize; 2]; 2]; rounds.len()];
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && anchor_identity.contains_key(&row.steam_id)
    }) {
        let round_index = rounds.partition_point(|round| round.tick < row.tick);
        if round_index >= rounds.len() || sample_ticks[round_index] != Some(row.tick) {
            continue;
        }
        let identity = anchor_identity[&row.steam_id].1;
        observations[round_index][usize::from(identity - 2)][usize::from(row.team_num - 2)] += 1;
    }

    observations.iter().map(complete_identity_sides).collect()
}

fn fill_unambiguous_identity_side_gaps(identity_sides: &mut [Option<[u8; 2]>]) {
    for index in 0..identity_sides.len() {
        if identity_sides[index].is_some() {
            continue;
        }
        let previous = identity_sides[..index]
            .iter()
            .rev()
            .copied()
            .flatten()
            .next();
        let next = identity_sides[index + 1..].iter().copied().flatten().next();
        if previous.is_some() && previous == next {
            identity_sides[index] = previous;
        }
    }
}

fn complete_identity_sides(observations: &[[usize; 2]; 2]) -> Option<[u8; 2]> {
    fn most_observed_side(counts: [usize; 2]) -> Option<u8> {
        match counts {
            [0, 0] => None,
            [t, ct] if t >= ct => Some(2),
            _ => Some(3),
        }
    }
    if let Some(side) = most_observed_side(observations[0]) {
        Some([side, opposite_side(side)])
    } else {
        most_observed_side(observations[1]).map(|side| [opposite_side(side), side])
    }
}

fn opposite_side(side: u8) -> u8 {
    if side == 2 {
        3
    } else {
        2
    }
}

fn team_name_for_team(
    parsed: &ParsedDemo,
    team_by_steam_id: &BTreeMap<u64, PersistentTeam>,
    team: PersistentTeam,
    match_start_tick: i32,
    match_end_tick: i32,
) -> Option<String> {
    parsed
        .rows
        .iter()
        .filter(|row| {
            row.tick >= match_start_tick
                && row.tick <= match_end_tick
                && team_by_steam_id
                    .get(&row.steam_id)
                    .is_some_and(|value| *value == team)
        })
        .filter_map(|row| clean_team_name(row).map(|name| (row.tick, name)))
        .max_by_key(|(tick, _)| *tick)
        .map(|(_, name)| name)
}

fn infer_demo_source(stem: &str, server_name: Option<&str>) -> Option<BrowserDemoSource> {
    let file_name = stem.to_lowercase();
    let server_name = server_name.unwrap_or_default().to_lowercase();
    let server_source = [
        ("faceit", "FACEIT"),
        ("5eplay", "5E"),
        ("5e play", "5E"),
        ("5ewin", "5E"),
        ("完美世界", "Perfect World"),
        ("wanmei", "Perfect World"),
        ("pracc.com", "PRACC"),
        ("pracc", "PRACC"),
        ("popflash", "PopFlash"),
        ("esportal", "Esportal"),
        ("gamers club", "Gamers Club"),
        ("gamersclub", "Gamers Club"),
        ("fastcup", "FASTCUP"),
        ("renown", "Renown"),
        ("cevo", "CEVO"),
        ("challengermode", "Challengermode"),
        ("esea", "ESEA"),
        ("starladder", "StarLadder"),
        ("flashpoint", "Flashpoint"),
        ("blast", "BLAST"),
        ("pgl", "PGL"),
        ("esl", "ESL"),
        ("matchzy", "MatchZy"),
        ("e-bot", "eBot"),
        ("ebot", "eBot"),
        ("get5", "Get5"),
    ]
    .into_iter()
    .find_map(|(needle, label)| server_name.contains(needle).then_some(label));
    if let Some(name) = server_source {
        return Some(BrowserDemoSource {
            name: name.to_string(),
            evidence: "serverName".to_string(),
        });
    }
    if server_name.contains("valve") {
        return Some(BrowserDemoSource {
            name: if server_name.contains("premier") {
                "Valve Premier"
            } else {
                "Matchmaking"
            }
            .to_string(),
            evidence: "serverName".to_string(),
        });
    }

    let leading_digits_then_team = {
        let digits = file_name
            .chars()
            .take_while(|ch| ch.is_ascii_digit())
            .count();
        digits > 0
            && file_name
                .get(digits..)
                .is_some_and(|rest| rest.starts_with("_team"))
    };
    let leading_g_digits_dash = file_name.strip_prefix('g').is_some_and(|rest| {
        let digits = rest.chars().take_while(|ch| ch.is_ascii_digit()).count();
        digits > 0 && rest.get(digits..).is_some_and(|tail| tail.starts_with('-'))
    });
    let file_source = if leading_g_digits_dash
        || file_name.starts_with("5e-")
        || file_name.starts_with("5e_")
        || file_name.contains("-5eplay-")
    {
        Some("5E")
    } else if leading_digits_then_team || file_name.contains("faceit") {
        Some("FACEIT")
    } else if file_name.contains("perfectworld") {
        Some("Perfect World")
    } else if file_name.contains("match730") || file_name.contains("matchmaking") {
        Some("Matchmaking")
    } else if file_name.contains("esl") {
        Some("ESL")
    } else if file_name.contains("esea") {
        Some("ESEA")
    } else {
        None
    };
    file_source.map(|name| BrowserDemoSource {
        name: name.to_string(),
        evidence: "fileName".to_string(),
    })
}

fn clean_team_name(row: &crate::model::ParsedPlayerTick) -> Option<String> {
    row.team_clan_name
        .as_deref()
        .filter(|value| is_real_team_name(value))
        .or_else(|| {
            row.team_name
                .as_deref()
                .filter(|value| is_real_team_name(value))
        })
        .map(str::trim)
        .map(str::to_string)
}

fn is_real_team_name(value: &str) -> bool {
    let normalized = value
        .chars()
        .filter(|character| character.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect::<String>();
    !normalized.is_empty()
        && !matches!(
            normalized.as_str(),
            "t" | "ct" | "terrorist" | "terrorists" | "counterterrorist" | "counterterrorists"
        )
}

fn side_label(team_num: u8) -> String {
    match team_num {
        2 => "T".to_string(),
        3 => "CT".to_string(),
        _ => "Unknown".to_string(),
    }
}

#[derive(Default)]
struct PlayerAccumulator {
    name: String,
    name_tick: Option<i32>,
    side: Option<u8>,
    side_tick: Option<i32>,
    player_color: Option<String>,
    player_color_tick: Option<i32>,
    team_name: Option<String>,
    team_name_tick: Option<i32>,
    scoreboard: Option<PlayerScoreboardSnapshot>,
    rows: usize,
    rounds: BTreeSet<u32>,
}

impl PlayerAccumulator {
    fn update_identity(&mut self, row: &crate::model::ParsedPlayerTick) {
        if !row.name.trim().is_empty() && is_at_least_as_recent(row.tick, self.name_tick) {
            self.name = row.name.trim().to_string();
            self.name_tick = Some(row.tick);
        }
        if is_at_least_as_recent(row.tick, self.side_tick) {
            self.side = Some(row.team_num);
            self.side_tick = Some(row.tick);
        }
        if let Some(player_color) = clean_player_color(row.player_color.as_deref()) {
            if is_at_least_as_recent(row.tick, self.player_color_tick) {
                self.player_color = Some(player_color);
                self.player_color_tick = Some(row.tick);
            }
        }
        if let Some(team_name) = clean_team_name(row) {
            if is_at_least_as_recent(row.tick, self.team_name_tick) {
                self.team_name = Some(team_name);
                self.team_name_tick = Some(row.tick);
            }
        }
    }

    fn update_scoreboard(&mut self, row: &crate::model::ParsedPlayerTick) {
        let mut candidate = PlayerScoreboardSnapshot {
            tick: row.tick,
            score: row.scoreboard_score,
            kills: row.scoreboard_kills,
            deaths: row.scoreboard_deaths,
            assists: row.scoreboard_assists,
            mvps: row.scoreboard_mvps,
        };
        if !candidate.has_data() {
            return;
        }
        if let Some(current) = self.scoreboard {
            if candidate.tick < current.tick {
                return;
            }
            candidate.score = candidate.score.or(current.score);
            candidate.kills = candidate.kills.or(current.kills);
            candidate.deaths = candidate.deaths.or(current.deaths);
            candidate.assists = candidate.assists.or(current.assists);
            candidate.mvps = candidate.mvps.or(current.mvps);
        }
        self.scoreboard = Some(candidate);
    }
}

fn clean_player_color(value: Option<&str>) -> Option<String> {
    let value = value?.trim().to_ascii_lowercase();
    matches!(
        value.as_str(),
        "blue" | "green" | "yellow" | "orange" | "purple"
    )
    .then_some(value)
}

fn is_at_least_as_recent(tick: i32, current_tick: Option<i32>) -> bool {
    current_tick.map(|current| tick >= current).unwrap_or(true)
}

#[derive(Clone, Copy, Default)]
struct PlayerScoreboardSnapshot {
    tick: i32,
    score: Option<i32>,
    kills: Option<u32>,
    deaths: Option<u32>,
    assists: Option<u32>,
    mvps: Option<u32>,
}

impl PlayerScoreboardSnapshot {
    fn has_data(self) -> bool {
        self.score.is_some()
            || self.kills.is_some()
            || self.deaths.is_some()
            || self.assists.is_some()
            || self.mvps.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{ParsedDemo, ParsedGameEvent, ParsedPlayerTick, ParsedServerConVar};

    fn row(round: u32, tick: i32, team_num: u8, steam_id: u64, name: &str) -> ParsedPlayerTick {
        ParsedPlayerTick {
            round,
            tick,
            team_num,
            steam_id,
            name: name.to_string(),
            is_alive: true,
            round_in_progress: true,
            ..ParsedPlayerTick::default()
        }
    }

    fn demo(rows: Vec<ParsedPlayerTick>) -> ParsedDemo {
        ParsedDemo {
            path: "demo.dem".to_string(),
            stem: "demo".to_string(),
            map: "de_anubis".to_string(),
            tick_rate: 64.0,
            rows,
            ..ParsedDemo::default()
        }
    }

    fn event(name: &str, tick: i32, winner_side: Option<u8>) -> ParsedGameEvent {
        ParsedGameEvent {
            name: name.to_string(),
            tick,
            winner_side,
            ..ParsedGameEvent::default()
        }
    }

    fn scored_demo(team_a_wins: usize, team_b_wins: usize, final_marker: bool) -> ParsedDemo {
        let total_rounds = team_a_wins + team_b_wins;
        let mut rows = Vec::new();
        let mut round_freeze_end_ticks = Vec::new();
        let mut events = vec![event("round_announce_match_start", 10, None)];
        for round_index in 0..total_rounds {
            let tick = 100 + i32::try_from(round_index).unwrap() * 100;
            let freeze_end_tick = tick - 20;
            round_freeze_end_ticks.push(freeze_end_tick);
            let team_a_side = if round_index < 12 {
                2
            } else if round_index < 24 {
                3
            } else if ((round_index - 24) / 3) % 2 == 0 {
                2
            } else {
                3
            };
            let team_b_side = opposite_side(team_a_side);
            let team_a_won = round_index < team_a_wins;
            events.push(event(
                "round_end",
                tick,
                Some(if team_a_won { team_a_side } else { team_b_side }),
            ));
            let mut alpha = row(
                u32::try_from(round_index).unwrap(),
                freeze_end_tick,
                team_a_side,
                1,
                "alpha",
            );
            alpha.team_clan_name = Some("Alpha".to_string());
            let mut bravo = row(
                u32::try_from(round_index).unwrap(),
                freeze_end_tick,
                team_b_side,
                2,
                "bravo",
            );
            bravo.team_clan_name = Some("Bravo".to_string());
            rows.extend([alpha, bravo]);
        }
        if final_marker {
            events.push(event(
                "cs_win_panel_match",
                100 + i32::try_from(total_rounds).unwrap() * 100,
                None,
            ));
        }
        let mut parsed = demo(rows);
        parsed.events = events;
        parsed.round_freeze_end_ticks = round_freeze_end_ticks;
        parsed
    }

    #[test]
    fn summarizes_players_by_steam_id_side_rounds_and_rows() {
        let mut rows = Vec::new();
        for steam_id in 1..=5 {
            rows.push(row(1, 100, 2, steam_id, &format!("t{steam_id}")));
            rows.push(row(2, 200, 2, steam_id, &format!("t{steam_id}")));
        }
        for steam_id in 6..=10 {
            rows.push(row(1, 100, 3, steam_id, &format!("ct{steam_id}")));
        }

        let players = summarize_players(&demo(rows), &BTreeMap::new());

        assert_eq!(players.len(), 10);
        assert_eq!(players[0].name, "t1");
        assert_eq!(players[0].side, "T");
        assert_eq!(players[0].rounds, 2);
        assert_eq!(players[0].rows, 2);
        assert_eq!(players[9].side, "CT");
    }

    #[test]
    fn summarizes_latest_player_identity_and_scoreboard_snapshot() {
        let mut latest = row(2, 300, 3, 1, "latest");
        latest.team_clan_name = Some("Latest Team".to_string());
        latest.scoreboard_score = Some(18);
        latest.scoreboard_kills = Some(7);
        latest.scoreboard_deaths = Some(4);
        latest.scoreboard_assists = Some(3);
        latest.scoreboard_mvps = Some(2);

        let mut early = row(1, 100, 2, 1, "early");
        early.team_clan_name = Some("Early Team".to_string());
        early.scoreboard_score = Some(5);
        early.scoreboard_kills = Some(2);
        early.scoreboard_deaths = Some(1);
        early.scoreboard_assists = Some(0);
        early.scoreboard_mvps = Some(0);

        let players = summarize_players(&demo(vec![latest, early]), &BTreeMap::new());
        let player = &players[0];

        assert_eq!(player.name, "latest");
        assert_eq!(player.side, "CT");
        assert_eq!(player.team_name.as_deref(), Some("Latest Team"));
        assert_eq!(player.score, Some(18));
        assert_eq!(player.kills, Some(7));
        assert_eq!(player.deaths, Some(4));
        assert_eq!(player.assists, Some(3));
        assert_eq!(player.mvps, Some(2));
    }

    #[test]
    fn keeps_last_valid_fields_when_later_rows_are_missing_them() {
        let mut scored = row(1, 100, 2, 1, "known");
        scored.team_name = Some("Known Team".to_string());
        scored.scoreboard_score = Some(11);
        scored.scoreboard_kills = Some(4);
        scored.scoreboard_deaths = Some(2);
        scored.scoreboard_assists = Some(1);
        scored.scoreboard_mvps = Some(1);

        let mut later = row(2, 200, 3, 1, "renamed");
        later.scoreboard_score = Some(12);
        let players = summarize_players(&demo(vec![scored, later]), &BTreeMap::new());
        let player = &players[0];

        assert_eq!(player.name, "renamed");
        assert_eq!(player.side, "CT");
        assert_eq!(player.team_name.as_deref(), Some("Known Team"));
        assert_eq!(player.score, Some(12));
        assert_eq!(player.kills, Some(4));
        assert_eq!(player.deaths, Some(2));
        assert_eq!(player.assists, Some(1));
        assert_eq!(player.mvps, Some(1));
    }

    #[test]
    fn browser_analysis_reports_demo_versions_and_round_span_duration() {
        let mut rows = Vec::new();
        for steam_id in 1..=10 {
            let team_num = if steam_id <= 5 { 2 } else { 3 };
            rows.push(row(1, 100, team_num, steam_id, &format!("p{steam_id}")));
            rows.push(row(1, 200, team_num, steam_id, &format!("p{steam_id}")));
            rows.push(row(2, 500, team_num, steam_id, &format!("p{steam_id}")));
            rows.push(row(2, 740, team_num, steam_id, &format!("p{steam_id}")));
        }
        let mut parsed = demo(rows);
        parsed.demo_patch_version = Some(14_228);
        parsed.demo_version_name = Some("2.0.0.0".to_string());
        parsed.playback_time_seconds = Some(12.5);

        let result = analyze_browser_demo(&parsed, AnalysisOptions::default());

        assert_eq!(result.demo_patch_version, Some(14_228));
        assert_eq!(result.demo_version_name.as_deref(), Some("2.0.0.0"));
        assert_eq!(result.analysis.rounds.len(), 2);
        assert_eq!(result.duration_seconds.to_bits(), 12.5_f32.to_bits());
    }

    #[test]
    fn returns_none_when_final_score_is_unavailable() {
        let parsed = demo(vec![row(1, 100, 2, 1, "alpha"), row(1, 100, 3, 2, "bravo")]);

        let summary = summarize_match(&parsed);
        assert!(summary.score.is_none());
        let players = summarize_players(&parsed, &summary.team_by_steam_id);
        assert!(players.iter().all(|player| {
            player.team_name.is_none()
                && player.score.is_none()
                && player.kills.is_none()
                && player.deaths.is_none()
                && player.assists.is_none()
                && player.mvps.is_none()
        }));
    }

    #[test]
    fn serializes_player_fields_for_the_desktop_boundary() {
        let player = BrowserPlayerSummary {
            name: "Player".to_string(),
            steam_id: "76561198012345678".to_string(),
            side: "CT".to_string(),
            player_color: Some("blue".to_string()),
            team: "b".to_string(),
            team_name: Some("Example Team".to_string()),
            score: Some(20),
            kills: Some(14),
            deaths: Some(9),
            assists: Some(4),
            mvps: Some(2),
            rounds: 24,
            rows: 128,
            details: None,
        };

        let value = serde_json::to_value(player).unwrap();

        assert_eq!(value["steamId"], "76561198012345678");
        assert_eq!(value["playerColor"], "blue");
        assert_eq!(value["teamName"], "Example Team");
        assert!(value.get("steam_id").is_none());
        assert!(value.get("team_name").is_none());
    }

    #[test]
    fn keeps_latest_valid_demo_player_color() {
        let mut first = row(1, 100, 2, 1, "alpha");
        first.player_color = Some("green".to_string());
        let mut second = row(1, 110, 2, 1, "alpha");
        second.player_color = Some("YELLOW".to_string());
        let mut invalid = row(1, 120, 2, 1, "alpha");
        invalid.player_color = Some("unknown".to_string());

        let players = summarize_players(&demo(vec![first, second, invalid]), &BTreeMap::new());

        assert_eq!(players[0].player_color.as_deref(), Some("yellow"));
    }

    #[test]
    fn rejects_side_labels_as_team_identity() {
        let mut player = row(1, 100, 2, 1, "alpha");
        player.team_name = Some("TERRORIST".to_string());
        assert_eq!(clean_team_name(&player), None);

        player.team_clan_name = Some("Actual Team".to_string());
        assert_eq!(clean_team_name(&player).as_deref(), Some("Actual Team"));
    }

    #[test]
    fn counts_round_end_winners_into_stable_teams_through_side_swaps() {
        let parsed = scored_demo(16, 13, true);
        let summary = summarize_match(&parsed);
        let score = summary.score.unwrap();

        assert_eq!(score.team_a.score, 16);
        assert_eq!(score.team_a.name.as_deref(), Some("Alpha"));
        assert_eq!(score.team_b.score, 13);
        assert_eq!(score.team_b.name.as_deref(), Some("Bravo"));
        assert_eq!(score.status, "final");
        assert_eq!(summary.team_by_steam_id.get(&1), Some(&PersistentTeam::A));
        assert_eq!(summary.team_by_steam_id.get(&2), Some(&PersistentTeam::B));
    }

    #[test]
    fn attributes_the_first_post_halftime_round_from_its_live_roster_snapshot() {
        let winner_sides = [
            2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 2, 2, 2, 2, 3, 3, 2, 3, 2, 3, 3,
        ];
        let mut rows = Vec::new();
        let mut round_freeze_end_ticks = Vec::new();
        let mut events = vec![event("round_announce_match_start", 10, None)];
        for (round_index, winner_side) in winner_sides.into_iter().enumerate() {
            let round_end_tick = 100 + i32::try_from(round_index).unwrap() * 100;
            let freeze_end_tick = round_end_tick - 20;
            let team_a_side = if round_index < 12 { 2 } else { 3 };
            let team_b_side = opposite_side(team_a_side);
            round_freeze_end_ticks.push(freeze_end_tick);
            events.push(event("round_end", round_end_tick, Some(winner_side)));

            let mut alpha = row(
                u32::try_from(round_index).unwrap(),
                freeze_end_tick,
                team_a_side,
                1,
                "alpha",
            );
            alpha.team_clan_name = Some("9z".to_string());
            let mut bravo = row(
                u32::try_from(round_index).unwrap(),
                freeze_end_tick,
                team_b_side,
                2,
                "bravo",
            );
            bravo.team_clan_name = Some("PV".to_string());
            rows.extend([alpha, bravo]);
        }

        // The side swap happens between R12's end and R13's freeze end. These
        // transition rows use the old sides and made the former whole-window
        // majority vote turn the real 13:10 into 14:9.
        for tick in (1_210..1_280).step_by(5) {
            rows.push(row(12, tick, 2, 1, "alpha"));
            rows.push(row(12, tick, 3, 2, "bravo"));
        }
        events.push(event("cs_win_panel_match", 2_400, None));
        let mut parsed = demo(rows);
        parsed.events = events;
        parsed.round_freeze_end_ticks = round_freeze_end_ticks;

        let summary = summarize_match(&parsed);
        let score = summary.score.unwrap();

        assert_eq!((score.team_a.score, score.team_b.score), (13, 10));
        assert_eq!(score.team_a.name.as_deref(), Some("9z"));
        assert_eq!(score.team_b.name.as_deref(), Some("PV"));
        assert_eq!(summary.team_by_steam_id.get(&1), Some(&PersistentTeam::A));
        assert_eq!(summary.team_by_steam_id.get(&2), Some(&PersistentTeam::B));
    }

    #[test]
    fn counts_overtime_without_hard_coding_a_target_score() {
        let score = summarize_match(&scored_demo(19, 17, true)).score.unwrap();

        assert_eq!((score.team_a.score, score.team_b.score), (19, 17));
        assert_eq!(score.status, "final");
    }

    #[test]
    fn filters_pre_match_rounds_and_marks_missing_match_end_as_completed() {
        let mut parsed = scored_demo(13, 8, false);
        parsed.events.push(event("round_end", 5, Some(3)));
        let score = summarize_match(&parsed).score.unwrap();

        assert_eq!((score.team_a.score, score.team_b.score), (13, 8));
        assert_eq!(score.status, "completed");
    }

    #[test]
    fn ignores_rounds_and_player_snapshots_after_match_end() {
        let mut parsed = scored_demo(13, 8, true);
        let match_end_tick = parsed
            .events
            .iter()
            .find(|event| event.name == "cs_win_panel_match")
            .unwrap()
            .tick;
        let last_alpha = parsed
            .rows
            .iter_mut()
            .filter(|row| row.steam_id == 1)
            .max_by_key(|row| row.tick)
            .unwrap();
        last_alpha.scoreboard_kills = Some(20);

        parsed
            .events
            .push(event("round_end", match_end_tick + 100, Some(3)));
        let mut tail = row(99, match_end_tick + 50, 2, 1, "post-match");
        tail.scoreboard_kills = Some(99);
        parsed.rows.push(tail);

        let summary = summarize_match(&parsed);
        let score = summary.score.as_ref().unwrap();
        assert_eq!((score.team_a.score, score.team_b.score), (13, 8));
        assert_eq!(score.status, "final");

        let players = summarize_players_in_window(
            &parsed,
            &summary.team_by_steam_id,
            summary.match_window,
            None,
            None,
        );
        let alpha = players
            .iter()
            .find(|player| player.steam_id == "1")
            .unwrap();
        assert_eq!(alpha.name, "alpha");
        assert_eq!(alpha.kills, Some(20));
    }

    #[test]
    fn keeps_early_leavers_on_their_stable_team_across_side_swaps() {
        let mut parsed = scored_demo(13, 8, true);
        for row in parsed
            .rows
            .iter_mut()
            .filter(|row| row.steam_id == 1 && row.round >= 5)
        {
            row.steam_id = 3;
            row.name = "replacement".to_string();
        }

        let summary = summarize_match(&parsed);
        let score = summary.score.as_ref().unwrap();
        assert_eq!((score.team_a.score, score.team_b.score), (13, 8));
        assert_eq!(summary.team_by_steam_id.get(&1), Some(&PersistentTeam::A));
        assert_eq!(summary.team_by_steam_id.get(&3), Some(&PersistentTeam::A));
        assert_eq!(summary.team_by_steam_id.get(&2), Some(&PersistentTeam::B));
    }

    #[test]
    fn deduplicates_same_tick_round_end_events() {
        let mut parsed = scored_demo(13, 8, true);
        let duplicate = parsed
            .events
            .iter()
            .find(|event| event.name == "round_end")
            .unwrap()
            .clone();
        parsed.events.push(duplicate);
        let score = summarize_match(&parsed).score.unwrap();

        assert_eq!((score.team_a.score, score.team_b.score), (13, 8));
    }

    #[test]
    fn ignores_side_less_round_end_during_a_match_reset() {
        let mut parsed = scored_demo(13, 8, true);
        parsed.events.push(event("round_end", 150, None));

        let summary = summarize_match(&parsed);
        let score = summary.score.unwrap();

        assert_eq!((score.team_a.score, score.team_b.score), (13, 8));
        assert_eq!(summary.team_by_steam_id.get(&1), Some(&PersistentTeam::A));
        assert_eq!(summary.team_by_steam_id.get(&2), Some(&PersistentTeam::B));
    }

    #[test]
    fn preserves_round_win_reason_for_the_desktop_boundary() {
        let mut parsed = scored_demo(1, 0, true);
        parsed
            .events
            .iter_mut()
            .find(|event| event.name == "round_end")
            .unwrap()
            .round_end_reason = Some("bomb_exploded".to_string());

        let summary = summarize_match(&parsed);

        assert_eq!(summary.round_outcomes.len(), 1);
        assert_eq!(summary.round_outcomes[0].winner_team, "a");
        assert_eq!(
            summary.round_outcomes[0].reason.as_deref(),
            Some("bomb_exploded")
        );
    }

    #[test]
    fn derives_opening_duels_and_exact_multi_kill_rounds_from_enemy_deaths() {
        let mut parsed = scored_demo(2, 0, true);
        for (round, tick) in [(0, 80), (1, 180)] {
            parsed.rows.extend([
                row(round, tick, 2, 3, "alpha-2"),
                row(round, tick, 3, 4, "bravo-2"),
                row(round, tick, 2, 5, "alpha-3"),
                row(round, tick, 3, 6, "bravo-3"),
            ]);
        }
        for (tick, victim) in [(30, 2), (40, 4), (50, 6), (130, 2), (140, 4)] {
            parsed.events.push(ParsedGameEvent {
                tick,
                name: "player_death".to_string(),
                attacker_steam_id: Some(1),
                victim_steam_id: Some(victim),
                ..ParsedGameEvent::default()
            });
        }
        // Same-team deaths must not pollute either opening-duel or multi-kill totals.
        parsed.events.push(ParsedGameEvent {
            tick: 20,
            name: "player_death".to_string(),
            attacker_steam_id: Some(3),
            victim_steam_id: Some(1),
            ..ParsedGameEvent::default()
        });

        let analysis = analyze_browser_demo(&parsed, AnalysisOptions::default());
        let alpha = analysis
            .players
            .iter()
            .find(|player| player.steam_id == "1")
            .unwrap();
        let bravo = analysis
            .players
            .iter()
            .find(|player| player.steam_id == "2")
            .unwrap();
        let alpha_details = alpha.details.as_ref().unwrap();
        let bravo_details = bravo.details.as_ref().unwrap();

        assert_eq!(alpha_details.first_kills, Some(2));
        assert_eq!(alpha_details.two_k_rounds, Some(1));
        assert_eq!(alpha_details.three_k_rounds, Some(1));
        assert_eq!(alpha_details.four_k_rounds, Some(0));
        assert_eq!(alpha_details.five_k_rounds, Some(0));
        assert_eq!(bravo_details.first_deaths, Some(2));
    }

    #[test]
    fn omits_combat_metrics_when_death_identities_cannot_be_attributed() {
        let mut parsed = scored_demo(1, 0, true);
        parsed.events.push(ParsedGameEvent {
            tick: 50,
            name: "player_death".to_string(),
            attacker_steam_id: Some(1),
            victim_steam_id: None,
            ..ParsedGameEvent::default()
        });

        let analysis = analyze_browser_demo(&parsed, AnalysisOptions::default());
        let alpha = analysis
            .players
            .iter()
            .find(|player| player.steam_id == "1")
            .unwrap();

        assert!(alpha.details.is_none());
    }

    #[test]
    fn friendly_fire_prefers_stable_server_convar_evidence() {
        let mut parsed = scored_demo(1, 0, true);
        parsed.server_convars.push(ParsedServerConVar {
            tick: 0,
            name: "mp_friendlyfire".to_string(),
            value: "0".to_string(),
        });

        let analysis = analyze_browser_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.friendly_fire.enabled, Some(false));
        assert_eq!(analysis.friendly_fire.evidence, "serverConVar");
        assert_eq!(analysis.friendly_fire.damage_events, 0);
    }

    #[test]
    fn friendly_fire_can_be_proven_on_by_observed_team_damage() {
        let mut parsed = scored_demo(1, 0, true);
        parsed.rows.push(row(0, 80, 2, 3, "alpha-2"));
        parsed.events.push(ParsedGameEvent {
            tick: 50,
            name: "player_hurt".to_string(),
            attacker_steam_id: Some(1),
            victim_steam_id: Some(3),
            damage: Some(27),
            ..ParsedGameEvent::default()
        });

        let analysis = analyze_browser_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.friendly_fire.enabled, Some(true));
        assert_eq!(analysis.friendly_fire.evidence, "observedDamage");
        assert_eq!(analysis.friendly_fire.damage_events, 1);
        assert_eq!(analysis.friendly_fire.damage, 27);
    }

    #[test]
    fn friendly_fire_remains_unknown_without_positive_evidence() {
        let analysis = analyze_browser_demo(&scored_demo(1, 0, true), AnalysisOptions::default());

        assert_eq!(analysis.friendly_fire.enabled, None);
        assert_eq!(analysis.friendly_fire.evidence, "unavailable");
    }

    #[test]
    fn infers_platform_from_header_before_filename() {
        let source = infer_demo_source("g123-faceit", Some("Valve 5EPlay Server")).unwrap();
        assert_eq!(source.name, "5E");
        assert_eq!(source.evidence, "serverName");

        let source = infer_demo_source("123_team_alpha", None).unwrap();
        assert_eq!(source.name, "FACEIT");
        assert_eq!(source.evidence, "fileName");
    }

    #[test]
    fn infers_common_community_platforms_without_guessing_from_unrelated_text() {
        let cases = [
            ("FACEIT.com register to play here", "FACEIT"),
            ("MatchZy PUG Server", "MatchZy"),
            ("PRACC.COM EU", "PRACC"),
            ("Gamers Club League", "Gamers Club"),
            ("Valve CS2 Premier Server", "Valve Premier"),
            ("BLAST.tv Premier CS2 Server", "BLAST"),
            ("FACEIT MatchZy Server", "FACEIT"),
        ];
        for (server, expected) in cases {
            assert_eq!(
                infer_demo_source("demo", Some(server)).unwrap().name,
                expected
            );
        }
        assert!(infer_demo_source("my5example", Some("Counter-Strike 2")).is_none());
    }
}
