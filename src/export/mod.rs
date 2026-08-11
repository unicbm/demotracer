/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::analysis::quality::{analyze_demo, AnalysisOptions};
use crate::demo_id::output_demo_id;
use crate::inspect_link::{item_inspect, weapon_inspect};
use crate::model::{
    public_demo_path, ConversionManifest, ConvertedFile, ConvertedRound, DemoAnalysis,
    EconomyClass, HighFidelityMetadata, ManifestAvatarOverride, ParsedAvatarOverride, ParsedDemo,
    ParsedEconItem, ParsedGameEvent, ParsedInventoryWeaponAttribute, ParsedInventoryWeaponCosmetic,
    ParsedPlayerTick, ParsedProjectile, ParsedWeaponSticker, ReplayAgentCosmetic,
    ReplayChatMessage, ReplayCosmetics, ReplayHifiEvent, ReplayHifiEventKind,
    ReplayInventoryItemCount, ReplayInventorySnapshot, ReplayItemCosmetic, ReplayPlayerScoreboard,
    ReplayProjectileMetadata, ReplayRoundScoreboard, ReplayScoreboardFlair, ReplayView,
    ReplayViewmodel, ReplayWeaponCharm, ReplayWeaponCosmetic, ReplayWeaponSticker, RoundSummary,
    Side, SubtickMode, TeamEconomy, DEMOTRACER_ABI, DTR_FORMAT_VERSION,
};
use crate::rec_writer::write_rec;
use crate::replay::context::{
    first_weapon_def_index_from_play_start, preload_weapon_def_indices_from_refs_from_play_start,
    replay_loadout,
};
use crate::replay::synthesis::{
    synthesize_player_rec_with_row_refs, SynthesisOptions, SynthesisStats,
};
use crate::{io_error, Error, Result};
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

// A generous internal ceiling lets export retain the complete freeze phase
// observed in the demo. The actual pre-roll begins at the contiguous freeze
// suffix connected to the live start, so this is a safety cap rather than a
// fixed amount of padding.
pub const DEFAULT_FREEZE_PREROLL_SECONDS: f32 = 120.0;
const STEAM_ID64_BASE: u64 = 76_561_197_960_265_728;
const KEYCHAIN_SLOT_0_ID_ATTR: u32 = 299;
const KEYCHAIN_SLOT_0_OFFSET_X_ATTR: u32 = 300;
const KEYCHAIN_SLOT_0_OFFSET_Y_ATTR: u32 = 301;
const KEYCHAIN_SLOT_0_OFFSET_Z_ATTR: u32 = 302;
const KEYCHAIN_SLOT_0_SEED_ATTR: u32 = 306;
const KEYCHAIN_SLOT_0_HIGHLIGHT_ATTR: u32 = 314;
const KEYCHAIN_SLOT_0_STICKER_ATTR: u32 = 321;

#[derive(Clone, Debug)]
pub struct ConvertOptions {
    pub output_dir: PathBuf,
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub freeze_preroll_seconds: f32,
    pub export_cosmetics: bool,
    pub export_stickers: bool,
    pub export_charms: bool,
    pub analysis: AnalysisOptions,
}

#[derive(Clone, Debug)]
pub struct ConvertMemoryOptions {
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub freeze_preroll_seconds: f32,
    pub export_cosmetics: bool,
    pub export_stickers: bool,
    pub export_charms: bool,
    pub analysis: AnalysisOptions,
}

impl From<&ConvertOptions> for ConvertMemoryOptions {
    fn from(options: &ConvertOptions) -> Self {
        Self {
            output_stem: options.output_stem.clone(),
            side: options.side,
            selected_rounds: options.selected_rounds.clone(),
            include_suspicious: options.include_suspicious,
            cut_before_bomb_plant: options.cut_before_bomb_plant,
            subtick_mode: options.subtick_mode,
            freeze_preroll_seconds: options.freeze_preroll_seconds,
            export_cosmetics: options.export_cosmetics,
            export_stickers: options.export_stickers,
            export_charms: options.export_charms,
            analysis: options.analysis,
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub files_written: usize,
    pub manifest: ConversionManifest,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ConversionArtifactKind {
    Dtr,
    Avatar,
    Manifest,
    Log,
}

#[derive(Clone, Debug)]
pub struct ConversionArtifact {
    pub path: String,
    pub bytes: Vec<u8>,
    pub kind: ConversionArtifactKind,
    pub round: Option<u32>,
    pub steam_id: Option<u64>,
}

enum PlayerExportOutcome {
    Skipped {
        steam_id: u64,
        rows: usize,
    },
    Written {
        file: ConvertedFile,
        artifact: ConversionArtifact,
        stats: SynthesisStats,
    },
}

#[derive(Clone, Debug)]
pub struct MemoryConversionReport {
    pub demo_id: String,
    pub files_written: usize,
    pub manifest: ConversionManifest,
    pub log: String,
    pub artifacts: Vec<ConversionArtifact>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(tag = "event", rename_all = "snake_case")]
pub enum ConversionProgress {
    AnalysisStarted,
    AnalysisFinished {
        rounds: usize,
        selected_rounds: usize,
        estimated_files: usize,
    },
    RoundSkipped {
        round: u32,
        reason: String,
    },
    RoundStarted {
        round: u32,
        estimated_players: usize,
    },
    PlayerSkipped {
        round: u32,
        steam_id: u64,
        reason: String,
    },
    PlayerWritten {
        round: u32,
        steam_id: u64,
        player_name: String,
        side: String,
        path: String,
        ticks: usize,
        subticks: usize,
    },
    RoundFinished {
        round: u32,
        files: usize,
    },
    ArtifactsWritingStarted {
        root: String,
        artifacts: usize,
    },
    ArtifactWritten {
        path: String,
        kind: ConversionArtifactKind,
    },
    Finished {
        root: String,
        manifest_path: String,
        files_written: usize,
    },
}

pub fn export_demo_to_memory(
    parsed: &ParsedDemo,
    options: &ConvertMemoryOptions,
) -> Result<MemoryConversionReport> {
    export_demo_to_memory_inner(parsed, options, None, None)
}

pub fn export_demo_to_memory_with_progress<F>(
    parsed: &ParsedDemo,
    options: &ConvertMemoryOptions,
    mut progress: F,
) -> Result<MemoryConversionReport>
where
    F: FnMut(ConversionProgress),
{
    export_demo_to_memory_inner(parsed, options, None, Some(&mut progress))
}

fn export_demo_to_memory_inner(
    parsed: &ParsedDemo,
    options: &ConvertMemoryOptions,
    preanalyzed: Option<&DemoAnalysis>,
    mut progress: Option<&mut dyn FnMut(ConversionProgress)>,
) -> Result<MemoryConversionReport> {
    validate_freeze_preroll_seconds(options.freeze_preroll_seconds)?;
    let owned_analysis;
    let analysis = if let Some(analysis) = preanalyzed {
        analysis
    } else {
        emit_conversion_progress(&mut progress, ConversionProgress::AnalysisStarted);
        owned_analysis = analyze_demo(parsed, options.analysis);
        &owned_analysis
    };
    let output_stem = output_demo_id(
        &parsed.stem,
        &parsed.demo_sha256,
        options.output_stem.as_deref(),
    )?;

    let mut manifest = ConversionManifest {
        demo_path: public_demo_path(&parsed.path),
        demo_id: output_stem.clone(),
        demo_sha256: parsed.demo_sha256.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        abi: DEMOTRACER_ABI,
        format_version: DTR_FORMAT_VERSION,
        avatar_overrides: Vec::new(),
        rounds: Vec::new(),
        files: Vec::new(),
    };
    let mut log = Vec::new();
    let mut artifacts = Vec::new();
    let mut subtick_stats = SynthesisStats::default();
    let rows_by_round = rows_by_round(&parsed.rows);
    let projectiles_by_steam_id = projectiles_by_steam_id(&parsed.projectiles);
    let (selected_rounds, estimated_files) =
        selected_round_summary(parsed, &rows_by_round, &analysis.rounds, options);
    emit_conversion_progress(
        &mut progress,
        ConversionProgress::AnalysisFinished {
            rounds: analysis.rounds.len(),
            selected_rounds,
            estimated_files,
        },
    );
    log.push(format!(
        "demo={} id={} sha256={} map={} tick_rate={:.3}",
        public_demo_path(&parsed.path),
        output_stem,
        parsed.demo_sha256,
        parsed.map,
        parsed.tick_rate
    ));
    let econ_glove_seeds = if options.export_cosmetics {
        glove_econ_seed_index(parsed)
    } else {
        BTreeMap::new()
    };
    let econ_knife_paints = if options.export_cosmetics {
        knife_econ_paint_index(parsed)
    } else {
        BTreeMap::new()
    };

    for round in &analysis.rounds {
        if let Some(reason) = round_skip_reason(round, options) {
            log.push(format!("skip round {}: {reason}", round.round));
            emit_conversion_progress(
                &mut progress,
                ConversionProgress::RoundSkipped {
                    round: round.round,
                    reason,
                },
            );
            continue;
        }

        let (end_tick, cut_reason) = if options.cut_before_bomb_plant {
            cut_before_bomb_plant(parsed, round.start_tick, round.end_tick)
        } else {
            (round.end_tick, None)
        };
        let bomb_planted_tick =
            bomb_planted_tick_for_round(parsed, round.start_tick, round.end_tick);
        let bomb_planted_seconds_after_live = bomb_planted_tick
            .map(|tick| ticks_to_seconds(tick - round.start_tick, parsed.tick_rate));
        if end_tick <= round.start_tick {
            let reason = format!("cut window empty after {cut_reason:?}");
            log.push(format!("skip round {}: {reason}", round.round));
            emit_conversion_progress(
                &mut progress,
                ConversionProgress::RoundSkipped {
                    round: round.round,
                    reason,
                },
            );
            continue;
        }
        let round_rows: &[&ParsedPlayerTick] = rows_by_round
            .get(&round.round)
            .map(Vec::as_slice)
            .unwrap_or(&[]);
        let recording_start_tick = recording_start_tick_for_round(
            round_rows,
            parsed.tick_rate,
            round.start_tick,
            options.freeze_preroll_seconds,
        );
        let freeze_preroll_ticks = round.start_tick.saturating_sub(recording_start_tick);
        let pistol_round = is_pistol_round(round.round);
        let t_economy = team_economy(round_rows, round.start_tick, end_tick, 2, pistol_round);
        let ct_economy = team_economy(round_rows, round.start_tick, end_tick, 3, pistol_round);
        let round_scoreboard = replay_round_scoreboard(round_rows);
        let round_chat_messages =
            replay_chat_messages(parsed, recording_start_tick, end_tick, round_rows);
        let first_file_index = manifest.files.len();

        let mut players: BTreeMap<u64, Vec<&ParsedPlayerTick>> = BTreeMap::new();
        for &row in round_rows {
            if row.tick < recording_start_tick
                || row.tick > end_tick
                || (row.tick < round.start_tick && !row.is_freeze_period)
                || !row.is_alive
                || row.steam_id == 0
                || !options.side.matches_team(row.team_num)
            {
                continue;
            }
            players.entry(row.steam_id).or_default().push(row);
        }
        emit_conversion_progress(
            &mut progress,
            ConversionProgress::RoundStarted {
                round: round.round,
                estimated_players: players.len(),
            },
        );
        let cosmetic_players = cosmetic_rows_by_player(round_rows, end_tick, options.side);

        let player_exports = players
            .into_iter()
            .collect::<Vec<_>>()
            .into_par_iter()
            .map(|(steam_id, mut player_rows)| {
                player_rows.sort_by_key(|row| row.tick);
                player_rows.dedup_by_key(|row| row.tick);
                if player_rows.len() < 2 {
                    return Ok(PlayerExportOutcome::Skipped {
                        steam_id,
                        rows: player_rows.len(),
                    });
                }
                let play_start_tick_index = play_start_tick_index(&player_rows, round.start_tick);
                let player_projectiles = projectiles_by_steam_id
                    .get(&steam_id)
                    .map(Vec::as_slice)
                    .unwrap_or(&[]);
                let cosmetic_player_rows = cosmetic_players
                    .get(&steam_id)
                    .map(Vec::as_slice)
                    .unwrap_or(player_rows.as_slice());
                let (mut rec, stats) = synthesize_player_rec_with_row_refs(
                    &player_rows,
                    player_projectiles,
                    &parsed.map,
                    parsed.tick_rate,
                    round.round,
                    SynthesisOptions {
                        subtick_mode: options.subtick_mode,
                        play_start_tick_index,
                    },
                )?;
                rec.high_fidelity = build_player_high_fidelity_metadata(
                    parsed,
                    round.round,
                    recording_start_tick,
                    round.start_tick,
                    end_tick,
                    &player_rows,
                    round_rows,
                    player_projectiles,
                );
                let team_dir = Side::team_dir(player_rows[0].team_num);
                let player_name = if player_rows[0].name.is_empty() {
                    steam_id.to_string()
                } else {
                    player_rows[0].name.clone()
                };
                let rel_path = Path::new(&format!("round{:02}", round.round))
                    .join(team_dir)
                    .join(format!("{}_{}.dtr", steam_id, slugify(&player_name)));
                let mut bytes = Vec::new();
                write_rec(&mut bytes, &rec)?;
                let path = rel_path.to_string_lossy().replace('\\', "/");
                let ticks = rec.ticks.len();
                let subticks = rec.subticks.len();
                let play_start_row = player_rows
                    .get(rec.header.play_start_tick_index as usize)
                    .copied()
                    .unwrap_or(player_rows[0]);
                let file = ConvertedFile {
                    path: path.clone(),
                    round: round.round,
                    side: team_dir.to_string(),
                    steam_id,
                    player_name,
                    ticks,
                    subticks,
                    play_start_tick_index: rec.header.play_start_tick_index,
                    first_weapon_def_index: first_weapon_def_index_from_play_start(
                        &rec,
                        rec.header.play_start_tick_index,
                    ),
                    preload_weapon_def_indices:
                        preload_weapon_def_indices_from_refs_from_play_start(
                            &player_rows,
                            &rec,
                            rec.header.play_start_tick_index,
                        ),
                    hifi_event_count: rec.high_fidelity.events.len(),
                    inventory_snapshot_count: rec.high_fidelity.inventory_snapshots.len(),
                    loadout: replay_loadout(play_start_row),
                    music_kit_id: stable_music_kit_id(&player_rows),
                    scoreboard_flair: stable_scoreboard_flair(&player_rows),
                    cosmetics: if options.export_cosmetics {
                        replay_cosmetics_at(
                            &player_rows,
                            cosmetic_player_rows,
                            rec.header.play_start_tick_index,
                            econ_glove_seeds.get(&steam_id),
                            econ_knife_paints.get(&steam_id),
                            options.export_stickers,
                            options.export_charms,
                        )
                    } else {
                        None
                    },
                    view: replay_view(&player_rows),
                    scoreboard: replay_player_scoreboard(cosmetic_player_rows),
                };
                let artifact = ConversionArtifact {
                    path,
                    bytes,
                    kind: ConversionArtifactKind::Dtr,
                    round: Some(round.round),
                    steam_id: Some(steam_id),
                };
                Ok(PlayerExportOutcome::Written {
                    file,
                    artifact,
                    stats,
                })
            })
            .collect::<Vec<Result<PlayerExportOutcome>>>();

        for player_export in player_exports {
            match player_export? {
                PlayerExportOutcome::Skipped { steam_id, rows } => {
                    let reason = format!("{rows} rows");
                    log.push(format!(
                        "skip round {} player {steam_id}: {reason}",
                        round.round
                    ));
                    emit_conversion_progress(
                        &mut progress,
                        ConversionProgress::PlayerSkipped {
                            round: round.round,
                            steam_id,
                            reason,
                        },
                    );
                }
                PlayerExportOutcome::Written {
                    file,
                    artifact,
                    stats,
                } => {
                    subtick_stats.add_assign(&stats);
                    emit_conversion_progress(
                        &mut progress,
                        ConversionProgress::PlayerWritten {
                            round: file.round,
                            steam_id: file.steam_id,
                            player_name: file.player_name.clone(),
                            side: file.side.clone(),
                            path: file.path.clone(),
                            ticks: file.ticks,
                            subticks: file.subticks,
                        },
                    );
                    manifest.files.push(file);
                    artifacts.push(artifact);
                }
            }
        }

        let files = manifest.files.len() - first_file_index;
        if files > 0 {
            let duration_seconds = if parsed.tick_rate > 0.0 {
                ((end_tick - round.start_tick).max(0) as f32) / parsed.tick_rate
            } else {
                0.0
            };
            manifest.rounds.push(ConvertedRound {
                round: round.round,
                recording_start_tick,
                start_tick: round.start_tick,
                end_tick,
                original_end_tick: round.end_tick,
                bomb_planted_tick,
                bomb_planted_seconds_after_live,
                freeze_preroll_ticks,
                duration_seconds,
                pistol_round,
                cut_reason,
                t_economy,
                ct_economy,
                scoreboard: round_scoreboard,
                chat_messages: round_chat_messages,
                files,
            });
        }
        emit_conversion_progress(
            &mut progress,
            ConversionProgress::RoundFinished {
                round: round.round,
                files,
            },
        );
    }

    append_avatar_override_artifacts(parsed, &mut manifest, &mut artifacts, &mut log);
    let manifest_json = serde_json::to_string_pretty(&manifest)?;

    log.push(format!("files_written={}", manifest.files.len()));
    log.push(format!(
        "subticks mode={} source={} written={} ticks_with_source={} ticks_with_written={} dropped_invalid={} dropped_overflow={} truncated_buttons={}",
        options.subtick_mode,
        subtick_stats.source_subticks,
        subtick_stats.written_subticks,
        subtick_stats.ticks_with_source_subticks,
        subtick_stats.ticks_with_written_subticks,
        subtick_stats.dropped_invalid_subticks,
        subtick_stats.dropped_overflow_subticks,
        subtick_stats.truncated_button_subticks
    ));
    let log = log.join("\n");
    artifacts.push(ConversionArtifact {
        path: "manifest.json".to_string(),
        bytes: manifest_json.into_bytes(),
        kind: ConversionArtifactKind::Manifest,
        round: None,
        steam_id: None,
    });
    artifacts.push(ConversionArtifact {
        path: "conversion.log".to_string(),
        bytes: log.as_bytes().to_vec(),
        kind: ConversionArtifactKind::Log,
        round: None,
        steam_id: None,
    });

    Ok(MemoryConversionReport {
        demo_id: output_stem,
        files_written: manifest.files.len(),
        manifest,
        log,
        artifacts,
    })
}

fn append_avatar_override_artifacts(
    parsed: &ParsedDemo,
    manifest: &mut ConversionManifest,
    artifacts: &mut Vec<ConversionArtifact>,
    log: &mut Vec<String>,
) {
    if parsed.avatar_overrides.is_empty() {
        return;
    }

    let mut written_paths = BTreeSet::new();
    for avatar in &parsed.avatar_overrides {
        let path = avatar_override_path(avatar);
        manifest.avatar_overrides.push(ManifestAvatarOverride {
            steam_id: avatar.steam_id,
            format: avatar.format,
            sha256: avatar.sha256.clone(),
            path: path.clone(),
            source: avatar.source.clone(),
            bytes: avatar.bytes.len(),
        });

        if written_paths.insert(path.clone()) {
            artifacts.push(ConversionArtifact {
                path,
                bytes: avatar.bytes.clone(),
                kind: ConversionArtifactKind::Avatar,
                round: None,
                steam_id: Some(avatar.steam_id),
            });
        }
    }

    log.push(format!(
        "avatar_overrides={} avatar_assets={}",
        manifest.avatar_overrides.len(),
        written_paths.len()
    ));
}

fn avatar_override_path(avatar: &ParsedAvatarOverride) -> String {
    format!("avatars/{}.{}", avatar.sha256, avatar.format.extension())
}

pub fn export_demo(parsed: &ParsedDemo, options: &ConvertOptions) -> Result<ConversionReport> {
    export_demo_with_progress(parsed, options, |_| {})
}

pub fn export_demo_with_progress<F>(
    parsed: &ParsedDemo,
    options: &ConvertOptions,
    progress: F,
) -> Result<ConversionReport>
where
    F: FnMut(ConversionProgress),
{
    let demo_id = output_demo_id(
        &parsed.stem,
        &parsed.demo_sha256,
        options.output_stem.as_deref(),
    )?;
    let root = options.output_dir.join(demo_id);
    export_demo_to_root_with_progress(parsed, options, &root, progress)
}

pub fn export_demo_to_root_with_progress<F>(
    parsed: &ParsedDemo,
    options: &ConvertOptions,
    root: &Path,
    mut progress: F,
) -> Result<ConversionReport>
where
    F: FnMut(ConversionProgress),
{
    let mut progress_ref = Some(&mut progress as &mut dyn FnMut(ConversionProgress));
    let memory = export_demo_to_memory_inner(
        parsed,
        &ConvertMemoryOptions::from(options),
        None,
        progress_ref,
    )?;
    fs::create_dir_all(root).map_err(|e| io_error(root, e))?;
    progress_ref = Some(&mut progress as &mut dyn FnMut(ConversionProgress));
    write_memory_conversion(memory, root, &mut progress_ref)
}

fn write_memory_conversion(
    memory: MemoryConversionReport,
    root: &Path,
    progress: &mut Option<&mut dyn FnMut(ConversionProgress)>,
) -> Result<ConversionReport> {
    emit_conversion_progress(
        progress,
        ConversionProgress::ArtifactsWritingStarted {
            root: root.display().to_string(),
            artifacts: memory.artifacts.len(),
        },
    );

    for artifact in &memory.artifacts {
        let path = root.join(&artifact.path);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| io_error(parent, e))?;
        }
        fs::write(&path, &artifact.bytes).map_err(|e| io_error(&path, e))?;
        emit_conversion_progress(
            progress,
            ConversionProgress::ArtifactWritten {
                path: artifact.path.clone(),
                kind: artifact.kind.clone(),
            },
        );
    }

    let report = ConversionReport {
        root: root.to_path_buf(),
        manifest_path: root.join("manifest.json"),
        files_written: memory.files_written,
        manifest: memory.manifest,
    };
    emit_conversion_progress(
        progress,
        ConversionProgress::Finished {
            root: report.root.display().to_string(),
            manifest_path: report.manifest_path.display().to_string(),
            files_written: report.files_written,
        },
    );
    Ok(report)
}

/// Exports a demo using an analysis snapshot that was already validated by the
/// caller. The snapshot must have been produced from `parsed` with
/// `options.analysis`; desktop workflows use this to keep round validation and
/// export on one immutable analysis result.
pub fn export_demo_to_root_with_analysis_and_progress<F>(
    parsed: &ParsedDemo,
    analysis: &DemoAnalysis,
    options: &ConvertOptions,
    root: &Path,
    mut progress: F,
) -> Result<ConversionReport>
where
    F: FnMut(ConversionProgress),
{
    let mut progress_ref = Some(&mut progress as &mut dyn FnMut(ConversionProgress));
    let memory = export_demo_to_memory_inner(
        parsed,
        &ConvertMemoryOptions::from(options),
        Some(analysis),
        progress_ref,
    )?;
    fs::create_dir_all(root).map_err(|e| io_error(root, e))?;
    progress_ref = Some(&mut progress as &mut dyn FnMut(ConversionProgress));
    write_memory_conversion(memory, root, &mut progress_ref)
}

fn emit_conversion_progress(
    progress: &mut Option<&mut dyn FnMut(ConversionProgress)>,
    event: ConversionProgress,
) {
    if let Some(callback) = progress.as_deref_mut() {
        callback(event);
    }
}

fn selected_round_summary(
    parsed: &ParsedDemo,
    rows_by_round: &BTreeMap<u32, Vec<&ParsedPlayerTick>>,
    rounds: &[RoundSummary],
    options: &ConvertMemoryOptions,
) -> (usize, usize) {
    let mut selected_rounds = 0_usize;
    let mut estimated_files = 0_usize;
    for round in rounds {
        if round_skip_reason(round, options).is_some() {
            continue;
        }
        selected_rounds += 1;
        estimated_files += estimate_round_files(parsed, rows_by_round, round, options);
    }
    (selected_rounds, estimated_files)
}

fn estimate_round_files(
    parsed: &ParsedDemo,
    rows_by_round: &BTreeMap<u32, Vec<&ParsedPlayerTick>>,
    round: &RoundSummary,
    options: &ConvertMemoryOptions,
) -> usize {
    let (end_tick, _) = if options.cut_before_bomb_plant {
        cut_before_bomb_plant(parsed, round.start_tick, round.end_tick)
    } else {
        (round.end_tick, None)
    };
    if end_tick <= round.start_tick {
        return 0;
    }
    let round_rows: &[&ParsedPlayerTick] = rows_by_round
        .get(&round.round)
        .map(Vec::as_slice)
        .unwrap_or(&[]);
    let recording_start_tick = recording_start_tick_for_round(
        round_rows,
        parsed.tick_rate,
        round.start_tick,
        options.freeze_preroll_seconds,
    );
    let mut ticks_by_player: BTreeMap<u64, BTreeSet<i32>> = BTreeMap::new();
    for &row in round_rows {
        if row.tick < recording_start_tick
            || row.tick > end_tick
            || (row.tick < round.start_tick && !row.is_freeze_period)
            || !row.is_alive
            || row.steam_id == 0
            || !options.side.matches_team(row.team_num)
        {
            continue;
        }
        ticks_by_player
            .entry(row.steam_id)
            .or_default()
            .insert(row.tick);
    }
    ticks_by_player
        .values()
        .filter(|ticks| ticks.len() >= 2)
        .count()
}

fn round_skip_reason(round: &RoundSummary, options: &ConvertMemoryOptions) -> Option<String> {
    let selected = match &options.selected_rounds {
        Some(rounds) => rounds.contains(&round.round),
        None => round.selected_by_default() || options.include_suspicious,
    };
    if !selected {
        return Some("not selected".to_string());
    }
    if round.requires_suspicious_opt_in() && !options.include_suspicious {
        return Some(format!("suspicious ({})", round.problems.join("; ")));
    }
    None
}

fn cut_before_bomb_plant(
    parsed: &ParsedDemo,
    start_tick: i32,
    end_tick: i32,
) -> (i32, Option<String>) {
    let first_plant_tick = parsed
        .bomb_beginplant_ticks
        .iter()
        .chain(parsed.bomb_planted_ticks.iter())
        .copied()
        .filter(|tick| *tick > start_tick && *tick <= end_tick)
        .min();
    match first_plant_tick {
        Some(tick) => (
            tick.saturating_sub(1),
            Some(format!("before_bomb_plant_tick_{tick}")),
        ),
        None => (end_tick, None),
    }
}

fn bomb_planted_tick_for_round(parsed: &ParsedDemo, start_tick: i32, end_tick: i32) -> Option<i32> {
    parsed
        .bomb_planted_ticks
        .iter()
        .copied()
        .filter(|tick| *tick >= start_tick && *tick <= end_tick)
        .min()
}

#[derive(Clone, Debug)]
struct AbsoluteHifiEvent {
    tick: i32,
    kind: ReplayHifiEventKind,
    actor_steam_id: Option<u64>,
    target_steam_id: Option<u64>,
    weapon_def_index: Option<i32>,
    item_name: Option<String>,
    entity_id: Option<i32>,
    actor_count_after: Option<u32>,
    target_count_after: Option<u32>,
    damage: Option<i32>,
    health: Option<i32>,
}

#[derive(Clone, Debug)]
struct InventoryChange {
    tick: i32,
    steam_id: u64,
    weapon_def_index: i32,
    count_after: u32,
    item_name: Option<String>,
    paired_steam_id: Option<u64>,
}

impl AbsoluteHifiEvent {
    fn with_tick_index(self, player_rows: &[&ParsedPlayerTick]) -> Option<ReplayHifiEvent> {
        let tick_index = tick_index_for_event(player_rows, self.tick)?;
        Some(ReplayHifiEvent {
            tick_index,
            tick: self.tick,
            kind: self.kind,
            actor_steam_id: self.actor_steam_id,
            target_steam_id: self.target_steam_id,
            weapon_def_index: self.weapon_def_index,
            item_name: self.item_name,
            entity_id: self.entity_id,
            actor_count_after: self.actor_count_after,
            target_count_after: self.target_count_after,
            damage: self.damage,
            health: self.health,
        })
    }
}

fn build_player_high_fidelity_metadata(
    parsed: &ParsedDemo,
    _round: u32,
    start_tick: i32,
    live_start_tick: i32,
    end_tick: i32,
    player_rows: &[&ParsedPlayerTick],
    round_rows: &[&ParsedPlayerTick],
    player_projectiles: &[&ParsedProjectile],
) -> HighFidelityMetadata {
    let steam_id = player_rows
        .first()
        .map(|row| row.steam_id)
        .unwrap_or_default();
    if steam_id == 0 {
        return HighFidelityMetadata::default();
    }

    let initial_c4_owner =
        infer_initial_c4_owner(parsed, start_tick, live_start_tick, end_tick, round_rows);
    let mut events = player_scoped_hifi_events(
        parsed,
        steam_id,
        start_tick,
        end_tick,
        round_rows,
        initial_c4_owner,
    )
    .into_iter()
    .filter_map(|event| event.with_tick_index(player_rows))
    .collect::<Vec<_>>();
    events.sort_by_key(|event| (event.tick_index, event.tick, hifi_event_rank(event.kind)));
    events.dedup_by(|lhs, rhs| {
        lhs.tick_index == rhs.tick_index
            && lhs.tick == rhs.tick
            && lhs.kind == rhs.kind
            && lhs.actor_steam_id == rhs.actor_steam_id
            && lhs.target_steam_id == rhs.target_steam_id
            && lhs.weapon_def_index == rhs.weapon_def_index
            && lhs.entity_id == rhs.entity_id
    });

    HighFidelityMetadata::with_projectiles(
        round_start_balance_for_player(player_rows, live_start_tick),
        events,
        inventory_snapshots_for_player(player_rows),
        projectile_hifi_metadata(player_projectiles, player_rows),
    )
}

fn round_start_balance_for_player(
    player_rows: &[&ParsedPlayerTick],
    live_start_tick: i32,
) -> Option<u32> {
    player_rows
        .iter()
        .copied()
        .find(|row| row.tick >= live_start_tick)
        .and_then(|row| row.account_balance)
}

fn projectile_hifi_metadata(
    projectiles: &[&ParsedProjectile],
    player_rows: &[&ParsedPlayerTick],
) -> Vec<ReplayProjectileMetadata> {
    projectiles
        .iter()
        .filter_map(|projectile| {
            let tick_index = tick_index_for_event(player_rows, projectile.tick)?;
            Some(ReplayProjectileMetadata {
                tick_index,
                tick: projectile.tick,
                kind: projectile.kind,
                weapon_def_index: projectile.weapon_def_index,
                effect_tick_index: projectile
                    .effect_tick
                    .and_then(|tick| tick_index_for_event(player_rows, tick)),
                effect_tick: projectile.effect_tick,
                effect_position: projectile.effect_position,
                effect_source: projectile.effect_source,
                effect_confidence: projectile.effect_confidence,
            })
        })
        .collect()
}

fn player_scoped_hifi_events(
    parsed: &ParsedDemo,
    steam_id: u64,
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
    initial_c4_owner: Option<u64>,
) -> Vec<AbsoluteHifiEvent> {
    let mut drops = inferred_inventory_drops(parsed, start_tick, end_tick, round_rows);
    let mut pickups = inferred_inventory_pickups(start_tick, end_tick, round_rows, &parsed.events);
    pair_inventory_transfers(&mut drops, &mut pickups);

    let mut events = Vec::new();
    if initial_c4_owner == Some(steam_id) {
        events.push(AbsoluteHifiEvent {
            tick: start_tick,
            kind: ReplayHifiEventKind::BombInitialOwner,
            actor_steam_id: Some(steam_id),
            target_steam_id: Some(steam_id),
            weapon_def_index: Some(49),
            item_name: Some("weapon_c4".to_string()),
            entity_id: None,
            actor_count_after: Some(1),
            target_count_after: Some(1),
            damage: None,
            health: None,
        });
    }

    for drop in drops.into_iter().filter(|drop| drop.steam_id == steam_id) {
        events.push(AbsoluteHifiEvent {
            tick: drop.tick,
            kind: ReplayHifiEventKind::ItemDrop,
            actor_steam_id: Some(drop.steam_id),
            target_steam_id: drop.paired_steam_id,
            weapon_def_index: Some(drop.weapon_def_index),
            item_name: drop.item_name,
            entity_id: None,
            actor_count_after: Some(drop.count_after),
            target_count_after: None,
            damage: None,
            health: None,
        });
    }
    for pickup in pickups
        .into_iter()
        .filter(|pickup| pickup.steam_id == steam_id)
    {
        events.push(AbsoluteHifiEvent {
            tick: pickup.tick,
            kind: if pickup.paired_steam_id.is_some() {
                ReplayHifiEventKind::ItemTransfer
            } else {
                ReplayHifiEventKind::ItemPickup
            },
            actor_steam_id: pickup.paired_steam_id,
            target_steam_id: Some(pickup.steam_id),
            weapon_def_index: Some(pickup.weapon_def_index),
            item_name: pickup.item_name,
            entity_id: None,
            actor_count_after: None,
            target_count_after: Some(pickup.count_after),
            damage: None,
            health: None,
        });
    }

    for event in &parsed.events {
        if event.tick < start_tick || event.tick > end_tick {
            continue;
        }
        if let Some(mapped) = map_recorded_game_event(event, steam_id) {
            events.push(mapped);
        }
    }

    events
}

fn infer_initial_c4_owner(
    parsed: &ParsedDemo,
    start_tick: i32,
    live_start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
) -> Option<u64> {
    let inventory_owner = round_rows
        .iter()
        .filter(|row| {
            row.tick >= start_tick
                && row.tick <= end_tick
                && row.team_num == 2
                && row.steam_id != 0
                && row.is_alive
        })
        .filter(|row| inventory_counts(row).get(&49).copied().unwrap_or(0) > 0)
        .min_by_key(|row| (row.tick, row.steam_id))
        .map(|row| row.steam_id);
    if inventory_owner.is_some() {
        return inventory_owner;
    }

    parsed
        .events
        .iter()
        .filter(|event| event.tick >= live_start_tick && event.tick <= end_tick)
        .filter(|event| {
            matches!(
                event.name.as_str(),
                "bomb_dropped" | "bomb_pickup" | "bomb_beginplant" | "bomb_planted"
            )
        })
        .min_by_key(|event| event.tick)
        .and_then(|event| event.user_steam_id.or(event.attacker_steam_id))
}

fn map_recorded_game_event(event: &ParsedGameEvent, steam_id: u64) -> Option<AbsoluteHifiEvent> {
    let kind = match event.name.as_str() {
        "bomb_dropped" => ReplayHifiEventKind::BombDrop,
        "bomb_pickup" => ReplayHifiEventKind::BombPickup,
        "bomb_beginplant" => ReplayHifiEventKind::BombBeginplant,
        "bomb_planted" => ReplayHifiEventKind::BombPlanted,
        "weapon_fire" => ReplayHifiEventKind::WeaponFire,
        "player_hurt" => ReplayHifiEventKind::PlayerHurt,
        "player_death" => ReplayHifiEventKind::PlayerDeath,
        _ => return None,
    };
    let actor_steam_id = match kind {
        ReplayHifiEventKind::PlayerHurt | ReplayHifiEventKind::PlayerDeath => {
            event.attacker_steam_id.or(event.user_steam_id)
        }
        _ => event.user_steam_id.or(event.attacker_steam_id),
    };
    let target_steam_id = match kind {
        ReplayHifiEventKind::BombPickup => event.user_steam_id,
        _ => event.victim_steam_id,
    };
    if actor_steam_id != Some(steam_id) && target_steam_id != Some(steam_id) {
        return None;
    }

    let weapon_def_index = event
        .weapon_def_index
        .map(normalize_weapon_def_index)
        .or_else(|| {
            event
                .item_name
                .as_deref()
                .and_then(weapon_def_index_from_item_name)
        })
        .or(match kind {
            ReplayHifiEventKind::BombDrop
            | ReplayHifiEventKind::BombPickup
            | ReplayHifiEventKind::BombBeginplant
            | ReplayHifiEventKind::BombPlanted => Some(49),
            _ => None,
        });

    Some(AbsoluteHifiEvent {
        tick: event.tick,
        kind,
        actor_steam_id,
        target_steam_id,
        weapon_def_index,
        item_name: weapon_def_index
            .and_then(weapon_item_name)
            .map(str::to_string)
            .or_else(|| event.item_name.clone()),
        entity_id: event.entity_id,
        actor_count_after: None,
        target_count_after: None,
        damage: event.damage,
        health: event.health,
    })
}

fn inferred_inventory_drops(
    parsed: &ParsedDemo,
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
) -> Vec<InventoryChange> {
    let mut changes = Vec::new();
    for rows in live_rows_by_player(round_rows).into_values() {
        for pair in rows.windows(2) {
            let prev = pair[0];
            let current = pair[1];
            if current.tick < start_tick || current.tick > end_tick {
                continue;
            }
            let prev_counts = inventory_counts(prev);
            let current_counts = inventory_counts(current);
            for (def, prev_count) in prev_counts {
                if !is_replay_equipment_event_def(def) {
                    continue;
                }
                let current_count = current_counts.get(&def).copied().unwrap_or(0);
                if current_count >= prev_count {
                    continue;
                }
                if projectile_consumed_inventory(parsed, current.steam_id, def, current.tick) {
                    continue;
                }
                changes.push(InventoryChange {
                    tick: current.tick,
                    steam_id: current.steam_id,
                    weapon_def_index: def,
                    count_after: current_count,
                    item_name: weapon_item_name(def).map(str::to_string),
                    paired_steam_id: None,
                });
            }
        }
    }
    changes.sort_by_key(|change| (change.tick, change.steam_id, change.weapon_def_index));
    changes
}

fn inferred_inventory_pickups(
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
    game_events: &[ParsedGameEvent],
) -> Vec<InventoryChange> {
    let by_player = live_rows_by_player(round_rows);
    let mut changes = Vec::new();
    let mut seen = BTreeSet::new();

    for event in game_events {
        if event.tick < start_tick || event.tick > end_tick || event.name != "item_pickup" {
            continue;
        }
        let Some(steam_id) = event.user_steam_id else {
            continue;
        };
        let Some(def) = event
            .weapon_def_index
            .map(normalize_weapon_def_index)
            .or_else(|| {
                event
                    .item_name
                    .as_deref()
                    .and_then(weapon_def_index_from_item_name)
            })
        else {
            continue;
        };
        if !is_replay_equipment_event_def(def) {
            continue;
        }
        if !seen.insert((event.tick, steam_id, def)) {
            continue;
        }
        let count_after = by_player
            .get(&steam_id)
            .and_then(|rows| inventory_count_at_or_after(rows, event.tick, def))
            .unwrap_or(1);
        changes.push(InventoryChange {
            tick: event.tick,
            steam_id,
            weapon_def_index: def,
            count_after,
            item_name: weapon_item_name(def)
                .map(str::to_string)
                .or_else(|| event.item_name.clone()),
            paired_steam_id: None,
        });
    }

    for rows in by_player.into_values() {
        for pair in rows.windows(2) {
            let prev = pair[0];
            let current = pair[1];
            if current.tick < start_tick || current.tick > end_tick {
                continue;
            }
            let prev_counts = inventory_counts(prev);
            let current_counts = inventory_counts(current);
            for (def, current_count) in current_counts {
                if !is_replay_equipment_event_def(def) {
                    continue;
                }
                let prev_count = prev_counts.get(&def).copied().unwrap_or(0);
                if current_count <= prev_count {
                    continue;
                }
                if !seen.insert((current.tick, current.steam_id, def)) {
                    continue;
                }
                changes.push(InventoryChange {
                    tick: current.tick,
                    steam_id: current.steam_id,
                    weapon_def_index: def,
                    count_after: current_count,
                    item_name: weapon_item_name(def).map(str::to_string),
                    paired_steam_id: None,
                });
            }
        }
    }

    changes.sort_by_key(|change| (change.tick, change.steam_id, change.weapon_def_index));
    changes
}

fn pair_inventory_transfers(drops: &mut [InventoryChange], pickups: &mut [InventoryChange]) {
    const TRANSFER_PAIR_TICKS: i32 = 128;
    for drop in drops.iter_mut() {
        let mut best_pickup = None;
        let mut best_delta = i32::MAX;
        for (index, pickup) in pickups.iter().enumerate() {
            if pickup.paired_steam_id.is_some()
                || pickup.steam_id == drop.steam_id
                || pickup.weapon_def_index != drop.weapon_def_index
                || pickup.tick < drop.tick
            {
                continue;
            }
            let delta = pickup.tick - drop.tick;
            if delta <= TRANSFER_PAIR_TICKS && delta < best_delta {
                best_pickup = Some(index);
                best_delta = delta;
            }
        }
        if let Some(index) = best_pickup {
            drop.paired_steam_id = Some(pickups[index].steam_id);
            pickups[index].paired_steam_id = Some(drop.steam_id);
        }
    }
}

fn inventory_snapshots_for_player(
    player_rows: &[&ParsedPlayerTick],
) -> Vec<ReplayInventorySnapshot> {
    let mut snapshots = Vec::new();
    let mut previous_counts: Option<Vec<ReplayInventoryItemCount>> = None;
    for (row_index, row) in player_rows.iter().enumerate() {
        let counts = inventory_counts(row)
            .into_iter()
            .map(|(weapon_def_index, count)| ReplayInventoryItemCount {
                weapon_def_index,
                count,
            })
            .collect::<Vec<_>>();
        if previous_counts.as_ref() == Some(&counts) && row_index != 0 {
            continue;
        }
        previous_counts = Some(counts.clone());
        let Some(tick_index) = tick_index_for_event(player_rows, row.tick) else {
            continue;
        };
        snapshots.push(ReplayInventorySnapshot {
            tick_index,
            tick: row.tick,
            steam_id: row.steam_id,
            weapon_def_counts: counts,
            active_weapon_def_index: normalize_weapon_def_index(row.item_def_idx),
            armor_value: row.armor_value,
            has_helmet: row.has_helmet,
            has_defuser: row.has_defuser,
        });
    }
    snapshots
}

fn live_rows_by_player<'a>(
    round_rows: &[&'a ParsedPlayerTick],
) -> BTreeMap<u64, Vec<&'a ParsedPlayerTick>> {
    let mut by_player: BTreeMap<u64, Vec<&ParsedPlayerTick>> = BTreeMap::new();
    for &row in round_rows {
        if row.steam_id == 0 || !row.is_alive {
            continue;
        }
        by_player.entry(row.steam_id).or_default().push(row);
    }
    for rows in by_player.values_mut() {
        rows.sort_by_key(|row| row.tick);
        rows.dedup_by_key(|row| row.tick);
    }
    by_player
}

fn cosmetic_rows_by_player<'a>(
    round_rows: &[&'a ParsedPlayerTick],
    end_tick: i32,
    side: Side,
) -> BTreeMap<u64, Vec<&'a ParsedPlayerTick>> {
    let mut by_player: BTreeMap<u64, Vec<&ParsedPlayerTick>> = BTreeMap::new();
    for &row in round_rows {
        if row.tick > end_tick
            || !row.is_alive
            || row.steam_id == 0
            || !side.matches_team(row.team_num)
        {
            continue;
        }
        by_player.entry(row.steam_id).or_default().push(row);
    }
    for rows in by_player.values_mut() {
        rows.sort_by_key(|row| row.tick);
        rows.dedup_by_key(|row| row.tick);
    }
    by_player
}

fn inventory_counts(row: &ParsedPlayerTick) -> BTreeMap<i32, u32> {
    let mut counts = BTreeMap::new();
    for raw_def in &row.inventory_as_ids {
        let def = normalize_weapon_def_index(*raw_def);
        if !is_known_weapon_def_index(def) {
            continue;
        }
        *counts.entry(def).or_insert(0) += 1;
    }
    counts
}

fn inventory_count_at_or_after(rows: &[&ParsedPlayerTick], tick: i32, def: i32) -> Option<u32> {
    rows.iter()
        .find(|row| row.tick >= tick)
        .map(|row| inventory_counts(row).get(&def).copied().unwrap_or(0))
}

fn projectile_consumed_inventory(
    parsed: &ParsedDemo,
    steam_id: u64,
    weapon_def_index: i32,
    tick: i32,
) -> bool {
    parsed.projectiles.iter().any(|projectile| {
        projectile.steam_id == steam_id
            && normalize_weapon_def_index(projectile.weapon_def_index) == weapon_def_index
            && (projectile.tick - tick).abs() <= 16
    })
}

fn tick_index_for_event(player_rows: &[&ParsedPlayerTick], tick: i32) -> Option<u32> {
    if player_rows.len() < 2 {
        return None;
    }
    let max_index = player_rows.len().saturating_sub(2);
    let index = player_rows
        .iter()
        .take(player_rows.len().saturating_sub(1))
        .position(|row| row.tick >= tick)
        .unwrap_or(max_index)
        .min(max_index);
    Some(index as u32)
}

fn hifi_event_rank(kind: ReplayHifiEventKind) -> u8 {
    match kind {
        ReplayHifiEventKind::BombInitialOwner => 0,
        ReplayHifiEventKind::RoundStart => 1,
        ReplayHifiEventKind::RoundFreezeEnd => 2,
        ReplayHifiEventKind::BombDrop => 3,
        ReplayHifiEventKind::ItemDrop => 4,
        ReplayHifiEventKind::BombPickup => 5,
        ReplayHifiEventKind::ItemPickup => 6,
        ReplayHifiEventKind::ItemTransfer => 7,
        ReplayHifiEventKind::BombBeginplant => 8,
        ReplayHifiEventKind::BombPlanted => 9,
        ReplayHifiEventKind::WeaponFire => 10,
        ReplayHifiEventKind::PlayerHurt => 11,
        ReplayHifiEventKind::PlayerDeath => 12,
    }
}

fn ticks_to_seconds(ticks: i32, tick_rate: f32) -> f32 {
    if tick_rate > 0.0 {
        ticks as f32 / tick_rate
    } else {
        0.0
    }
}

fn validate_freeze_preroll_seconds(seconds: f32) -> Result<()> {
    if seconds.is_finite() && seconds >= 0.0 {
        return Ok(());
    }
    Err(Error::InvalidDemo(
        "freeze pre-roll must be a finite non-negative number".to_string(),
    ))
}

fn rows_by_round(rows: &[ParsedPlayerTick]) -> BTreeMap<u32, Vec<&ParsedPlayerTick>> {
    let mut by_round: BTreeMap<u32, Vec<&ParsedPlayerTick>> = BTreeMap::new();
    for row in rows {
        by_round.entry(row.round).or_default().push(row);
    }
    by_round
}

fn projectiles_by_steam_id(
    projectiles: &[ParsedProjectile],
) -> BTreeMap<u64, Vec<&ParsedProjectile>> {
    let mut by_steam_id: BTreeMap<u64, Vec<&ParsedProjectile>> = BTreeMap::new();
    for projectile in projectiles {
        by_steam_id
            .entry(projectile.steam_id)
            .or_default()
            .push(projectile);
    }
    by_steam_id
}

fn recording_start_tick_for_round(
    round_rows: &[&ParsedPlayerTick],
    tick_rate: f32,
    live_start_tick: i32,
    freeze_preroll_seconds: f32,
) -> i32 {
    let cap_ticks = seconds_to_ticks(freeze_preroll_seconds, tick_rate);
    if cap_ticks <= 0 {
        return live_start_tick;
    }
    let floor_tick = live_start_tick.saturating_sub(cap_ticks);
    let mut ticks_by_player = BTreeMap::<u64, BTreeSet<i32>>::new();
    for row in round_rows.iter().copied().filter(|row| {
        row.tick >= floor_tick
            && row.tick <= live_start_tick
            && (row.tick == live_start_tick || row.is_freeze_period)
            && row.is_alive
            && row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
    }) {
        ticks_by_player
            .entry(row.steam_id)
            .or_default()
            .insert(row.tick);
    }

    let mut common_start_tick = floor_tick;
    let mut found_live_player = false;
    for ticks in ticks_by_player.values() {
        if !ticks.contains(&live_start_tick) {
            // A freeze-only player would otherwise create an isolated replay
            // fragment. Dropping pre-roll for this round is safer than
            // synthesizing across a missing interval.
            return live_start_tick;
        }
        found_live_player = true;
        let mut player_start_tick = live_start_tick;
        while player_start_tick > floor_tick && ticks.contains(&player_start_tick.saturating_sub(1))
        {
            player_start_tick = player_start_tick.saturating_sub(1);
        }
        common_start_tick = common_start_tick.max(player_start_tick);
    }

    found_live_player
        .then_some(common_start_tick)
        .unwrap_or(live_start_tick)
}

fn seconds_to_ticks(seconds: f32, tick_rate: f32) -> i32 {
    if !seconds.is_finite() || !tick_rate.is_finite() || seconds <= 0.0 || tick_rate <= 0.0 {
        return 0;
    }
    (seconds * tick_rate).round() as i32
}

fn play_start_tick_index(rows: &[&ParsedPlayerTick], live_start_tick: i32) -> u32 {
    let tick_count = rows.len().saturating_sub(1);
    if tick_count == 0 {
        return 0;
    }
    rows.iter()
        .take(tick_count)
        .position(|row| row.tick >= live_start_tick)
        .unwrap_or_else(|| tick_count.saturating_sub(1)) as u32
}

fn team_economy(
    round_rows: &[&ParsedPlayerTick],
    start_tick: i32,
    end_tick: i32,
    team_num: u8,
    pistol_round: bool,
) -> TeamEconomy {
    let mut first_rows: BTreeMap<u64, &ParsedPlayerTick> = BTreeMap::new();
    for &row in round_rows {
        if row.tick < start_tick
            || row.tick > end_tick
            || row.team_num != team_num
            || !row.is_alive
            || row.steam_id == 0
        {
            continue;
        }
        first_rows.entry(row.steam_id).or_insert(row);
    }

    let mut round_start_equipment_value = 0_u32;
    let mut equipment_value_total = 0_u32;
    let mut money_saved_total = 0_u32;
    let mut cash_spent_this_round = 0_u32;
    for row in first_rows.values() {
        let inferred = infer_inventory_value(&row.inventory_as_ids);
        let start_value = row.round_start_equip_value.max(inferred);
        let total_value = row.equipment_value_total.max(start_value);
        round_start_equipment_value = round_start_equipment_value.saturating_add(start_value);
        equipment_value_total = equipment_value_total.saturating_add(total_value);
        money_saved_total = money_saved_total.saturating_add(row.money_saved_total);
        cash_spent_this_round = cash_spent_this_round.saturating_add(row.cash_spent_this_round);
    }

    let class = classify_economy(
        first_rows.len(),
        round_start_equipment_value.max(equipment_value_total),
        pistol_round,
    );
    TeamEconomy {
        side: Side::team_dir(team_num).to_string(),
        players: first_rows.len(),
        round_start_equipment_value,
        equipment_value_total,
        money_saved_total,
        cash_spent_this_round,
        class,
    }
}

fn classify_economy(players: usize, team_equipment_value: u32, pistol_round: bool) -> EconomyClass {
    if pistol_round {
        return EconomyClass::Pistol;
    }
    if players == 0 {
        return EconomyClass::Unknown;
    }
    let per_player = team_equipment_value as f32 / players as f32;
    if per_player < 1_400.0 {
        EconomyClass::Eco
    } else if per_player < 3_600.0 {
        EconomyClass::Force
    } else {
        EconomyClass::Full
    }
}

fn infer_inventory_value(defs: &[i32]) -> u32 {
    defs.iter()
        .map(|def| weapon_value(normalize_weapon_def_index(*def)))
        .sum()
}

fn weapon_value(def: i32) -> u32 {
    match def {
        1 => 700,
        2 => 300,
        3 => 500,
        4 => 200,
        7 => 2700,
        8 => 3300,
        9 => 4750,
        10 => 2050,
        11 => 5000,
        13 => 1800,
        14 => 5200,
        16 => 3100,
        17 => 1050,
        19 => 2350,
        23 => 1500,
        24 => 1200,
        25 => 2000,
        26 => 1400,
        27 => 1300,
        28 => 1700,
        29 => 1100,
        30 => 500,
        31 => 200,
        32 => 200,
        33 => 1500,
        34 => 1250,
        35 => 1050,
        36 => 300,
        38 => 5000,
        39 => 3000,
        40 => 1700,
        43 => 200,
        44 => 300,
        45 => 300,
        46 => 400,
        47 => 50,
        48 => 600,
        60 => 2900,
        61 => 200,
        63 => 500,
        64 => 600,
        _ => 0,
    }
}

fn is_pistol_round(round: u32) -> bool {
    round == 0 || round == 12
}

fn replay_view(rows: &[&ParsedPlayerTick]) -> Option<ReplayView> {
    let mut crosshair_codes = BTreeSet::new();
    let mut left_handed_values = BTreeSet::new();
    let mut fov_values = BTreeSet::new();
    let mut offset_x_values = BTreeSet::new();
    let mut offset_y_values = BTreeSet::new();
    let mut offset_z_values = BTreeSet::new();

    for row in rows {
        if !row.is_alive || row.steam_id == 0 {
            continue;
        }

        if let Some(code) = row
            .crosshair_code
            .as_deref()
            .map(str::trim)
            .filter(|code| !code.is_empty())
        {
            crosshair_codes.insert(code.to_string());
        }

        if let Some(value) = row.viewmodel_left_handed {
            left_handed_values.insert(value);
        }
        insert_stable_f32(&mut fov_values, row.viewmodel_fov);
        insert_stable_f32(&mut offset_x_values, row.viewmodel_offset_x);
        insert_stable_f32(&mut offset_y_values, row.viewmodel_offset_y);
        insert_stable_f32(&mut offset_z_values, row.viewmodel_offset_z);
    }

    let viewmodel = ReplayViewmodel {
        left_handed: stable_bool(left_handed_values),
        fov: stable_f32(fov_values),
        offset_x: stable_f32(offset_x_values),
        offset_y: stable_f32(offset_y_values),
        offset_z: stable_f32(offset_z_values),
    };

    let view = ReplayView {
        crosshair_code: (crosshair_codes.len() == 1)
            .then(|| crosshair_codes.into_iter().next())
            .flatten(),
        viewmodel: (!viewmodel.is_empty()).then_some(viewmodel),
    };
    (!view.is_empty()).then_some(view)
}

fn insert_stable_f32(values: &mut BTreeSet<u32>, value: Option<f32>) {
    if let Some(value) = value.filter(|value| value.is_finite()) {
        let normalized = if value == 0.0 { 0.0 } else { value };
        values.insert(normalized.to_bits());
    }
}

fn stable_bool(values: BTreeSet<bool>) -> Option<bool> {
    (values.len() == 1)
        .then(|| values.into_iter().next())
        .flatten()
}

fn stable_f32(values: BTreeSet<u32>) -> Option<f32> {
    (values.len() == 1)
        .then(|| values.into_iter().next().map(f32::from_bits))
        .flatten()
}

fn replay_round_scoreboard(round_rows: &[&ParsedPlayerTick]) -> Option<ReplayRoundScoreboard> {
    let mut t_score = None;
    let mut ct_score = None;
    let mut t_team_name = None;
    let mut ct_team_name = None;

    for &row in round_rows {
        match row.team_num {
            2 => {
                if let Some(score) = row.team_rounds_total {
                    t_score.get_or_insert(score);
                }
                if t_team_name.is_none() {
                    t_team_name = clean_replay_team_name(row);
                }
            }
            3 => {
                if let Some(score) = row.team_rounds_total {
                    ct_score.get_or_insert(score);
                }
                if ct_team_name.is_none() {
                    ct_team_name = clean_replay_team_name(row);
                }
            }
            _ => {}
        }
        if t_score.is_some()
            && ct_score.is_some()
            && t_team_name.is_some()
            && ct_team_name.is_some()
        {
            break;
        }
    }

    Some(ReplayRoundScoreboard {
        t_score: t_score?,
        ct_score: ct_score?,
        t_team_name,
        ct_team_name,
    })
}

fn replay_chat_messages(
    parsed: &ParsedDemo,
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
) -> Vec<ReplayChatMessage> {
    let mut names_by_steam_id = BTreeMap::new();
    for &row in round_rows {
        if row.steam_id != 0 && !row.name.trim().is_empty() {
            names_by_steam_id
                .entry(row.steam_id)
                .or_insert_with(|| row.name.trim().to_string());
        }
    }

    let mut seen = BTreeSet::new();
    let mut messages = Vec::new();
    for event in &parsed.events {
        if event.tick < start_tick || event.tick > end_tick {
            continue;
        }
        let Some(text) = event.chat_text.as_deref().and_then(clean_replay_chat_text) else {
            continue;
        };
        let scope = clean_replay_chat_scope(event.chat_scope.as_deref());
        let sender_steam_id = event.user_steam_id.unwrap_or_default();
        if sender_steam_id == 0 && scope != "server" {
            continue;
        }
        let key = (event.tick, sender_steam_id, scope.clone(), text.clone());
        if !seen.insert(key) {
            continue;
        }
        messages.push(ReplayChatMessage {
            tick: event.tick,
            sender_steam_id,
            sender_name: names_by_steam_id.get(&sender_steam_id).cloned(),
            scope,
            text,
        });
    }

    messages.sort_by(|left, right| {
        (left.tick, left.sender_steam_id, &left.scope, &left.text).cmp(&(
            right.tick,
            right.sender_steam_id,
            &right.scope,
            &right.text,
        ))
    });
    messages
}

fn clean_replay_chat_text(value: &str) -> Option<String> {
    let cleaned = value
        .trim()
        .chars()
        .filter(|ch| !ch.is_control() || *ch == '\t')
        .take(256)
        .collect::<String>()
        .trim()
        .to_string();
    (!cleaned.is_empty()).then_some(cleaned)
}

fn clean_replay_chat_scope(value: Option<&str>) -> String {
    match value.unwrap_or("all").trim().to_ascii_lowercase().as_str() {
        "team" => "team".to_string(),
        "server" | "admin" => "server".to_string(),
        _ => "all".to_string(),
    }
}

fn replay_player_scoreboard(rows: &[&ParsedPlayerTick]) -> Option<ReplayPlayerScoreboard> {
    for row in rows {
        let scoreboard = ReplayPlayerScoreboard {
            player_user_id: row.player_user_id,
            player_entity_id: row.player_entity_id,
            player_color: clean_replay_player_color(row.player_color.as_deref()),
            score: row.scoreboard_score,
            kills: row.scoreboard_kills,
            deaths: row.scoreboard_deaths,
            assists: row.scoreboard_assists,
            mvps: row.scoreboard_mvps,
        };
        if !scoreboard.is_empty() {
            return Some(scoreboard);
        }
    }

    None
}

fn clean_replay_team_name(row: &ParsedPlayerTick) -> Option<String> {
    row.team_clan_name
        .as_deref()
        .filter(|value| !value.trim().is_empty())
        .or_else(|| {
            row.team_name
                .as_deref()
                .filter(|value| !value.trim().is_empty())
        })
        .map(str::trim)
        .map(str::to_string)
}

fn clean_replay_player_color(value: Option<&str>) -> Option<String> {
    let value = value?.trim().to_ascii_lowercase();
    matches!(
        value.as_str(),
        "blue" | "green" | "yellow" | "orange" | "purple"
    )
    .then_some(value)
}

fn replay_cosmetics_at(
    rows: &[&ParsedPlayerTick],
    cosmetic_rows: &[&ParsedPlayerTick],
    play_start_tick_index: u32,
    econ_glove_seeds: Option<&EconGloveSeedMap>,
    econ_knife_paints: Option<&EconKnifePaintMap>,
    export_stickers: bool,
    export_charms: bool,
) -> Option<ReplayCosmetics> {
    let play_start = (play_start_tick_index as usize).min(rows.len().saturating_sub(1));
    let active_rows = rows.get(play_start..).unwrap_or(rows);
    let mut cosmetics = replay_active_cosmetics(
        active_rows,
        econ_glove_seeds,
        econ_knife_paints,
        export_stickers,
    )
    .unwrap_or_default();
    if let Some(agent) = replay_agent_cosmetic(rows) {
        cosmetics.agent = Some(agent);
    }
    if let Some(weapons) = live_start_inventory_weapon_cosmetics(
        rows,
        play_start_tick_index,
        export_stickers,
        export_charms,
    ) {
        cosmetics.weapons = weapons;
    } else if let Some(weapons) =
        round_inventory_weapon_cosmetics(cosmetic_rows, export_stickers, export_charms)
    {
        cosmetics.weapons = weapons;
    }
    cosmetics
        .weapons
        .sort_by_key(|weapon| weapon.weapon_def_index);
    populate_cosmetic_inspect(&mut cosmetics);
    (!cosmetics.is_empty()).then_some(cosmetics)
}

fn populate_cosmetic_inspect(cosmetics: &mut ReplayCosmetics) {
    for weapon in &mut cosmetics.weapons {
        let rarity = weapon_cosmetic_rarity(weapon.weapon_def_index, weapon.paint_kit);
        let inspect = weapon_inspect(weapon, rarity);
        weapon.inspect = inspect;
    }
    if let Some(knife) = cosmetics.knife.as_mut() {
        let inspect = item_inspect(knife, Some(6));
        knife.inspect = inspect;
    }
    if let Some(glove) = cosmetics.glove.as_mut() {
        let inspect = item_inspect(glove, Some(6));
        glove.inspect = inspect;
    }
}

const AGENT_MODEL_FAMILIES: &[&str] = &[
    "ctm_diver",
    "ctm_fbi",
    "ctm_gendarmerie",
    "ctm_gign",
    "ctm_gsg9",
    "ctm_heavy",
    "ctm_idf",
    "ctm_sas",
    "ctm_st6",
    "ctm_swat",
    "tm_anarchist",
    "tm_balkan",
    "tm_jungle_raider",
    "tm_jumpsuit",
    "tm_leet",
    "tm_phoenix_heavy",
    "tm_phoenix",
    "tm_pirate",
    "tm_professional",
    "tm_separatist",
];

fn replay_agent_cosmetic(rows: &[&ParsedPlayerTick]) -> Option<ReplayAgentCosmetic> {
    let mut item_defs = BTreeSet::new();
    let mut names = BTreeSet::new();
    let mut model_paths = BTreeSet::new();

    for row in rows {
        if row.steam_id == 0 {
            continue;
        }
        let Some(item_def) = row.agent_item_def_index.filter(|value| *value != 0) else {
            continue;
        };
        if !valid_agent_item_def_index(item_def) {
            continue;
        }
        let Some(name) = row.agent_skin.as_deref() else {
            continue;
        };
        let Some(model_path) = agent_model_path_from_name(name) else {
            continue;
        };
        item_defs.insert(item_def);
        names.insert(name.to_string());
        model_paths.insert(model_path);
    }

    if item_defs.len() != 1 || model_paths.len() != 1 {
        return None;
    }

    Some(ReplayAgentCosmetic {
        item_def_index: *item_defs.iter().next()?,
        model_path: model_paths.into_iter().next()?,
        name: (names.len() == 1)
            .then(|| names.into_iter().next())
            .flatten(),
    })
}

fn agent_model_path_from_name(name: &str) -> Option<String> {
    let normalized = name.trim().to_ascii_lowercase();
    let stem = normalized.strip_prefix("customplayer_")?;
    if stem == "t_map_based" || stem == "ct_map_based" {
        return None;
    }
    if !stem
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
    {
        return None;
    }

    let family = AGENT_MODEL_FAMILIES
        .iter()
        .filter(|family| stem == **family || stem.starts_with(&format!("{family}_")))
        .max_by_key(|family| family.len())?;
    Some(format!("agents\\models\\{family}\\{stem}.vmdl"))
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct EconGloveKey {
    item_def_index: i32,
    paint_kit: u32,
    wear_bits: u32,
}

pub(crate) type EconGloveSeedMap = BTreeMap<EconGloveKey, Option<u32>>;
pub(crate) type EconGloveSeedIndex = BTreeMap<u64, EconGloveSeedMap>;

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct EconKnifePaint {
    pub(crate) paint_kit: u32,
    pub(crate) seed: u32,
    pub(crate) wear_bits: u32,
    pub(crate) custom_name: Option<String>,
}

pub(crate) type EconKnifePaintMap = BTreeMap<i32, Option<EconKnifePaint>>;
pub(crate) type EconKnifePaintIndex = BTreeMap<u64, EconKnifePaintMap>;

pub(crate) fn knife_econ_paint_index(parsed: &ParsedDemo) -> EconKnifePaintIndex {
    let mut paints_by_player = EconKnifePaintIndex::new();
    for item in &parsed.econ_items {
        let Some(steam_id) = item.steam_id else {
            continue;
        };
        let Some(item_def_index) = item
            .item_def_index
            .and_then(|value| i32::try_from(value).ok())
            .filter(|value| is_knife_cosmetic_def_index(*value))
        else {
            continue;
        };
        let Some(spec) = cosmetic_paint_spec(
            item.paint_kit,
            item.paint_seed,
            item.paint_wear_raw.map(f32::from_bits).or(item.paint_wear),
        ) else {
            continue;
        };
        let paint = EconKnifePaint {
            paint_kit: spec.paint_kit,
            seed: spec.seed,
            wear_bits: spec.wear_bits,
            custom_name: cosmetic_custom_name_value(item.custom_name.as_deref()),
        };
        let paints = paints_by_player.entry(steam_id).or_default();
        match paints.entry(item_def_index) {
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert(Some(paint));
            }
            std::collections::btree_map::Entry::Occupied(mut entry) => {
                let Some(current) = entry.get_mut() else {
                    continue;
                };
                if current.paint_kit != paint.paint_kit
                    || current.seed != paint.seed
                    || current.wear_bits != paint.wear_bits
                {
                    *entry.get_mut() = None;
                    continue;
                }
                match (&current.custom_name, paint.custom_name) {
                    (None, Some(name)) => current.custom_name = Some(name),
                    (Some(existing), Some(name)) if existing != &name => {
                        current.custom_name = None;
                    }
                    _ => {}
                }
            }
        }
    }
    paints_by_player
}

fn matching_econ_knife_paint(
    paints: Option<&EconKnifePaintMap>,
    item_def_index: i32,
) -> Option<EconKnifePaint> {
    paints?.get(&item_def_index).cloned().flatten()
}

pub(crate) fn matching_active_econ_knife_paint(
    paints: Option<&EconKnifePaintMap>,
    row: &ParsedPlayerTick,
) -> Option<EconKnifePaint> {
    let paint = matching_econ_knife_paint(paints, row.item_def_idx)?;
    if active_cosmetic_owned_by(row) {
        if let Some(observed_wear) = row
            .active_weapon_paint_wear
            .filter(|wear| wear.is_finite() && (0.0..=1.0).contains(wear))
        {
            return (row.active_weapon_paint_seed == Some(paint.seed)
                && observed_wear.to_bits() == paint.wear_bits)
                .then_some(paint);
        }
    }

    // Some tournament demos expose the exact custom knife defindex while every
    // live econ field remains at its network default. The end-of-match inventory
    // is already scoped to this SteamID; when it contains one unambiguous paint
    // for that same knife type, the active defindex is the missing linkage.
    let live_econ_unavailable = row.active_weapon_paint_kit.is_none_or(|value| value == 0)
        && row.active_weapon_paint_seed.is_none_or(|value| value == 0)
        && row.active_weapon_paint_wear.is_none()
        && row
            .active_weapon_original_owner_steam_id
            .is_none_or(|value| value == 0)
        && row
            .active_weapon_item_account_id
            .is_none_or(|value| value <= 1)
        && row.active_weapon_item_id.is_none_or(|value| value == 0);
    live_econ_unavailable.then_some(paint)
}

pub(crate) fn glove_econ_seed_index(parsed: &ParsedDemo) -> EconGloveSeedIndex {
    let mut seeds_by_player = EconGloveSeedIndex::new();
    for item in &parsed.econ_items {
        let Some(steam_id) = item.steam_id else {
            continue;
        };
        let Some((key, seed)) = econ_glove_seed(item) else {
            continue;
        };
        let seeds = seeds_by_player.entry(steam_id).or_default();
        match seeds.entry(key) {
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert(Some(seed));
            }
            std::collections::btree_map::Entry::Occupied(mut entry) => {
                if entry.get().is_some_and(|current| current != seed) {
                    entry.insert(None);
                }
            }
        }
    }
    seeds_by_player
}

pub(crate) fn matching_econ_glove_seed(
    seeds: Option<&EconGloveSeedMap>,
    item_def_index: i32,
    paint_kit: u32,
    wear_bits: u32,
) -> Option<u32> {
    seeds?
        .get(&EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        })
        .copied()
        .flatten()
}

fn econ_glove_seed(item: &ParsedEconItem) -> Option<(EconGloveKey, u32)> {
    let item_def_index = i32::try_from(item.item_def_index?).ok()?;
    if !valid_glove_item_def_index(item_def_index) {
        return None;
    }
    let paint_kit = item.paint_kit.filter(|value| valid_paint_kit(*value))?;
    let seed = item.paint_seed?;
    let wear_bits = item.paint_wear_raw?;
    let wear = f32::from_bits(wear_bits);
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    Some((
        EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        },
        seed,
    ))
}

#[derive(Clone, Copy, Debug)]
struct ObservedGloveSpec {
    key: EconGloveKey,
    seed: Option<u32>,
}

fn replay_active_cosmetics(
    rows: &[&ParsedPlayerTick],
    econ_glove_seeds: Option<&EconGloveSeedMap>,
    econ_knife_paints: Option<&EconKnifePaintMap>,
    export_stickers: bool,
) -> Option<ReplayCosmetics> {
    let mut weapon_specs: BTreeMap<i32, BTreeSet<CosmeticPaintSpec>> = BTreeMap::new();
    let mut weapon_custom_names: BTreeMap<i32, BTreeSet<String>> = BTreeMap::new();
    let mut weapon_original_owners: BTreeMap<i32, BTreeSet<u64>> = BTreeMap::new();
    let mut weapon_account_ids: BTreeMap<i32, BTreeSet<u32>> = BTreeMap::new();
    let mut weapon_item_ids: BTreeMap<i32, BTreeSet<u64>> = BTreeMap::new();
    let mut weapon_stickers: BTreeMap<i32, BTreeSet<Vec<CosmeticStickerSpec>>> = BTreeMap::new();
    let mut weapon_stickers_missing = BTreeSet::new();
    let mut knife_specs = BTreeSet::new();
    let mut knife_custom_names = BTreeSet::new();
    let mut glove = None;

    for row in rows {
        if !row.is_alive || row.steam_id == 0 {
            continue;
        }

        let raw_def = row.item_def_idx;
        if is_knife_cosmetic_def_index(raw_def) {
            let active_spec = active_cosmetic_owned_by(row)
                .then(|| {
                    cosmetic_paint_spec(
                        row.active_weapon_paint_kit,
                        row.active_weapon_paint_seed,
                        row.active_weapon_paint_wear,
                    )
                })
                .flatten();
            let econ_paint = matching_active_econ_knife_paint(econ_knife_paints, row);
            let econ_spec = econ_paint.as_ref().map(|paint| CosmeticPaintSpec {
                paint_kit: paint.paint_kit,
                seed: paint.seed,
                wear_bits: paint.wear_bits,
            });
            if let Some(spec) = active_spec.or(econ_spec) {
                knife_specs.insert((raw_def, spec));
            }
            if active_cosmetic_owned_by(row) {
                if let Some(name) = active_cosmetic_custom_name(row) {
                    knife_custom_names.insert((raw_def, name));
                }
            }
            if let Some(name) = econ_paint.and_then(|paint| paint.custom_name) {
                knife_custom_names.insert((raw_def, name));
            }
        } else {
            let def = normalize_weapon_def_index(raw_def);
            if is_weapon_cosmetic_def_index(def) {
                if has_trusted_active_weapon_cosmetic_identity(row) {
                    if let Some(spec) = cosmetic_paint_spec(
                        row.active_weapon_paint_kit,
                        row.active_weapon_paint_seed,
                        row.active_weapon_paint_wear,
                    ) {
                        if valid_weapon_cosmetic_paint(def, spec.paint_kit) {
                            weapon_specs.entry(def).or_default().insert(spec);
                        }
                    }
                    if let Some(name) = active_cosmetic_custom_name(row) {
                        weapon_custom_names.entry(def).or_default().insert(name);
                    }
                    if let Some(owner) = row
                        .active_weapon_original_owner_steam_id
                        .filter(|value| *value != 0)
                    {
                        weapon_original_owners.entry(def).or_default().insert(owner);
                    }
                    if let Some(account_id) =
                        row.active_weapon_item_account_id.filter(|value| *value > 1)
                    {
                        weapon_account_ids
                            .entry(def)
                            .or_default()
                            .insert(account_id);
                    }
                    if let Some(item_id) = row.active_weapon_item_id.filter(|value| *value != 0) {
                        weapon_item_ids.entry(def).or_default().insert(item_id);
                    }
                    if export_stickers {
                        match active_cosmetic_sticker_set(row) {
                            Some(stickers) => {
                                weapon_stickers.entry(def).or_default().insert(stickers);
                            }
                            None => {
                                weapon_stickers_missing.insert(def);
                            }
                        }
                    }
                }
            }
        }

        if glove.is_none_or(|spec: ObservedGloveSpec| spec.seed.is_none()) {
            if let Some(candidate) = observed_glove_spec(row, econ_glove_seeds) {
                match &mut glove {
                    None => glove = Some(candidate),
                    Some(current)
                        if current.seed.is_none()
                            && candidate.seed.is_some()
                            && current.key == candidate.key =>
                    {
                        current.seed = candidate.seed;
                    }
                    Some(_) => {}
                }
            }
        }
    }

    let mut cosmetics = ReplayCosmetics::default();
    for (weapon_def_index, specs) in weapon_specs {
        if specs.len() != 1 {
            continue;
        }
        let spec = *specs.iter().next().expect("checked one cosmetic spec");
        cosmetics.weapons.push(ReplayWeaponCosmetic {
            weapon_def_index,
            paint_kit: spec.paint_kit,
            seed: spec.seed,
            wear: f32::from_bits(spec.wear_bits),
            quality: None,
            stattrak_counter: None,
            original_owner_steam_id: stable_weapon_u64_value(
                &weapon_original_owners,
                weapon_def_index,
            ),
            item_account_id: stable_weapon_u32_value(&weapon_account_ids, weapon_def_index),
            item_id: stable_weapon_u64_value(&weapon_item_ids, weapon_def_index),
            custom_name: stable_weapon_custom_name(&weapon_custom_names, weapon_def_index),
            stickers: stable_weapon_stickers(
                &weapon_stickers,
                &weapon_stickers_missing,
                weapon_def_index,
            ),
            charms: Vec::new(),
            inspect: None,
        });
    }

    if knife_specs.len() == 1 {
        if let Some((item_def_index, spec)) = knife_specs.iter().next().copied() {
            cosmetics.knife = Some(ReplayItemCosmetic {
                item_def_index: Some(item_def_index),
                paint_kit: spec.paint_kit,
                seed: spec.seed,
                seed_known: None,
                wear: f32::from_bits(spec.wear_bits),
                custom_name: stable_knife_custom_name(&knife_custom_names, item_def_index),
                inspect: None,
            });
        }
    }

    if let Some(glove) = glove {
        let seed_known = glove.seed.is_some();
        cosmetics.glove = Some(ReplayItemCosmetic {
            item_def_index: Some(glove.key.item_def_index),
            paint_kit: glove.key.paint_kit,
            seed: glove
                .seed
                .unwrap_or_else(|| stable_glove_fallback_seed(rows, glove.key)),
            seed_known: (!seed_known).then_some(false),
            wear: f32::from_bits(glove.key.wear_bits),
            custom_name: None,
            inspect: None,
        });
    }

    cosmetics
        .weapons
        .sort_by_key(|weapon| weapon.weapon_def_index);
    (!cosmetics.is_empty()).then_some(cosmetics)
}

fn stable_glove_fallback_seed(rows: &[&ParsedPlayerTick], key: EconGloveKey) -> u32 {
    let identity = rows
        .iter()
        .find(|row| row.steam_id != 0)
        .map(|row| (row.steam_id, row.team_num))
        .unwrap_or_default();
    let mut hasher = Sha256::new();
    hasher.update(b"cs2-demotracer-glove-fallback-seed-v1\0");
    hasher.update(identity.0.to_le_bytes());
    hasher.update(identity.1.to_le_bytes());
    hasher.update(key.item_def_index.to_le_bytes());
    hasher.update(key.paint_kit.to_le_bytes());
    hasher.update(key.wear_bits.to_le_bytes());
    let digest = hasher.finalize();
    1 + u32::from_le_bytes(digest[..4].try_into().expect("SHA-256 prefix")) % 1_000
}

fn live_start_inventory_weapon_cosmetics(
    rows: &[&ParsedPlayerTick],
    play_start_tick_index: u32,
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let row = live_start_inventory_cosmetic_row(rows, play_start_tick_index)?;
    Some(
        inventory_weapon_cosmetics_for_row(row, export_stickers, export_charms).unwrap_or_default(),
    )
}

fn round_inventory_weapon_cosmetics(
    rows: &[&ParsedPlayerTick],
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let mut by_def = BTreeMap::new();
    for row in rows {
        if !row.is_alive || row.steam_id == 0 {
            continue;
        }
        let Some(weapons) = inventory_weapon_cosmetics_for_row(row, export_stickers, export_charms)
        else {
            continue;
        };
        for weapon in weapons {
            by_def.entry(weapon.weapon_def_index).or_insert(weapon);
        }
    }
    let weapons = by_def.into_values().collect::<Vec<_>>();
    (!weapons.is_empty()).then_some(weapons)
}

fn live_start_inventory_cosmetic_row<'a>(
    rows: &[&'a ParsedPlayerTick],
    play_start_tick_index: u32,
) -> Option<&'a ParsedPlayerTick> {
    if rows.is_empty() {
        return None;
    }
    let center = (play_start_tick_index as usize).min(rows.len().saturating_sub(1));
    let mut candidates = Vec::with_capacity(5);
    candidates.push(center);
    for offset in 1..=2 {
        if center + offset < rows.len() {
            candidates.push(center + offset);
        }
    }
    for offset in 1..=2 {
        if let Some(idx) = center.checked_sub(offset) {
            candidates.push(idx);
        }
    }
    candidates
        .into_iter()
        .map(|idx| rows[idx])
        .find(|row| !row.inventory_weapon_cosmetics.is_empty())
}

fn inventory_weapon_cosmetics_for_row(
    row: &ParsedPlayerTick,
    export_stickers: bool,
    export_charms: bool,
) -> Option<Vec<ReplayWeaponCosmetic>> {
    let mut weapon_specs: BTreeMap<i32, BTreeSet<CosmeticPaintSpec>> = BTreeMap::new();
    let mut weapon_custom_names: BTreeMap<i32, BTreeSet<String>> = BTreeMap::new();
    let mut weapon_qualities: BTreeMap<i32, BTreeSet<i32>> = BTreeMap::new();
    let mut weapon_stattrak_counters: BTreeMap<i32, BTreeSet<i32>> = BTreeMap::new();
    let mut weapon_original_owners: BTreeMap<i32, BTreeSet<u64>> = BTreeMap::new();
    let mut weapon_account_ids: BTreeMap<i32, BTreeSet<u32>> = BTreeMap::new();
    let mut weapon_item_ids: BTreeMap<i32, BTreeSet<u64>> = BTreeMap::new();
    let mut weapon_stickers: BTreeMap<i32, BTreeSet<Vec<CosmeticStickerSpec>>> = BTreeMap::new();
    let mut weapon_charms: BTreeMap<i32, BTreeSet<Vec<CosmeticCharmSpec>>> = BTreeMap::new();
    let weapon_stickers_missing = BTreeSet::new();
    let weapon_charms_missing = BTreeSet::new();

    for item in row.inventory_weapon_cosmetics.iter() {
        let def = normalize_weapon_def_index(item.item_def_index);
        if !is_weapon_cosmetic_def_index(def) {
            continue;
        }
        if !has_trusted_inventory_weapon_cosmetic_identity(item) {
            continue;
        }
        if let Some(spec) = inventory_cosmetic_paint_spec(item) {
            if valid_weapon_cosmetic_paint(def, spec.paint_kit) {
                weapon_specs.entry(def).or_default().insert(spec);
            }
        }
        if let Some(name) = cosmetic_custom_name_value(item.custom_name.as_deref()) {
            weapon_custom_names.entry(def).or_default().insert(name);
        }
        if item.entity_quality == Some(9) {
            weapon_qualities.entry(def).or_default().insert(9);
        }
        if let Some(counter) = inventory_stattrak_counter(item) {
            weapon_stattrak_counters
                .entry(def)
                .or_default()
                .insert(counter);
        }
        if let Some(owner) = item.original_owner_xuid.filter(|value| *value != 0) {
            weapon_original_owners.entry(def).or_default().insert(owner);
        }
        if let Some(account_id) = item.item_account_id.filter(|value| *value != 0) {
            weapon_account_ids
                .entry(def)
                .or_default()
                .insert(account_id);
        }
        if let Some(item_id) =
            combine_item_id(item.item_id_high, item.item_id_low).filter(|value| *value != 0)
        {
            weapon_item_ids.entry(def).or_default().insert(item_id);
        }
        if export_stickers {
            if let Some(stickers) = cosmetic_sticker_set_from_slice(&item.stickers) {
                weapon_stickers.entry(def).or_default().insert(stickers);
            }
        }
        if export_charms {
            if let Some(charms) = cosmetic_charm_set_from_attributes(&item.attributes) {
                weapon_charms.entry(def).or_default().insert(charms);
            }
        }
    }

    let mut weapons = Vec::new();
    for (weapon_def_index, specs) in weapon_specs {
        if specs.len() != 1 {
            continue;
        }
        let spec = *specs.iter().next().expect("checked one cosmetic spec");
        weapons.push(ReplayWeaponCosmetic {
            weapon_def_index,
            paint_kit: spec.paint_kit,
            seed: spec.seed,
            wear: f32::from_bits(spec.wear_bits),
            quality: stable_weapon_i32_value(&weapon_qualities, weapon_def_index),
            stattrak_counter: stable_weapon_i32_value(&weapon_stattrak_counters, weapon_def_index),
            original_owner_steam_id: stable_weapon_u64_value(
                &weapon_original_owners,
                weapon_def_index,
            ),
            item_account_id: stable_weapon_u32_value(&weapon_account_ids, weapon_def_index),
            item_id: stable_weapon_u64_value(&weapon_item_ids, weapon_def_index),
            custom_name: stable_weapon_custom_name(&weapon_custom_names, weapon_def_index),
            stickers: stable_weapon_stickers(
                &weapon_stickers,
                &weapon_stickers_missing,
                weapon_def_index,
            ),
            charms: stable_weapon_charms(&weapon_charms, &weapon_charms_missing, weapon_def_index),
            inspect: None,
        });
    }
    weapons.sort_by_key(|weapon| weapon.weapon_def_index);
    (!weapons.is_empty()).then_some(weapons)
}

pub(crate) fn inventory_item_cosmetic_evidence(
    item: &ParsedInventoryWeaponCosmetic,
) -> Option<ReplayWeaponCosmetic> {
    let weapon_def_index = normalize_weapon_def_index(item.item_def_index);
    if !is_weapon_cosmetic_def_index(weapon_def_index)
        || !has_trusted_inventory_weapon_cosmetic_identity(item)
    {
        return None;
    }
    let spec = inventory_cosmetic_paint_spec(item)?;
    if !valid_weapon_cosmetic_paint(weapon_def_index, spec.paint_kit) {
        return None;
    }

    let stickers = cosmetic_sticker_set_from_slice(&item.stickers)
        .unwrap_or_default()
        .into_iter()
        .map(|sticker| ReplayWeaponSticker {
            slot: sticker.slot,
            sticker_id: sticker.sticker_id,
            wear: f32::from_bits(sticker.wear_bits),
            offset_x: f32::from_bits(sticker.offset_x_bits),
            offset_y: f32::from_bits(sticker.offset_y_bits),
            scale: sticker.scale_bits.map(f32::from_bits),
            rotation: sticker.rotation_bits.map(f32::from_bits),
        })
        .collect();
    let charms = cosmetic_charm_set_from_attributes(&item.attributes)
        .unwrap_or_default()
        .into_iter()
        .map(|charm| ReplayWeaponCharm {
            slot: charm.slot,
            charm_id: charm.charm_id,
            offset_x: f32::from_bits(charm.offset_x_bits),
            offset_y: f32::from_bits(charm.offset_y_bits),
            offset_z: f32::from_bits(charm.offset_z_bits),
            seed: charm.seed,
            highlight: charm.highlight,
            sticker_id: charm.sticker_id,
        })
        .collect();
    let mut cosmetic = ReplayWeaponCosmetic {
        weapon_def_index,
        paint_kit: spec.paint_kit,
        seed: spec.seed,
        wear: f32::from_bits(spec.wear_bits),
        quality: (item.entity_quality == Some(9)).then_some(9),
        stattrak_counter: inventory_stattrak_counter(item),
        original_owner_steam_id: item.original_owner_xuid.filter(|value| *value != 0),
        item_account_id: item.item_account_id.filter(|value| *value != 0),
        item_id: combine_item_id(item.item_id_high, item.item_id_low).filter(|value| *value != 0),
        custom_name: cosmetic_custom_name_value(item.custom_name.as_deref()),
        stickers,
        charms,
        inspect: None,
    };
    cosmetic.inspect = weapon_inspect(
        &cosmetic,
        weapon_cosmetic_rarity(cosmetic.weapon_def_index, cosmetic.paint_kit),
    );
    Some(cosmetic)
}

fn inventory_cosmetic_paint_spec(
    item: &ParsedInventoryWeaponCosmetic,
) -> Option<CosmeticPaintSpec> {
    cosmetic_paint_spec(
        Some(item.paint_kit),
        Some(item.paint_seed),
        Some(item.paint_wear),
    )
}

fn inventory_stattrak_counter(item: &ParsedInventoryWeaponCosmetic) -> Option<i32> {
    if let Some(counter) = item.stattrak_counter.filter(|counter| *counter >= 0) {
        return Some(counter);
    }
    if item.entity_quality != Some(9) {
        return None;
    }
    item.attributes
        .iter()
        .find(|attribute| attribute.definition_index == 80)
        .and_then(|attribute| i32::try_from(attribute.raw_value_bits).ok())
}

fn has_trusted_inventory_weapon_cosmetic_identity(item: &ParsedInventoryWeaponCosmetic) -> bool {
    combine_item_id(item.item_id_high, item.item_id_low).is_some_and(|value| value != 0)
        || item.item_account_id.is_some_and(|value| value > 1)
}

fn has_trusted_active_weapon_cosmetic_identity(row: &ParsedPlayerTick) -> bool {
    row.active_weapon_item_id.is_some_and(|value| value != 0)
        || row
            .active_weapon_item_account_id
            .is_some_and(|value| value > 1)
}

fn active_cosmetic_owned_by(row: &ParsedPlayerTick) -> bool {
    let account_id = row
        .steam_id
        .checked_sub(STEAM_ID64_BASE)
        .and_then(|value| u32::try_from(value).ok());
    row.active_weapon_item_account_id
        .zip(account_id)
        .is_some_and(|(actual, expected)| actual == expected)
        || row.active_weapon_original_owner_steam_id == Some(row.steam_id)
}

fn active_cosmetic_custom_name(row: &ParsedPlayerTick) -> Option<String> {
    cosmetic_custom_name_value(row.active_weapon_custom_name.as_deref())
}

fn cosmetic_custom_name_value(value: Option<&str>) -> Option<String> {
    let cleaned = value?
        .trim()
        .chars()
        .filter(|ch| !ch.is_control() || *ch == '\t')
        .take(128)
        .collect::<String>();
    let cleaned = cleaned.trim();
    (!cleaned.is_empty()).then(|| cleaned.to_string())
}

fn stable_weapon_custom_name(
    names: &BTreeMap<i32, BTreeSet<String>>,
    weapon_def_index: i32,
) -> Option<String> {
    let names = names.get(&weapon_def_index)?;
    if names.len() == 1 {
        names.iter().next().cloned()
    } else {
        None
    }
}

fn stable_weapon_i32_value(
    values: &BTreeMap<i32, BTreeSet<i32>>,
    weapon_def_index: i32,
) -> Option<i32> {
    let values = values.get(&weapon_def_index)?;
    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn stable_weapon_u32_value(
    values: &BTreeMap<i32, BTreeSet<u32>>,
    weapon_def_index: i32,
) -> Option<u32> {
    let values = values.get(&weapon_def_index)?;
    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn stable_weapon_u64_value(
    values: &BTreeMap<i32, BTreeSet<u64>>,
    weapon_def_index: i32,
) -> Option<u64> {
    let values = values.get(&weapon_def_index)?;
    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn stable_music_kit_id(rows: &[&ParsedPlayerTick]) -> Option<u32> {
    let mut values = BTreeSet::new();
    for row in rows {
        if let Some(value) = row.music_kit_id.filter(|value| valid_music_kit_id(*value)) {
            values.insert(value);
        }
    }
    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn stable_scoreboard_flair(rows: &[&ParsedPlayerTick]) -> Option<ReplayScoreboardFlair> {
    let mut values = BTreeSet::new();
    for row in rows {
        let Some(flair) = row.scoreboard_flair else {
            continue;
        };
        if !valid_scoreboard_flair_item_def(flair.item_def_index) {
            continue;
        }
        values.insert(ReplayScoreboardFlair {
            item_def_index: flair.item_def_index,
        });
    }

    if values.len() == 1 {
        values.iter().next().copied()
    } else {
        None
    }
}

fn combine_item_id(high: Option<u32>, low: Option<u32>) -> Option<u64> {
    Some((u64::from(high?) << 32) | u64::from(low?))
}

fn stable_knife_custom_name(
    names: &BTreeSet<(i32, String)>,
    item_def_index: i32,
) -> Option<String> {
    let mut matching = names
        .iter()
        .filter_map(|(def, name)| (*def == item_def_index).then_some(name.clone()))
        .collect::<BTreeSet<_>>();
    if matching.len() == 1 {
        matching.pop_first()
    } else {
        None
    }
}

fn active_cosmetic_sticker_set(row: &ParsedPlayerTick) -> Option<Vec<CosmeticStickerSpec>> {
    cosmetic_sticker_set_from_slice(&row.active_weapon_stickers)
}

fn cosmetic_sticker_set_from_slice(
    stickers: &[ParsedWeaponSticker],
) -> Option<Vec<CosmeticStickerSpec>> {
    if stickers.is_empty() {
        return None;
    }

    let mut slots = BTreeSet::new();
    let mut parsed = Vec::with_capacity(stickers.len());
    for sticker in stickers {
        if sticker.slot > 4
            || sticker.sticker_id == 0
            || !valid_sticker_id(sticker.sticker_id)
            || !sticker.wear.is_finite()
            || !(0.0..=1.0).contains(&sticker.wear)
            || !sticker.offset_x.is_finite()
            || !sticker.offset_y.is_finite()
            || sticker.scale.is_some_and(|value| !value.is_finite())
            || sticker.rotation.is_some_and(|value| !value.is_finite())
            || !slots.insert(sticker.slot)
        {
            return None;
        }
        parsed.push(CosmeticStickerSpec {
            slot: sticker.slot,
            sticker_id: sticker.sticker_id,
            wear_bits: sticker.wear.to_bits(),
            offset_x_bits: sticker.offset_x.to_bits(),
            offset_y_bits: sticker.offset_y.to_bits(),
            scale_bits: sticker.scale.map(f32::to_bits),
            rotation_bits: sticker.rotation.map(f32::to_bits),
        });
    }
    parsed.sort();
    (!parsed.is_empty()).then_some(parsed)
}

fn stable_weapon_stickers(
    stickers: &BTreeMap<i32, BTreeSet<Vec<CosmeticStickerSpec>>>,
    missing: &BTreeSet<i32>,
    weapon_def_index: i32,
) -> Vec<ReplayWeaponSticker> {
    if missing.contains(&weapon_def_index) {
        return Vec::new();
    }
    let Some(sets) = stickers.get(&weapon_def_index) else {
        return Vec::new();
    };
    if sets.len() != 1 {
        return Vec::new();
    }
    sets.iter()
        .next()
        .into_iter()
        .flat_map(|set| set.iter())
        .map(|sticker| ReplayWeaponSticker {
            slot: sticker.slot,
            sticker_id: sticker.sticker_id,
            wear: f32::from_bits(sticker.wear_bits),
            offset_x: f32::from_bits(sticker.offset_x_bits),
            offset_y: f32::from_bits(sticker.offset_y_bits),
            scale: sticker.scale_bits.map(f32::from_bits),
            rotation: sticker.rotation_bits.map(f32::from_bits),
        })
        .collect()
}

fn cosmetic_charm_set_from_attributes(
    attributes: &[ParsedInventoryWeaponAttribute],
) -> Option<Vec<CosmeticCharmSpec>> {
    let charm_id = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_ID_ATTR)
        .filter(|id| valid_keychain_id(*id))?;
    let offset_x = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_X_ATTR)?;
    let offset_y = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_Y_ATTR)?;
    let offset_z = inventory_attribute_f32(attributes, KEYCHAIN_SLOT_0_OFFSET_Z_ATTR)?;
    if !offset_x.is_finite() || !offset_y.is_finite() || !offset_z.is_finite() {
        return None;
    }

    let seed = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_SEED_ATTR);
    let highlight = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_HIGHLIGHT_ATTR)
        .filter(|value| *value > 0);
    let sticker_id = inventory_attribute_u32(attributes, KEYCHAIN_SLOT_0_STICKER_ATTR)
        .filter(|value| valid_sticker_id(*value));
    Some(vec![CosmeticCharmSpec {
        slot: 0,
        charm_id,
        offset_x_bits: offset_x.to_bits(),
        offset_y_bits: offset_y.to_bits(),
        offset_z_bits: offset_z.to_bits(),
        seed,
        highlight,
        sticker_id,
    }])
}

fn inventory_attribute_u32(
    attributes: &[ParsedInventoryWeaponAttribute],
    definition_index: u32,
) -> Option<u32> {
    attributes
        .iter()
        .find(|attribute| attribute.definition_index == definition_index)
        .map(|attribute| attribute.raw_value_bits)
}

fn inventory_attribute_f32(
    attributes: &[ParsedInventoryWeaponAttribute],
    definition_index: u32,
) -> Option<f32> {
    attributes
        .iter()
        .find(|attribute| attribute.definition_index == definition_index)
        .map(|attribute| attribute.raw_value)
}

fn stable_weapon_charms(
    charms: &BTreeMap<i32, BTreeSet<Vec<CosmeticCharmSpec>>>,
    missing: &BTreeSet<i32>,
    weapon_def_index: i32,
) -> Vec<ReplayWeaponCharm> {
    if missing.contains(&weapon_def_index) {
        return Vec::new();
    }
    let Some(sets) = charms.get(&weapon_def_index) else {
        return Vec::new();
    };
    if sets.len() != 1 {
        return Vec::new();
    }
    sets.iter()
        .next()
        .into_iter()
        .flat_map(|set| set.iter())
        .map(|charm| ReplayWeaponCharm {
            slot: charm.slot,
            charm_id: charm.charm_id,
            offset_x: f32::from_bits(charm.offset_x_bits),
            offset_y: f32::from_bits(charm.offset_y_bits),
            offset_z: f32::from_bits(charm.offset_z_bits),
            seed: charm.seed,
            highlight: charm.highlight,
            sticker_id: charm.sticker_id,
        })
        .collect()
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticPaintSpec {
    paint_kit: u32,
    seed: u32,
    wear_bits: u32,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticStickerSpec {
    slot: u8,
    sticker_id: u32,
    wear_bits: u32,
    offset_x_bits: u32,
    offset_y_bits: u32,
    scale_bits: Option<u32>,
    rotation_bits: Option<u32>,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CosmeticCharmSpec {
    slot: u8,
    charm_id: u32,
    offset_x_bits: u32,
    offset_y_bits: u32,
    offset_z_bits: u32,
    seed: Option<u32>,
    highlight: Option<u32>,
    sticker_id: Option<u32>,
}

fn cosmetic_paint_spec(
    paint_kit: Option<u32>,
    seed: Option<u32>,
    wear: Option<f32>,
) -> Option<CosmeticPaintSpec> {
    let paint_kit = paint_kit.filter(|value| valid_paint_kit(*value))?;
    let seed = seed?;
    let wear = wear?;
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    Some(CosmeticPaintSpec {
        paint_kit,
        seed,
        wear_bits: wear.to_bits(),
    })
}

fn valid_weapon_cosmetic_paint(weapon_def_index: i32, paint_kit: u32) -> bool {
    cs2_lib_econ_index()
        .weapon_paints
        .contains(&(normalize_weapon_def_index(weapon_def_index), paint_kit))
}

fn weapon_cosmetic_rarity(weapon_def_index: i32, paint_kit: u32) -> Option<u32> {
    cs2_lib_econ_index()
        .weapon_paint_rarities
        .get(&(normalize_weapon_def_index(weapon_def_index), paint_kit))
        .copied()
}

fn valid_paint_kit(paint_kit: u32) -> bool {
    cs2_lib_econ_index().paint_kit_ids.contains(&paint_kit)
}

fn valid_sticker_id(sticker_id: u32) -> bool {
    cs2_lib_econ_index().sticker_ids.contains(&sticker_id)
}

fn valid_keychain_id(keychain_id: u32) -> bool {
    cs2_lib_econ_index().keychain_ids.contains(&keychain_id)
}

pub(crate) fn valid_music_kit_id(music_kit_id: u32) -> bool {
    cs2_lib_econ_index().music_kit_ids.contains(&music_kit_id)
}

pub(crate) fn valid_music_kit_evidence_id(music_kit_id: u32) -> bool {
    // Valve's CS2 and CS:GO soundtracks are free defaults. A player-state ID
    // can preserve playback configuration without proving cosmetic ownership.
    valid_music_kit_id(music_kit_id) && !matches!(music_kit_id, 1 | 70)
}

fn valid_scoreboard_flair_item_def(item_def_index: u32) -> bool {
    item_def_index == 0
        || cs2_lib_econ_index()
            .scoreboard_flair_defidx
            .contains(&item_def_index)
}

fn valid_glove_item_def_index(item_def_index: i32) -> bool {
    cs2_lib_econ_index().glove_defidx.contains(&item_def_index)
}

pub(crate) fn valid_knife_item_def_index(item_def_index: i32) -> bool {
    cs2_lib_econ_index().knife_defidx.contains(&item_def_index)
}

fn valid_agent_item_def_index(item_def_index: u32) -> bool {
    cs2_lib_econ_index().agent_defidx.contains(&item_def_index)
}

#[derive(Debug, Deserialize)]
struct RawCs2LibEconIndex {
    weapon_paints: Vec<RawPaintPair>,
    paint_kit_ids: Vec<u32>,
    replay_equipment_defidx: Vec<i32>,
    replay_equipment: Vec<RawReplayEquipment>,
    knife_defidx: Vec<i32>,
    glove_defidx: Vec<i32>,
    agent_defidx: Vec<u32>,
    sticker_ids: Vec<u32>,
    keychain_ids: Vec<u32>,
    music_kit_ids: Vec<u32>,
    scoreboard_flair_defidx: Vec<u32>,
}

#[derive(Debug, Deserialize)]
struct RawPaintPair {
    weapon_defidx: i32,
    paint_kit: u32,
    rarity: Option<u32>,
}

#[derive(Debug, Deserialize)]
struct RawReplayEquipment {
    weapon_defidx: i32,
    class_name: String,
    replay_slot: ReplayEquipmentSlot,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "snake_case")]
enum ReplayEquipmentSlot {
    Primary,
    Secondary,
    Utility,
    C4,
    Taser,
    Knife,
}

#[derive(Debug)]
struct Cs2LibEconIndex {
    weapon_paints: BTreeSet<(i32, u32)>,
    weapon_paint_rarities: BTreeMap<(i32, u32), u32>,
    paint_kit_ids: BTreeSet<u32>,
    replay_equipment_defidx: BTreeSet<i32>,
    replay_equipment_by_class: BTreeMap<String, i32>,
    replay_equipment_class_by_defidx: BTreeMap<i32, String>,
    replay_equipment_slot_by_defidx: BTreeMap<i32, ReplayEquipmentSlot>,
    knife_defidx: BTreeSet<i32>,
    glove_defidx: BTreeSet<i32>,
    agent_defidx: BTreeSet<u32>,
    sticker_ids: BTreeSet<u32>,
    keychain_ids: BTreeSet<u32>,
    music_kit_ids: BTreeSet<u32>,
    scoreboard_flair_defidx: BTreeSet<u32>,
}

fn cs2_lib_econ_index() -> &'static Cs2LibEconIndex {
    static INDEX: OnceLock<Cs2LibEconIndex> = OnceLock::new();
    INDEX.get_or_init(|| {
        let raw: RawCs2LibEconIndex = serde_json::from_str(include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../shared/econ/cs2-lib-econ-index.v1.json"
        )))
        .expect("embedded cs2-lib-econ-index.v1.json must be valid JSON");
        let knife_defidx = raw.knife_defidx.into_iter().collect::<BTreeSet<_>>();
        let replay_equipment_defidx = raw
            .replay_equipment_defidx
            .into_iter()
            .collect::<BTreeSet<_>>();
        let mut replay_equipment_by_class = BTreeMap::new();
        let mut replay_equipment_class_by_defidx = BTreeMap::new();
        let mut replay_equipment_slot_by_defidx = BTreeMap::new();
        for item in raw.replay_equipment {
            assert!(
                replay_equipment_defidx.contains(&item.weapon_defidx),
                "cs2-lib replay equipment mapping contains an unknown defindex"
            );
            assert!(
                replay_equipment_by_class
                    .insert(item.class_name.clone(), item.weapon_defidx)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate class"
            );
            assert!(
                replay_equipment_class_by_defidx
                    .insert(item.weapon_defidx, item.class_name)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate defindex"
            );
            assert!(
                replay_equipment_slot_by_defidx
                    .insert(item.weapon_defidx, item.replay_slot)
                    .is_none(),
                "cs2-lib replay equipment mapping contains a duplicate slot defindex"
            );
        }
        assert_eq!(
            replay_equipment_class_by_defidx.len(),
            replay_equipment_defidx.len(),
            "cs2-lib replay equipment mapping must cover every replay defindex"
        );
        let default_knife_defidx = *replay_equipment_by_class
            .get("weapon_knife")
            .expect("cs2-lib replay equipment mapping must contain weapon_knife");
        let mut weapon_paints = BTreeSet::new();
        let mut weapon_paint_rarities = BTreeMap::new();
        for pair in raw.weapon_paints {
            if pair.paint_kit == 0 {
                continue;
            }
            let key = (
                normalize_weapon_def_index_with_knives(
                    pair.weapon_defidx,
                    &knife_defidx,
                    default_knife_defidx,
                ),
                pair.paint_kit,
            );
            weapon_paints.insert(key);
            if let Some(rarity) = pair.rarity.filter(|value| *value <= 7) {
                weapon_paint_rarities.insert(key, rarity);
            }
        }
        Cs2LibEconIndex {
            weapon_paints,
            weapon_paint_rarities,
            paint_kit_ids: raw
                .paint_kit_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            replay_equipment_defidx,
            replay_equipment_by_class,
            replay_equipment_class_by_defidx,
            replay_equipment_slot_by_defidx,
            knife_defidx,
            glove_defidx: raw.glove_defidx.into_iter().collect(),
            agent_defidx: raw.agent_defidx.into_iter().collect(),
            sticker_ids: raw
                .sticker_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            keychain_ids: raw
                .keychain_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            music_kit_ids: raw
                .music_kit_ids
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
            scoreboard_flair_defidx: raw
                .scoreboard_flair_defidx
                .into_iter()
                .filter(|value| *value > 0)
                .collect(),
        }
    })
}

fn observed_glove_spec(
    row: &ParsedPlayerTick,
    econ_glove_seeds: Option<&EconGloveSeedMap>,
) -> Option<ObservedGloveSpec> {
    let item_def_index = row
        .glove_item_def_index
        .filter(|value| valid_glove_item_def_index(*value))?;
    let paint_kit = row
        .glove_paint_kit
        .filter(|value| valid_paint_kit(*value))?;
    let wear = row.glove_paint_wear?;
    if !wear.is_finite() || !(0.0..=1.0).contains(&wear) {
        return None;
    }
    let wear_bits = wear.to_bits();
    Some(ObservedGloveSpec {
        key: EconGloveKey {
            item_def_index,
            paint_kit,
            wear_bits,
        },
        seed: row.glove_paint_seed.or_else(|| {
            matching_econ_glove_seed(econ_glove_seeds, item_def_index, paint_kit, wear_bits)
        }),
    })
}

fn normalize_weapon_def_index(def: i32) -> i32 {
    let index = cs2_lib_econ_index();
    normalize_weapon_def_index_with_knives(
        def,
        &index.knife_defidx,
        *index
            .replay_equipment_by_class
            .get("weapon_knife")
            .expect("cs2-lib replay equipment mapping must contain weapon_knife"),
    )
}

fn normalize_weapon_def_index_with_knives(
    def: i32,
    knife_defidx: &BTreeSet<i32>,
    default_knife_defidx: i32,
) -> i32 {
    if knife_defidx.contains(&def) {
        default_knife_defidx
    } else {
        def
    }
}

pub(crate) fn valid_replay_equipment_item_def_index(def: i32) -> bool {
    cs2_lib_econ_index()
        .replay_equipment_defidx
        .contains(&normalize_weapon_def_index(def))
}

fn is_known_weapon_def_index(def: i32) -> bool {
    valid_replay_equipment_item_def_index(def)
}

fn is_replay_equipment_event_def(def: i32) -> bool {
    cs2_lib_econ_index()
        .replay_equipment_slot_by_defidx
        .get(&normalize_weapon_def_index(def))
        == Some(&ReplayEquipmentSlot::Utility)
}

fn is_weapon_cosmetic_def_index(def: i32) -> bool {
    matches!(
        cs2_lib_econ_index()
            .replay_equipment_slot_by_defidx
            .get(&normalize_weapon_def_index(def)),
        Some(
            ReplayEquipmentSlot::Primary
                | ReplayEquipmentSlot::Secondary
                | ReplayEquipmentSlot::Taser
        )
    )
}

pub(crate) fn is_knife_cosmetic_def_index(def: i32) -> bool {
    // The CT/T defaults are playback equipment, not ownable knife models.
    valid_knife_item_def_index(def) && !matches!(def, 42 | 59)
}

fn weapon_def_index_from_item_name(item_name: &str) -> Option<i32> {
    cs2_lib_econ_index()
        .replay_equipment_by_class
        .get(&normalize_item_event_name(item_name))
        .copied()
        .map(normalize_weapon_def_index)
}

fn weapon_item_name(def: i32) -> Option<&'static str> {
    cs2_lib_econ_index()
        .replay_equipment_class_by_defidx
        .get(&normalize_weapon_def_index(def))
        .map(String::as_str)
}

fn normalize_item_event_name(item_name: &str) -> String {
    let lower = item_name.trim().to_ascii_lowercase();
    match lower.as_str() {
        "decoy_grenade" | "weapon_decoy_grenade" => "weapon_decoy".to_string(),
        "c4" | "weapon_c4_explosive" => "weapon_c4".to_string(),
        value if value.starts_with("weapon_") => value.to_string(),
        value => format!("weapon_{value}"),
    }
}

fn slugify(value: &str) -> String {
    let mut out = String::new();
    for ch in value.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch);
        } else if ch.is_whitespace() {
            out.push('_');
        }
    }
    if out.is_empty() {
        "player".to_string()
    } else {
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{
        AvatarImageFormat, Cs2Rec, ParsedAvatarOverride, ParsedDemo, ParsedEconItem,
        ParsedGameEvent, ParsedInventoryWeaponCosmetic, ParsedScoreboardFlair, ParsedWeaponSticker,
        ReplayTick,
    };
    use crate::rec_writer::read_rec;
    use crate::replay::context::{
        first_weapon_def_index, preload_weapon_def_indices_from_refs,
        preload_weapon_def_indices_from_refs_from_play_start,
    };

    #[test]
    fn preload_weapon_defs_are_normalized_and_deduped() {
        let rec = Cs2Rec {
            ticks: vec![
                ReplayTick {
                    weapon_def_index: 508,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 61,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 61,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 43,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 49,
                    ..ReplayTick::default()
                },
            ],
            ..Cs2Rec::default()
        };

        assert_eq!(first_weapon_def_index(&rec), 42);
        let rows = vec![
            ParsedPlayerTick {
                inventory_as_ids: vec![7, 43],
                ..ParsedPlayerTick::default()
            },
            ParsedPlayerTick {
                inventory_as_ids: vec![7, 44],
                ..ParsedPlayerTick::default()
            },
        ];

        let row_refs = rows.iter().collect::<Vec<_>>();
        assert_eq!(
            preload_weapon_def_indices_from_refs(&row_refs, &rec),
            vec![7, 43, 44, 61]
        );
    }

    #[test]
    fn play_start_preload_does_not_include_later_pickups() {
        let rec = Cs2Rec {
            ticks: vec![
                ReplayTick {
                    weapon_def_index: 42,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 3,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 7,
                    ..ReplayTick::default()
                },
            ],
            ..Cs2Rec::default()
        };
        let rows = vec![
            ParsedPlayerTick {
                inventory_as_ids: vec![45, 3],
                ..ParsedPlayerTick::default()
            },
            ParsedPlayerTick {
                inventory_as_ids: vec![45, 3, 7],
                ..ParsedPlayerTick::default()
            },
        ];
        let row_refs = rows.iter().collect::<Vec<_>>();

        assert_eq!(
            preload_weapon_def_indices_from_refs_from_play_start(&row_refs, &rec, 0),
            vec![45, 3]
        );
    }

    #[test]
    fn empty_play_start_inventory_does_not_fall_back_to_future_pickups() {
        let rec = Cs2Rec {
            ticks: vec![
                ReplayTick {
                    weapon_def_index: 42,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 7,
                    ..ReplayTick::default()
                },
            ],
            ..Cs2Rec::default()
        };
        let rows = vec![
            ParsedPlayerTick {
                inventory_as_ids: Vec::new(),
                ..ParsedPlayerTick::default()
            },
            ParsedPlayerTick {
                inventory_as_ids: vec![7],
                ..ParsedPlayerTick::default()
            },
        ];
        let row_refs = rows.iter().collect::<Vec<_>>();

        assert!(
            preload_weapon_def_indices_from_refs_from_play_start(&row_refs, &rec, 0).is_empty()
        );
    }

    #[test]
    fn replay_loadout_keeps_counts_and_skips_non_equipment() {
        let row = ParsedPlayerTick {
            inventory_as_ids: vec![7, 43, 43, 49, 508, 31],
            armor_value: 87,
            has_helmet: true,
            has_defuser: true,
            ..ParsedPlayerTick::default()
        };

        let loadout = replay_loadout(&row);
        assert_eq!(loadout.weapon_def_indices, vec![7, 43, 43, 31]);
        assert_eq!(loadout.armor_value, 87);
        assert!(loadout.has_helmet);
        assert!(loadout.has_defuser);
    }

    #[test]
    fn manifest_includes_demo_backed_cosmetics() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                inventory_as_ids: vec![7, 61],
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_custom_name: Some("alpha rifle".to_string()),
                active_weapon_stickers: Vec::new(),
                glove_item_def_index: Some(5032),
                glove_paint_kit: Some(10036),
                glove_paint_seed: Some(23),
                glove_paint_wear: Some(0.138_722_94),
                ..sample_row(100)
            }),
            ParsedPlayerTick {
                item_def_idx: 508,
                inventory_as_ids: vec![7, 61],
                active_weapon_original_owner_steam_id: Some(76561198000000001),
                active_weapon_paint_kit: Some(38),
                active_weapon_paint_seed: Some(321),
                active_weapon_paint_wear: Some(0.01),
                active_weapon_custom_name: Some("alpha knife".to_string()),
                active_weapon_stickers: Vec::new(),
                glove_item_def_index: Some(5032),
                glove_paint_kit: Some(10036),
                glove_paint_seed: Some(23),
                glove_paint_wear: Some(0.138_722_94),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let cosmetics = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence");

        assert_eq!(cosmetics.weapons.len(), 1);
        assert_eq!(cosmetics.weapons[0].weapon_def_index, 7);
        assert_eq!(cosmetics.weapons[0].paint_kit, 180);
        assert_eq!(cosmetics.weapons[0].seed, 12);
        assert_eq!(cosmetics.weapons[0].wear.to_bits(), 0.125_f32.to_bits());
        assert_eq!(
            cosmetics.weapons[0].custom_name.as_deref(),
            Some("alpha rifle")
        );
        let weapon_inspect = cosmetics.weapons[0]
            .inspect
            .as_ref()
            .expect("expected weapon inspect data");
        assert!(weapon_inspect
            .command
            .starts_with("csgo_econ_action_preview "));
        assert_eq!(weapon_cosmetic_rarity(7, 180), Some(6));
        assert!(weapon_inspect.command.contains("2806"));
        assert!(weapon_inspect.steam_url.is_some());
        assert_eq!(cosmetics.knife.as_ref().unwrap().item_def_index, Some(508));
        assert_eq!(cosmetics.knife.as_ref().unwrap().paint_kit, 38);
        assert_eq!(cosmetics.knife.as_ref().unwrap().seed, 321);
        assert_eq!(
            cosmetics.knife.as_ref().unwrap().wear.to_bits(),
            0.01_f32.to_bits()
        );
        assert_eq!(
            cosmetics.knife.as_ref().unwrap().custom_name.as_deref(),
            Some("alpha knife")
        );
        assert!(cosmetics
            .knife
            .as_ref()
            .unwrap()
            .inspect
            .as_ref()
            .unwrap()
            .steam_url
            .is_some());
        assert_eq!(cosmetics.glove.as_ref().unwrap().item_def_index, Some(5032));
        assert_eq!(cosmetics.glove.as_ref().unwrap().paint_kit, 10036);
        assert_eq!(cosmetics.glove.as_ref().unwrap().seed, 23);
        assert_eq!(
            cosmetics.glove.as_ref().unwrap().wear.to_bits(),
            0.138_722_94_f32.to_bits()
        );
        assert!(cosmetics
            .glove
            .as_ref()
            .unwrap()
            .inspect
            .as_ref()
            .unwrap()
            .steam_url
            .is_some());
    }

    #[test]
    fn manifest_preserves_stable_music_kit_id_for_playback() {
        for music_kit_id in [1, 70, 42] {
            let mut parsed = sample_demo();
            parsed.rows = vec![
                ParsedPlayerTick {
                    music_kit_id: Some(music_kit_id),
                    ..sample_row(100)
                },
                ParsedPlayerTick {
                    music_kit_id: Some(music_kit_id),
                    ..sample_row(164)
                },
            ];

            let memory = export_memory(parsed);

            assert_eq!(memory.manifest.files[0].music_kit_id, Some(music_kit_id));
        }
    }

    #[test]
    fn manifest_includes_stable_scoreboard_flair() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                scoreboard_flair: Some(ParsedScoreboardFlair {
                    item_def_index: 4974,
                }),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                scoreboard_flair: Some(ParsedScoreboardFlair {
                    item_def_index: 4974,
                }),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);

        assert_eq!(
            memory.manifest.files[0]
                .scoreboard_flair
                .as_ref()
                .map(|flair| flair.item_def_index),
            Some(4974)
        );
    }

    #[test]
    fn manifest_includes_empty_scoreboard_flair_evidence() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                scoreboard_flair: Some(ParsedScoreboardFlair { item_def_index: 0 }),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                scoreboard_flair: Some(ParsedScoreboardFlair { item_def_index: 0 }),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);

        assert_eq!(
            memory.manifest.files[0]
                .scoreboard_flair
                .as_ref()
                .map(|flair| flair.item_def_index),
            Some(0)
        );
    }

    #[test]
    fn manifest_includes_stable_agent_cosmetic() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                agent_item_def_index: Some(4713),
                agent_skin: Some("customplayer_ctm_swat_variantg".to_string()),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                agent_item_def_index: Some(4713),
                agent_skin: Some("customplayer_ctm_swat_variantg".to_string()),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let agent = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .and_then(|cosmetics| cosmetics.agent.as_ref())
            .expect("agent cosmetic exported");

        assert_eq!(agent.item_def_index, 4713);
        assert_eq!(
            agent.name.as_deref(),
            Some("customplayer_ctm_swat_variantg")
        );
        assert_eq!(
            agent.model_path,
            "agents\\models\\ctm_swat\\ctm_swat_variantg.vmdl"
        );
    }

    #[test]
    fn agent_model_path_uses_known_family_prefixes() {
        assert_eq!(
            agent_model_path_from_name("customplayer_tm_jungle_raider_variantb2").as_deref(),
            Some("agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantb2.vmdl")
        );
        assert_eq!(
            agent_model_path_from_name("customplayer_tm_professional_varf1").as_deref(),
            Some("agents\\models\\tm_professional\\tm_professional_varf1.vmdl")
        );
        assert_eq!(
            agent_model_path_from_name("customplayer_tm_phoenix_heavy").as_deref(),
            Some("agents\\models\\tm_phoenix_heavy\\tm_phoenix_heavy.vmdl")
        );
        assert_eq!(agent_model_path_from_name("customplayer_t_map_based"), None);
        assert_eq!(
            agent_model_path_from_name("customplayer_ct_map_based"),
            None
        );
    }

    #[test]
    fn inventory_weapon_cosmetics_export_provenance_identity() {
        let mut weapon = inventory_weapon_cosmetic(16, 926, 42, 0.123, None, Vec::new());
        weapon.original_owner_xuid = Some(76561197989430253);
        weapon.item_account_id = Some(29164525);
        weapon.item_id_high = Some(8);
        weapon.item_id_low = Some(9);

        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 16,
                inventory_as_ids: vec![16, 61],
                inventory_weapon_cosmetics: vec![weapon].into(),
                active_weapon_paint_kit: Some(309),
                active_weapon_paint_seed: Some(7),
                active_weapon_paint_wear: Some(0.4),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 16,
                inventory_as_ids: vec![16, 61],
                active_weapon_paint_kit: Some(309),
                active_weapon_paint_seed: Some(7),
                active_weapon_paint_wear: Some(0.4),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons
            .first()
            .expect("expected weapon cosmetic evidence");

        assert_eq!(weapon.original_owner_steam_id, Some(76561197989430253));
        assert_eq!(weapon.item_account_id, Some(29164525));
        assert_eq!(weapon.item_id, Some((8_u64 << 32) | 9));
    }

    #[test]
    fn manifest_fills_a_missing_glove_seed_from_matching_end_metadata() {
        let mut parsed = sample_demo();
        let wear = 0.204_478_43_f32;
        parsed.rows = vec![
            ParsedPlayerTick {
                tick: 100,
                steam_id: 76561198074762801,
                name: "m0NESY".to_string(),
                team_num: 3,
                is_alive: true,
                round: 1,
                round_in_progress: true,
                item_def_idx: 61,
                glove_item_def_index: Some(5034),
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(wear),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                tick: 164,
                steam_id: 76561198074762801,
                name: "m0NESY".to_string(),
                team_num: 3,
                is_alive: true,
                round: 1,
                round_in_progress: true,
                item_def_idx: 61,
                glove_item_def_index: Some(5034),
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(wear),
                ..sample_row(164)
            },
        ];
        parsed.econ_items = vec![ParsedEconItem {
            steam_id: Some(76561198074762801),
            item_def_index: Some(5034),
            paint_kit: Some(10033),
            paint_seed: Some(260),
            paint_wear_raw: Some(wear.to_bits()),
            paint_wear: Some(wear),
            custom_name: None,
            item_name: None,
            skin_name: Some("Crimson Kimono".to_string()),
        }];

        let memory = export_memory_with_cosmetics(parsed);
        let glove = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .and_then(|cosmetics| cosmetics.glove.as_ref())
            .expect("expected glove evidence");

        assert_eq!(glove.item_def_index, Some(5034));
        assert_eq!(glove.paint_kit, 10033);
        assert_eq!(glove.seed, 260);
        assert_eq!(glove.wear.to_bits(), 0.204_478_43_f32.to_bits());
    }

    #[test]
    fn manifest_keeps_live_gloves_without_using_conflicting_end_metadata() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                glove_item_def_index: Some(5030),
                glove_paint_kit: Some(10038),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.148_281_16),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                glove_item_def_index: Some(5030),
                glove_paint_kit: Some(10038),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.148_281_16),
                ..sample_row(164)
            },
        ];
        parsed.econ_items = vec![ParsedEconItem {
            steam_id: Some(76561198074762801),
            item_def_index: Some(5030),
            paint_kit: Some(10037),
            paint_seed: Some(641),
            paint_wear_raw: Some(0.145_554_07_f32.to_bits()),
            paint_wear: Some(0.145_554_07),
            ..ParsedEconItem::default()
        }];

        let memory = export_memory_with_cosmetics(parsed);
        let glove = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .and_then(|cosmetics| cosmetics.glove.as_ref())
            .expect("expected partial glove evidence");
        assert_eq!(glove.item_def_index, Some(5030));
        assert_eq!(glove.paint_kit, 10038);
        assert!((1..=1_000).contains(&glove.seed));
        assert_eq!(glove.seed_known, Some(false));
        assert_eq!(glove.wear.to_bits(), 0.148_281_16_f32.to_bits());
        assert!(glove.inspect.is_none());
    }

    #[test]
    fn manifest_omits_demo_backed_cosmetics_by_default() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 7,
                inventory_as_ids: vec![7, 61],
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                glove_item_def_index: Some(5030),
                glove_paint_kit: Some(10006),
                glove_paint_seed: Some(4),
                glove_paint_wear: Some(0.2),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 508,
                inventory_as_ids: vec![7, 61],
                active_weapon_paint_kit: Some(38),
                active_weapon_paint_seed: Some(321),
                active_weapon_paint_wear: Some(0.01),
                glove_item_def_index: Some(5030),
                glove_paint_kit: Some(10006),
                glove_paint_seed: Some(4),
                glove_paint_wear: Some(0.2),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);

        assert!(memory.manifest.files[0].cosmetics.is_none());
        let json = serde_json::to_string(&memory.manifest.files[0]).unwrap();
        assert!(!json.contains("cosmetics"));
    }

    #[test]
    fn manifest_omits_cosmetics_without_complete_evidence() {
        let memory = export_memory(sample_demo());

        assert!(memory.manifest.files[0].cosmetics.is_none());
        let json = serde_json::to_string(&memory.manifest.files[0]).unwrap();
        assert!(!json.contains("cosmetics"));
    }

    #[test]
    fn manifest_includes_demo_chat_messages() {
        let mut parsed = sample_demo();
        parsed.events = vec![
            ParsedGameEvent {
                tick: 120,
                name: "chat_message".to_string(),
                user_steam_id: Some(76561198000000001),
                chat_scope: Some("team".to_string()),
                chat_text: Some("  hello\u{0007} team  ".to_string()),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 120,
                name: "chat_message".to_string(),
                user_steam_id: Some(76561198000000001),
                chat_scope: Some("team".to_string()),
                chat_text: Some("hello team".to_string()),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 132,
                name: "server_message".to_string(),
                chat_scope: Some("server".to_string()),
                chat_text: Some("admin pause".to_string()),
                ..ParsedGameEvent::default()
            },
        ];

        let memory = export_memory(parsed);
        let chat = &memory.manifest.rounds[0].chat_messages;

        assert_eq!(chat.len(), 2);
        assert_eq!(chat[0].tick, 120);
        assert_eq!(chat[0].sender_steam_id, 76561198000000001);
        assert_eq!(chat[0].sender_name.as_deref(), Some("alpha"));
        assert_eq!(chat[0].scope, "team");
        assert_eq!(chat[0].text, "hello team");
        assert_eq!(chat[1].tick, 132);
        assert_eq!(chat[1].sender_steam_id, 0);
        assert_eq!(chat[1].scope, "server");
        assert_eq!(chat[1].text, "admin pause");
    }

    #[test]
    fn manifest_skips_invalid_demo_chat_messages() {
        let mut parsed = sample_demo();
        parsed.events = vec![
            ParsedGameEvent {
                tick: 120,
                name: "chat_message".to_string(),
                user_steam_id: None,
                chat_scope: Some("all".to_string()),
                chat_text: Some("orphan".to_string()),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 128,
                name: "chat_message".to_string(),
                user_steam_id: Some(76561198000000001),
                chat_scope: Some("all".to_string()),
                chat_text: Some(" \u{0008}\t ".to_string()),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 240,
                name: "server_message".to_string(),
                chat_scope: Some("server".to_string()),
                chat_text: Some("after round".to_string()),
                ..ParsedGameEvent::default()
            },
        ];

        let memory = export_memory(parsed);

        assert!(memory.manifest.rounds[0].chat_messages.is_empty());
    }

    #[test]
    fn manifest_writes_demo_avatar_override_assets() {
        let mut parsed = sample_demo();
        let bytes = b"\x89PNG\r\n\x1a\navatar".to_vec();
        let sha256 = crate::demo_id::sha256_hex(&bytes);
        parsed.avatar_overrides = vec![
            ParsedAvatarOverride {
                steam_id: 76561198000000001,
                format: AvatarImageFormat::Png,
                sha256: sha256.clone(),
                source: "ServerAvatarOverrides".to_string(),
                bytes: bytes.clone(),
            },
            ParsedAvatarOverride {
                steam_id: 76561198000000002,
                format: AvatarImageFormat::Png,
                sha256: sha256.clone(),
                source: "ServerAvatarOverrides".to_string(),
                bytes: bytes.clone(),
            },
        ];

        let memory = export_memory(parsed);
        let expected_path = format!("avatars/{sha256}.png");

        assert_eq!(memory.manifest.avatar_overrides.len(), 2);
        assert_eq!(
            memory.manifest.avatar_overrides[0].steam_id,
            76561198000000001
        );
        assert_eq!(
            memory.manifest.avatar_overrides[0].path.as_str(),
            expected_path.as_str()
        );
        assert!(memory.log.contains("avatar_overrides=2 avatar_assets=1"));

        let avatar_artifacts = memory
            .artifacts
            .iter()
            .filter(|artifact| artifact.kind == ConversionArtifactKind::Avatar)
            .collect::<Vec<_>>();
        assert_eq!(avatar_artifacts.len(), 1);
        assert_eq!(avatar_artifacts[0].bytes.as_slice(), bytes.as_slice());

        let manifest_artifact = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Manifest)
            .unwrap();
        let manifest_json = std::str::from_utf8(&manifest_artifact.bytes).unwrap();
        assert!(manifest_json.contains("\"avatar_overrides\""));
        assert!(manifest_json.contains(&format!("\"path\": \"{expected_path}\"")));
    }

    #[test]
    fn manifest_uses_a_stable_fallback_without_claiming_missing_seed_evidence() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                glove_item_def_index: Some(5034),
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.382),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                glove_item_def_index: Some(5034),
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.382),
                ..sample_row(164)
            },
        ];

        let repeated_parsed = parsed.clone();
        let memory = export_memory_with_cosmetics(parsed);
        let glove = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .and_then(|cosmetics| cosmetics.glove.as_ref())
            .expect("expected partial glove evidence");

        assert_eq!(glove.item_def_index, Some(5034));
        assert_eq!(glove.paint_kit, 10033);
        assert!((1..=1_000).contains(&glove.seed));
        assert_eq!(glove.seed_known, Some(false));
        assert_eq!(glove.wear.to_bits(), 0.382_f32.to_bits());
        assert!(glove.inspect.is_none());

        let repeated = export_memory_with_cosmetics(repeated_parsed);
        assert_eq!(
            repeated.manifest.files[0]
                .cosmetics
                .as_ref()
                .and_then(|cosmetics| cosmetics.glove.as_ref())
                .expect("repeated partial glove evidence")
                .seed,
            glove.seed
        );
    }

    #[test]
    fn glove_cosmetics_require_item_def_evidence() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                glove_item_def_index: None,
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.382),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                glove_item_def_index: None,
                glove_paint_kit: Some(10033),
                glove_paint_seed: None,
                glove_paint_wear: Some(0.382),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);

        assert!(memory.manifest.files[0].cosmetics.is_none());
    }

    #[test]
    fn manifest_includes_stable_crosshair_code() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                crosshair_code: Some("CSGO-aBcDe-fGhIj-kLmNo-pQrSt-uVwXy".to_string()),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                crosshair_code: Some(" CSGO-aBcDe-fGhIj-kLmNo-pQrSt-uVwXy ".to_string()),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let view = memory.manifest.files[0]
            .view
            .as_ref()
            .expect("expected view metadata");

        assert_eq!(
            view.crosshair_code.as_deref(),
            Some("CSGO-aBcDe-fGhIj-kLmNo-pQrSt-uVwXy")
        );
    }

    #[test]
    fn conflicting_crosshair_codes_are_skipped() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                crosshair_code: Some("CSGO-aaaaa-aaaaa-aaaaa-aaaaa-aaaaa".to_string()),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                crosshair_code: Some("CSGO-bbbbb-bbbbb-bbbbb-bbbbb-bbbbb".to_string()),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);

        assert!(memory.manifest.files[0].view.is_none());
    }

    #[test]
    fn manifest_includes_stable_viewmodel() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                viewmodel_left_handed: Some(false),
                viewmodel_fov: Some(68.0),
                viewmodel_offset_x: Some(2.5),
                viewmodel_offset_y: Some(0.0),
                viewmodel_offset_z: Some(-1.5),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                viewmodel_left_handed: Some(false),
                viewmodel_fov: Some(68.0),
                viewmodel_offset_x: Some(2.5),
                viewmodel_offset_y: Some(-0.0),
                viewmodel_offset_z: Some(-1.5),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);
        let viewmodel = memory.manifest.files[0]
            .view
            .as_ref()
            .and_then(|view| view.viewmodel.as_ref())
            .expect("expected viewmodel metadata");

        assert_eq!(viewmodel.left_handed, Some(false));
        assert_eq!(viewmodel.fov.map(f32::to_bits), Some(68.0_f32.to_bits()));
        assert_eq!(
            viewmodel.offset_x.map(f32::to_bits),
            Some(2.5_f32.to_bits())
        );
        assert_eq!(
            viewmodel.offset_y.map(f32::to_bits),
            Some(0.0_f32.to_bits())
        );
        assert_eq!(
            viewmodel.offset_z.map(f32::to_bits),
            Some((-1.5_f32).to_bits())
        );
    }

    #[test]
    fn conflicting_viewmodel_fields_are_skipped() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                viewmodel_left_handed: Some(false),
                viewmodel_fov: Some(68.0),
                viewmodel_offset_x: Some(2.5),
                viewmodel_offset_y: Some(0.0),
                viewmodel_offset_z: Some(-1.5),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                viewmodel_left_handed: Some(true),
                viewmodel_fov: Some(60.0),
                viewmodel_offset_x: Some(1.0),
                viewmodel_offset_y: Some(0.0),
                viewmodel_offset_z: Some(-1.5),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);
        let viewmodel = memory.manifest.files[0]
            .view
            .as_ref()
            .and_then(|view| view.viewmodel.as_ref())
            .expect("expected partial viewmodel metadata");

        assert_eq!(viewmodel.left_handed, None);
        assert_eq!(viewmodel.fov, None);
        assert_eq!(viewmodel.offset_x, None);
        assert_eq!(
            viewmodel.offset_y.map(f32::to_bits),
            Some(0.0_f32.to_bits())
        );
        assert_eq!(
            viewmodel.offset_z.map(f32::to_bits),
            Some((-1.5_f32).to_bits())
        );
    }

    #[test]
    fn conflicting_cosmetic_evidence_is_skipped() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(181),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                ..sample_row(164)
            },
        ];

        let memory = export_memory(parsed);

        assert!(memory.manifest.files[0].cosmetics.is_none());
    }

    #[test]
    fn conflicting_custom_name_only_skips_custom_name() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_custom_name: Some("first".to_string()),
                active_weapon_stickers: Vec::new(),
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_custom_name: Some("second".to_string()),
                active_weapon_stickers: Vec::new(),
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert_eq!(weapon.paint_kit, 180);
        assert!(weapon.custom_name.is_none());
    }

    #[test]
    fn sticker_export_is_default_off_under_cosmetics() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert!(weapon.stickers.is_empty());
        let json = serde_json::to_string(weapon).unwrap();
        assert!(!json.contains("stickers"));
    }

    #[test]
    fn stable_weapon_stickers_are_serialized_when_enabled() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![
                    sticker(1, 478, 0.0, -0.25, 0.75),
                    sticker_with_transform(0, 477, 0.05, 0.1, 0.2, 1.35, 27.5),
                ],
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![
                    sticker(1, 478, 0.0, -0.25, 0.75),
                    sticker_with_transform(0, 477, 0.05, 0.1, 0.2, 1.35, 27.5),
                ],
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_stickers(parsed);
        let stickers = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0]
            .stickers;

        assert_eq!(stickers.len(), 2);
        assert_eq!(stickers[0].slot, 0);
        assert_eq!(stickers[0].sticker_id, 477);
        assert_eq!(stickers[0].wear.to_bits(), 0.05_f32.to_bits());
        assert_eq!(stickers[0].offset_x.to_bits(), 0.1_f32.to_bits());
        assert_eq!(stickers[0].offset_y.to_bits(), 0.2_f32.to_bits());
        assert_eq!(stickers[0].scale.unwrap().to_bits(), 1.35_f32.to_bits());
        assert_eq!(stickers[0].rotation.unwrap().to_bits(), 27.5_f32.to_bits());
        assert_eq!(stickers[1].slot, 1);
        assert_eq!(stickers[1].sticker_id, 478);
    }

    #[test]
    fn stable_weapon_charms_are_serialized_when_enabled() {
        let mut parsed = sample_demo();
        parsed.rows = vec![charm_weapon_row(100, true), charm_weapon_row(164, true)];

        let memory = export_memory_with_charms(parsed);
        let charms = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0]
            .charms;

        assert_eq!(charms.len(), 1);
        assert_eq!(charms[0].slot, 0);
        assert_eq!(charms[0].charm_id, 37);
        assert_eq!(charms[0].offset_x.to_bits(), (-1.25_f32).to_bits());
        assert_eq!(charms[0].offset_y.to_bits(), 0.5_f32.to_bits());
        assert_eq!(charms[0].offset_z.to_bits(), 2.75_f32.to_bits());
        assert_eq!(charms[0].seed, Some(12345));
        assert_eq!(charms[0].sticker_id, Some(68));
    }

    #[test]
    fn charm_export_is_default_off_under_cosmetics() {
        let mut parsed = sample_demo();
        parsed.rows = vec![charm_weapon_row(100, false), charm_weapon_row(164, false)];

        let memory = export_memory_with_cosmetics(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert!(weapon.charms.is_empty());
        let json = serde_json::to_string(weapon).unwrap();
        assert!(!json.contains("charms"));
    }

    #[test]
    fn live_start_inventory_weapon_cosmetics_override_active_weapon_cosmetics() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 16,
                inventory_as_ids: vec![16, 61],
                inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                    16,
                    926,
                    42,
                    0.123,
                    Some("Dove1e"),
                    vec![
                        sticker(0, 225, 0.0, 0.0, 0.0),
                        sticker(3, 7891, 0.0, -0.1, 0.2),
                    ],
                )]
                .into(),
                active_weapon_paint_kit: Some(309),
                active_weapon_paint_seed: Some(7),
                active_weapon_paint_wear: Some(0.4),
                active_weapon_custom_name: None,
                active_weapon_stickers: vec![sticker(0, 60, 0.0, 0.0, 0.0)],
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 16,
                inventory_as_ids: vec![16, 61],
                active_weapon_paint_kit: Some(309),
                active_weapon_paint_seed: Some(7),
                active_weapon_paint_wear: Some(0.4),
                active_weapon_stickers: vec![sticker(0, 60, 0.0, 0.0, 0.0)],
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_stickers(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert_eq!(weapon.weapon_def_index, 16);
        assert_eq!(weapon.paint_kit, 926);
        assert_eq!(weapon.seed, 42);
        assert_eq!(weapon.wear.to_bits(), 0.123_f32.to_bits());
        assert_eq!(weapon.custom_name.as_deref(), Some("Dove1e"));
        assert_eq!(weapon.stickers.len(), 2);
        assert_eq!(weapon.stickers[0].sticker_id, 225);
        assert_eq!(weapon.stickers[1].sticker_id, 7891);
    }

    #[test]
    fn taser_inventory_cosmetic_is_exported() {
        let item = inventory_weapon_cosmetic(31, 1183, 733, 0.139_071_45, None, Vec::new());

        let cosmetic = inventory_item_cosmetic_evidence(&item)
            .expect("Zeus x27 cosmetic evidence should be exported");

        assert_eq!(cosmetic.weapon_def_index, 31);
        assert_eq!(cosmetic.paint_kit, 1183);
        assert_eq!(cosmetic.seed, 733);
        assert_eq!(cosmetic.wear.to_bits(), 0.139_071_45_f32.to_bits());
    }

    #[test]
    fn inventory_weapon_cosmetics_export_stattrak_evidence() {
        let mut stattrak_without_counter =
            inventory_weapon_cosmetic(61, 1040, 853, 0.099, None, Vec::new());
        stattrak_without_counter.entity_quality = Some(9);
        stattrak_without_counter.stattrak_counter = Some(-1);
        stattrak_without_counter.attributes = vec![stattrak_count_attribute(1923)];
        let mut stattrak_with_counter =
            inventory_weapon_cosmetic(16, 926, 42, 0.123, None, Vec::new());
        stattrak_with_counter.entity_quality = Some(9);
        stattrak_with_counter.stattrak_counter = Some(42);

        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 61,
                inventory_as_ids: vec![16, 61],
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 61,
                inventory_as_ids: vec![16, 61],
                inventory_weapon_cosmetics: vec![stattrak_without_counter, stattrak_with_counter]
                    .into(),
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_cosmetics(parsed);
        let cosmetics = memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence");
        let m4a4 = cosmetics
            .weapons
            .iter()
            .find(|weapon| weapon.weapon_def_index == 16)
            .expect("expected counted StatTrak weapon");
        let usps = cosmetics
            .weapons
            .iter()
            .find(|weapon| weapon.weapon_def_index == 61)
            .expect("expected StatTrak weapon");

        assert_eq!(m4a4.quality, Some(9));
        assert_eq!(m4a4.stattrak_counter, Some(42));
        assert_eq!(usps.quality, Some(9));
        assert_eq!(usps.stattrak_counter, Some(1923));
    }

    #[test]
    fn round_inventory_weapon_cosmetics_use_first_stable_weapon_entity() {
        let early = ParsedPlayerTick {
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                16,
                926,
                727,
                0.000_518_145_2,
                Some("Dove1e"),
                vec![sticker(0, 225, 0.0, 0.0, 0.0)],
            )]
            .into(),
            ..sample_row(100)
        };
        let later = ParsedPlayerTick {
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                16,
                309,
                941,
                0.032_685_65,
                None,
                vec![sticker(0, 60, 0.0, 0.0, 0.0)],
            )]
            .into(),
            ..sample_row(164)
        };
        let rows = vec![&early, &later];

        let weapons = round_inventory_weapon_cosmetics(&rows, true, false)
            .expect("expected inventory weapon evidence");

        assert_eq!(weapons.len(), 1);
        assert_eq!(weapons[0].weapon_def_index, 16);
        assert_eq!(weapons[0].paint_kit, 926);
        assert_eq!(weapons[0].seed, 727);
        assert_eq!(weapons[0].custom_name.as_deref(), Some("Dove1e"));
        assert_eq!(weapons[0].stickers[0].sticker_id, 225);
    }

    #[test]
    fn live_start_inventory_weapon_cosmetics_prevent_freeze_transfer_pollution() {
        let freeze_purchase = ParsedPlayerTick {
            item_def_idx: 2,
            inventory_as_ids: vec![2],
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                2,
                747,
                451,
                0.067_470_92,
                None,
                Vec::new(),
            )]
            .into(),
            ..sample_row(100)
        };
        let live_start = ParsedPlayerTick {
            item_def_idx: 61,
            inventory_as_ids: vec![61],
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                61,
                1040,
                853,
                0.099,
                None,
                Vec::new(),
            )]
            .into(),
            ..sample_row(164)
        };
        let rows = vec![&freeze_purchase, &live_start];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 1, None, None, true, false)
            .expect("expected live-start cosmetic evidence");

        assert_eq!(cosmetics.weapons.len(), 1);
        assert_eq!(cosmetics.weapons[0].weapon_def_index, 61);
        assert_eq!(cosmetics.weapons[0].paint_kit, 1040);
        assert_eq!(cosmetics.weapons[0].seed, 853);
    }

    #[test]
    fn unpainted_live_start_inventory_blocks_active_weapon_false_positive() {
        let active_false_positive = ParsedPlayerTick {
            item_def_idx: 32,
            inventory_as_ids: vec![32],
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic(
                32,
                0,
                0,
                0.0,
                None,
                Vec::new(),
            )]
            .into(),
            active_weapon_paint_kit: Some(339),
            active_weapon_paint_seed: Some(24),
            active_weapon_paint_wear: Some(0.055_136_383),
            ..sample_row(164)
        };
        let rows = vec![&active_false_positive];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 0, None, None, true, false);

        assert!(cosmetics.is_none());
    }

    #[test]
    fn weak_inventory_weapon_identity_does_not_export_weapon_cosmetic() {
        let weak_inventory_false_positive = ParsedInventoryWeaponCosmetic {
            item_def_index: 61,
            item_account_id: Some(1),
            original_owner_xuid: Some(76561198422853814),
            paint_kit: 705,
            paint_seed: 186,
            paint_wear: 0.377_419_86,
            ..Default::default()
        };
        let row = ParsedPlayerTick {
            steam_id: 76561198422853814,
            item_def_idx: 61,
            inventory_as_ids: vec![61],
            inventory_weapon_cosmetics: vec![weak_inventory_false_positive].into(),
            ..sample_row(164)
        };
        let rows = vec![&row];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 0, None, None, true, false);

        assert!(cosmetics.is_none());
    }

    #[test]
    fn weak_active_weapon_identity_does_not_export_weapon_cosmetic() {
        let active_false_positive = ParsedPlayerTick {
            steam_id: 76561198422853814,
            item_def_idx: 61,
            inventory_as_ids: vec![61],
            active_weapon_paint_kit: Some(705),
            active_weapon_paint_seed: Some(186),
            active_weapon_paint_wear: Some(0.377_419_86),
            active_weapon_original_owner_steam_id: Some(76561198422853814),
            active_weapon_item_account_id: Some(1),
            active_weapon_item_id: None,
            ..sample_row(164)
        };
        let rows = vec![&active_false_positive];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 0, None, None, true, false);

        assert!(cosmetics.is_none());
    }

    #[test]
    fn default_knife_paint_false_positive_is_skipped() {
        let steam_id = 76561198169234291;
        let t_default_knife_false_positive = ParsedPlayerTick {
            steam_id,
            team_num: 2,
            item_def_idx: 59,
            active_weapon_paint_kit: Some(38),
            active_weapon_paint_seed: Some(774),
            active_weapon_paint_wear: Some(0.014_620_723),
            active_weapon_original_owner_steam_id: Some(steam_id),
            ..sample_row(164)
        };
        let ct_default_knife_false_positive = ParsedPlayerTick {
            steam_id,
            team_num: 3,
            item_def_idx: 42,
            active_weapon_paint_kit: Some(38),
            active_weapon_paint_seed: Some(774),
            active_weapon_paint_wear: Some(0.014_620_723),
            active_weapon_original_owner_steam_id: Some(steam_id),
            ..sample_row(165)
        };
        let rows = vec![
            &t_default_knife_false_positive,
            &ct_default_knife_false_positive,
        ];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 0, None, None, true, false);

        assert!(cosmetics.is_none());
    }

    #[test]
    fn end_of_match_econ_knife_recovers_missing_live_econ_fields() {
        let naf_steam_id = 76561198001151695;
        let elige_steam_id = 76561198066693739;
        let wear = 0.031_718_593_f32;
        let elige_wear = 0.016_897_384_f32;
        let parsed = ParsedDemo {
            econ_items: vec![
                ParsedEconItem {
                    steam_id: Some(naf_steam_id),
                    item_def_index: Some(507),
                    paint_kit: Some(570),
                    paint_seed: Some(330),
                    paint_wear_raw: Some(wear.to_bits()),
                    paint_wear: Some(wear),
                    ..ParsedEconItem::default()
                },
                ParsedEconItem {
                    steam_id: Some(elige_steam_id),
                    item_def_index: Some(507),
                    paint_kit: Some(570),
                    paint_seed: Some(80),
                    paint_wear_raw: Some(elige_wear.to_bits()),
                    paint_wear: Some(elige_wear),
                    custom_name: Some("player scoped knife".to_string()),
                    ..ParsedEconItem::default()
                },
            ],
            ..ParsedDemo::default()
        };
        let paints = knife_econ_paint_index(&parsed);

        let naf_row = ParsedPlayerTick {
            steam_id: naf_steam_id,
            item_def_idx: 507,
            active_weapon_paint_seed: Some(330),
            active_weapon_paint_wear: Some(wear),
            active_weapon_item_account_id: u32::try_from(naf_steam_id - STEAM_ID64_BASE).ok(),
            ..sample_row(164)
        };
        let naf_rows = vec![&naf_row];
        let naf_cosmetics = replay_cosmetics_at(
            &naf_rows,
            &naf_rows,
            0,
            None,
            paints.get(&naf_steam_id),
            true,
            false,
        )
        .expect("NAF knife should be recovered from player-scoped econ data");
        let knife = naf_cosmetics.knife.expect("NAF knife");
        assert_eq!(knife.item_def_index, Some(507));
        assert_eq!(knife.paint_kit, 570);
        assert_eq!(knife.seed, 330);
        assert_eq!(knife.wear.to_bits(), wear.to_bits());

        let elige_row = ParsedPlayerTick {
            steam_id: elige_steam_id,
            item_def_idx: 507,
            ..sample_row(164)
        };
        let elige_rows = vec![&elige_row];
        let elige_cosmetics = replay_cosmetics_at(
            &elige_rows,
            &elige_rows,
            0,
            None,
            paints.get(&elige_steam_id),
            true,
            false,
        )
        .expect("player-scoped econ knife should fill unavailable live econ fields");
        let elige_knife = elige_cosmetics.knife.expect("player-scoped econ knife");
        assert_eq!(elige_knife.paint_kit, 570);
        assert_eq!(elige_knife.seed, 80);
        assert_eq!(elige_knife.wear.to_bits(), elige_wear.to_bits());
        assert_eq!(
            elige_knife.custom_name.as_deref(),
            Some("player scoped knife")
        );
    }

    #[test]
    fn invalid_weapon_paint_pair_is_skipped_without_inventory_evidence() {
        let active_false_positive = ParsedPlayerTick {
            item_def_idx: 32,
            inventory_as_ids: vec![32],
            active_weapon_paint_kit: Some(339),
            active_weapon_paint_seed: Some(24),
            active_weapon_paint_wear: Some(0.055_136_383),
            ..sample_row(164)
        };
        let rows = vec![&active_false_positive];

        let cosmetics = replay_cosmetics_at(&rows, &rows, 0, None, None, true, false);

        assert!(cosmetics.is_none());
    }

    #[test]
    fn conflicting_weapon_stickers_only_skip_stickers() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(0, 478, 0.05, 0.1, 0.2)],
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_stickers(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert_eq!(weapon.paint_kit, 180);
        assert!(weapon.stickers.is_empty());
    }

    #[test]
    fn invalid_weapon_stickers_are_skipped() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![
                    sticker(0, 477, 0.05, 0.1, 0.2),
                    sticker(0, 478, 0.05, 0.1, 0.2),
                ],
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(5, 477, f32::NAN, 0.1, 0.2)],
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_stickers(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert!(weapon.stickers.is_empty());
    }

    #[test]
    fn missing_weapon_stickers_skip_stickers_only() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(100)
            }),
            active_weapon_identity(ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_paint_kit: Some(180),
                active_weapon_paint_seed: Some(12),
                active_weapon_paint_wear: Some(0.125),
                active_weapon_stickers: Vec::new(),
                ..sample_row(164)
            }),
        ];

        let memory = export_memory_with_stickers(parsed);
        let weapon = &memory.manifest.files[0]
            .cosmetics
            .as_ref()
            .expect("expected cosmetic evidence")
            .weapons[0];

        assert_eq!(weapon.paint_kit, 180);
        assert!(weapon.stickers.is_empty());
    }

    #[test]
    fn sticker_only_evidence_does_not_create_weapon_cosmetic() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(100)
            },
            ParsedPlayerTick {
                item_def_idx: 7,
                active_weapon_stickers: vec![sticker(0, 477, 0.05, 0.1, 0.2)],
                ..sample_row(164)
            },
        ];

        let memory = export_memory_with_stickers(parsed);

        assert!(memory.manifest.files[0].cosmetics.is_none());
    }

    #[test]
    fn memory_export_matches_filesystem_export_surface() {
        let parsed = sample_demo();
        let selected_rounds = Some(BTreeSet::from([1]));
        let memory_options = ConvertMemoryOptions {
            output_stem: Some("sample-demo".to_string()),
            side: Side::Both,
            selected_rounds: selected_rounds.clone(),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
            analysis: AnalysisOptions::default(),
        };

        let memory = export_demo_to_memory(&parsed, &memory_options).unwrap();

        assert_eq!(memory.demo_id, "sample-demo");
        assert_eq!(memory.files_written, 1);
        assert!(memory
            .artifacts
            .iter()
            .any(|artifact| artifact.path == "manifest.json"));
        assert!(memory
            .artifacts
            .iter()
            .any(|artifact| artifact.path == "conversion.log"));
        assert!(memory.log.contains("files_written=1"));

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        assert_eq!(dtr.path, "round01/t/76561198000000001_alpha.dtr");
        let parsed_rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(parsed_rec.header.round, 1);
        assert_eq!(parsed_rec.ticks.len(), 64);

        let temp = tempfile::tempdir().unwrap();
        let filesystem = export_demo(
            &parsed,
            &ConvertOptions {
                output_dir: temp.path().to_path_buf(),
                output_stem: Some("sample-demo".to_string()),
                side: Side::Both,
                selected_rounds,
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                export_cosmetics: false,
                export_stickers: false,
                export_charms: false,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(
            serde_json::to_value(&filesystem.manifest).unwrap(),
            serde_json::to_value(&memory.manifest).unwrap()
        );
        let disk_dtr = std::fs::read(filesystem.root.join(&dtr.path)).unwrap();
        assert_eq!(disk_dtr, dtr.bytes);
        assert!(filesystem.manifest_path.exists());
        assert!(filesystem.root.join("conversion.log").exists());
    }

    #[test]
    fn filesystem_export_can_write_to_an_explicit_demo_root() {
        let parsed = sample_demo();
        let temp = tempfile::tempdir().unwrap();
        let default_root = temp.path().join("default-output");
        let explicit_root = temp.path().join(".sample-demo.tmp.test");
        let options = ConvertOptions {
            output_dir: default_root.clone(),
            output_stem: Some("sample-demo".to_string()),
            side: Side::Both,
            selected_rounds: Some(BTreeSet::from([1])),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
            analysis: AnalysisOptions::default(),
        };

        let report =
            export_demo_to_root_with_progress(&parsed, &options, &explicit_root, |_| {}).unwrap();

        assert_eq!(report.root, explicit_root);
        assert_eq!(report.manifest_path, report.root.join("manifest.json"));
        assert!(report.manifest_path.exists());
        assert!(report.root.join("conversion.log").exists());
        assert!(!default_root.exists());
    }

    #[test]
    fn explicit_root_export_reports_every_artifact_write_failure() {
        let parsed = sample_demo();
        let options = ConvertOptions {
            output_dir: PathBuf::from("unused-default-output"),
            output_stem: Some("sample-demo".to_string()),
            side: Side::Both,
            selected_rounds: Some(BTreeSet::from([1])),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
            analysis: AnalysisOptions::default(),
        };
        let memory = export_demo_to_memory(&parsed, &ConvertMemoryOptions::from(&options)).unwrap();
        assert!(memory
            .artifacts
            .iter()
            .any(|artifact| artifact.path == "manifest.json"));

        for artifact in &memory.artifacts {
            let temp = tempfile::tempdir().unwrap();
            let staging_root = temp.path().join(".sample-demo.tmp.test");
            fs::create_dir_all(staging_root.join(&artifact.path)).unwrap();

            let result =
                export_demo_to_root_with_progress(&parsed, &options, &staging_root, |_| {});

            assert!(
                result.is_err(),
                "expected write failure for artifact {}",
                artifact.path
            );
        }
    }

    #[test]
    fn export_progress_reports_ordered_round_and_player_events() {
        let parsed = sample_demo();
        let options = ConvertMemoryOptions {
            output_stem: Some("progress-demo".to_string()),
            side: Side::Both,
            selected_rounds: Some(BTreeSet::from([1])),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
            analysis: AnalysisOptions::default(),
        };
        let mut events = Vec::new();

        let report = export_demo_to_memory_with_progress(&parsed, &options, |event| {
            events.push(event);
        })
        .unwrap();

        assert_eq!(report.files_written, 1);
        assert!(matches!(
            events.first(),
            Some(ConversionProgress::AnalysisStarted)
        ));
        assert!(matches!(
            events.get(1),
            Some(ConversionProgress::AnalysisFinished {
                selected_rounds: 1,
                estimated_files: 1,
                ..
            })
        ));
        assert!(events
            .iter()
            .any(|event| matches!(event, ConversionProgress::RoundStarted { round: 1, .. })));
        assert!(events.iter().any(|event| matches!(
            event,
            ConversionProgress::PlayerWritten {
                round: 1,
                steam_id: 76561198000000001,
                ..
            }
        )));
        assert!(matches!(
            events.last(),
            Some(ConversionProgress::RoundFinished { round: 1, files: 1 })
        ));
    }

    #[test]
    fn export_progress_reports_round_skip_reason() {
        let parsed = sample_demo();
        let options = ConvertMemoryOptions {
            output_stem: Some("skip-demo".to_string()),
            side: Side::Both,
            selected_rounds: Some(BTreeSet::from([2])),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
            analysis: AnalysisOptions::default(),
        };
        let mut events = Vec::new();

        let report = export_demo_to_memory_with_progress(&parsed, &options, |event| {
            events.push(event);
        })
        .unwrap();

        assert_eq!(report.files_written, 0);
        assert!(events.iter().any(|event| matches!(
            event,
            ConversionProgress::RoundSkipped { round: 1, reason }
                if reason == "not selected"
        )));
        assert!(report.log.contains("skip round 1: not selected"));
    }

    #[test]
    fn export_rejects_escaping_output_stem() {
        let parsed = sample_demo();
        let err = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("../escape".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                export_cosmetics: false,
                export_stickers: false,
                export_charms: false,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap_err();

        assert!(err.to_string().contains("output_stem"));
    }

    #[test]
    fn export_rejects_non_consecutive_player_ticks() {
        let mut parsed = sample_demo();
        parsed.rows = vec![sample_row(100), sample_row(102)];

        let error = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("gap-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                export_cosmetics: false,
                export_stickers: false,
                export_charms: false,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap_err();

        assert!(error.to_string().contains("100 -> 102 (expected 101)"));
    }

    #[test]
    fn export_includes_bounded_freeze_preroll_and_live_play_start() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            freeze_row(20),
            freeze_row(80),
            sample_row(100),
            sample_row(164),
            sample_row(228),
        ];
        parsed.projectiles = vec![crate::model::ParsedProjectile {
            tick: 164,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            grenade_type: "smokegrenade_projectile".to_string(),
            kind: crate::model::ProjectileKind::Smoke,
            weapon_def_index: 45,
            initial_position: [1.0, 2.0, 3.0],
            initial_velocity: [4.0, 5.0, 6.0],
            detonation_position: [7.0, 8.0, 9.0],
            effect_position: [7.0, 8.0, 9.0],
            effect_tick: Some(164),
            effect_source: crate::model::ProjectileEffectSource::SmokeDetonationProp,
            effect_confidence: 0.9,
        }];
        densify_test_demo_rows(&mut parsed);

        let memory = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("freeze-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                export_cosmetics: false,
                export_stickers: false,
                export_charms: false,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(memory.manifest.rounds[0].recording_start_tick, 20);
        assert_eq!(memory.manifest.rounds[0].start_tick, 100);
        assert_eq!(memory.manifest.rounds[0].freeze_preroll_ticks, 80);
        assert_eq!(memory.manifest.files[0].play_start_tick_index, 80);

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        let rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(rec.header.play_start_tick_index, 80);
        assert_eq!(rec.ticks.len(), 208);
        assert_eq!(rec.ticks[0].pre.origin[0], 20.0);
        assert_eq!(rec.ticks[80].pre.origin[0], 100.0);
        assert_eq!(rec.projectiles[0].tick_index, 144);
        assert_eq!(rec.high_fidelity.schema_version, 4);
        assert_eq!(rec.high_fidelity.projectiles.len(), 1);
        assert_eq!(rec.high_fidelity.projectiles[0].tick_index, 144);
        assert_eq!(
            rec.high_fidelity.projectiles[0].effect_tick_index,
            Some(144)
        );
        assert_eq!(rec.high_fidelity.projectiles[0].effect_confidence, 0.9);
    }

    #[test]
    fn freeze_preroll_uses_only_the_contiguous_suffix_before_live_start() {
        let mut rows = Vec::new();
        for tick in 100..=199 {
            rows.push(freeze_row(tick));
        }
        for tick in 400..600 {
            rows.push(freeze_row(tick));
        }
        rows.push(sample_row(600));
        let row_refs = rows.iter().collect::<Vec<_>>();

        assert_eq!(
            recording_start_tick_for_round(&row_refs, 64.0, 600, DEFAULT_FREEZE_PREROLL_SECONDS,),
            400
        );
    }

    #[test]
    fn export_caps_freeze_preroll_before_pause_tail() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            freeze_row(-1_000),
            freeze_row(60),
            sample_row(100),
            sample_row(164),
        ];
        densify_test_demo_rows(&mut parsed);

        let memory = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("cap-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: 1.0,
                export_cosmetics: false,
                export_stickers: false,
                export_charms: false,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(memory.manifest.rounds[0].recording_start_tick, 60);
        assert_eq!(memory.manifest.rounds[0].freeze_preroll_ticks, 40);
        assert_eq!(memory.manifest.files[0].play_start_tick_index, 40);

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        let rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(rec.ticks[0].pre.origin[0], 60.0);
    }

    #[test]
    fn grenade_throw_inventory_loss_is_not_item_drop() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, 76561198000000001, "alpha", vec![7, 45]),
            row_with_inventory(116, 76561198000000001, "alpha", vec![7]),
        ];
        parsed.projectiles = vec![crate::model::ParsedProjectile {
            tick: 116,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            grenade_type: "smokegrenade_projectile".to_string(),
            kind: crate::model::ProjectileKind::Smoke,
            weapon_def_index: 45,
            initial_position: [1.0, 2.0, 3.0],
            initial_velocity: [4.0, 5.0, 6.0],
            detonation_position: [7.0, 8.0, 9.0],
            effect_position: [0.0; 3],
            effect_tick: None,
            effect_source: crate::model::ProjectileEffectSource::Unknown,
            effect_confidence: 0.0,
        }];

        let rec = rec_for_steam(&export_memory(parsed), 76561198000000001);

        assert!(rec.high_fidelity.events.iter().all(|event| {
            !matches!(
                event.kind,
                ReplayHifiEventKind::ItemDrop
                    | ReplayHifiEventKind::ItemPickup
                    | ReplayHifiEventKind::ItemTransfer
            )
        }));
    }

    #[test]
    fn primary_weapon_inventory_loss_is_not_item_drop() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7, 45]),
            row_with_inventory(116, steam_id, "alpha", vec![45]),
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert!(rec
            .high_fidelity
            .events
            .iter()
            .all(|event| event.kind != ReplayHifiEventKind::ItemDrop));
    }

    #[test]
    fn smoke_transfer_generates_source_drop_and_target_transfer() {
        let source = 76561198000000001;
        let target = 76561198000000002;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, source, "magixx", vec![45]),
            row_with_inventory(112, source, "magixx", vec![]),
            row_with_inventory(100, target, "sh1ro", vec![]),
            row_with_inventory(120, target, "sh1ro", vec![45]),
        ];
        parsed.events = vec![ParsedGameEvent {
            tick: 120,
            name: "item_pickup".to_string(),
            user_steam_id: Some(target),
            weapon_def_index: Some(45),
            item_name: Some("smokegrenade".to_string()),
            ..ParsedGameEvent::default()
        }];

        let memory = export_memory(parsed);
        let source_rec = rec_for_steam(&memory, source);
        let target_rec = rec_for_steam(&memory, target);

        let drop = source_rec
            .high_fidelity
            .events
            .iter()
            .find(|event| event.kind == ReplayHifiEventKind::ItemDrop)
            .unwrap();
        assert_eq!(drop.actor_steam_id, Some(source));
        assert_eq!(drop.target_steam_id, Some(target));
        assert_eq!(drop.weapon_def_index, Some(45));
        assert_eq!(drop.actor_count_after, Some(0));

        let transfer = target_rec
            .high_fidelity
            .events
            .iter()
            .find(|event| event.kind == ReplayHifiEventKind::ItemTransfer)
            .unwrap();
        assert_eq!(transfer.actor_steam_id, Some(source));
        assert_eq!(transfer.target_steam_id, Some(target));
        assert_eq!(transfer.weapon_def_index, Some(45));
        assert_eq!(transfer.target_count_after, Some(1));
    }

    #[test]
    fn bomb_events_are_recorded_from_game_events() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7, 49]),
            row_with_inventory(128, steam_id, "alpha", vec![7, 49]),
        ];
        parsed.events = vec![
            ParsedGameEvent {
                tick: 110,
                name: "bomb_beginplant".to_string(),
                user_steam_id: Some(steam_id),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 124,
                name: "bomb_planted".to_string(),
                user_steam_id: Some(steam_id),
                ..ParsedGameEvent::default()
            },
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert!(rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombBeginplant
                && event.weapon_def_index == Some(49)));
        assert!(rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombPlanted
                && event.weapon_def_index == Some(49)));
    }

    #[test]
    fn initial_c4_owner_is_recorded_from_first_visible_inventory() {
        let owner = 76561198000000002;
        let other = 76561198000000003;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, owner, "owner", vec![7, 49]),
            row_with_inventory(164, owner, "owner", vec![7, 49]),
            row_with_inventory(100, other, "other", vec![7]),
            row_with_inventory(164, other, "other", vec![7]),
        ];

        let owner_rec = rec_for_steam(&export_memory(parsed.clone()), owner);
        let other_rec = rec_for_steam(&export_memory(parsed), other);

        assert!(owner_rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombInitialOwner
                && event.actor_steam_id == Some(owner)
                && event.weapon_def_index == Some(49)));
        assert!(other_rec
            .high_fidelity
            .events
            .iter()
            .all(|event| event.kind != ReplayHifiEventKind::BombInitialOwner));
    }

    #[test]
    fn initial_c4_owner_falls_back_to_first_bomb_event_actor() {
        let owner = 76561198000000002;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, owner, "owner", vec![7]),
            row_with_inventory(164, owner, "owner", vec![7]),
        ];
        parsed.events = vec![ParsedGameEvent {
            tick: 120,
            name: "bomb_pickup".to_string(),
            user_steam_id: Some(owner),
            ..ParsedGameEvent::default()
        }];

        let rec = rec_for_steam(&export_memory(parsed), owner);

        assert!(rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombInitialOwner
                && event.actor_steam_id == Some(owner)
                && event.weapon_def_index == Some(49)));
    }

    #[test]
    fn inventory_snapshots_are_written_only_when_inventory_changes() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7]),
            row_with_inventory(110, steam_id, "alpha", vec![7]),
            row_with_inventory(120, steam_id, "alpha", vec![7, 45]),
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert_eq!(rec.high_fidelity.inventory_snapshots.len(), 2);
        assert_eq!(rec.high_fidelity.inventory_snapshots[0].tick, 100);
        assert_eq!(rec.high_fidelity.inventory_snapshots[1].tick, 120);
        assert_eq!(
            rec.high_fidelity.inventory_snapshots[1].weapon_def_counts,
            vec![
                ReplayInventoryItemCount {
                    weapon_def_index: 7,
                    count: 1,
                },
                ReplayInventoryItemCount {
                    weapon_def_index: 45,
                    count: 1,
                },
            ]
        );
    }

    #[test]
    fn round_start_balance_uses_first_live_row_with_evidence() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            ParsedPlayerTick {
                account_balance: Some(5_250),
                ..row_with_inventory(100, steam_id, "alpha", vec![7])
            },
            ParsedPlayerTick {
                account_balance: Some(8_500),
                ..row_with_inventory(164, steam_id, "alpha", vec![7])
            },
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert_eq!(rec.high_fidelity.schema_version, 4);
        assert_eq!(rec.high_fidelity.round_start_balance, Some(5_250));
    }

    #[test]
    fn round_start_balance_does_not_fall_forward_from_missing_live_evidence() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7]),
            ParsedPlayerTick {
                account_balance: Some(8_500),
                ..row_with_inventory(164, steam_id, "alpha", vec![7])
            },
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert_eq!(rec.high_fidelity.round_start_balance, None);
    }

    fn sticker(
        slot: u8,
        sticker_id: u32,
        wear: f32,
        offset_x: f32,
        offset_y: f32,
    ) -> ParsedWeaponSticker {
        ParsedWeaponSticker {
            slot,
            sticker_id,
            wear,
            offset_x,
            offset_y,
            scale: None,
            rotation: None,
        }
    }

    fn sticker_with_transform(
        slot: u8,
        sticker_id: u32,
        wear: f32,
        offset_x: f32,
        offset_y: f32,
        scale: f32,
        rotation: f32,
    ) -> ParsedWeaponSticker {
        ParsedWeaponSticker {
            slot,
            sticker_id,
            wear,
            offset_x,
            offset_y,
            scale: Some(scale),
            rotation: Some(rotation),
        }
    }

    fn charm_weapon_row(tick: i32, include_optional: bool) -> ParsedPlayerTick {
        let mut attributes = vec![
            charm_int_attribute(KEYCHAIN_SLOT_0_ID_ATTR, 37),
            charm_float_attribute(KEYCHAIN_SLOT_0_OFFSET_X_ATTR, -1.25),
            charm_float_attribute(KEYCHAIN_SLOT_0_OFFSET_Y_ATTR, 0.5),
            charm_float_attribute(KEYCHAIN_SLOT_0_OFFSET_Z_ATTR, 2.75),
        ];
        if include_optional {
            attributes.push(charm_int_attribute(KEYCHAIN_SLOT_0_SEED_ATTR, 12345));
            attributes.push(charm_int_attribute(KEYCHAIN_SLOT_0_STICKER_ATTR, 68));
        }

        ParsedPlayerTick {
            item_def_idx: 7,
            inventory_as_ids: vec![7],
            inventory_weapon_cosmetics: vec![inventory_weapon_cosmetic_with_attributes(
                7,
                180,
                12,
                0.125,
                None,
                Vec::new(),
                attributes,
            )]
            .into(),
            ..sample_row(tick)
        }
    }

    fn inventory_weapon_cosmetic(
        item_def_index: i32,
        paint_kit: u32,
        paint_seed: u32,
        paint_wear: f32,
        custom_name: Option<&str>,
        stickers: Vec<ParsedWeaponSticker>,
    ) -> ParsedInventoryWeaponCosmetic {
        inventory_weapon_cosmetic_with_attributes(
            item_def_index,
            paint_kit,
            paint_seed,
            paint_wear,
            custom_name,
            stickers,
            Vec::new(),
        )
    }

    fn inventory_weapon_cosmetic_with_attributes(
        item_def_index: i32,
        paint_kit: u32,
        paint_seed: u32,
        paint_wear: f32,
        custom_name: Option<&str>,
        stickers: Vec<ParsedWeaponSticker>,
        attributes: Vec<crate::model::ParsedInventoryWeaponAttribute>,
    ) -> ParsedInventoryWeaponCosmetic {
        ParsedInventoryWeaponCosmetic {
            item_def_index,
            item_id_high: Some(29),
            item_id_low: Some(u32::try_from(item_def_index).unwrap_or_default()),
            item_account_id: Some(29164525),
            original_owner_xuid: None,
            paint_kit,
            paint_seed,
            paint_wear,
            entity_quality: None,
            stattrak_counter: None,
            attributes,
            custom_name: custom_name.map(str::to_string),
            stickers,
        }
    }

    fn stattrak_count_attribute(count: u32) -> crate::model::ParsedInventoryWeaponAttribute {
        crate::model::ParsedInventoryWeaponAttribute {
            definition_index: 80,
            raw_value: f32::from_bits(count),
            raw_value_bits: count,
        }
    }

    fn charm_int_attribute(
        definition_index: u32,
        value: u32,
    ) -> crate::model::ParsedInventoryWeaponAttribute {
        crate::model::ParsedInventoryWeaponAttribute {
            definition_index,
            raw_value: f32::from_bits(value),
            raw_value_bits: value,
        }
    }

    fn charm_float_attribute(
        definition_index: u32,
        value: f32,
    ) -> crate::model::ParsedInventoryWeaponAttribute {
        crate::model::ParsedInventoryWeaponAttribute {
            definition_index,
            raw_value: value,
            raw_value_bits: value.to_bits(),
        }
    }

    fn export_memory(parsed: ParsedDemo) -> MemoryConversionReport {
        export_memory_with_options(parsed, false, false, false)
    }

    fn export_memory_with_cosmetics(parsed: ParsedDemo) -> MemoryConversionReport {
        export_memory_with_options(parsed, true, false, false)
    }

    fn export_memory_with_stickers(parsed: ParsedDemo) -> MemoryConversionReport {
        export_memory_with_options(parsed, true, true, false)
    }

    fn export_memory_with_charms(parsed: ParsedDemo) -> MemoryConversionReport {
        export_memory_with_options(parsed, true, false, true)
    }

    fn active_weapon_identity(mut row: ParsedPlayerTick) -> ParsedPlayerTick {
        row.active_weapon_item_account_id = Some(29164525);
        row.active_weapon_item_id = Some((8_u64 << 32) | 9);
        row.active_weapon_original_owner_steam_id = Some(row.steam_id);
        row
    }

    fn export_memory_with_options(
        mut parsed: ParsedDemo,
        export_cosmetics: bool,
        export_stickers: bool,
        export_charms: bool,
    ) -> MemoryConversionReport {
        densify_test_demo_rows(&mut parsed);
        export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("hifi-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: false,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                export_cosmetics,
                export_stickers,
                export_charms,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap()
    }

    fn rec_for_steam(memory: &MemoryConversionReport, steam_id: u64) -> Cs2Rec {
        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.steam_id == Some(steam_id))
            .unwrap();
        read_rec(&mut &dtr.bytes[..]).unwrap()
    }

    fn sample_demo() -> ParsedDemo {
        let mut parsed = ParsedDemo {
            path: "<demo.dem>".to_string(),
            stem: "demo".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_mirage".to_string(),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            playback_time_seconds: None,
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows: vec![sample_row(100), sample_row(164)],
            projectiles: Vec::new(),
            voice_frames: Vec::new(),
            events: Vec::new(),
            server_convars: Vec::new(),
            avatar_overrides: Vec::new(),
            econ_items: Vec::new(),
        };
        densify_test_demo_rows(&mut parsed);
        parsed
    }

    fn densify_test_demo_rows(parsed: &mut ParsedDemo) {
        let mut players = BTreeMap::<(u32, u64), Vec<ParsedPlayerTick>>::new();
        for row in std::mem::take(&mut parsed.rows) {
            players
                .entry((row.round, row.steam_id))
                .or_default()
                .push(row);
        }

        for rows in players.values_mut() {
            rows.sort_by_key(|row| row.tick);
        }

        parsed.rows = players
            .into_values()
            .flat_map(|rows| {
                let mut dense: Vec<ParsedPlayerTick> = Vec::new();
                for row in rows {
                    if let Some(previous) = dense.last().cloned() {
                        let gap = i64::from(row.tick) - i64::from(previous.tick);
                        if gap > 1 && gap <= 256 {
                            for tick in previous.tick.saturating_add(1)..row.tick {
                                let mut filler = previous.clone();
                                filler.tick = tick;
                                filler.game_time = Some(tick as f32 / parsed.tick_rate);
                                dense.push(filler);
                            }
                        }
                    }
                    dense.push(row);
                }
                dense
            })
            .collect();
    }

    fn row_with_inventory(
        tick: i32,
        steam_id: u64,
        name: &str,
        inventory_as_ids: Vec<i32>,
    ) -> ParsedPlayerTick {
        ParsedPlayerTick {
            steam_id,
            name: name.to_string(),
            inventory_as_ids,
            item_def_idx: 7,
            ..sample_row(tick)
        }
    }

    fn sample_row(tick: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            team_num: 2,
            is_alive: true,
            round: 1,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: Some(tick as f32 / 64.0),
            origin: [tick as f32, 1.0, 2.0],
            velocity: [1.0, 0.0, 0.0],
            pitch: 3.0,
            yaw: 4.0,
            buttons: 1,
            buttonstates_present: true,
            buttonstate1: 1,
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
            item_def_idx: 7,
            inventory_as_ids: vec![7],
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
            armor_value: 100,
            has_helmet: true,
            has_defuser: false,
            round_start_equip_value: 2_700,
            equipment_value_total: 2_700,
            money_saved_total: 800,
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

    fn freeze_row(tick: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            round_in_progress: false,
            is_freeze_period: true,
            ..sample_row(tick)
        }
    }
}
