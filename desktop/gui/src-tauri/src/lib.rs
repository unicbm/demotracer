/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

mod activity_log;
mod archive_info;
mod batch;
mod catalog;
mod diagnostics;
mod gsi;
mod gui_preferences;
mod http_client;
mod inventory_simulator;
mod playback_manager;
mod server_config;
mod steam_profile;
mod target_lock;
mod telemetry;

use activity_log::{
    append_activity_log, clear_activity_logs, list_activity_logs, maintain_activity_logs,
    ActivityLogLevel, ActivityLogState,
};
use base64::Engine as _;
use batch::{
    cancel_batch_import, choose_demo_batch_dir, list_batch_imports, read_batch_import,
    resume_batch_import, scan_demo_folder, start_batch_import,
};
use cs2_demotracer::browser_analysis::{
    analyze_browser_demo, BrowserDemoAnalysis, BrowserDemoSource, BrowserFriendlyFireSummary,
    BrowserPlayerDetails, BrowserPlayerSummary, BrowserReplayInputStatus,
    BrowserReplayInputSummary, BrowserRoundOutcome, BrowserScoreSummary,
};
use cs2_demotracer::demo_id::sha256_hex;
use cs2_demotracer::demo_reader::{
    demo_content_sha256, is_supported_demo_path, is_zstd_demo_path, ReadDemoOptions,
};
use cs2_demotracer::demo_series::{
    demo_source_sha256, read_demo_source_with_options, read_demo_source_with_options_and_cancel,
    resolve_demo_source, resolve_demo_source_for_sha256, DemoSourceMetadata, DemoSourceSet,
};
use cs2_demotracer::dtr::read_rec_file;
use cs2_demotracer::export::{
    export_demo_to_root_with_analysis_and_progress, ConversionArtifactKind, ConversionProgress,
    ConversionReport, ConvertOptions, DEFAULT_FREEZE_PREROLL_SECONDS,
};
use cs2_demotracer::model::{
    public_demo_path, ConvertedFile, DemoAnalysis, ParsedDemo, RoundStatus, Side, SubtickMode,
    DEMOTRACER_ABI, DTR_FORMAT_VERSION,
};
use cs2_demotracer::quality::AnalysisOptions;
use cs2_demotracer::validate::validate_dtr_path;
use cs2_demotracer::voice_export::export_round_voice_sidecars;
use diagnostics::{choose_cs2_dir, detect_cs2_installations, inspect_cs2_install};
use gsi::{configure_gsi, gsi_status, GsiState};
use gui_preferences::{load_gui_preferences, save_gui_preferences};
use inventory_simulator::{set_inventory_simulator_panel, start_inventory_simulator_batch};
use playback_manager::{
    choose_playback_bundle, install_latest_playback_bundle, install_playback_bundle,
    playback_release_status, playback_update_status, rollback_playback_install,
};
use serde::{Deserialize, Serialize};
use server_config::{load_server_config, save_server_config, validate_server_config};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use steam_profile::SteamProfileDto;
use tauri::ipc::Channel;
use tauri::{AppHandle, Manager, State};

const COSMETIC_CONFIRMATION_PHRASE: &str = "I ACCEPT COSMETIC EXPORT RISK";
const MAX_FREEZE_PREROLL_SECONDS: f32 = 120.0;
const MIN_MAX_ROUND_SECONDS: f32 = 30.0;
const MAX_MAX_ROUND_SECONDS: f32 = 1800.0;
const MAX_MANIFEST_BYTES: u64 = 32 * 1024 * 1024;
const MAX_LIBRARY_AVATAR_BYTES: usize = 4 * 1024 * 1024;
const MAX_WORKSPACE_BACKGROUND_BYTES: usize = 16 * 1024 * 1024;
const MIN_SUPPORTED_MANIFEST_ABI: i32 = 12;
const MIN_SUPPORTED_DTR_FORMAT_VERSION: u32 = 3;
const OUTPUT_COMPLETION_MARKER: &str = ".demotracer-complete";
const OUTPUT_COMPLETION_MARKER_CONTENT: &[u8] = b"CS2 DemoTracer output completed successfully.\n";
const INTERACTIVE_ANALYSIS_READ_OPTIONS: ReadDemoOptions = ReadDemoOptions {
    // Single-demo conversion reuses this ParsedDemo. Collect optional export
    // evidence here so enabling voice or cosmetics never produces a silent,
    // incomplete export without reparsing the demo.
    collect_voice: true,
    collect_cosmetics: true,
};

static NEXT_STAGING_NONCE: AtomicU64 = AtomicU64::new(1);
static BATCH_CONVERSION_LOCK: Mutex<()> = Mutex::new(());

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LibraryAvatarRequestDto {
    manifest_path: String,
    relative_path: String,
    sha256: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct WorkspaceBackgroundDialogRequestDto {
    title: String,
    filter_label: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct WorkspaceBackgroundDto {
    data_url: String,
    width: u32,
    height: u32,
    bytes: usize,
}

pub(crate) type CommandResult<T> = Result<T, CommandErrorDto>;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CommandErrorDto {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

impl CommandErrorDto {
    pub(crate) fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            path: None,
        }
    }

    pub(crate) fn at_path(
        code: impl Into<String>,
        message: impl Into<String>,
        path: impl AsRef<Path>,
    ) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            path: Some(path.as_ref().display().to_string()),
        }
    }

    fn from_core(code: &'static str, error: cs2_demotracer::Error) -> Self {
        Self::new(code, error.to_string())
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum TaskEvent {
    Phase { phase: TaskPhase },
    Log { level: LogLevel, message: String },
    Progress { progress: ConversionProgressDto },
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskPhase {
    Decompressing,
    Parsing,
    Analyzing,
    Exporting,
    Voice,
    Validating,
    Complete,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum LogLevel {
    Info,
    Warning,
    Error,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(
    tag = "event",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ConversionProgressDto {
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
        steam_id: String,
        reason: String,
    },
    PlayerWritten {
        round: u32,
        steam_id: String,
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
        artifact_kind: String,
    },
    Finished {
        root: String,
        manifest_path: String,
        files_written: usize,
    },
}

impl From<ConversionProgress> for ConversionProgressDto {
    fn from(value: ConversionProgress) -> Self {
        match value {
            ConversionProgress::AnalysisStarted => Self::AnalysisStarted,
            ConversionProgress::AnalysisFinished {
                rounds,
                selected_rounds,
                estimated_files,
            } => Self::AnalysisFinished {
                rounds,
                selected_rounds,
                estimated_files,
            },
            ConversionProgress::RoundSkipped { round, reason } => {
                Self::RoundSkipped { round, reason }
            }
            ConversionProgress::RoundStarted {
                round,
                estimated_players,
            } => Self::RoundStarted {
                round,
                estimated_players,
            },
            ConversionProgress::PlayerSkipped {
                round,
                steam_id,
                reason,
            } => Self::PlayerSkipped {
                round,
                steam_id: steam_id.to_string(),
                reason,
            },
            ConversionProgress::PlayerWritten {
                round,
                steam_id,
                player_name,
                side,
                path,
                ticks,
                subticks,
            } => Self::PlayerWritten {
                round,
                steam_id: steam_id.to_string(),
                player_name,
                side,
                path,
                ticks,
                subticks,
            },
            ConversionProgress::RoundFinished { round, files } => {
                Self::RoundFinished { round, files }
            }
            ConversionProgress::ArtifactsWritingStarted { root, artifacts } => {
                Self::ArtifactsWritingStarted { root, artifacts }
            }
            ConversionProgress::ArtifactWritten { path, kind } => Self::ArtifactWritten {
                path,
                artifact_kind: artifact_kind_label(&kind).to_string(),
            },
            ConversionProgress::Finished {
                root,
                manifest_path,
                files_written,
            } => Self::Finished {
                root,
                manifest_path,
                files_written,
            },
        }
    }
}

fn artifact_kind_label(kind: &ConversionArtifactKind) -> &'static str {
    match kind {
        ConversionArtifactKind::Dtr => "dtr",
        ConversionArtifactKind::Avatar => "avatar",
        ConversionArtifactKind::Manifest => "manifest",
        ConversionArtifactKind::Log => "log",
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AnalyzeDemoRequest {
    pub path: String,
    #[serde(default)]
    pub expected_demo_sha256: Option<String>,
    #[serde(default = "default_max_round_seconds")]
    pub max_round_seconds: f32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreflightDemoSourceRequest {
    path: String,
    #[serde(default)]
    library_roots: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PreflightDemoSourceDto {
    source_path: String,
    demo_sha256: String,
    source_size_bytes: String,
    source_modified_at_ms: Option<u64>,
    compressed: bool,
    matches: Vec<catalog::LibraryEntryDto>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AnalysisDto {
    pub analysis_id: String,
    pub source_path: String,
    pub file_name: String,
    pub output_demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub row_count: usize,
    pub source_modified_at_ms: Option<u64>,
    pub source_size_bytes: Option<String>,
    pub duration_seconds: f32,
    pub demo_patch_version: Option<i32>,
    pub demo_version_name: Option<String>,
    pub server_name: Option<String>,
    pub demo_source: Option<BrowserDemoSource>,
    pub converter_version: String,
    pub players: Vec<BrowserPlayerSummary>,
    pub score: Option<BrowserScoreSummary>,
    pub round_outcomes: Vec<BrowserRoundOutcome>,
    pub friendly_fire: BrowserFriendlyFireSummary,
    pub replay_input: BrowserReplayInputSummary,
    pub rounds: Vec<RoundDto>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoundDto {
    pub round: u32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub duration_seconds: f32,
    pub t_players: usize,
    pub ct_players: usize,
    pub total_players: usize,
    pub valid_rows: usize,
    pub status: String,
    pub problems: Vec<String>,
    pub selected_by_default: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConvertDemoRequest {
    pub analysis_id: String,
    pub output_dir: String,
    pub selected_rounds: Vec<u32>,
    #[serde(default)]
    pub include_suspicious: bool,
    #[serde(default)]
    pub full_round: bool,
    #[serde(default)]
    pub side: Side,
    #[serde(default)]
    pub subtick_mode: SubtickMode,
    #[serde(default = "default_max_round_seconds")]
    pub max_round_seconds: f32,
    #[serde(default = "default_freeze_preroll_seconds")]
    pub freeze_preroll_seconds: f32,
    #[serde(default = "default_true")]
    pub export_voice: bool,
    #[serde(default)]
    pub export_cosmetics: bool,
    #[serde(default)]
    pub export_stickers: bool,
    #[serde(default)]
    pub export_charms: bool,
    #[serde(default)]
    pub cosmetic_consent: Option<CosmeticConsentDto>,
    #[serde(default)]
    pub overwrite: OverwriteModeDto,
}

fn default_freeze_preroll_seconds() -> f32 {
    DEFAULT_FREEZE_PREROLL_SECONDS
}

fn default_max_round_seconds() -> f32 {
    AnalysisOptions::default().max_round_seconds
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CosmeticConsentDto {
    pub phrase: String,
}

#[derive(Debug, Clone, Copy, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum OverwriteModeDto {
    #[default]
    Deny,
    Replace,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConversionSummaryDto {
    pub root: String,
    pub manifest_path: String,
    pub files_written: usize,
    pub validated_files: usize,
    pub output_bytes: String,
    pub rounds_exported: usize,
    pub first_exported_round: Option<u32>,
    pub rounds: Vec<RoundFileCountDto>,
    pub players: Vec<PlayerSummaryDto>,
    pub voice: VoiceSummaryDto,
    pub cosmetics: CosmeticSummaryDto,
    pub commands: CommandSummaryDto,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoundFileCountDto {
    pub round: u32,
    pub files: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerSummaryDto {
    pub team: usize,
    pub steam_id: String,
    pub name: String,
    pub rounds: usize,
    pub files: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub side: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub match_team: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub team_name: Option<String>,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    pub details: Option<BrowserPlayerDetails>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct VoiceSummaryDto {
    pub requested: bool,
    pub sidecars: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CosmeticSummaryDto {
    pub requested: Option<bool>,
    pub sticker_requested: Option<bool>,
    pub charm_requested: Option<bool>,
    pub files: usize,
    pub sticker_files: usize,
    pub charm_files: usize,
    pub preset: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandSummaryDto {
    pub go_round: String,
    pub go_sequence: String,
    pub round: String,
    pub sequence: String,
    pub cosmetic_round: Option<String>,
    pub cosmetic_sequence: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OpenOutputRequest {
    pub path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OpenExternalRequest {
    pub url: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreflightOutputRequest {
    pub analysis_id: String,
    pub output_dir: String,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PreflightOutputDto {
    pub root: String,
    pub exists: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshArchiveMetadataRequest {
    pub manifest_path: String,
    #[serde(default)]
    pub demo_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshArchiveMetadataDto {
    pub manifest_path: String,
    pub info_path: String,
    pub display_name: String,
    pub source_path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolveArchiveSourceRequest {
    pub manifest_path: String,
    #[serde(default)]
    pub demo_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolveArchiveSourceDto {
    pub source_path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshLibraryMetadataRequest {
    pub library_root: String,
    #[serde(default)]
    pub demo_root: Option<String>,
    #[serde(default)]
    pub source_paths: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshLibraryMetadataDto {
    pub demos_scanned: usize,
    pub demos_matched: usize,
    pub archives_updated: usize,
    pub archives_current: usize,
    pub archives_unmatched: usize,
    pub source_unmatched: usize,
    pub source_paths: BTreeMap<String, String>,
    pub failures: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportArchivesRequest {
    pub library_root: String,
    pub source_root: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportArchivesDto {
    pub archives_found: usize,
    pub archives_imported: usize,
    pub duplicates_skipped: usize,
    pub archives_rejected: usize,
    pub failures: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteArchiveRequest {
    pub manifest_path: String,
    pub library_roots: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveArchiveNoteRequest {
    pub manifest_path: String,
    pub note: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveArchiveNoteDto {
    pub manifest_path: String,
    pub note: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveDto {
    pub root: String,
    pub manifest_path: String,
    pub demo_path: String,
    pub demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    pub compatibility: String,
    pub total_files: usize,
    pub playable_files: usize,
    pub output_bytes: String,
    pub players: Vec<PlayerSummaryDto>,
    pub voice: ManifestArchiveVoiceDto,
    pub cosmetics: CosmeticSummaryDto,
    pub rounds: Vec<ManifestArchiveRoundDto>,
    pub issues: Vec<ManifestIssueDto>,
    pub playable: bool,
    pub display_name: String,
    pub note: Option<String>,
    pub metadata_status: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_modified_at_ms: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub duration_seconds: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_patch_version: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_version_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_source: Option<BrowserDemoSource>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub score: Option<BrowserScoreSummary>,
    pub round_outcomes: Vec<BrowserRoundOutcome>,
    pub friendly_fire: BrowserFriendlyFireSummary,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub converter_version: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveVoiceDto {
    pub requested: Option<bool>,
    pub sidecars: usize,
    pub rounds: Vec<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveRoundDto {
    pub round: u32,
    pub files: usize,
    pub t_files: usize,
    pub ct_files: usize,
    pub t_steam_ids: Vec<String>,
    pub ct_steam_ids: Vec<String>,
    pub cosmetic_files: usize,
    pub sticker_files: usize,
    pub charm_files: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub duration_seconds: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pistol_round: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cut_reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub t_economy: Option<ManifestTeamEconomyDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ct_economy: Option<ManifestTeamEconomyDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scoreboard: Option<ManifestRoundScoreboardDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bomb_planted_seconds: Option<f32>,
    pub ticks: usize,
    pub subticks: usize,
    pub hifi_events: usize,
    pub inventory_snapshots: usize,
    pub sequence_length: usize,
    pub available: bool,
    pub commands: CommandSummaryDto,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestRoundScoreboardDto {
    pub t_score: u32,
    pub ct_score: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub t_team_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ct_team_name: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestTeamEconomyDto {
    pub side: String,
    pub players: usize,
    pub round_start_equipment_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub class: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ManifestIssueDto {
    pub code: String,
    pub severity: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub round: Option<u32>,
}

impl ManifestIssueDto {
    fn warning(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            severity: "warning".to_string(),
            message: message.into(),
            path: None,
            round: None,
        }
    }

    fn error(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            severity: "error".to_string(),
            message: message.into(),
            path: None,
            round: None,
        }
    }

    fn at_file(mut self, path: impl Into<String>, round: Option<u32>) -> Self {
        self.path = Some(path.into());
        self.round = round;
        self
    }

    fn at_round(mut self, round: u32) -> Self {
        self.round = Some(round);
        self
    }
}

#[derive(Debug, Deserialize)]
struct ManifestArchiveWire {
    #[serde(default)]
    demo_path: String,
    #[serde(default)]
    demo_id: String,
    #[serde(default)]
    demo_sha256: String,
    #[serde(default)]
    map: String,
    tick_rate: Option<f32>,
    abi: Option<i32>,
    format_version: Option<u32>,
    dtr_format_version: Option<u32>,
    avatar_overrides: Option<Vec<ManifestAvatarWire>>,
    rounds: Option<Vec<ManifestRoundWire>>,
    files: Option<Vec<ManifestFileWire>>,
}

#[derive(Debug, Deserialize)]
struct ManifestAvatarWire {
    steam_id: Option<u64>,
    path: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestRoundWire {
    round: Option<u32>,
    bomb_planted_seconds_after_live: Option<f32>,
    duration_seconds: Option<f32>,
    pistol_round: Option<bool>,
    cut_reason: Option<String>,
    t_economy: Option<ManifestTeamEconomyWire>,
    ct_economy: Option<ManifestTeamEconomyWire>,
    scoreboard: Option<ManifestRoundScoreboardWire>,
    files: Option<usize>,
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestRoundScoreboardWire {
    #[serde(default)]
    t_score: u32,
    #[serde(default)]
    ct_score: u32,
    t_team_name: Option<String>,
    ct_team_name: Option<String>,
}

impl From<ManifestRoundScoreboardWire> for ManifestRoundScoreboardDto {
    fn from(value: ManifestRoundScoreboardWire) -> Self {
        Self {
            t_score: value.t_score,
            ct_score: value.ct_score,
            t_team_name: value.t_team_name,
            ct_team_name: value.ct_team_name,
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestTeamEconomyWire {
    #[serde(default)]
    side: String,
    #[serde(default)]
    players: usize,
    #[serde(default)]
    round_start_equipment_value: u32,
    #[serde(default)]
    equipment_value_total: u32,
    #[serde(default)]
    money_saved_total: u32,
    #[serde(default)]
    cash_spent_this_round: u32,
    #[serde(default)]
    class: String,
}

impl From<ManifestTeamEconomyWire> for ManifestTeamEconomyDto {
    fn from(value: ManifestTeamEconomyWire) -> Self {
        Self {
            side: value.side,
            players: value.players,
            round_start_equipment_value: value.round_start_equipment_value,
            equipment_value_total: value.equipment_value_total,
            money_saved_total: value.money_saved_total,
            cash_spent_this_round: value.cash_spent_this_round,
            class: value.class,
        }
    }
}

#[derive(Debug, Deserialize)]
struct ManifestFileWire {
    path: Option<String>,
    round: Option<u32>,
    side: Option<String>,
    steam_id: Option<u64>,
    player_name: Option<String>,
    #[serde(default)]
    ticks: usize,
    #[serde(default)]
    subticks: usize,
    #[serde(default)]
    hifi_event_count: usize,
    #[serde(default)]
    inventory_snapshot_count: usize,
    scoreboard: Option<ManifestPlayerScoreboardWire>,
    cosmetics: Option<ManifestCosmeticsWire>,
}

#[derive(Debug, Clone, Default, Deserialize)]
struct ManifestPlayerScoreboardWire {
    player_color: Option<String>,
    score: Option<i32>,
    kills: Option<u32>,
    deaths: Option<u32>,
    assists: Option<u32>,
    mvps: Option<u32>,
}

#[derive(Debug, Default, Deserialize)]
struct ManifestCosmeticsWire {
    #[serde(default)]
    weapons: Vec<ManifestWeaponCosmeticWire>,
    knife: Option<serde_json::Value>,
    glove: Option<serde_json::Value>,
    agent: Option<serde_json::Value>,
}

impl ManifestCosmeticsWire {
    fn is_empty(&self) -> bool {
        self.weapons.is_empty()
            && self.knife.is_none()
            && self.glove.is_none()
            && self.agent.is_none()
    }

    fn has_stickers(&self) -> bool {
        self.weapons
            .iter()
            .any(|weapon| !weapon.stickers.is_empty())
    }

    fn has_charms(&self) -> bool {
        self.weapons.iter().any(|weapon| !weapon.charms.is_empty())
    }
}

#[derive(Debug, Default, Deserialize)]
struct ManifestWeaponCosmeticWire {
    #[serde(default)]
    stickers: Vec<serde_json::Value>,
    #[serde(default)]
    charms: Vec<serde_json::Value>,
}

#[derive(Debug, Default)]
struct ManifestRoundAccumulator {
    files: usize,
    t_files: usize,
    ct_files: usize,
    t_steam_ids: BTreeSet<u64>,
    ct_steam_ids: BTreeSet<u64>,
    playable_files: usize,
    cosmetic_files: usize,
    sticker_files: usize,
    charm_files: usize,
    ticks: usize,
    subticks: usize,
    hifi_events: usize,
    inventory_snapshots: usize,
}

#[derive(Debug)]
struct PlayableManifestFile {
    round: u32,
    side: String,
    steam_id: u64,
    player_name: String,
    scoreboard: Option<ManifestPlayerScoreboardWire>,
    has_cosmetics: bool,
    has_stickers: bool,
    has_charms: bool,
}

#[derive(Clone)]
struct CachedDemo {
    analysis_id: String,
    parsed: Arc<ParsedDemo>,
    analysis: DemoAnalysis,
    browser: BrowserDemoAnalysis,
    analysis_options: AnalysisOptions,
    archive_id: String,
    source_modified_at_ms: Option<u64>,
    source_size_bytes: Option<u64>,
}

struct AppState {
    cached: Mutex<Option<CachedDemo>>,
    busy: AtomicBool,
    analysis_cancel_requested: Arc<AtomicBool>,
    next_analysis_id: AtomicU64,
}

impl Default for AppState {
    fn default() -> Self {
        Self {
            cached: Mutex::new(None),
            busy: AtomicBool::new(false),
            analysis_cancel_requested: Arc::new(AtomicBool::new(false)),
            next_analysis_id: AtomicU64::new(1),
        }
    }
}

impl AppState {
    fn acquire_busy(&self) -> CommandResult<BusyGuard<'_>> {
        self.busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| {
                CommandErrorDto::new(
                    "busy",
                    "Another demo analysis or conversion is already running.",
                )
            })?;
        Ok(BusyGuard { busy: &self.busy })
    }

    fn cache(&self) -> CommandResult<MutexGuard<'_, Option<CachedDemo>>> {
        self.cached
            .lock()
            .map_err(|_| CommandErrorDto::new("state_poisoned", "Demo cache is unavailable."))
    }

    fn cached_demo(&self, analysis_id: &str) -> CommandResult<CachedDemo> {
        self.cache()?
            .as_ref()
            .filter(|cached| cached.analysis_id == analysis_id)
            .cloned()
            .ok_or_else(|| {
                CommandErrorDto::new(
                    "stale_analysis",
                    "The analyzed demo is no longer cached. Analyze it again before converting.",
                )
            })
    }

    fn session_id(&self, parsed: &ParsedDemo) -> String {
        let sequence = self.next_analysis_id.fetch_add(1, Ordering::Relaxed);
        let hash = parsed.demo_sha256.chars().take(12).collect::<String>();
        format!("{hash}-{sequence}")
    }
}

struct BusyGuard<'a> {
    busy: &'a AtomicBool,
}

impl Drop for BusyGuard<'_> {
    fn drop(&mut self) {
        self.busy.store(false, Ordering::Release);
    }
}

fn image_data_url(bytes: &[u8], media_type: &str) -> String {
    format!(
        "data:{media_type};base64,{}",
        base64::engine::general_purpose::STANDARD.encode(bytes)
    )
}

fn png_dimensions(bytes: &[u8]) -> CommandResult<(u32, u32)> {
    const PNG_SIGNATURE: &[u8; 8] = b"\x89PNG\r\n\x1a\n";
    if bytes.len() < 24 || &bytes[..8] != PNG_SIGNATURE || &bytes[12..16] != b"IHDR" {
        return Err(CommandErrorDto::new(
            "workspace_background_invalid_png",
            "Choose a valid PNG image.",
        ));
    }
    let width = u32::from_be_bytes(bytes[16..20].try_into().unwrap_or_default());
    let height = u32::from_be_bytes(bytes[20..24].try_into().unwrap_or_default());
    if width == 0 || height == 0 || width > 16_384 || height > 16_384 {
        return Err(CommandErrorDto::new(
            "workspace_background_dimensions_invalid",
            "PNG dimensions must be between 1 and 16384 pixels.",
        ));
    }
    Ok((width, height))
}

fn workspace_background_path(app: &AppHandle) -> CommandResult<PathBuf> {
    app.path()
        .app_local_data_dir()
        .map(|root| root.join("appearance").join("workspace-background.png"))
        .map_err(|error| {
            CommandErrorDto::new("workspace_background_path_failed", error.to_string())
        })
}

fn read_workspace_background_for(path: &Path) -> CommandResult<Option<WorkspaceBackgroundDto>> {
    if !path.exists() {
        return Ok(None);
    }
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        CommandErrorDto::at_path("workspace_background_read_failed", error.to_string(), path)
    })?;
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() > MAX_WORKSPACE_BACKGROUND_BYTES as u64
    {
        return Err(CommandErrorDto::at_path(
            "workspace_background_invalid",
            "The saved background is not a supported PNG file.",
            path,
        ));
    }
    let bytes = fs::read(path).map_err(|error| {
        CommandErrorDto::at_path("workspace_background_read_failed", error.to_string(), path)
    })?;
    let (width, height) = png_dimensions(&bytes)?;
    Ok(Some(WorkspaceBackgroundDto {
        data_url: image_data_url(&bytes, "image/png"),
        width,
        height,
        bytes: bytes.len(),
    }))
}

#[tauri::command]
fn read_workspace_background(app: AppHandle) -> CommandResult<Option<WorkspaceBackgroundDto>> {
    read_workspace_background_for(&workspace_background_path(&app)?)
}

#[tauri::command]
async fn choose_workspace_background(
    app: AppHandle,
    request: WorkspaceBackgroundDialogRequestDto,
) -> CommandResult<Option<WorkspaceBackgroundDto>> {
    let selected = tauri::async_runtime::spawn_blocking(move || {
        rfd::FileDialog::new()
            .set_title(request.title)
            .add_filter(request.filter_label, &["png"])
            .pick_file()
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))?;
    let Some(selected) = selected else {
        return Ok(None);
    };
    let metadata = fs::symlink_metadata(&selected).map_err(|error| {
        CommandErrorDto::at_path(
            "workspace_background_read_failed",
            error.to_string(),
            &selected,
        )
    })?;
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() > MAX_WORKSPACE_BACKGROUND_BYTES as u64
    {
        return Err(CommandErrorDto::at_path(
            "workspace_background_invalid",
            "Choose a regular PNG no larger than 16 MiB.",
            &selected,
        ));
    }
    let bytes = fs::read(&selected).map_err(|error| {
        CommandErrorDto::at_path(
            "workspace_background_read_failed",
            error.to_string(),
            &selected,
        )
    })?;
    png_dimensions(&bytes)?;
    let destination = workspace_background_path(&app)?;
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent).map_err(|error| {
            CommandErrorDto::at_path(
                "workspace_background_write_failed",
                error.to_string(),
                parent,
            )
        })?;
    }
    match fs::symlink_metadata(&destination) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.file_type().is_file() => {
            return Err(CommandErrorDto::at_path(
                "workspace_background_write_failed",
                "Refusing to replace a non-regular workspace background file.",
                &destination,
            ));
        }
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => {
            return Err(CommandErrorDto::at_path(
                "workspace_background_write_failed",
                error.to_string(),
                &destination,
            ));
        }
    }
    fs::write(&destination, bytes).map_err(|error| {
        CommandErrorDto::at_path(
            "workspace_background_write_failed",
            error.to_string(),
            &destination,
        )
    })?;
    read_workspace_background_for(&destination)
}

#[tauri::command]
fn clear_workspace_background(app: AppHandle) -> CommandResult<bool> {
    let path = workspace_background_path(&app)?;
    match fs::symlink_metadata(&path) {
        Ok(metadata) if metadata.file_type().is_dir() => {
            return Err(CommandErrorDto::at_path(
                "workspace_background_clear_failed",
                "Refusing to remove a directory as a workspace background.",
                &path,
            ));
        }
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => {
            return Err(CommandErrorDto::at_path(
                "workspace_background_clear_failed",
                error.to_string(),
                &path,
            ));
        }
    }
    fs::remove_file(&path).map_err(|error| {
        CommandErrorDto::at_path(
            "workspace_background_clear_failed",
            error.to_string(),
            &path,
        )
    })?;
    Ok(true)
}

#[tauri::command]
fn load_library_avatar(request: LibraryAvatarRequestDto) -> CommandResult<Option<String>> {
    let manifest_path = validate_manifest_input_path(&request.manifest_path)?;
    let entry = catalog::summarize_manifest(&manifest_path).map_err(|message| {
        CommandErrorDto::at_path("archive_summary_failed", message, &manifest_path)
    })?;
    let requested_path = request.relative_path.trim().replace('\\', "/");
    let requested_sha = request.sha256.trim().to_ascii_lowercase();
    if !entry
        .avatar_overrides
        .iter()
        .any(|avatar| avatar.path == requested_path && avatar.sha256 == requested_sha)
    {
        return Ok(None);
    }
    let relative = validate_manifest_child_path(&requested_path)
        .map_err(|(code, message)| CommandErrorDto::new(code, message))?;
    let root = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let canonical_root = fs::canonicalize(root).map_err(|error| {
        CommandErrorDto::at_path("manifest_root_unavailable", error.to_string(), root)
    })?;
    let path = fs::canonicalize(root.join(relative)).map_err(|error| {
        CommandErrorDto::at_path("manifest_avatar_missing", error.to_string(), root)
    })?;
    if !path.starts_with(&canonical_root) || !path.is_file() {
        return Ok(None);
    }
    let bytes = fs::read(&path).map_err(|error| {
        CommandErrorDto::at_path("manifest_avatar_read_failed", error.to_string(), &path)
    })?;
    if bytes.len() > MAX_LIBRARY_AVATAR_BYTES || sha256_hex(&bytes) != requested_sha {
        return Ok(None);
    }
    let media_type = if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
        "image/png"
    } else if bytes.starts_with(&[0xff, 0xd8, 0xff]) {
        "image/jpeg"
    } else {
        return Ok(None);
    };
    Ok(Some(image_data_url(&bytes, media_type)))
}

#[tauri::command]
async fn choose_demo(initial_path: Option<String>) -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new()
            .set_title("Choose a CS2 demo")
            .add_filter("CS2 demo (.dem, .dem.zst)", &["dem", "zst"]);
        if let Some(value) = initial_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
        {
            let hint = Path::new(value);
            if hint.is_dir() {
                dialog = dialog.set_directory(hint);
            } else {
                if let Some(parent) = hint.parent().filter(|parent| parent.is_dir()) {
                    dialog = dialog.set_directory(parent);
                }
                if let Some(name) = hint.file_name().and_then(|name| name.to_str()) {
                    dialog = dialog.set_file_name(name);
                }
            }
        }
        dialog.pick_file().map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn choose_demos(initial_path: Option<String>) -> CommandResult<Option<Vec<String>>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new()
            .set_title("Choose up to 8 CS2 demos")
            .add_filter("CS2 demo (.dem, .dem.zst)", &["dem", "zst"]);
        if let Some(value) = initial_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
        {
            let hint = Path::new(value);
            if hint.is_dir() {
                dialog = dialog.set_directory(hint);
            } else if let Some(parent) = hint.parent().filter(|parent| parent.is_dir()) {
                dialog = dialog.set_directory(parent);
            }
        }
        let Some(paths) = dialog.pick_files() else {
            return Ok(None);
        };
        if paths.len() > batch::MAX_BATCH_ITEMS {
            return Err(CommandErrorDto::new(
                "demo_selection_too_large",
                format!(
                    "Select at most {} demos for one parallel conversion.",
                    batch::MAX_BATCH_ITEMS
                ),
            ));
        }
        Ok(Some(
            paths
                .into_iter()
                .map(|path| path.display().to_string())
                .collect(),
        ))
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))?
}

#[tauri::command]
async fn choose_manifest() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose a DemoTracer manifest")
            .add_filter("DemoTracer manifest", &["json"])
            .pick_file()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn choose_output_dir() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose an output folder")
            .pick_folder()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
fn default_library_dir(app: AppHandle) -> CommandResult<String> {
    let documents = app
        .path()
        .document_dir()
        .map_err(|error| CommandErrorDto::new("documents_dir_unavailable", error.to_string()))?;
    let root = documents.join("CS2 DemoTracer").join("Library");
    fs::create_dir_all(&root).map_err(|error| {
        CommandErrorDto::at_path("default_library_create_failed", error.to_string(), &root)
    })?;
    Ok(root.display().to_string())
}

#[tauri::command]
async fn choose_library_dir() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose a DemoTracer archive folder")
            .pick_folder()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn choose_demo_source_dir(initial_path: Option<String>) -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog =
            rfd::FileDialog::new().set_title("Choose a folder containing original CS2 demos");
        if let Some(value) = initial_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
        {
            let hint = Path::new(value);
            if hint.is_dir() {
                dialog = dialog.set_directory(hint);
            } else if let Some(parent) = hint.parent().filter(|parent| parent.is_dir()) {
                dialog = dialog.set_directory(parent);
            }
        }
        dialog.pick_folder().map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn analyze_demo(
    request: AnalyzeDemoRequest,
    events: Channel<TaskEvent>,
    state: State<'_, AppState>,
) -> CommandResult<AnalysisDto> {
    validate_max_round_seconds(request.max_round_seconds)?;
    let _busy = state.acquire_busy()?;
    state
        .analysis_cancel_requested
        .store(false, Ordering::Release);
    let requested_path = validate_demo_path(&request.path)?;
    let expected_sha256 = request
        .expected_demo_sha256
        .as_deref()
        .map(str::trim)
        .filter(|expected| !expected.is_empty());
    let source = if let Some(expected_sha256) = expected_sha256 {
        resolve_validated_demo_source_for_sha256(&requested_path, expected_sha256)?.ok_or_else(
            || {
                CommandErrorDto::at_path(
                    "analysis_demo_hash_mismatch",
                    "This demo is not the original source for the selected archive.",
                    &requested_path,
                )
            },
        )?
    } else {
        resolve_validated_demo_source(&requested_path)?
    };
    let source_path = source.primary_path().to_path_buf();
    let source_metadata = source.metadata().ok();
    *state.cache()? = None;

    emit_demo_source(&events, &source);

    let worker_source = source.clone();
    let worker_events = events.clone();
    let worker_cancel = Arc::clone(&state.analysis_cancel_requested);
    let analysis_options = AnalysisOptions {
        max_round_seconds: request.max_round_seconds,
        ..AnalysisOptions::default()
    };
    let parsed_result = tauri::async_runtime::spawn_blocking(move || {
        if worker_source.paths().any(is_zstd_demo_path) {
            emit_phase(&worker_events, TaskPhase::Decompressing);
        }
        emit_phase(&worker_events, TaskPhase::Parsing);
        let parsed = match read_demo_source_with_options_and_cancel(
            &worker_source,
            INTERACTIVE_ANALYSIS_READ_OPTIONS,
            &worker_cancel,
        ) {
            Ok(parsed) => parsed,
            Err(_) if worker_cancel.load(Ordering::Acquire) => {
                return Err(CommandErrorDto::new(
                    "analysis_cancelled",
                    "Demo analysis was stopped.",
                ));
            }
            Err(error) => {
                emit_log(&worker_events, LogLevel::Error, error.to_string());
                return Err(CommandErrorDto::from_core("analysis_failed", error));
            }
        };
        if worker_cancel.load(Ordering::Acquire) {
            return Err(CommandErrorDto::new(
                "analysis_cancelled",
                "Demo analysis was stopped.",
            ));
        }
        emit_phase(&worker_events, TaskPhase::Analyzing);
        let analysis = analyze_browser_demo(&parsed, analysis_options);
        if worker_cancel.load(Ordering::Acquire) {
            return Err(CommandErrorDto::new(
                "analysis_cancelled",
                "Demo analysis was stopped.",
            ));
        }
        Ok::<_, CommandErrorDto>((Arc::new(parsed), analysis))
    })
    .await
    .map_err(|error| CommandErrorDto::new("analysis_worker_failed", error.to_string()))?;

    let (parsed, browser_analysis) = parsed_result?;
    if let Some(warning) = replay_input_warning(&browser_analysis.replay_input) {
        emit_log(&events, LogLevel::Warning, warning);
    }
    if expected_sha256.is_some_and(|expected| !parsed.demo_sha256.eq_ignore_ascii_case(expected)) {
        return Err(CommandErrorDto::at_path(
            "analysis_demo_hash_mismatch",
            "This demo is not the original source for the selected archive.",
            &source_path,
        ));
    }
    let analysis_id = state.session_id(&parsed);
    let output_demo_id = archive_info::archive_directory_name(&parsed, &browser_analysis);
    let source_modified_at_ms = source_metadata.and_then(source_metadata_modified_at_ms);
    let source_size_bytes = source_metadata.map(|metadata| metadata.size_bytes);
    let dto = browser_analysis_dto(
        &analysis_id,
        &source_path,
        &output_demo_id,
        &parsed,
        &browser_analysis,
        source_modified_at_ms,
        source_size_bytes,
    );

    *state.cache()? = Some(CachedDemo {
        analysis_id,
        parsed,
        analysis: browser_analysis.analysis.clone(),
        browser: browser_analysis,
        analysis_options,
        archive_id: output_demo_id,
        source_modified_at_ms,
        source_size_bytes,
    });
    emit_phase(&events, TaskPhase::Complete);
    Ok(dto)
}

#[tauri::command]
fn cancel_analysis(state: State<'_, AppState>) -> CommandResult<()> {
    state
        .analysis_cancel_requested
        .store(true, Ordering::Release);
    Ok(())
}

#[tauri::command]
async fn preflight_demo_source(
    request: PreflightDemoSourceRequest,
    state: State<'_, AppState>,
) -> CommandResult<PreflightDemoSourceDto> {
    let _busy = state.acquire_busy()?;
    tauri::async_runtime::spawn_blocking(move || preflight_demo_source_for(&request))
        .await
        .map_err(|error| CommandErrorDto::new("demo_preflight_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn convert_demo(
    request: ConvertDemoRequest,
    events: Channel<TaskEvent>,
    state: State<'_, AppState>,
) -> CommandResult<ConversionSummaryDto> {
    let _busy = state.acquire_busy()?;
    let cached = state.cached_demo(&request.analysis_id)?;
    let prepared = prepare_conversion(&request, &cached)?;

    let worker_events = events.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        run_conversion(cached, prepared, request, worker_events)
    })
    .await
    .map_err(|error| CommandErrorDto::new("conversion_worker_failed", error.to_string()))?;

    if let Err(error) = &result {
        emit_log(&events, LogLevel::Error, error.message.clone());
    }
    result
}

#[tauri::command]
async fn preflight_output(
    request: PreflightOutputRequest,
    state: State<'_, AppState>,
) -> CommandResult<PreflightOutputDto> {
    let cached = state.cached_demo(&request.analysis_id)?;
    preflight_output_for(&cached, &request.output_dir)
}

#[tauri::command]
async fn read_manifest(path: String) -> CommandResult<ManifestArchiveDto> {
    tauri::async_runtime::spawn_blocking(move || read_manifest_for(&path))
        .await
        .map_err(|error| CommandErrorDto::new("manifest_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn save_archive_note(request: SaveArchiveNoteRequest) -> CommandResult<SaveArchiveNoteDto> {
    tauri::async_runtime::spawn_blocking(move || save_archive_note_for(&request))
        .await
        .map_err(|_| {
            CommandErrorDto::new(
                "archive_note_worker_failed",
                "The note could not be saved. Try again.",
            )
        })?
}

#[tauri::command]
async fn scan_demo_library(root: String) -> CommandResult<catalog::LibraryScanDto> {
    tauri::async_runtime::spawn_blocking(move || catalog::scan_demo_library_for(&root))
        .await
        .map_err(|error| CommandErrorDto::new("library_scan_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn delete_archive(
    request: DeleteArchiveRequest,
    state: State<'_, AppState>,
) -> CommandResult<()> {
    let _busy = state.acquire_busy()?;
    tauri::async_runtime::spawn_blocking(move || delete_archive_for(&request))
        .await
        .map_err(|error| CommandErrorDto::new("archive_delete_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn refresh_archive_metadata(
    request: RefreshArchiveMetadataRequest,
    state: State<'_, AppState>,
) -> CommandResult<RefreshArchiveMetadataDto> {
    let _busy = state.acquire_busy()?;
    let manifest_path = validate_manifest_input_path(&request.manifest_path)?;
    tauri::async_runtime::spawn_blocking(move || {
        refresh_archive_metadata_for(&manifest_path, request.demo_path.as_deref())
    })
    .await
    .map_err(|error| CommandErrorDto::new("metadata_refresh_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn resolve_archive_source(
    request: ResolveArchiveSourceRequest,
    state: State<'_, AppState>,
) -> CommandResult<ResolveArchiveSourceDto> {
    let _busy = state.acquire_busy()?;
    let manifest_path = validate_manifest_input_path(&request.manifest_path)?;
    tauri::async_runtime::spawn_blocking(move || {
        resolve_archive_source_for(&manifest_path, request.demo_path.as_deref())
    })
    .await
    .map_err(|error| CommandErrorDto::new("source_resolve_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn refresh_library_metadata(
    request: RefreshLibraryMetadataRequest,
    state: State<'_, AppState>,
) -> CommandResult<RefreshLibraryMetadataDto> {
    let _busy = state.acquire_busy()?;
    tauri::async_runtime::spawn_blocking(move || refresh_library_metadata_for(&request))
        .await
        .map_err(|error| {
            CommandErrorDto::new("metadata_refresh_worker_failed", error.to_string())
        })?
}

#[tauri::command]
async fn import_archives(
    request: ImportArchivesRequest,
    state: State<'_, AppState>,
) -> CommandResult<ImportArchivesDto> {
    let _busy = state.acquire_busy()?;
    tauri::async_runtime::spawn_blocking(move || import_archives_for(&request))
        .await
        .map_err(|error| CommandErrorDto::new("archive_import_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn open_output(request: OpenOutputRequest) -> CommandResult<()> {
    let path = PathBuf::from(request.path.trim());
    if !path.is_dir() {
        return Err(CommandErrorDto::at_path(
            "output_not_found",
            "The output folder does not exist.",
            &path,
        ));
    }
    let worker_path = path.clone();
    tauri::async_runtime::spawn_blocking(move || open_folder_path(&worker_path))
        .await
        .map_err(|error| CommandErrorDto::new("open_output_worker_failed", error.to_string()))?
        .map_err(|error| CommandErrorDto::at_path("open_output_failed", error.to_string(), &path))
}

#[tauri::command]
async fn open_activity_log_directory(state: State<'_, ActivityLogState>) -> CommandResult<()> {
    let path = state.root().to_path_buf();
    let worker_path = path.clone();
    tauri::async_runtime::spawn_blocking(move || open_folder_path(&worker_path))
        .await
        .map_err(|error| {
            CommandErrorDto::new("open_activity_log_worker_failed", error.to_string())
        })?
        .map_err(|error| {
            CommandErrorDto::at_path("open_activity_log_failed", error.to_string(), &path)
        })
}

#[tauri::command]
async fn reveal_output(request: OpenOutputRequest) -> CommandResult<()> {
    let path = PathBuf::from(request.path.trim());
    if !path.is_file() {
        return Err(CommandErrorDto::at_path(
            "output_not_found",
            "The file does not exist.",
            &path,
        ));
    }
    let worker_path = path.clone();
    tauri::async_runtime::spawn_blocking(move || reveal_file_path(&worker_path))
        .await
        .map_err(|error| CommandErrorDto::new("reveal_output_worker_failed", error.to_string()))?
        .map_err(|error| CommandErrorDto::at_path("reveal_output_failed", error.to_string(), &path))
}

#[tauri::command]
async fn open_external(request: OpenExternalRequest) -> CommandResult<()> {
    let url = validate_external_url(&request.url)?;
    let worker_url = url.clone();
    tauri::async_runtime::spawn_blocking(move || open_external_url(&worker_url))
        .await
        .map_err(|error| CommandErrorDto::new("open_external_worker_failed", error.to_string()))?
        .map_err(|error| CommandErrorDto::new("open_external_failed", error.to_string()))
}

#[tauri::command]
async fn load_steam_profiles(app: AppHandle, steam_ids: Vec<String>) -> Vec<SteamProfileDto> {
    let local_data_root = app.path().app_local_data_dir().ok();
    tauri::async_runtime::spawn_blocking(move || {
        steam_profile::resolve_profiles(local_data_root, steam_ids)
    })
    .await
    .unwrap_or_default()
}

fn validate_external_url(value: &str) -> CommandResult<String> {
    const PROFILE_PREFIX: &str = "https://steamcommunity.com/profiles/";
    const INSPECT_PREFIX: &str = "steam://run/730/en/+csgo_econ_action_preview%20";
    const LEGACY_INSPECT_PREFIX: &str =
        "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20";
    const CREDIT_GITHUB_URLS: &[&str] = &[
        "https://github.com/unicbm/demotracer",
        "https://github.com/unicbm/demotracer/releases",
        "https://github.com/unicbm",
        "https://github.com/ed0ard",
        "https://github.com/Newbie046",
        "https://github.com/XBribo",
        "https://github.com/Misaka17032",
        "https://github.com/T1mLuk0",
        "https://github.com/ianlucas",
        "https://github.com/LaihoE",
        "https://github.com/XBribo/CS2-Bot-Controller",
        "https://github.com/XBribo/CS2-Bot-Hider",
        "https://github.com/ianlucas/cs2-inventory-simulator",
        "https://github.com/ianlucas/cs2-lib",
        "https://github.com/ianlucas/cs2-lib-inspect",
        "https://github.com/LaihoE/demoparser",
    ];
    const STEAM_PROTOCOL_CHAR_LIMIT: usize = 300;
    let value = value.trim();
    if CREDIT_GITHUB_URLS.contains(&value) {
        return Ok(value.to_string());
    }
    if let Some(steam_id) = value.strip_prefix(PROFILE_PREFIX) {
        let valid = steam_id.len() == 17
            && steam_id.chars().all(|character| character.is_ascii_digit())
            && steam_id
                .parse::<u64>()
                .is_ok_and(|number| number >= 76_561_197_960_265_728);
        if valid {
            return Ok(value.to_string());
        }
    }
    for prefix in [INSPECT_PREFIX, LEGACY_INSPECT_PREFIX] {
        if let Some(payload) = value.strip_prefix(prefix) {
            let canonical = format!("{INSPECT_PREFIX}{payload}");
            let valid = value.len() <= STEAM_PROTOCOL_CHAR_LIMIT
                && canonical.len() <= STEAM_PROTOCOL_CHAR_LIMIT
                && payload.len() >= 10
                && payload.len() % 2 == 0
                && payload
                    .chars()
                    .all(|character| character.is_ascii_hexdigit());
            if valid {
                return Ok(canonical);
            }
        }
    }
    Err(CommandErrorDto::new(
        "external_url_not_allowed",
        "Only supported Steam links and the GitHub profiles or repositories shown in DemoTracer credits can be opened.",
    ))
}

fn refresh_archive_metadata_for(
    manifest_path: &Path,
    requested_demo_path: Option<&str>,
) -> CommandResult<RefreshArchiveMetadataDto> {
    let entry = catalog::summarize_manifest(manifest_path).map_err(|message| {
        CommandErrorDto::at_path("archive_summary_failed", message, manifest_path)
    })?;
    if entry.demo_sha256.trim().is_empty() {
        return Err(CommandErrorDto::at_path(
            "archive_demo_hash_missing",
            "This legacy manifest does not contain a demo SHA-256 and cannot be matched safely.",
            manifest_path,
        ));
    }
    let archive_root = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let requested_demo_path = requested_demo_path
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let source = resolve_source_demo(
        entry.demo_sha256.trim(),
        entry.source_path.as_deref(),
        &entry.demo_path,
        requested_demo_path,
    )?;
    let demo_path = source.primary_path().to_path_buf();
    let expected_info_revision =
        archive_info::read_matching_demo_info(archive_root, entry.demo_sha256.trim())
            .map(|info| info.write_revision)
            .unwrap_or(0);
    let previous_conversion =
        preserved_conversion_for_manifest(archive_root, entry.demo_sha256.trim(), manifest_path);
    let source_metadata = source.metadata().ok();
    let parsed = read_demo_source_with_options(
        &source,
        ReadDemoOptions {
            collect_voice: false,
            collect_cosmetics: previous_conversion
                .as_ref()
                .is_some_and(|conversion| conversion.cosmetics),
        },
    )
    .map_err(|error| CommandErrorDto::from_core("metadata_demo_parse_failed", error))?;
    if !parsed
        .demo_sha256
        .eq_ignore_ascii_case(entry.demo_sha256.trim())
    {
        return Err(CommandErrorDto::at_path(
            "metadata_demo_hash_mismatch",
            "The original demo changed while it was being read. Locate the matching demo and try again.",
            &demo_path,
        ));
    }
    let browser = analyze_browser_demo(&parsed, AnalysisOptions::default());
    let mut info = archive_info::DemoArchiveInfo::from_analysis(
        entry.demo_id,
        &parsed,
        &browser,
        source_metadata.and_then(source_metadata_modified_at_ms),
        source_metadata.map(|metadata| metadata.size_bytes),
        entry.abi,
        entry.format_version,
        "metadataRefresh",
    );
    restrict_player_cosmetic_details(
        &mut info.players,
        previous_conversion
            .as_ref()
            .is_some_and(|conversion| conversion.cosmetics),
        previous_conversion
            .as_ref()
            .is_some_and(|conversion| conversion.stickers),
        previous_conversion
            .as_ref()
            .is_some_and(|conversion| conversion.charms),
    );
    info.conversion = previous_conversion;
    let info_path = archive_info::write_demo_source_and_info_if_revision(
        archive_root,
        entry.demo_sha256.trim(),
        source.primary_path(),
        &info,
        expected_info_revision,
    )
    .map_err(|error| {
        CommandErrorDto::at_path(
            if error.kind() == std::io::ErrorKind::WouldBlock {
                "demo_info_write_conflict"
            } else {
                "demo_info_write_failed"
            },
            error.to_string(),
            archive_info::demo_info_path(archive_root),
        )
    })?;
    Ok(RefreshArchiveMetadataDto {
        manifest_path: manifest_path.display().to_string(),
        info_path: info_path.display().to_string(),
        display_name: info.display_name,
        source_path: info
            .source_file_path
            .unwrap_or_else(|| demo_path.display().to_string()),
    })
}

fn resolve_archive_source_for(
    manifest_path: &Path,
    requested_demo_path: Option<&str>,
) -> CommandResult<ResolveArchiveSourceDto> {
    let entry = catalog::summarize_manifest(manifest_path).map_err(|message| {
        CommandErrorDto::at_path("archive_summary_failed", message, manifest_path)
    })?;
    if entry.demo_sha256.trim().is_empty() {
        return Err(CommandErrorDto::at_path(
            "archive_demo_hash_missing",
            "This legacy manifest does not contain a demo SHA-256 and cannot be matched safely.",
            manifest_path,
        ));
    }
    let archive_root = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let requested_demo_path = requested_demo_path
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let source = resolve_source_demo(
        entry.demo_sha256.trim(),
        entry.source_path.as_deref(),
        &entry.demo_path,
        requested_demo_path,
    )?;
    let source_path = source.primary_path().to_path_buf();

    // Relocation should be remembered immediately. The pointer and any
    // matching demo-info sidecar are updated under one archive-scoped lock;
    // manifest.json remains portable and sanitized.
    archive_info::update_demo_source_metadata(archive_root, entry.demo_sha256.trim(), &source_path)
        .map_err(|error| {
            CommandErrorDto::at_path(
                "demo_source_write_failed",
                error.to_string(),
                archive_info::demo_source_path(archive_root),
            )
        })?;

    Ok(ResolveArchiveSourceDto {
        source_path: source_path.display().to_string(),
    })
}

fn resolve_source_demo(
    expected_sha256: &str,
    recorded_path: Option<&str>,
    fallback_name: &str,
    requested_path: Option<&str>,
) -> CommandResult<DemoSourceSet> {
    let demo_path_hint = requested_path.or(recorded_path);
    let Some(demo_path_hint) = demo_path_hint else {
        return Err(CommandErrorDto::at_path(
            "source_demo_unavailable",
            "The original demo location is not recorded. Locate it once and DemoTracer will remember it.",
            Path::new(fallback_name),
        ));
    };
    let requested_demo_path = match validate_demo_path(demo_path_hint) {
        Ok(path) => path,
        Err(_) if requested_path.is_none() => {
            return Err(CommandErrorDto::at_path(
                "source_demo_unavailable",
                "The original demo was moved or removed. Locate it once to update this archive.",
                Path::new(demo_path_hint),
            ));
        }
        Err(error) => return Err(error),
    };
    resolve_validated_demo_source_for_sha256(&requested_demo_path, expected_sha256)?.ok_or_else(
        || {
            CommandErrorDto::at_path(
                "metadata_demo_hash_mismatch",
                "The selected original demo does not match this archive's full SHA-256.",
                &requested_demo_path,
            )
        },
    )
}

fn preserved_conversion_for_manifest(
    archive_root: &Path,
    demo_sha256: &str,
    manifest_path: &Path,
) -> Option<archive_info::DemoInfoConversion> {
    let manifest_bytes = fs::read(manifest_path).ok()?;
    let current_manifest_sha256 = sha256_hex(&manifest_bytes);
    let conversion = archive_info::read_matching_demo_info(archive_root, demo_sha256)
        .and_then(|previous| previous.conversion)?;
    match conversion.manifest_sha256.as_deref() {
        Some(expected) if expected.eq_ignore_ascii_case(&current_manifest_sha256) => {
            Some(conversion)
        }
        Some(_) => None,
        // Preserve old intent as unbound evidence, but never attach a new hash
        // that the old sidecar did not actually observe. Readers expose its
        // flags only when a manifest hash is present and matches.
        None => Some(conversion),
    }
}

fn restrict_player_cosmetic_details(
    players: &mut [BrowserPlayerSummary],
    export_cosmetics: bool,
    export_stickers: bool,
    export_charms: bool,
) {
    for player in players {
        if let Some(details) = player.details.as_mut() {
            details.restrict_cosmetic_export(export_cosmetics, export_stickers, export_charms);
            if details.is_empty() {
                player.details = None;
            }
        }
    }
}

fn refresh_library_metadata_for(
    request: &RefreshLibraryMetadataRequest,
) -> CommandResult<RefreshLibraryMetadataDto> {
    let scan = catalog::scan_demo_library_for(&request.library_root)?;
    let mut archives_current = 0_usize;
    let mut targets = BTreeMap::<String, Vec<catalog::LibraryEntryDto>>::new();
    let mut failures = Vec::new();
    let mut demos_scanned = 0_usize;
    let mut matched_source_paths = BTreeMap::<String, String>::new();
    for entry in scan.entries {
        let hash = entry.demo_sha256.trim().to_ascii_lowercase();
        if hash.is_empty() {
            failures.push(format!(
                "{}: manifest has no demo SHA-256",
                entry.manifest_path
            ));
            continue;
        }
        if entry.metadata_status == "current" && entry.source_available {
            let source_matches = entry.source_path.as_deref().is_some_and(|source_path| {
                match local_demo_hashes(Path::new(source_path)) {
                    Ok(source_hashes) if !source_hashes.is_empty() => {
                        demos_scanned += 1;
                        source_hashes
                            .iter()
                            .any(|source_hash| source_hash.eq_ignore_ascii_case(&hash))
                    }
                    Ok(_) => false,
                    Err(error) => {
                        failures.push(format!("{source_path}: {error}"));
                        false
                    }
                }
            });
            if source_matches {
                if let Some(source_path) = entry.source_path.as_ref() {
                    matched_source_paths.insert(hash.clone(), source_path.clone());
                }
                archives_current += 1;
                continue;
            }
        }
        targets.entry(hash).or_default().push(entry);
    }
    let target_count = targets.values().map(Vec::len).sum::<usize>();
    let mut demos_matched = 0_usize;
    let mut archives_updated = 0_usize;

    // Resolve recorded archive pointers and the machine-local hash index
    // before asking the user for a directory or scanning one.
    for hash in targets.keys().cloned().collect::<Vec<_>>() {
        let mut candidates = BTreeSet::<String>::new();
        if let Some(path) = request.source_paths.get(&hash) {
            candidates.insert(path.clone());
        }
        if let Some(entries) = targets.get(&hash) {
            candidates.extend(entries.iter().filter_map(|entry| entry.source_path.clone()));
        }
        for candidate in candidates {
            let demo_path = PathBuf::from(candidate);
            let candidate_hashes = match local_demo_hashes(&demo_path) {
                Ok(candidate_hashes) if !candidate_hashes.is_empty() => {
                    demos_scanned += 1;
                    candidate_hashes
                }
                Ok(_) | Err(_) => continue,
            };
            if !candidate_hashes
                .iter()
                .any(|candidate_hash| candidate_hash.eq_ignore_ascii_case(&hash))
            {
                continue;
            }
            let entries = targets.get(&hash).cloned().unwrap_or_default();
            match refresh_metadata_entries_from_demo(&demo_path, &entries, &mut failures) {
                Ok(updated) => {
                    archives_updated += updated;
                    demos_matched += 1;
                    if updated > 0 {
                        matched_source_paths.insert(hash.clone(), demo_path.display().to_string());
                    }
                    targets.remove(&hash);
                }
                Err(error) => failures.push(format!("{}: {error}", demo_path.display())),
            }
            // The source demo was located and hash-matched. A parse or write
            // failure is not a missing-source condition and must not trigger a
            // redundant directory picker in the UI.
            targets.remove(&hash);
            break;
        }
    }

    if let Some(demo_root) = request
        .demo_root
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
    {
        for demo_path in collect_demo_files(Path::new(demo_root))? {
            if targets.is_empty() {
                break;
            }
            let hashes = match local_demo_hashes(&demo_path) {
                Ok(hashes) if !hashes.is_empty() => {
                    demos_scanned += 1;
                    hashes
                }
                Ok(_) => continue,
                Err(error) => {
                    failures.push(format!("{}: {error}", demo_path.display()));
                    continue;
                }
            };
            let matching_hashes = hashes
                .into_iter()
                .filter(|hash| targets.contains_key(hash))
                .collect::<Vec<_>>();
            if matching_hashes.is_empty() {
                continue;
            }
            for hash in matching_hashes {
                let entries = targets
                    .get(&hash)
                    .cloned()
                    .expect("matched target hash exists");
                match refresh_metadata_entries_from_demo(&demo_path, &entries, &mut failures) {
                    Ok(updated) => {
                        archives_updated += updated;
                        demos_matched += 1;
                        if updated > 0 {
                            matched_source_paths
                                .insert(hash.clone(), demo_path.display().to_string());
                        }
                    }
                    Err(error) => failures.push(format!("{}: {error}", demo_path.display())),
                }
                targets.remove(&hash);
            }
        }
    }

    let source_unmatched = targets.values().map(Vec::len).sum::<usize>();
    failures.truncate(32);
    Ok(RefreshLibraryMetadataDto {
        demos_scanned,
        demos_matched,
        archives_updated,
        archives_current,
        archives_unmatched: target_count.saturating_sub(archives_updated),
        source_unmatched,
        source_paths: matched_source_paths,
        failures,
    })
}

fn local_demo_hashes(path: &Path) -> Result<Vec<String>, String> {
    if !path.is_file() || !is_supported_demo_path(path) {
        return Ok(Vec::new());
    }
    let source = match resolve_demo_source(path) {
        Ok(source) => source,
        Err(_) => {
            return demo_content_sha256(path)
                .map(|hash| vec![hash])
                .map_err(|error| error.to_string());
        }
    };
    let compound = demo_source_sha256(&source).map_err(|error| error.to_string())?;
    let mut hashes = vec![compound];
    if source.is_segmented() {
        let raw = demo_content_sha256(path).map_err(|error| error.to_string())?;
        if !hashes.iter().any(|hash| hash.eq_ignore_ascii_case(&raw)) {
            hashes.push(raw);
        }
    }
    Ok(hashes)
}

fn preflight_demo_source_for(
    request: &PreflightDemoSourceRequest,
) -> CommandResult<PreflightDemoSourceDto> {
    let requested_path = validate_demo_path(&request.path)?;
    let source = resolve_validated_demo_source(&requested_path)?;
    let source_path = source.primary_path().to_path_buf();
    let source_metadata = source
        .metadata()
        .map_err(|error| CommandErrorDto::from_core("demo_source_inspect_failed", error))?;
    let hashes = local_demo_hashes(&source_path).map_err(|message| {
        CommandErrorDto::at_path("demo_fingerprint_failed", message, &source_path)
    })?;
    let demo_sha256 = hashes.first().cloned().ok_or_else(|| {
        CommandErrorDto::at_path(
            "demo_fingerprint_failed",
            "The selected demo could not be fingerprinted.",
            &source_path,
        )
    })?;
    let hashes = hashes
        .into_iter()
        .map(|hash| hash.to_ascii_lowercase())
        .collect::<BTreeSet<_>>();

    let mut matches = Vec::new();
    let mut seen_manifests = BTreeSet::new();
    let mut seen_roots = BTreeSet::new();
    for root in request
        .library_roots
        .iter()
        .map(|root| root.trim())
        .filter(|root| !root.is_empty())
        .take(32)
    {
        let root_key = root.replace('\\', "/").to_ascii_lowercase();
        if !seen_roots.insert(root_key) {
            continue;
        }
        let Ok(scan) = catalog::scan_demo_library_for(root) else {
            // A missing or temporarily unavailable secondary library should
            // not prevent the user from opening an otherwise valid demo.
            continue;
        };
        for entry in scan.entries {
            if !hashes.contains(&entry.demo_sha256.trim().to_ascii_lowercase()) {
                continue;
            }
            let manifest_key = entry.manifest_path.replace('\\', "/").to_ascii_lowercase();
            if seen_manifests.insert(manifest_key) {
                matches.push(entry);
            }
        }
    }
    matches.sort_by(|left, right| {
        right
            .modified_at_ms
            .cmp(&left.modified_at_ms)
            .then_with(|| left.manifest_path.cmp(&right.manifest_path))
    });
    let compressed = source.paths().any(|path| {
        path.file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.to_ascii_lowercase().ends_with(".dem.zst"))
    });

    Ok(PreflightDemoSourceDto {
        source_path: source_path.display().to_string(),
        demo_sha256,
        source_size_bytes: source_metadata.size_bytes.to_string(),
        source_modified_at_ms: source_metadata
            .modified
            .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX)),
        compressed,
        matches,
    })
}

fn refresh_metadata_entries_from_demo(
    demo_path: &Path,
    entries: &[catalog::LibraryEntryDto],
    failures: &mut Vec<String>,
) -> Result<usize, String> {
    let expected_hash = entries
        .first()
        .map(|entry| entry.demo_sha256.trim())
        .unwrap_or_default();
    let source = resolve_demo_source_for_sha256(demo_path, expected_hash)
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "source demo does not match the archive SHA-256".to_string())?;
    let conversions = entries
        .iter()
        .map(|entry| {
            let archive_root = Path::new(&entry.root);
            let revision =
                archive_info::read_matching_demo_info(archive_root, entry.demo_sha256.trim())
                    .map(|info| info.write_revision)
                    .unwrap_or(0);
            (
                preserved_conversion_for_manifest(
                    archive_root,
                    entry.demo_sha256.trim(),
                    Path::new(&entry.manifest_path),
                ),
                revision,
            )
        })
        .collect::<Vec<_>>();
    let parsed = read_demo_source_with_options(
        &source,
        ReadDemoOptions {
            collect_voice: false,
            collect_cosmetics: conversions
                .iter()
                .filter_map(|(conversion, _)| conversion.as_ref())
                .any(|conversion| conversion.cosmetics),
        },
    )
    .map_err(|error| error.to_string())?;
    if expected_hash.is_empty() || !parsed.demo_sha256.eq_ignore_ascii_case(expected_hash) {
        return Err(format!(
            "source demo changed while it was being read (expected {expected_hash}, got {})",
            parsed.demo_sha256
        ));
    }
    let browser = analyze_browser_demo(&parsed, AnalysisOptions::default());
    let source_metadata = source.metadata().ok();
    let mut updated = 0_usize;
    for (entry, (conversion, expected_info_revision)) in entries.iter().zip(conversions) {
        let archive_root = Path::new(&entry.root);
        let mut info = archive_info::DemoArchiveInfo::from_analysis(
            entry.demo_id.clone(),
            &parsed,
            &browser,
            source_metadata.and_then(source_metadata_modified_at_ms),
            source_metadata.map(|metadata| metadata.size_bytes),
            entry.abi,
            entry.format_version,
            "metadataRefresh",
        );
        restrict_player_cosmetic_details(
            &mut info.players,
            conversion
                .as_ref()
                .is_some_and(|conversion| conversion.cosmetics),
            conversion
                .as_ref()
                .is_some_and(|conversion| conversion.stickers),
            conversion
                .as_ref()
                .is_some_and(|conversion| conversion.charms),
        );
        info.conversion = conversion;
        match archive_info::write_demo_source_and_info_if_revision(
            archive_root,
            entry.demo_sha256.trim(),
            source.primary_path(),
            &info,
            expected_info_revision,
        ) {
            Ok(_) => updated += 1,
            Err(error) => failures.push(format!("{}: {error}", entry.manifest_path)),
        }
    }
    Ok(updated)
}

fn import_archives_for(request: &ImportArchivesRequest) -> CommandResult<ImportArchivesDto> {
    let source_root = request.source_root.trim();
    let library_root = PathBuf::from(request.library_root.trim());
    if source_root.is_empty() {
        return Err(CommandErrorDto::new(
            "archive_import_source_invalid",
            "Choose a folder containing existing DemoTracer archives.",
        ));
    }
    if library_root.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "library_root_invalid",
            "Choose the main DemoTracer library before importing archives.",
        ));
    }

    let source_scan = catalog::scan_demo_library_for(source_root)?;
    fs::create_dir_all(&library_root).map_err(|error| {
        CommandErrorDto::at_path(
            "library_root_create_failed",
            error.to_string(),
            &library_root,
        )
    })?;
    let library_metadata = fs::symlink_metadata(&library_root).map_err(|error| {
        CommandErrorDto::at_path(
            "library_root_inspect_failed",
            error.to_string(),
            &library_root,
        )
    })?;
    if !library_metadata.is_dir() || catalog::is_symlink_or_reparse(&library_metadata) {
        return Err(CommandErrorDto::at_path(
            "library_root_invalid",
            "The main library must be a normal local folder.",
            &library_root,
        ));
    }
    let canonical_library = canonicalize_public_path(&library_root).map_err(|error| {
        CommandErrorDto::at_path(
            "library_root_inspect_failed",
            error.to_string(),
            &library_root,
        )
    })?;
    let target_scan = catalog::scan_demo_library_for(&library_root.display().to_string())?;
    let mut known_hashes = target_scan
        .entries
        .iter()
        .map(|entry| entry.demo_sha256.trim().to_ascii_lowercase())
        .filter(|hash| !hash.is_empty())
        .collect::<BTreeSet<_>>();

    let archives_found = source_scan.entries.len() + source_scan.skipped.len();
    let mut archives_imported = 0_usize;
    let mut duplicates_skipped = 0_usize;
    let mut archives_rejected = source_scan.skipped.len();
    let mut failures = source_scan
        .skipped
        .into_iter()
        .map(|item| format!("{}: {}", item.path, item.message))
        .collect::<Vec<_>>();

    for entry in source_scan.entries {
        let source_archive_root = PathBuf::from(&entry.root);
        let canonical_source = match canonicalize_public_path(&source_archive_root) {
            Ok(path) => path,
            Err(error) => {
                archives_rejected += 1;
                failures.push(format!("{}: {error}", source_archive_root.display()));
                continue;
            }
        };
        if canonical_source.starts_with(&canonical_library) {
            duplicates_skipped += 1;
            continue;
        }

        let demo_hash = entry.demo_sha256.trim().to_ascii_lowercase();
        if demo_hash.len() != 64 || !demo_hash.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            archives_rejected += 1;
            failures.push(format!(
                "{}: manifest has no valid full demo SHA-256",
                entry.manifest_path
            ));
            continue;
        }
        if known_hashes.contains(&demo_hash) {
            duplicates_skipped += 1;
            continue;
        }

        let strict_source = match read_manifest_for(&entry.manifest_path) {
            Ok(archive) if archive.playable => archive,
            Ok(_) => {
                archives_rejected += 1;
                failures.push(format!(
                    "{}: archive has no fully playable round",
                    entry.manifest_path
                ));
                continue;
            }
            Err(error) => {
                archives_rejected += 1;
                failures.push(format!("{}: {}", entry.manifest_path, error.message));
                continue;
            }
        };
        if !strict_source.demo_sha256.eq_ignore_ascii_case(&demo_hash) {
            archives_rejected += 1;
            failures.push(format!(
                "{}: strict manifest hash disagrees with the library index",
                entry.manifest_path
            ));
            continue;
        }

        let display_name = entry
            .display_name
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
            .or_else(|| {
                Path::new(&entry.demo_path)
                    .file_stem()
                    .and_then(|stem| stem.to_str())
                    .map(str::to_string)
            })
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| entry.demo_id.clone());
        let Some((map_dir, final_root)) = choose_import_destination(
            &canonical_library,
            &strict_source.map,
            &display_name,
            &demo_hash,
        )?
        else {
            duplicates_skipped += 1;
            known_hashes.insert(demo_hash);
            continue;
        };

        let mut file_ops = RealOutputFileOps;
        let imported = run_output_transaction(
            &map_dir,
            &final_root,
            OverwriteModeDto::Deny,
            &mut file_ops,
            |staging_root| {
                copy_archive_tree(&source_archive_root, staging_root)?;
                let copied_manifest = staging_root.join("manifest.json");
                let copied = read_manifest_for(&copied_manifest.display().to_string())?;
                if !copied.playable || !copied.demo_sha256.eq_ignore_ascii_case(&demo_hash) {
                    return Err(CommandErrorDto::at_path(
                        "archive_import_validation_failed",
                        "The copied archive failed strict playback or identity validation.",
                        copied_manifest,
                    ));
                }
                Ok(())
            },
        );
        match imported {
            Ok(transaction) => {
                archives_imported += 1;
                known_hashes.insert(demo_hash);
                if let Some(warning) = transaction.backup_cleanup_warning {
                    failures.push(warning);
                }
            }
            Err(error) => {
                archives_rejected += 1;
                failures.push(format!("{}: {}", entry.manifest_path, error.message));
            }
        }
    }

    failures.truncate(32);
    Ok(ImportArchivesDto {
        archives_found,
        archives_imported,
        duplicates_skipped,
        archives_rejected,
        failures,
    })
}

fn choose_import_destination(
    library_root: &Path,
    map: &str,
    display_name: &str,
    demo_hash: &str,
) -> CommandResult<Option<(PathBuf, PathBuf)>> {
    let map_dir = checked_library_map_directory(library_root, map)?;
    for hash_len in [12_usize, 16, 24, 64] {
        let directory_name =
            archive_info::archive_directory_name_from_parts(display_name, demo_hash, hash_len);
        let final_root = map_dir.join(directory_name);
        let backup_root = output_backup_root(&map_dir, &final_root)?;
        let final_metadata = path_metadata(&final_root).map_err(|error| {
            CommandErrorDto::at_path("archive_import_path_failed", error.to_string(), &final_root)
        })?;
        let backup_metadata = path_metadata(&backup_root).map_err(|error| {
            CommandErrorDto::at_path(
                "archive_import_path_failed",
                error.to_string(),
                &backup_root,
            )
        })?;
        if final_metadata.is_none() && backup_metadata.is_none() {
            return Ok(Some((map_dir, final_root)));
        }
        if let Ok(existing) = catalog::summarize_manifest(&final_root.join("manifest.json")) {
            if existing.demo_sha256.eq_ignore_ascii_case(demo_hash) {
                return Ok(None);
            }
        }
    }
    Err(CommandErrorDto::at_path(
        "archive_import_path_collision",
        "Every safe hash-based destination is occupied by different content.",
        map_dir,
    ))
}

fn copy_archive_tree(source_root: &Path, destination_root: &Path) -> CommandResult<()> {
    const MAX_ARCHIVE_COPY_DEPTH: usize = 16;
    const MAX_ARCHIVE_COPY_FILES: usize = 32_768;
    const MAX_ARCHIVE_COPY_BYTES: u64 = 128 * 1024 * 1024 * 1024;

    let canonical_source = fs::canonicalize(source_root).map_err(|error| {
        CommandErrorDto::at_path(
            "archive_import_source_failed",
            error.to_string(),
            source_root,
        )
    })?;
    let canonical_destination = fs::canonicalize(destination_root).map_err(|error| {
        CommandErrorDto::at_path(
            "archive_import_destination_failed",
            error.to_string(),
            destination_root,
        )
    })?;
    if canonical_destination.starts_with(&canonical_source) {
        return Err(CommandErrorDto::at_path(
            "archive_import_recursive_destination",
            "The main library cannot be placed inside an archive being imported.",
            destination_root,
        ));
    }

    let mut pending = vec![(canonical_source, canonical_destination, 0_usize)];
    let mut file_count = 0_usize;
    let mut total_bytes = 0_u64;
    while let Some((source_dir, destination_dir, depth)) = pending.pop() {
        let mut entries = fs::read_dir(&source_dir)
            .map_err(|error| {
                CommandErrorDto::at_path(
                    "archive_import_read_failed",
                    error.to_string(),
                    &source_dir,
                )
            })?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|error| {
                CommandErrorDto::at_path(
                    "archive_import_read_failed",
                    error.to_string(),
                    &source_dir,
                )
            })?;
        entries.sort_by_key(fs::DirEntry::file_name);
        for entry in entries {
            let name = entry.file_name();
            let name_text = name.to_string_lossy();
            if name_text.eq_ignore_ascii_case(OUTPUT_COMPLETION_MARKER)
                || (name_text.starts_with('.')
                    && (name_text.contains(".tmp.") || name_text.contains(".backup")))
            {
                continue;
            }
            let source_path = entry.path();
            let destination_path = destination_dir.join(&name);
            let metadata = fs::symlink_metadata(&source_path).map_err(|error| {
                CommandErrorDto::at_path(
                    "archive_import_inspect_failed",
                    error.to_string(),
                    &source_path,
                )
            })?;
            if catalog::is_symlink_or_reparse(&metadata) {
                return Err(CommandErrorDto::at_path(
                    "archive_import_reparse_point",
                    "Archive import refuses symbolic links, junctions, and reparse points.",
                    source_path,
                ));
            }
            if metadata.is_dir() {
                if depth >= MAX_ARCHIVE_COPY_DEPTH {
                    return Err(CommandErrorDto::at_path(
                        "archive_import_depth_limit",
                        "Archive directory nesting exceeds the safety limit.",
                        source_path,
                    ));
                }
                fs::create_dir(&destination_path).map_err(|error| {
                    CommandErrorDto::at_path(
                        "archive_import_copy_failed",
                        error.to_string(),
                        &destination_path,
                    )
                })?;
                pending.push((source_path, destination_path, depth + 1));
            } else if metadata.is_file() {
                file_count += 1;
                total_bytes = total_bytes.saturating_add(metadata.len());
                if file_count > MAX_ARCHIVE_COPY_FILES || total_bytes > MAX_ARCHIVE_COPY_BYTES {
                    return Err(CommandErrorDto::at_path(
                        "archive_import_size_limit",
                        "Archive exceeds the safe copy size or file-count limit.",
                        source_path,
                    ));
                }
                let copied = fs::copy(&source_path, &destination_path).map_err(|error| {
                    CommandErrorDto::at_path(
                        "archive_import_copy_failed",
                        error.to_string(),
                        &source_path,
                    )
                })?;
                if copied != metadata.len() {
                    return Err(CommandErrorDto::at_path(
                        "archive_import_copy_changed",
                        "A source file changed while the archive was being copied.",
                        source_path,
                    ));
                }
            } else {
                return Err(CommandErrorDto::at_path(
                    "archive_import_special_file",
                    "Archive import only accepts normal files and folders.",
                    source_path,
                ));
            }
        }
    }
    Ok(())
}

fn collect_demo_files(root: &Path) -> CommandResult<Vec<PathBuf>> {
    const MAX_DEMO_SCAN_DEPTH: usize = 8;
    const MAX_DEMO_FILES: usize = 4096;
    let metadata = fs::symlink_metadata(root).map_err(|error| {
        CommandErrorDto::at_path("demo_source_root_invalid", error.to_string(), root)
    })?;
    if !metadata.is_dir() || catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "demo_source_root_invalid",
            "Choose a normal folder containing original .dem or .dem.zst files.",
            root,
        ));
    }

    let mut pending = vec![(root.to_path_buf(), 0_usize)];
    let mut demos = Vec::new();
    while let Some((directory, depth)) = pending.pop() {
        let mut paths = fs::read_dir(&directory)
            .map_err(|error| {
                CommandErrorDto::at_path("demo_source_scan_failed", error.to_string(), &directory)
            })?
            .filter_map(Result::ok)
            .map(|entry| entry.path())
            .collect::<Vec<_>>();
        paths.sort();
        for path in paths {
            let Ok(metadata) = fs::symlink_metadata(&path) else {
                continue;
            };
            if catalog::is_symlink_or_reparse(&metadata) {
                continue;
            }
            if metadata.is_dir() && depth < MAX_DEMO_SCAN_DEPTH {
                pending.push((path, depth + 1));
            } else if metadata.is_file() && is_supported_demo_path(&path) {
                demos.push(path);
                if demos.len() >= MAX_DEMO_FILES {
                    return Ok(demos);
                }
            }
        }
    }
    demos.sort();
    Ok(demos)
}

fn resolve_validated_demo_source(path: &Path) -> CommandResult<DemoSourceSet> {
    let mut source = resolve_demo_source(path)
        .map_err(|error| CommandErrorDto::from_core("demo_source_resolve_failed", error))?;
    for part in &mut source.parts {
        part.path = validate_demo_path(&part.path.display().to_string())?;
    }
    Ok(source)
}

fn resolve_validated_demo_source_for_sha256(
    path: &Path,
    expected_sha256: &str,
) -> CommandResult<Option<DemoSourceSet>> {
    let requested_path = validate_demo_path(&path.display().to_string())?;
    let Some(mut source) = resolve_demo_source_for_sha256(&requested_path, expected_sha256)
        .map_err(|error| CommandErrorDto::from_core("demo_source_resolve_failed", error))?
    else {
        return Ok(None);
    };
    for part in &mut source.parts {
        part.path = validate_demo_path(&part.path.display().to_string())?;
    }
    Ok(Some(source))
}

fn validate_demo_path(value: &str) -> CommandResult<PathBuf> {
    let path = PathBuf::from(value.trim());
    let is_demo = path.is_file() && is_supported_demo_path(&path);
    if !is_demo {
        return Err(CommandErrorDto::at_path(
            "invalid_demo_path",
            "Choose an existing CS2 .dem or .dem.zst file.",
            &path,
        ));
    }
    Ok(path)
}

fn read_manifest_for(value: &str) -> CommandResult<ManifestArchiveDto> {
    let manifest_path = validate_manifest_input_path(value)?;
    let root = manifest_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .to_path_buf();
    let canonical_root = fs::canonicalize(&root).map_err(|error| {
        CommandErrorDto::at_path("manifest_root_unavailable", error.to_string(), &root)
    })?;
    let text = fs::read_to_string(&manifest_path).map_err(|error| {
        CommandErrorDto::at_path("manifest_read_failed", error.to_string(), &manifest_path)
    })?;
    let mut manifest: ManifestArchiveWire = serde_json::from_str(&text).map_err(|error| {
        CommandErrorDto::at_path("manifest_invalid_json", error.to_string(), &manifest_path)
    })?;

    if manifest.files.is_none() {
        return Err(CommandErrorDto::at_path(
            "manifest_schema_invalid",
            "The selected JSON is not a per-demo replay manifest (files[] is missing).",
            &manifest_path,
        ));
    }

    let declared_rounds = manifest.rounds.take().unwrap_or_default();
    let declared_files = manifest.files.take().unwrap_or_default();
    let declared_avatars = manifest.avatar_overrides.take().unwrap_or_default();
    let total_files = declared_files.len();
    let abi = manifest.abi.unwrap_or(0);
    let declared_dtr_format_version = manifest.dtr_format_version.unwrap_or(0);
    let format_version = if declared_dtr_format_version != 0 {
        declared_dtr_format_version
    } else {
        manifest.format_version.unwrap_or(0)
    };
    let abi_supported = abi == 0 || (MIN_SUPPORTED_MANIFEST_ABI..=DEMOTRACER_ABI).contains(&abi);
    let format_supported = format_version == 0
        || (MIN_SUPPORTED_DTR_FORMAT_VERSION..=DTR_FORMAT_VERSION).contains(&format_version);
    let version_supported = abi_supported && format_supported;
    let compatibility = if !version_supported {
        "unsupported"
    } else if abi == 0 || format_version == 0 {
        "legacy"
    } else if abi == DEMOTRACER_ABI && format_version == DTR_FORMAT_VERSION {
        "current"
    } else {
        "supported"
    }
    .to_string();

    let mut issues = Vec::new();
    let mut fatal_metadata_issue = false;
    let mut fatal_manifest_structure = false;
    if manifest.abi.is_none() || abi == 0 {
        issues.push(ManifestIssueDto::warning(
            "manifest_abi_missing",
            "The manifest has no explicit ABI and is treated as legacy.",
        ));
    } else if !abi_supported {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_abi_unsupported",
            format!(
                "Manifest ABI {abi} is unsupported; expected {MIN_SUPPORTED_MANIFEST_ABI}..{DEMOTRACER_ABI}."
            ),
        ));
    }
    if manifest.dtr_format_version.is_none() && manifest.format_version.is_none()
        || format_version == 0
    {
        issues.push(ManifestIssueDto::warning(
            "manifest_format_missing",
            "The manifest has no explicit replay format version and is treated as legacy.",
        ));
    } else if !format_supported {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_format_unsupported",
            format!(
                "Replay format {format_version} is unsupported; expected {MIN_SUPPORTED_DTR_FORMAT_VERSION}..{DTR_FORMAT_VERSION}."
            ),
        ));
    }
    if manifest.map.trim().is_empty() {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_map_missing",
            "The manifest map is required for playback.",
        ));
    }
    let tick_rate = manifest.tick_rate.unwrap_or(0.0);
    if !tick_rate.is_finite() || tick_rate <= 0.0 {
        issues.push(ManifestIssueDto::warning(
            "manifest_tick_rate_invalid",
            "The manifest does not contain a positive tick rate.",
        ));
    }
    if total_files == 0 {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_files_empty",
            "The manifest does not contain replay files.",
        ));
    }
    let manifest_display = manifest_path.display().to_string();
    if manifest_display.contains(['\r', '\n']) {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_path_not_console_safe",
            "The manifest path contains a line break and cannot be used in a server command.",
        ));
    }

    let mut metadata_by_round = BTreeMap::new();
    for metadata in declared_rounds {
        let Some(round) = metadata.round else {
            issues.push(ManifestIssueDto::warning(
                "manifest_round_number_missing",
                "A round metadata entry has no source round number.",
            ));
            continue;
        };
        if round > i32::MAX as u32 {
            fatal_manifest_structure = true;
            issues.push(ManifestIssueDto::error(
                "manifest_round_out_of_range",
                format!("Round {round} exceeds the server-supported integer range."),
            ));
            continue;
        }
        if metadata_by_round.contains_key(&round) {
            issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_duplicate",
                    format!("Round {round} has duplicate metadata entries."),
                )
                .at_round(round),
            );
            continue;
        }
        metadata_by_round.insert(round, metadata);
    }

    let mut rounds_by_id = BTreeMap::<u32, ManifestRoundAccumulator>::new();
    let mut playable_files = Vec::new();
    let mut replay_bytes = 0_u64;
    let mut seen_declared_paths = BTreeSet::new();
    let mut seen_canonical_paths = BTreeSet::new();

    let mut seen_avatar_steam_ids = BTreeSet::new();
    for (index, avatar) in declared_avatars.into_iter().enumerate() {
        let steam_id = avatar.steam_id.unwrap_or(0);
        let path = avatar.path.unwrap_or_default();
        if steam_id == 0 {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_avatar_steam_id_invalid",
                    format!("Avatar override {index} steam_id must be a non-zero integer."),
                )
                .at_file(path.clone(), None),
            );
        } else if !seen_avatar_steam_ids.insert(steam_id) {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_avatar_steam_id_duplicate",
                    format!("Avatar override steam_id {steam_id} is duplicated."),
                )
                .at_file(path.clone(), None),
            );
        }
        if let Err((code, message)) = validate_manifest_child_path(&path) {
            fatal_manifest_structure = true;
            issues.push(ManifestIssueDto::error(code, message).at_file(path, None));
        }
    }

    for (index, file) in declared_files.into_iter().enumerate() {
        let Some(round) = file.round else {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_round_missing",
                    format!("Manifest file {index} has no source round."),
                )
                .at_file(format!("files[{index}]"), None),
            );
            continue;
        };
        if round > i32::MAX as u32 {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_round_out_of_range",
                    format!("Manifest file {index} round {round} exceeds the server-supported integer range."),
                )
                .at_file(file.path.clone().unwrap_or_default(), None),
            );
            continue;
        }
        let accumulator = rounds_by_id.entry(round).or_default();
        accumulator.files += 1;
        let path = file.path.unwrap_or_default();
        let side = file.side.unwrap_or_default().to_ascii_lowercase();
        let mut valid = true;
        if side == "t" {
            accumulator.t_files += 1;
        } else if side == "ct" {
            accumulator.ct_files += 1;
        } else {
            valid = false;
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_side_invalid",
                    format!("Replay side must be t or ct, got {side:?}."),
                )
                .at_file(path.clone(), Some(round)),
            );
        }

        let declared_key = normalized_manifest_path_key(&path);
        if declared_key.is_empty() || !seen_declared_paths.insert(declared_key) {
            valid = false;
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_path_duplicate",
                    "Replay file paths must be non-empty and unique.",
                )
                .at_file(path.clone(), Some(round)),
            );
        }

        let resolved = match resolve_manifest_dtr_path(&root, &canonical_root, &path) {
            Ok(resolved) => {
                let canonical_key = normalized_manifest_path_key(&resolved.display().to_string());
                if !seen_canonical_paths.insert(canonical_key) {
                    valid = false;
                    fatal_manifest_structure = true;
                    issues.push(
                        ManifestIssueDto::error(
                            "manifest_file_target_duplicate",
                            "Multiple manifest entries resolve to the same replay file.",
                        )
                        .at_file(path.clone(), Some(round)),
                    );
                }
                Some(resolved)
            }
            Err((code, message)) => {
                valid = false;
                if !matches!(code, "manifest_file_missing" | "manifest_file_not_regular") {
                    fatal_manifest_structure = true;
                }
                issues.push(
                    ManifestIssueDto::error(code, message).at_file(path.clone(), Some(round)),
                );
                None
            }
        };

        let steam_id = file.steam_id.unwrap_or(0);
        if steam_id == 0 {
            valid = false;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_steam_id_invalid",
                    "Replay file steam_id must be a non-zero integer.",
                )
                .at_file(path.clone(), Some(round)),
            );
        }
        if valid {
            if let Some(resolved) = resolved.as_ref() {
                if let Err(message) =
                    validate_manifest_dtr(resolved, round, &side, steam_id, manifest.map.trim())
                {
                    valid = false;
                    issues.push(
                        ManifestIssueDto::error("manifest_file_invalid_dtr", message)
                            .at_file(path.clone(), Some(round)),
                    );
                }
            }
        }
        if !valid || resolved.is_none() {
            continue;
        }

        accumulator.playable_files += 1;
        if side == "t" {
            accumulator.t_steam_ids.insert(steam_id);
        } else {
            accumulator.ct_steam_ids.insert(steam_id);
        }
        accumulator.ticks = accumulator.ticks.saturating_add(file.ticks);
        accumulator.subticks = accumulator.subticks.saturating_add(file.subticks);
        accumulator.hifi_events = accumulator
            .hifi_events
            .saturating_add(file.hifi_event_count);
        accumulator.inventory_snapshots = accumulator
            .inventory_snapshots
            .saturating_add(file.inventory_snapshot_count);
        replay_bytes = replay_bytes.saturating_add(
            resolved
                .as_ref()
                .and_then(|path| fs::metadata(path).ok())
                .map_or(0, |metadata| metadata.len()),
        );
        let cosmetics = file.cosmetics.as_ref();
        let has_cosmetics = cosmetics.is_some_and(|value| !value.is_empty());
        let has_stickers = cosmetics.is_some_and(ManifestCosmeticsWire::has_stickers);
        let has_charms = cosmetics.is_some_and(ManifestCosmeticsWire::has_charms);
        accumulator.cosmetic_files += usize::from(has_cosmetics);
        accumulator.sticker_files += usize::from(has_stickers);
        accumulator.charm_files += usize::from(has_charms);
        playable_files.push(PlayableManifestFile {
            round,
            side,
            steam_id,
            player_name: file
                .player_name
                .filter(|name| !name.is_empty())
                .unwrap_or_else(|| steam_id.to_string()),
            scoreboard: file.scoreboard.clone(),
            has_cosmetics,
            has_stickers,
            has_charms,
        });
    }

    for (&round, accumulator) in &rounds_by_id {
        match metadata_by_round
            .get(&round)
            .and_then(|metadata| metadata.files)
        {
            Some(files) if files != accumulator.files => issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_file_count_mismatch",
                    format!(
                        "Round {round} metadata reports {files} files, but files[] contains {}.",
                        accumulator.files
                    ),
                )
                .at_round(round),
            ),
            None => issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_metadata_missing",
                    format!("Round {round} has replay files but incomplete round metadata."),
                )
                .at_round(round),
            ),
            _ => {}
        }
    }
    for &round in metadata_by_round.keys() {
        if !rounds_by_id.contains_key(&round) {
            issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_without_files",
                    format!("Round {round} metadata has no replay files and is not selectable."),
                )
                .at_round(round),
            );
        }
    }

    let round_ids = rounds_by_id.keys().copied().collect::<Vec<_>>();
    let voice_rounds = collect_manifest_voice_rounds(&root, &canonical_root, &round_ids);
    let cosmetic_files = playable_files
        .iter()
        .filter(|file| file.has_cosmetics)
        .count();
    let sticker_files = playable_files
        .iter()
        .filter(|file| file.has_stickers)
        .count();
    let charm_files = playable_files.iter().filter(|file| file.has_charms).count();
    let cosmetic_preset = if cosmetic_files == 0 {
        None
    } else if sticker_files > 0 || charm_files > 0 {
        Some("full".to_string())
    } else {
        Some("basic".to_string())
    };

    let mut rounds = rounds_by_id
        .iter()
        .map(|(&round, accumulator)| {
            let metadata = metadata_by_round.get(&round);
            let available = version_supported
                && !fatal_metadata_issue
                && !fatal_manifest_structure
                && accumulator.files > 0
                && accumulator.playable_files == accumulator.files;
            ManifestArchiveRoundDto {
                round,
                files: accumulator.files,
                t_files: accumulator.t_files,
                ct_files: accumulator.ct_files,
                t_steam_ids: accumulator.t_steam_ids.iter().map(u64::to_string).collect(),
                ct_steam_ids: accumulator
                    .ct_steam_ids
                    .iter()
                    .map(u64::to_string)
                    .collect(),
                cosmetic_files: accumulator.cosmetic_files,
                sticker_files: accumulator.sticker_files,
                charm_files: accumulator.charm_files,
                duration_seconds: metadata.and_then(|value| value.duration_seconds),
                pistol_round: metadata.and_then(|value| value.pistol_round),
                cut_reason: metadata.and_then(|value| value.cut_reason.clone()),
                t_economy: metadata
                    .and_then(|value| value.t_economy.clone())
                    .map(Into::into),
                ct_economy: metadata
                    .and_then(|value| value.ct_economy.clone())
                    .map(Into::into),
                scoreboard: metadata
                    .and_then(|value| value.scoreboard.clone())
                    .map(Into::into),
                bomb_planted_seconds: metadata
                    .and_then(|value| value.bomb_planted_seconds_after_live),
                ticks: accumulator.ticks,
                subticks: accumulator.subticks,
                hifi_events: accumulator.hifi_events,
                inventory_snapshots: accumulator.inventory_snapshots,
                sequence_length: 0,
                available,
                commands: build_commands(
                    &manifest_path,
                    Some(round),
                    voice_rounds.len(),
                    cosmetic_preset.as_deref(),
                ),
            }
        })
        .collect::<Vec<_>>();
    if let (Some(first_round), Some(last_round)) = (
        rounds.first().map(|round| round.round),
        rounds.last().map(|round| round.round),
    ) {
        let mut rounds_by_number = rounds
            .into_iter()
            .map(|round| (round.round, round))
            .collect::<BTreeMap<_, _>>();
        rounds = (first_round..=last_round)
            .map(|round| {
                if let Some(existing) = rounds_by_number.remove(&round) {
                    return existing;
                }

                issues.push(
                    ManifestIssueDto::warning(
                        "manifest_round_gap",
                        format!(
                            "Round {round} has no replay files; sequence playback cannot cross this source-round gap."
                        ),
                    )
                    .at_round(round),
                );
                let metadata = metadata_by_round.get(&round);
                ManifestArchiveRoundDto {
                    round,
                    files: 0,
                    t_files: 0,
                    ct_files: 0,
                    t_steam_ids: Vec::new(),
                    ct_steam_ids: Vec::new(),
                    cosmetic_files: 0,
                    sticker_files: 0,
                    charm_files: 0,
                    duration_seconds: metadata.and_then(|value| value.duration_seconds),
                    pistol_round: metadata.and_then(|value| value.pistol_round),
                    cut_reason: metadata.and_then(|value| value.cut_reason.clone()),
                    t_economy: metadata
                        .and_then(|value| value.t_economy.clone())
                        .map(Into::into),
                    ct_economy: metadata
                        .and_then(|value| value.ct_economy.clone())
                        .map(Into::into),
                    scoreboard: metadata
                        .and_then(|value| value.scoreboard.clone())
                        .map(Into::into),
                    bomb_planted_seconds: metadata
                        .and_then(|value| value.bomb_planted_seconds_after_live),
                    ticks: 0,
                    subticks: 0,
                    hifi_events: 0,
                    inventory_snapshots: 0,
                    sequence_length: 0,
                    available: false,
                    commands: build_commands(
                        &manifest_path,
                        Some(round),
                        voice_rounds.len(),
                        cosmetic_preset.as_deref(),
                    ),
                }
            })
            .collect();
    }
    let sequence_lengths = (0..rounds.len())
        .map(|index| {
            let suffix = &rounds[index..];
            if suffix.iter().all(|round| round.available) {
                suffix.len()
            } else {
                0
            }
        })
        .collect::<Vec<_>>();
    for (round, sequence_length) in rounds.iter_mut().zip(sequence_lengths) {
        round.sequence_length = sequence_length;
    }

    let demo_path = if manifest.demo_path.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_path_missing",
            "The manifest does not identify its source demo.",
        ));
        String::new()
    } else {
        public_demo_path(&manifest.demo_path)
    };
    let demo_id = if manifest.demo_id.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_id_missing",
            "The manifest does not contain a demo ID; the folder name is used for display.",
        ));
        root.file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default()
    } else {
        manifest.demo_id
    };
    if manifest.demo_sha256.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_hash_missing",
            "The manifest does not contain the source demo hash.",
        ));
    }
    let playable = rounds.iter().any(|round| round.available);
    let mut display_name = Path::new(&demo_path)
        .file_stem()
        .map(|value| value.to_string_lossy().into_owned())
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| demo_id.clone());
    let mut metadata_status = "missing".to_string();
    let mut source_path = archive_info::read_demo_source_path(&root, &manifest.demo_sha256)
        .or_else(|| {
            archive_info::read_matching_demo_info(&root, &manifest.demo_sha256)
                .and_then(|info| info.source_file_path.clone())
        });
    let mut source_modified_at_ms = None;
    let mut duration_seconds = None;
    let mut demo_patch_version = None;
    let mut demo_version_name = None;
    let mut demo_source = None;
    let mut score = None;
    let mut round_outcomes = Vec::new();
    let mut friendly_fire = BrowserFriendlyFireSummary::default();
    let mut converter_version = None;
    let mut voice_requested = None;
    let mut cosmetic_requested = None;
    let mut sticker_requested = None;
    let mut charm_requested = None;
    let mut players = summarize_manifest_players(&playable_files);
    match archive_info::read_demo_info(&root, &manifest.demo_sha256) {
        archive_info::DemoInfoRead::Current(info) => {
            let current_manifest_sha256 = sha256_hex(text.as_bytes());
            let manifest_hash_matches = info
                .conversion
                .as_ref()
                .and_then(|conversion| conversion.manifest_sha256.as_deref())
                .is_none_or(|expected| expected.eq_ignore_ascii_case(&current_manifest_sha256));
            let metadata_matches = info.map.eq_ignore_ascii_case(&manifest.map)
                && info.manifest_abi == abi
                && info.dtr_format_version == format_version
                && manifest_hash_matches;
            if metadata_matches {
                metadata_status = "current".to_string();
                display_name = info.display_name.clone();
                if source_path.is_none() && info.source_file_path.is_some() {
                    source_path = info.source_file_path.clone();
                }
                source_modified_at_ms = info.source_file_modified_at_ms;
                duration_seconds = Some(info.duration_seconds);
                demo_patch_version = info.demo_patch_version;
                demo_version_name = info.demo_version_name.clone();
                demo_source = info.demo_source.clone();
                score = info.score.clone();
                round_outcomes = info.round_outcomes.clone();
                friendly_fire = info.friendly_fire.clone();
                let bound_conversion = info.conversion.as_ref().filter(|conversion| {
                    conversion
                        .manifest_sha256
                        .as_deref()
                        .is_some_and(|expected| {
                            expected.eq_ignore_ascii_case(&current_manifest_sha256)
                        })
                });
                if let Some(conversion) = bound_conversion {
                    voice_requested = Some(conversion.voice);
                    cosmetic_requested = Some(conversion.cosmetics);
                    sticker_requested = Some(conversion.stickers);
                    charm_requested = Some(conversion.charms);
                }
                converter_version =
                    bound_conversion.map(|conversion| conversion.converter_version.clone());
                players = overlay_archive_players(players, &info.players);
            } else {
                metadata_status = "stale".to_string();
            }
        }
        archive_info::DemoInfoRead::Missing => {}
        archive_info::DemoInfoRead::Stale => metadata_status = "stale".to_string(),
        archive_info::DemoInfoRead::Invalid => metadata_status = "invalid".to_string(),
    }

    Ok(ManifestArchiveDto {
        root: root.display().to_string(),
        manifest_path: manifest_display,
        demo_path,
        demo_id,
        demo_sha256: manifest.demo_sha256,
        map: manifest.map,
        tick_rate,
        abi,
        format_version,
        compatibility,
        total_files,
        playable_files: playable_files.len(),
        output_bytes: replay_bytes.to_string(),
        players,
        voice: ManifestArchiveVoiceDto {
            requested: voice_requested,
            sidecars: voice_rounds.len(),
            rounds: voice_rounds,
        },
        cosmetics: CosmeticSummaryDto {
            requested: cosmetic_requested,
            sticker_requested,
            charm_requested,
            files: cosmetic_files,
            sticker_files,
            charm_files,
            preset: cosmetic_preset,
        },
        rounds,
        issues,
        playable,
        display_name,
        note: archive_info::read_archive_note(&root),
        metadata_status,
        source_path,
        source_modified_at_ms,
        duration_seconds,
        demo_patch_version,
        demo_version_name,
        demo_source,
        score,
        round_outcomes,
        friendly_fire,
        converter_version,
    })
}

fn save_archive_note_for(request: &SaveArchiveNoteRequest) -> CommandResult<SaveArchiveNoteDto> {
    let manifest_path = validate_manifest_input_path(&request.manifest_path)?;
    catalog::summarize_manifest(&manifest_path).map_err(|_| {
        CommandErrorDto::at_path(
            "archive_note_manifest_invalid",
            "This archive could not be opened, so its note was not changed.",
            &manifest_path,
        )
    })?;
    let root = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let note = archive_info::write_archive_note(root, &request.note).map_err(|_| {
        CommandErrorDto::at_path(
            "archive_note_save_failed",
            "The note could not be saved. Check that the archive folder is writable.",
            &manifest_path,
        )
    })?;
    Ok(SaveArchiveNoteDto {
        manifest_path: manifest_path.display().to_string(),
        note,
    })
}

fn validate_manifest_input_path(value: &str) -> CommandResult<PathBuf> {
    let value = value.trim();
    if value.is_empty() {
        return Err(CommandErrorDto::new(
            "invalid_manifest_path",
            "Choose a DemoTracer manifest JSON file.",
        ));
    }
    let input = PathBuf::from(value);
    let path = if input.is_absolute() {
        normalize_display_path(&input)
    } else {
        let current = std::env::current_dir()
            .map_err(|error| CommandErrorDto::new("manifest_path_failed", error.to_string()))?;
        normalize_display_path(&current.join(input))
    };
    let is_json = path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("json"));
    let metadata = fs::symlink_metadata(&path).map_err(|error| {
        CommandErrorDto::at_path("manifest_not_found", error.to_string(), &path)
    })?;
    if !is_json || !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(CommandErrorDto::at_path(
            "invalid_manifest_path",
            "Choose an existing regular .json manifest file.",
            &path,
        ));
    }
    if metadata.len() > MAX_MANIFEST_BYTES {
        return Err(CommandErrorDto::at_path(
            "manifest_too_large",
            format!(
                "Manifest exceeds the {} MiB safety limit.",
                MAX_MANIFEST_BYTES / 1024 / 1024
            ),
            &path,
        ));
    }
    Ok(path)
}

fn normalize_display_path(path: &Path) -> PathBuf {
    let mut output = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                if matches!(
                    output.components().next_back(),
                    Some(std::path::Component::Normal(_))
                ) {
                    output.pop();
                } else {
                    output.push("..");
                }
            }
            other => output.push(other.as_os_str()),
        }
    }
    output
}

fn canonicalize_public_path(path: &Path) -> std::io::Result<PathBuf> {
    let canonical = fs::canonicalize(path)?;
    #[cfg(windows)]
    {
        use std::ffi::OsString;
        use std::os::windows::ffi::{OsStrExt, OsStringExt};

        const VERBATIM_PREFIX: &[u16] = &[92, 92, 63, 92];
        const VERBATIM_UNC_PREFIX: &[u16] = &[92, 92, 63, 92, 85, 78, 67, 92];
        let wide = canonical.as_os_str().encode_wide().collect::<Vec<_>>();
        if wide.starts_with(VERBATIM_UNC_PREFIX) {
            let mut normal = vec![92, 92];
            normal.extend_from_slice(&wide[VERBATIM_UNC_PREFIX.len()..]);
            return Ok(PathBuf::from(OsString::from_wide(&normal)));
        }
        if wide.starts_with(VERBATIM_PREFIX) {
            return Ok(PathBuf::from(OsString::from_wide(
                &wide[VERBATIM_PREFIX.len()..],
            )));
        }
    }
    Ok(canonical)
}

fn normalized_manifest_path_key(value: &str) -> String {
    value
        .trim()
        .replace('\\', "/")
        .trim_start_matches("./")
        .to_ascii_lowercase()
}

fn validate_manifest_child_path(value: &str) -> Result<PathBuf, (&'static str, String)> {
    if value.trim().is_empty() {
        return Err((
            "manifest_file_path_empty",
            "Manifest child path is empty.".to_string(),
        ));
    }
    if value.starts_with('/')
        || value.starts_with('\\')
        || value.contains(':')
        || Path::new(value).is_absolute()
    {
        return Err((
            "manifest_file_path_absolute",
            "Manifest child path must be relative to the manifest folder.".to_string(),
        ));
    }
    let normalized = value.replace('\\', std::path::MAIN_SEPARATOR_STR);
    let relative = PathBuf::from(normalized);
    if relative.components().any(|component| {
        matches!(
            component,
            std::path::Component::ParentDir
                | std::path::Component::RootDir
                | std::path::Component::Prefix(_)
        )
    }) {
        return Err((
            "manifest_file_path_escape",
            "Manifest child path escapes the manifest folder.".to_string(),
        ));
    }
    Ok(relative)
}

fn resolve_manifest_dtr_path(
    root: &Path,
    canonical_root: &Path,
    value: &str,
) -> Result<PathBuf, (&'static str, String)> {
    let relative = validate_manifest_child_path(value)?;
    if !relative
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("dtr"))
    {
        return Err((
            "manifest_file_extension_invalid",
            "Replay file path must use the .dtr extension.".to_string(),
        ));
    }
    let candidate = root.join(&relative);
    let canonical = fs::canonicalize(&candidate).map_err(|error| {
        (
            "manifest_file_missing",
            format!("Replay file is missing or unreadable: {error}"),
        )
    })?;
    if !canonical.starts_with(canonical_root) {
        return Err((
            "manifest_file_path_escape",
            "Replay file resolves outside the manifest folder.".to_string(),
        ));
    }
    if !canonical.is_file() {
        return Err((
            "manifest_file_not_regular",
            "Replay target is not a regular file.".to_string(),
        ));
    }
    Ok(canonical)
}

fn validate_manifest_dtr(
    path: &Path,
    round: u32,
    side: &str,
    steam_id: u64,
    map: &str,
) -> Result<(), String> {
    let recording = read_rec_file(path).map_err(|error| error.to_string())?;
    if recording.ticks.is_empty() {
        return Err("Replay file contains no ticks.".to_string());
    }
    if recording.header.round != round {
        return Err(format!(
            "Replay header round {} does not match manifest round {round}.",
            recording.header.round
        ));
    }
    let expected_side = if side.eq_ignore_ascii_case("t") { 2 } else { 3 };
    if recording.header.side != expected_side {
        return Err(format!(
            "Replay header side {} does not match manifest side {side}.",
            recording.header.side
        ));
    }
    if recording.header.steam_id != steam_id {
        return Err(format!(
            "Replay header SteamID {} does not match manifest SteamID {steam_id}.",
            recording.header.steam_id
        ));
    }
    if !map.is_empty() && !recording.header.map.eq_ignore_ascii_case(map) {
        return Err(format!(
            "Replay header map {:?} does not match manifest map {map:?}.",
            recording.header.map
        ));
    }
    Ok(())
}

fn collect_manifest_voice_rounds(root: &Path, canonical_root: &Path, rounds: &[u32]) -> Vec<u32> {
    rounds
        .iter()
        .copied()
        .filter(|round| {
            let candidate = root.join("voice").join(format!("round{round:02}.dtv"));
            fs::canonicalize(candidate)
                .is_ok_and(|path| path.starts_with(canonical_root) && path.is_file())
        })
        .collect()
}

fn summarize_manifest_players(files: &[PlayableManifestFile]) -> Vec<PlayerSummaryDto> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    for file in files {
        let player = players
            .entry(file.steam_id)
            .or_insert_with(|| PlayerAccumulator {
                first_round: file.round,
                first_side: file.side.clone(),
                name: file.player_name.clone(),
                rounds: BTreeSet::new(),
                files: 0,
                scoreboard_round: file.round,
                scoreboard: file.scoreboard.clone(),
            });
        if file.round < player.first_round
            || (file.round == player.first_round
                && side_rank(&file.side) < side_rank(&player.first_side))
        {
            player.first_round = file.round;
            player.first_side = file.side.clone();
        }
        if player.name == file.steam_id.to_string() && !file.player_name.is_empty() {
            player.name = file.player_name.clone();
        }
        player.rounds.insert(file.round);
        player.files += 1;
        if file.scoreboard.is_some() && file.round >= player.scoreboard_round {
            player.scoreboard_round = file.round;
            player.scoreboard = file.scoreboard.clone();
        }
    }
    let mut summaries = players
        .into_iter()
        .map(|(steam_id, player)| PlayerSummaryDto {
            team: team_index_from_first_side(&player.first_side),
            steam_id: steam_id.to_string(),
            name: player.name,
            rounds: player.rounds.len(),
            files: player.files,
            side: Some(player.first_side),
            player_color: player
                .scoreboard
                .as_ref()
                .and_then(|value| value.player_color.clone()),
            match_team: None,
            team_name: None,
            score: player.scoreboard.as_ref().and_then(|value| value.score),
            kills: player.scoreboard.as_ref().and_then(|value| value.kills),
            deaths: player.scoreboard.as_ref().and_then(|value| value.deaths),
            assists: player.scoreboard.as_ref().and_then(|value| value.assists),
            mvps: player.scoreboard.as_ref().and_then(|value| value.mvps),
            details: None,
        })
        .collect::<Vec<_>>();
    summaries.sort_by(|left, right| {
        (left.team, left.steam_id.as_str()).cmp(&(right.team, right.steam_id.as_str()))
    });
    summaries
}

fn overlay_archive_players(
    mut summaries: Vec<PlayerSummaryDto>,
    analyzed: &[BrowserPlayerSummary],
) -> Vec<PlayerSummaryDto> {
    for player in analyzed {
        if let Some(summary) = summaries
            .iter_mut()
            .find(|summary| summary.steam_id == player.steam_id)
        {
            if !player.name.trim().is_empty() {
                summary.name = player.name.clone();
            }
            summary.team = match player.team.as_str() {
                "a" => 1,
                "b" => 2,
                _ => summary.team,
            };
            summary.side = Some(player.side.clone());
            if player.player_color.is_some() {
                summary.player_color = player.player_color.clone();
            }
            summary.match_team = Some(player.team.clone());
            if player.team_name.is_some() {
                summary.team_name = player.team_name.clone();
            }
            if player.score.is_some() {
                summary.score = player.score;
            }
            if player.kills.is_some() {
                summary.kills = player.kills;
            }
            if player.deaths.is_some() {
                summary.deaths = player.deaths;
            }
            if player.assists.is_some() {
                summary.assists = player.assists;
            }
            if player.mvps.is_some() {
                summary.mvps = player.mvps;
            }
            if player.details.is_some() {
                summary.details = player.details.clone();
            }
            summary.rounds = summary.rounds.max(player.rounds);
        } else {
            summaries.push(PlayerSummaryDto {
                team: match player.team.as_str() {
                    "a" => 1,
                    "b" => 2,
                    _ => team_index_from_first_side(&player.side),
                },
                steam_id: player.steam_id.clone(),
                name: player.name.clone(),
                rounds: player.rounds,
                files: 0,
                side: Some(player.side.clone()),
                player_color: player.player_color.clone(),
                match_team: Some(player.team.clone()),
                team_name: player.team_name.clone(),
                score: player.score,
                kills: player.kills,
                deaths: player.deaths,
                assists: player.assists,
                mvps: player.mvps,
                details: player.details.clone(),
            });
        }
    }
    summaries.sort_by(|left, right| {
        (
            left.team,
            left.name.to_ascii_lowercase(),
            left.steam_id.as_str(),
        )
            .cmp(&(
                right.team,
                right.name.to_ascii_lowercase(),
                right.steam_id.as_str(),
            ))
    });
    summaries
}

fn analysis_dto(
    analysis_id: &str,
    source_path: &Path,
    output_demo_id: &str,
    analysis: &DemoAnalysis,
) -> AnalysisDto {
    AnalysisDto {
        analysis_id: analysis_id.to_string(),
        source_path: source_path.display().to_string(),
        file_name: source_path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| analysis.demo_stem.clone()),
        output_demo_id: output_demo_id.to_string(),
        demo_sha256: String::new(),
        map: analysis.map.clone(),
        tick_rate: analysis.tick_rate,
        row_count: analysis.row_count,
        source_modified_at_ms: None,
        source_size_bytes: None,
        duration_seconds: 0.0,
        demo_patch_version: None,
        demo_version_name: None,
        server_name: None,
        demo_source: None,
        converter_version: env!("CARGO_PKG_VERSION").to_string(),
        players: Vec::new(),
        score: None,
        round_outcomes: Vec::new(),
        friendly_fire: BrowserFriendlyFireSummary::default(),
        replay_input: BrowserReplayInputSummary::default(),
        rounds: analysis
            .rounds
            .iter()
            .map(|round| RoundDto {
                round: round.round,
                start_tick: round.start_tick,
                end_tick: round.end_tick,
                duration_seconds: round.duration_seconds,
                t_players: round.t_players,
                ct_players: round.ct_players,
                total_players: round.total_players,
                valid_rows: round.valid_rows,
                status: match round.status {
                    RoundStatus::Recommended => "recommended",
                    RoundStatus::Partial => "partial",
                    RoundStatus::Suspicious => "suspicious",
                }
                .to_string(),
                problems: round.problems.clone(),
                selected_by_default: round.selected_by_default(),
            })
            .collect(),
    }
}

fn browser_analysis_dto(
    analysis_id: &str,
    source_path: &Path,
    output_demo_id: &str,
    parsed: &ParsedDemo,
    browser: &BrowserDemoAnalysis,
    source_modified_at_ms: Option<u64>,
    source_size_bytes: Option<u64>,
) -> AnalysisDto {
    let mut dto = analysis_dto(analysis_id, source_path, output_demo_id, &browser.analysis);
    dto.demo_sha256 = parsed.demo_sha256.clone();
    dto.source_modified_at_ms = source_modified_at_ms;
    dto.source_size_bytes = source_size_bytes.map(|value| value.to_string());
    dto.duration_seconds = browser.duration_seconds;
    dto.demo_patch_version = browser.demo_patch_version;
    dto.demo_version_name = browser.demo_version_name.clone();
    dto.server_name = browser.server_name.clone();
    dto.demo_source = browser.demo_source.clone();
    dto.players = browser.players.clone();
    dto.score = browser.score.clone();
    dto.round_outcomes = browser.round_outcomes.clone();
    dto.friendly_fire = browser.friendly_fire.clone();
    dto.replay_input = browser.replay_input.clone();
    dto
}

fn source_metadata_modified_at_ms(metadata: DemoSourceMetadata) -> Option<u64> {
    metadata
        .modified
        .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
}

struct PreparedConversion {
    output_dir: PathBuf,
    root: PathBuf,
    options: ConvertOptions,
    cosmetics: CosmeticFlags,
}

#[derive(Clone, Copy, Debug)]
struct CosmeticFlags {
    cosmetics: bool,
    stickers: bool,
    charms: bool,
}

fn prepare_conversion(
    request: &ConvertDemoRequest,
    cached: &CachedDemo,
) -> CommandResult<PreparedConversion> {
    let cosmetics = validate_cosmetic_request(request)?;
    prepare_conversion_with_cosmetics(request, cached, cosmetics)
}

fn prepare_conversion_with_cosmetics(
    request: &ConvertDemoRequest,
    cached: &CachedDemo,
    cosmetics: CosmeticFlags,
) -> CommandResult<PreparedConversion> {
    validate_max_round_seconds(request.max_round_seconds)?;
    if request.max_round_seconds.to_bits() != cached.analysis_options.max_round_seconds.to_bits() {
        return Err(CommandErrorDto::new(
            "analysis_options_changed",
            "Round analysis settings changed. Analyze the demo again before converting.",
        ));
    }
    if !request.freeze_preroll_seconds.is_finite()
        || !(0.0..=MAX_FREEZE_PREROLL_SECONDS).contains(&request.freeze_preroll_seconds)
    {
        return Err(CommandErrorDto::new(
            "invalid_freeze_preroll",
            "Freeze pre-roll must be between 0 and 120 seconds.",
        ));
    }

    let selected_rounds = request
        .selected_rounds
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    validate_round_selection(
        &cached.analysis,
        &selected_rounds,
        request.include_suspicious,
    )?;
    let resolved = resolve_output_paths(&request.output_dir, cached)?;
    let options = ConvertOptions {
        output_dir: resolved.output_dir.clone(),
        output_stem: Some(resolved.demo_id),
        side: request.side,
        selected_rounds: Some(selected_rounds),
        include_suspicious: request.include_suspicious,
        cut_before_bomb_plant: !request.full_round,
        subtick_mode: request.subtick_mode,
        freeze_preroll_seconds: request.freeze_preroll_seconds,
        export_cosmetics: cosmetics.cosmetics,
        export_stickers: cosmetics.stickers,
        export_charms: cosmetics.charms,
        analysis: cached.analysis_options,
    };
    Ok(PreparedConversion {
        output_dir: resolved.output_dir,
        root: resolved.root,
        options,
        cosmetics,
    })
}

fn validate_max_round_seconds(value: f32) -> CommandResult<()> {
    if value.is_finite() && (MIN_MAX_ROUND_SECONDS..=MAX_MAX_ROUND_SECONDS).contains(&value) {
        Ok(())
    } else {
        Err(CommandErrorDto::new(
            "invalid_max_round_seconds",
            "Maximum round duration must be between 30 and 1800 seconds.",
        ))
    }
}

fn preflight_output_for(
    cached: &CachedDemo,
    output_dir: &str,
) -> CommandResult<PreflightOutputDto> {
    let resolved = resolve_output_paths(output_dir, cached)?;
    let backup_root = output_backup_root(&resolved.output_dir, &resolved.root)?;
    let final_exists = path_metadata(&resolved.root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_inspect_failed", error.to_string(), &resolved.root)
        })?
        .is_some();
    let backup_exists = path_metadata(&backup_root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_inspect_failed", error.to_string(), &backup_root)
        })?
        .is_some();
    Ok(PreflightOutputDto {
        exists: final_exists || backup_exists,
        root: resolved.root.display().to_string(),
    })
}

struct ResolvedOutputPaths {
    output_dir: PathBuf,
    root: PathBuf,
    demo_id: String,
}

fn resolve_output_paths(
    output_dir: &str,
    cached: &CachedDemo,
) -> CommandResult<ResolvedOutputPaths> {
    let requested_library_root = PathBuf::from(output_dir.trim());
    if requested_library_root.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "invalid_output_dir",
            "Choose an output folder before converting.",
        ));
    }
    let library_root = canonical_normal_library_root(&requested_library_root)?;

    let map_dir = checked_library_map_directory(&library_root, &cached.parsed.map)?;
    let desired_root = map_dir.join(&cached.archive_id);
    let scan = catalog::scan_demo_library_for(&library_root.display().to_string())?;
    let mut matching = scan
        .entries
        .into_iter()
        .filter(|entry| {
            !entry.demo_sha256.is_empty()
                && entry
                    .demo_sha256
                    .eq_ignore_ascii_case(&cached.parsed.demo_sha256)
        })
        .collect::<Vec<_>>();
    if matching.len() > 1 {
        if let Some(index) = matching
            .iter()
            .position(|entry| Path::new(&entry.root) == desired_root)
        {
            let selected = matching.swap_remove(index);
            matching.clear();
            matching.push(selected);
        } else {
            return Err(CommandErrorDto::at_path(
                "duplicate_demo_archives",
                "This library contains multiple archives for the same demo hash. Open the library and resolve the duplicates before replacing one.",
                &library_root,
            ));
        }
    }
    if let Some(existing) = matching.pop() {
        let root = checked_existing_archive_root(&library_root, Path::new(&existing.root))?;
        let output_dir = root.parent().map(Path::to_path_buf).ok_or_else(|| {
            CommandErrorDto::at_path(
                "unsafe_output_root",
                "Existing archive has no parent directory.",
                &root,
            )
        })?;
        let demo_id = if existing.demo_id.trim().is_empty() {
            root.file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| cached.archive_id.clone())
        } else {
            existing.demo_id
        };
        return Ok(ResolvedOutputPaths {
            output_dir,
            root,
            demo_id,
        });
    }

    for hash_len in [12_usize, 16, 24, 64] {
        let demo_id = archive_info::archive_directory_name_with_hash_len(
            &cached.parsed,
            &cached.browser,
            hash_len,
        );
        let root = map_dir.join(&demo_id);
        match fs::symlink_metadata(&root) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(ResolvedOutputPaths {
                    output_dir: map_dir,
                    root,
                    demo_id,
                });
            }
            Ok(metadata) if metadata.is_dir() && !catalog::is_symlink_or_reparse(&metadata) => {
                let root = checked_existing_archive_root(&library_root, &root)?;
                if let Ok(existing) = catalog::summarize_manifest(&root.join("manifest.json")) {
                    if existing
                        .demo_sha256
                        .eq_ignore_ascii_case(&cached.parsed.demo_sha256)
                    {
                        return Ok(ResolvedOutputPaths {
                            output_dir: root.parent().unwrap_or(&map_dir).to_path_buf(),
                            root,
                            demo_id: existing.demo_id,
                        });
                    }
                }
            }
            Ok(_) | Err(_) => {}
        }
    }

    Err(CommandErrorDto::at_path(
        "archive_path_collision",
        "Every safe hash-based archive path is already occupied by different content.",
        &desired_root,
    ))
}

fn canonical_normal_library_root(path: &Path) -> CommandResult<PathBuf> {
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        CommandErrorDto::at_path("library_root_inspect_failed", error.to_string(), path)
    })?;
    if !metadata.is_dir() || catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "library_root_invalid",
            "The main library must be an existing normal local folder.",
            path,
        ));
    }
    canonicalize_public_path(path).map_err(|error| {
        CommandErrorDto::at_path("library_root_inspect_failed", error.to_string(), path)
    })
}

fn checked_library_map_directory(library_root: &Path, map: &str) -> CommandResult<PathBuf> {
    let map_dir = library_root.join(archive_info::map_directory_name(map));
    match fs::symlink_metadata(&map_dir) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(map_dir),
        Err(error) => Err(CommandErrorDto::at_path(
            "map_directory_inspect_failed",
            error.to_string(),
            &map_dir,
        )),
        Ok(metadata) => {
            if !metadata.is_dir() || catalog::is_symlink_or_reparse(&metadata) {
                return Err(CommandErrorDto::at_path(
                    "map_directory_invalid",
                    "The map archive path must be a normal folder, not a link or junction.",
                    &map_dir,
                ));
            }
            let canonical = canonicalize_public_path(&map_dir).map_err(|error| {
                CommandErrorDto::at_path(
                    "map_directory_inspect_failed",
                    error.to_string(),
                    &map_dir,
                )
            })?;
            if !canonical.starts_with(library_root) {
                return Err(CommandErrorDto::at_path(
                    "map_directory_escape",
                    "The map archive path resolves outside the main library.",
                    &map_dir,
                ));
            }
            Ok(canonical)
        }
    }
}

fn checked_existing_archive_root(library_root: &Path, root: &Path) -> CommandResult<PathBuf> {
    let metadata = fs::symlink_metadata(root).map_err(|error| {
        CommandErrorDto::at_path("archive_root_inspect_failed", error.to_string(), root)
    })?;
    if !metadata.is_dir() || catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "archive_root_invalid",
            "An existing archive must be a normal folder, not a link or junction.",
            root,
        ));
    }
    let canonical = canonicalize_public_path(root).map_err(|error| {
        CommandErrorDto::at_path("archive_root_inspect_failed", error.to_string(), root)
    })?;
    if !canonical.starts_with(library_root) || canonical == library_root {
        return Err(CommandErrorDto::at_path(
            "archive_root_escape",
            "The archive resolves outside the main library.",
            root,
        ));
    }
    let parent = canonical.parent().ok_or_else(|| {
        CommandErrorDto::at_path(
            "archive_root_invalid",
            "The archive has no parent folder.",
            root,
        )
    })?;
    let parent_metadata = fs::symlink_metadata(parent).map_err(|error| {
        CommandErrorDto::at_path("archive_root_inspect_failed", error.to_string(), parent)
    })?;
    if !parent_metadata.is_dir() || catalog::is_symlink_or_reparse(&parent_metadata) {
        return Err(CommandErrorDto::at_path(
            "archive_parent_invalid",
            "The archive parent must be a normal folder, not a link or junction.",
            parent,
        ));
    }
    Ok(canonical)
}

fn delete_archive_for(request: &DeleteArchiveRequest) -> CommandResult<()> {
    let manifest_path = validate_manifest_input_path(&request.manifest_path)?;
    let is_manifest = manifest_path
        .file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.eq_ignore_ascii_case("manifest.json"));
    if !is_manifest {
        return Err(CommandErrorDto::at_path(
            "archive_manifest_invalid",
            "Only a DemoTracer manifest.json archive can be deleted.",
            &manifest_path,
        ));
    }
    catalog::summarize_manifest(&manifest_path).map_err(|message| {
        CommandErrorDto::at_path("archive_manifest_invalid", message, &manifest_path)
    })?;
    let canonical_manifest = canonicalize_public_path(&manifest_path).map_err(|error| {
        CommandErrorDto::at_path(
            "archive_manifest_inspect_failed",
            error.to_string(),
            &manifest_path,
        )
    })?;
    let archive_root = canonical_manifest.parent().ok_or_else(|| {
        CommandErrorDto::at_path(
            "archive_root_invalid",
            "The archive manifest has no parent folder.",
            &canonical_manifest,
        )
    })?;

    let mut checked_archive_root = None;
    for library_root in request
        .library_roots
        .iter()
        .map(|root| root.trim())
        .filter(|root| !root.is_empty())
    {
        let Ok(library_root) = canonical_normal_library_root(Path::new(library_root)) else {
            continue;
        };
        if archive_root.starts_with(&library_root) && archive_root != library_root {
            checked_archive_root =
                Some(checked_existing_archive_root(&library_root, archive_root)?);
            break;
        }
    }
    let archive_root = checked_archive_root.ok_or_else(|| {
        CommandErrorDto::at_path(
            "archive_outside_library",
            "The archive is not inside one of the indexed DemoTracer library folders.",
            archive_root,
        )
    })?;
    if canonical_manifest.parent() != Some(archive_root.as_path()) {
        return Err(CommandErrorDto::at_path(
            "archive_manifest_escape",
            "The manifest resolves outside the archive folder.",
            &canonical_manifest,
        ));
    }

    validate_archive_tree_for_deletion(&archive_root)?;
    fs::remove_dir_all(&archive_root).map_err(|error| {
        CommandErrorDto::at_path("archive_delete_failed", error.to_string(), &archive_root)
    })
}

fn validate_archive_tree_for_deletion(root: &Path) -> CommandResult<()> {
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let entries = fs::read_dir(&directory).map_err(|error| {
            CommandErrorDto::at_path(
                "archive_delete_inspect_failed",
                error.to_string(),
                &directory,
            )
        })?;
        for entry in entries {
            let entry = entry.map_err(|error| {
                CommandErrorDto::at_path(
                    "archive_delete_inspect_failed",
                    error.to_string(),
                    &directory,
                )
            })?;
            let path = entry.path();
            let metadata = fs::symlink_metadata(&path).map_err(|error| {
                CommandErrorDto::at_path("archive_delete_inspect_failed", error.to_string(), &path)
            })?;
            if catalog::is_symlink_or_reparse(&metadata) {
                return Err(CommandErrorDto::at_path(
                    "archive_delete_link_rejected",
                    "The archive contains a link or junction and was not deleted.",
                    &path,
                ));
            }
            if metadata.is_dir() {
                pending.push(path);
            } else if metadata.is_file() && is_supported_demo_path(&path) {
                return Err(CommandErrorDto::at_path(
                    "archive_contains_demo",
                    "The archive contains a demo file and was not deleted.",
                    &path,
                ));
            }
        }
    }
    Ok(())
}

fn validate_round_selection(
    analysis: &DemoAnalysis,
    selected: &BTreeSet<u32>,
    include_suspicious: bool,
) -> CommandResult<()> {
    if selected.is_empty() {
        return Err(CommandErrorDto::new(
            "no_rounds_selected",
            "Select at least one round to export.",
        ));
    }
    let known = analysis
        .rounds
        .iter()
        .map(|round| round.round)
        .collect::<BTreeSet<_>>();
    let unknown = selected.difference(&known).copied().collect::<Vec<_>>();
    if !unknown.is_empty() {
        return Err(CommandErrorDto::new(
            "unknown_rounds",
            format!("Selected rounds are not present in this analysis: {unknown:?}"),
        ));
    }
    if !include_suspicious {
        let suspicious = analysis
            .rounds
            .iter()
            .filter(|round| {
                selected.contains(&round.round) && round.status == RoundStatus::Suspicious
            })
            .map(|round| round.round)
            .collect::<Vec<_>>();
        if !suspicious.is_empty() {
            return Err(CommandErrorDto::new(
                "suspicious_rounds_not_allowed",
                format!("Enable suspicious rounds before exporting rounds {suspicious:?}."),
            ));
        }
    }
    Ok(())
}

fn validate_cosmetic_request(request: &ConvertDemoRequest) -> CommandResult<CosmeticFlags> {
    validate_cosmetic_options(
        request.export_cosmetics,
        request.export_stickers,
        request.export_charms,
        request.cosmetic_consent.as_ref(),
    )
}

fn validate_cosmetic_options(
    export_cosmetics: bool,
    export_stickers: bool,
    export_charms: bool,
    cosmetic_consent: Option<&CosmeticConsentDto>,
) -> CommandResult<CosmeticFlags> {
    if !export_cosmetics {
        if cosmetic_consent.is_some_and(|consent| !consent.phrase.trim().is_empty()) {
            return Err(CommandErrorDto::new(
                "unexpected_cosmetic_consent",
                "Cosmetic risk confirmation requires cosmetic export to be enabled.",
            ));
        }
        return Ok(CosmeticFlags {
            cosmetics: false,
            stickers: false,
            charms: false,
        });
    }

    let consent = cosmetic_consent.ok_or_else(|| {
        CommandErrorDto::new(
            "cosmetic_consent_required",
            "Cosmetic export requires the exact risk-confirmation phrase.",
        )
    })?;
    if consent.phrase.trim() != COSMETIC_CONFIRMATION_PHRASE {
        return Err(CommandErrorDto::new(
            "cosmetic_consent_required",
            format!("Type {COSMETIC_CONFIRMATION_PHRASE:?} exactly."),
        ));
    }
    Ok(CosmeticFlags {
        cosmetics: true,
        stickers: export_stickers,
        charms: export_charms,
    })
}

fn run_conversion(
    cached: CachedDemo,
    prepared: PreparedConversion,
    request: ConvertDemoRequest,
    events: Channel<TaskEvent>,
) -> CommandResult<ConversionSummaryDto> {
    let sink: TaskEventSink = Arc::new(move |event| emit(&events, event));
    run_conversion_with_sink(cached, prepared, request, sink)
}

type TaskEventSink = Arc<dyn Fn(TaskEvent) + Send + Sync>;

fn emit_to_sink(sink: &TaskEventSink, event: TaskEvent) {
    sink(event);
}

fn emit_phase_to_sink(sink: &TaskEventSink, phase: TaskPhase) {
    emit_to_sink(sink, TaskEvent::Phase { phase });
}

fn emit_log_to_sink(sink: &TaskEventSink, level: LogLevel, message: impl Into<String>) {
    emit_to_sink(
        sink,
        TaskEvent::Log {
            level,
            message: message.into(),
        },
    );
}

fn emit_demo_source_to_sink(sink: &TaskEventSink, source: &DemoSourceSet) {
    if source.is_segmented() {
        emit_log_to_sink(
            sink,
            LogLevel::Info,
            format!(
                "Detected {}-part demo series {}; merging it as one match",
                source.parts.len(),
                source.logical_stem
            ),
        );
    }
    for (index, part) in source.parts.iter().enumerate() {
        emit_log_to_sink(
            sink,
            LogLevel::Info,
            format!(
                "Reading part {}/{}: {}",
                index + 1,
                source.parts.len(),
                part.path.display()
            ),
        );
    }
}

fn run_conversion_with_sink(
    cached: CachedDemo,
    prepared: PreparedConversion,
    request: ConvertDemoRequest,
    events: TaskEventSink,
) -> CommandResult<ConversionSummaryDto> {
    let cosmetics = prepared.cosmetics;
    let final_root = prepared.root.clone();
    let output_dir = prepared.output_dir.clone();
    let mut demo_info = archive_info::DemoArchiveInfo::from_analysis(
        prepared
            .options
            .output_stem
            .clone()
            .unwrap_or_else(|| cached.archive_id.clone()),
        &cached.parsed,
        &cached.browser,
        cached.source_modified_at_ms,
        cached.source_size_bytes,
        DEMOTRACER_ABI,
        DTR_FORMAT_VERSION,
        "conversion",
    );
    restrict_player_cosmetic_details(
        &mut demo_info.players,
        cosmetics.cosmetics,
        cosmetics.stickers,
        cosmetics.charms,
    );
    let mut selected_rounds = request.selected_rounds.clone();
    selected_rounds.sort_unstable();
    selected_rounds.dedup();
    demo_info.conversion = Some(archive_info::DemoInfoConversion {
        converter_version: env!("CARGO_PKG_VERSION").to_string(),
        selected_rounds,
        side: request.side.to_string(),
        full_round: request.full_round,
        include_suspicious: request.include_suspicious,
        freeze_preroll_seconds: request.freeze_preroll_seconds,
        voice: request.export_voice,
        cosmetics: cosmetics.cosmetics,
        stickers: cosmetics.stickers,
        charms: cosmetics.charms,
        manifest_sha256: None,
        manifest_bytes: None,
        round_count: None,
        file_count: None,
    });
    let mut file_ops = RealOutputFileOps;
    let transaction = run_output_transaction(
        &output_dir,
        &final_root,
        request.overwrite,
        &mut file_ops,
        |staging_root| {
            emit_phase_to_sink(&events, TaskPhase::Exporting);
            let progress_events = events.clone();
            let progress_final_root = final_root.clone();
            let report = export_demo_to_root_with_analysis_and_progress(
                &cached.parsed,
                &cached.analysis,
                &prepared.options,
                staging_root,
                move |progress| {
                    if let Some(progress) =
                        public_conversion_progress(progress, &progress_final_root)
                    {
                        emit_to_sink(&progress_events, TaskEvent::Progress { progress });
                    }
                },
            )
            .map_err(|error| CommandErrorDto::from_core("conversion_failed", error))?;

            let mut voice_summaries = Vec::new();
            if request.export_voice {
                emit_phase_to_sink(&events, TaskPhase::Voice);
                let reports = export_round_voice_sidecars(&cached.parsed, &report)
                    .map_err(|error| CommandErrorDto::from_core("voice_export_failed", error))?;
                if reports.is_empty() {
                    emit_log_to_sink(
                        &events,
                        LogLevel::Warning,
                        "Voice sidecar export skipped: the selected rounds contain no voice frames.",
                    );
                } else {
                    for voice in reports {
                        let public_path =
                            public_output_path(&voice.path, staging_root, &final_root);
                        voice_summaries.push(VoiceSidecarSummary {
                            public_path,
                            frame_count: voice.frame_count,
                            speaker_count: voice.speaker_count,
                            duration_seconds: voice.duration_seconds,
                        });
                    }
                }
            }

            emit_phase_to_sink(&events, TaskPhase::Validating);
            let validated_files = validate_dtr_path(&report.root)
                .map_err(|error| CommandErrorDto::from_core("validation_failed", error))?;
            let manifest_bytes = fs::read(&report.manifest_path).map_err(|error| {
                CommandErrorDto::at_path(
                    "manifest_read_failed",
                    error.to_string(),
                    &report.manifest_path,
                )
            })?;
            if let Some(conversion) = demo_info.conversion.as_mut() {
                conversion.manifest_sha256 = Some(sha256_hex(&manifest_bytes));
                conversion.manifest_bytes = Some(manifest_bytes.len() as u64);
                conversion.round_count = Some(report.manifest.rounds.len());
                conversion.file_count = Some(report.manifest.files.len());
            }
            archive_info::write_demo_info(staging_root, &demo_info).map_err(|error| {
                CommandErrorDto::at_path(
                    "demo_info_write_failed",
                    error.to_string(),
                    archive_info::demo_info_path(staging_root),
                )
            })?;
            archive_info::write_demo_source_pointer(
                staging_root,
                &cached.parsed.demo_sha256,
                Path::new(&cached.parsed.path),
            )
            .map_err(|error| {
                CommandErrorDto::at_path(
                    "demo_source_write_failed",
                    error.to_string(),
                    archive_info::demo_source_path(staging_root),
                )
            })?;
            Ok(StagedConversion {
                report,
                validated_files,
                voice_summaries,
            })
        },
    )?;

    if let Some(warning) = transaction.backup_cleanup_warning {
        emit_log_to_sink(&events, LogLevel::Warning, warning);
    }
    let mut staged = transaction.value;
    for voice in &staged.voice_summaries {
        emit_log_to_sink(
            &events,
            LogLevel::Info,
            format!(
                "Voice sidecar {}: {} frames, {} speakers, {:.2}s",
                voice.public_path.display(),
                voice.frame_count,
                voice.speaker_count,
                voice.duration_seconds
            ),
        );
    }
    staged.report.root = final_root.clone();
    staged.report.manifest_path = final_root.join("manifest.json");
    emit_to_sink(
        &events,
        TaskEvent::Progress {
            progress: ConversionProgressDto::Finished {
                root: staged.report.root.display().to_string(),
                manifest_path: staged.report.manifest_path.display().to_string(),
                files_written: staged.report.files_written,
            },
        },
    );
    let summary = summarize_conversion(
        staged.report,
        staged.validated_files,
        request.export_voice,
        staged.voice_summaries.len(),
        cosmetics.cosmetics,
        cosmetics.stickers,
        cosmetics.charms,
    );
    emit_phase_to_sink(&events, TaskPhase::Complete);
    Ok(summary)
}

fn process_batch_demo(
    request: batch::BatchProcessRequest,
    events: TaskEventSink,
) -> CommandResult<batch::BatchProcessResult> {
    validate_max_round_seconds(request.settings.max_round_seconds)?;
    let requested_path = validate_demo_path(&request.source_path.display().to_string())?;
    let source = resolve_validated_demo_source(&requested_path)?;
    let source_path = source.primary_path().to_path_buf();
    let source_metadata = source.metadata().ok();

    emit_demo_source_to_sink(&events, &source);
    if source.paths().any(is_zstd_demo_path) {
        emit_phase_to_sink(&events, TaskPhase::Decompressing);
    }
    emit_phase_to_sink(&events, TaskPhase::Parsing);
    let parsed = read_demo_source_with_options(
        &source,
        ReadDemoOptions {
            collect_voice: request.settings.export_voice,
            collect_cosmetics: request.settings.export_cosmetics,
        },
    )
    .map_err(|error| {
        emit_log_to_sink(&events, LogLevel::Error, error.to_string());
        CommandErrorDto::from_core("analysis_failed", error)
    })?;

    emit_phase_to_sink(&events, TaskPhase::Analyzing);
    let browser = analyze_browser_demo(
        &parsed,
        AnalysisOptions {
            max_round_seconds: request.settings.max_round_seconds,
            ..AnalysisOptions::default()
        },
    );
    if let Some(warning) = replay_input_warning(&browser.replay_input) {
        emit_log_to_sink(&events, LogLevel::Warning, warning);
    }
    let selected_rounds = browser
        .analysis
        .rounds
        .iter()
        .filter(|round| request.settings.include_suspicious || round.selected_by_default())
        .map(|round| round.round)
        .collect::<Vec<_>>();
    if selected_rounds.is_empty() {
        return Err(CommandErrorDto::at_path(
            "no_recommended_rounds",
            "No recommended rounds were found. Import this demo individually to inspect or include suspicious rounds.",
            &source_path,
        ));
    }

    let demo_sha256 = parsed.demo_sha256.clone();
    let map = browser.analysis.map.clone();
    let server_name = browser.server_name.clone();
    let demo_source = browser.demo_source.clone();
    let archive_id = archive_info::archive_directory_name(&parsed, &browser);
    let cached = CachedDemo {
        analysis_id: format!("{}-{}", request.batch_id, request.item_id),
        parsed: Arc::new(parsed),
        analysis: browser.analysis.clone(),
        browser,
        analysis_options: AnalysisOptions {
            max_round_seconds: request.settings.max_round_seconds,
            ..AnalysisOptions::default()
        },
        archive_id,
        source_modified_at_ms: source_metadata.and_then(source_metadata_modified_at_ms),
        source_size_bytes: source_metadata.map(|metadata| metadata.size_bytes),
    };
    let convert_request = ConvertDemoRequest {
        analysis_id: cached.analysis_id.clone(),
        output_dir: request.library_root.display().to_string(),
        selected_rounds,
        include_suspicious: request.settings.include_suspicious,
        full_round: request.settings.full_round,
        side: request.settings.side,
        subtick_mode: request.settings.subtick_mode,
        max_round_seconds: request.settings.max_round_seconds,
        freeze_preroll_seconds: request.settings.freeze_preroll_seconds,
        export_voice: request.settings.export_voice,
        export_cosmetics: request.settings.export_cosmetics,
        export_stickers: request.settings.export_stickers,
        export_charms: request.settings.export_charms,
        cosmetic_consent: None,
        overwrite: if request.replace_existing {
            OverwriteModeDto::Replace
        } else {
            OverwriteModeDto::Deny
        },
    };
    // Parsing and analysis stay concurrent. Archive selection, writing, and validation are
    // serialized so duplicate demos cannot race for the same transaction root and several
    // exporters cannot multiply peak memory use.
    let _conversion_guard = BATCH_CONVERSION_LOCK.lock().map_err(|_| {
        CommandErrorDto::new(
            "batch_conversion_state_poisoned",
            "The batch conversion coordinator is unavailable. Resume the batch after restarting the app.",
        )
    })?;
    let prepared = prepare_conversion_with_cosmetics(
        &convert_request,
        &cached,
        CosmeticFlags {
            cosmetics: request.settings.export_cosmetics,
            stickers: request.settings.export_stickers,
            charms: request.settings.export_charms,
        },
    )?;
    if !request.replace_existing {
        if let Some(existing) = reconcile_completed_batch_archive(
            &prepared.root,
            &demo_sha256,
            &map,
            server_name.clone(),
            demo_source.clone(),
            &events,
        )? {
            return Ok(existing);
        }
    }
    let summary = run_conversion_with_sink(cached, prepared, convert_request, events)?;

    Ok(batch::BatchProcessResult {
        archive_root: PathBuf::from(&summary.root),
        manifest_path: PathBuf::from(&summary.manifest_path),
        demo_sha256,
        map,
        server_name,
        demo_source,
        rounds_exported: summary.rounds_exported,
        files_written: summary.files_written,
    })
}

fn reconcile_completed_batch_archive(
    root: &Path,
    expected_demo_sha256: &str,
    map: &str,
    server_name: Option<String>,
    demo_source: Option<BrowserDemoSource>,
    events: &TaskEventSink,
) -> CommandResult<Option<batch::BatchProcessResult>> {
    let marker = root.join(OUTPUT_COMPLETION_MARKER);
    let manifest_path = root.join("manifest.json");
    if !file_contents_equal(&marker, OUTPUT_COMPLETION_MARKER_CONTENT).unwrap_or(false)
        || !manifest_path.is_file()
    {
        return Ok(None);
    }

    emit_phase_to_sink(events, TaskPhase::Validating);
    let archive = match read_manifest_for(&manifest_path.display().to_string()) {
        Ok(archive)
            if archive.playable
                && archive
                    .demo_sha256
                    .eq_ignore_ascii_case(expected_demo_sha256) =>
        {
            archive
        }
        Ok(_) | Err(_) => return Ok(None),
    };
    emit_log_to_sink(
        events,
        LogLevel::Info,
        format!(
            "A complete validated archive already exists at {}; keeping it.",
            root.display()
        ),
    );
    emit_phase_to_sink(events, TaskPhase::Complete);
    Ok(Some(batch::BatchProcessResult {
        archive_root: root.to_path_buf(),
        manifest_path,
        demo_sha256: expected_demo_sha256.to_string(),
        map: if archive.map.trim().is_empty() {
            map.to_string()
        } else {
            archive.map
        },
        server_name,
        demo_source,
        rounds_exported: archive
            .rounds
            .iter()
            .filter(|round| round.available)
            .count(),
        files_written: archive.total_files,
    }))
}

struct StagedConversion {
    report: ConversionReport,
    validated_files: usize,
    voice_summaries: Vec<VoiceSidecarSummary>,
}

struct VoiceSidecarSummary {
    public_path: PathBuf,
    frame_count: usize,
    speaker_count: usize,
    duration_seconds: f32,
}

fn public_conversion_progress(
    progress: ConversionProgress,
    final_root: &Path,
) -> Option<ConversionProgressDto> {
    match progress {
        ConversionProgress::ArtifactsWritingStarted { artifacts, .. } => {
            Some(ConversionProgressDto::ArtifactsWritingStarted {
                root: final_root.display().to_string(),
                artifacts,
            })
        }
        ConversionProgress::Finished { .. } => None,
        other => Some(other.into()),
    }
}

fn public_output_path(path: &Path, staging_root: &Path, final_root: &Path) -> PathBuf {
    path.strip_prefix(staging_root)
        .map(|relative| final_root.join(relative))
        .unwrap_or_else(|_| {
            final_root
                .join("voice")
                .join(path.file_name().unwrap_or_default())
        })
}

fn output_exists_error(path: &Path) -> CommandErrorDto {
    CommandErrorDto::at_path(
        "output_exists",
        "Output for this demo already exists. Confirm replacement to continue.",
        path,
    )
}

#[derive(Debug)]
struct OutputTransaction<T> {
    value: T,
    backup_cleanup_warning: Option<String>,
}

trait OutputFileOps {
    fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>>;
    fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool>;
    fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()>;
    fn create_dir(&mut self, path: &Path) -> std::io::Result<()>;
    fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()>;
    fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()>;
    fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()>;
}

struct RealOutputFileOps;

impl OutputFileOps for RealOutputFileOps {
    fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>> {
        path_metadata(path)
    }

    fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool> {
        file_contents_equal(path, expected)
    }

    fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
        fs::create_dir_all(path)
    }

    fn create_dir(&mut self, path: &Path) -> std::io::Result<()> {
        fs::create_dir(path)
    }

    fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()> {
        fs::rename(from, to)
    }

    fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
        fs::remove_dir_all(path)
    }

    fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()> {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)?;
        std::io::Write::write_all(&mut file, bytes)
    }
}

fn path_metadata(path: &Path) -> std::io::Result<Option<fs::Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => Ok(Some(metadata)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn file_contents_equal(path: &Path, expected: &[u8]) -> std::io::Result<bool> {
    let Some(metadata) = path_metadata(path)? else {
        return Ok(false);
    };
    if !metadata.file_type().is_file() || metadata.len() != expected.len() as u64 {
        return Ok(false);
    }
    let mut file = fs::File::open(path)?;
    let mut actual = vec![0_u8; expected.len()];
    std::io::Read::read_exact(&mut file, &mut actual)?;
    let mut trailing = [0_u8; 1];
    if std::io::Read::read(&mut file, &mut trailing)? != 0 {
        return Ok(false);
    }
    Ok(actual == expected)
}

fn output_backup_root(output_dir: &Path, final_root: &Path) -> CommandResult<PathBuf> {
    if final_root.parent() != Some(output_dir) {
        return Err(CommandErrorDto::at_path(
            "unsafe_output_root",
            "Refusing to use transaction state outside the selected folder.",
            final_root,
        ));
    }
    let demo_id = output_root_name(final_root)?.to_string_lossy();
    Ok(output_dir.join(format!(".{demo_id}.backup")))
}

fn output_lock_path(output_dir: &Path, final_root: &Path) -> CommandResult<PathBuf> {
    if final_root.parent() != Some(output_dir) {
        return Err(CommandErrorDto::at_path(
            "unsafe_output_root",
            "Refusing to lock transaction state outside the selected folder.",
            final_root,
        ));
    }
    let demo_id = output_root_name(final_root)?.to_string_lossy();
    Ok(output_dir.join(format!(".{demo_id}.lock")))
}

fn output_root_name(final_root: &Path) -> CommandResult<&std::ffi::OsStr> {
    final_root
        .file_name()
        .filter(|name| !name.is_empty())
        .ok_or_else(|| {
            CommandErrorDto::at_path(
                "unsafe_output_root",
                "Output root must have a demo identifier.",
                final_root,
            )
        })
}

fn run_output_transaction<T, F, O>(
    output_dir: &Path,
    final_root: &Path,
    overwrite: OverwriteModeDto,
    file_ops: &mut O,
    build: F,
) -> CommandResult<OutputTransaction<T>>
where
    F: FnOnce(&Path) -> CommandResult<T>,
    O: OutputFileOps,
{
    let backup_root = output_backup_root(output_dir, final_root)?;
    let lock_path = output_lock_path(output_dir, final_root)?;
    let demo_id = output_root_name(final_root)?.to_string_lossy();

    file_ops.create_dir_all(output_dir).map_err(|error| {
        CommandErrorDto::at_path("output_stage_failed", error.to_string(), output_dir)
    })?;
    let output_dir_metadata = file_ops
        .metadata(output_dir)
        .map_err(|error| {
            CommandErrorDto::at_path("output_stage_failed", error.to_string(), output_dir)
        })?
        .ok_or_else(|| {
            CommandErrorDto::at_path(
                "output_stage_failed",
                "The output parent disappeared before locking.",
                output_dir,
            )
        })?;
    require_normal_output_directory(&output_dir_metadata, output_dir)?;
    let _target_lock =
        crate::target_lock::TargetFileLock::acquire(&lock_path).map_err(|error| {
            CommandErrorDto::at_path(
                if error.kind() == std::io::ErrorKind::WouldBlock {
                    "output_locked"
                } else {
                    "output_lock_failed"
                },
                if error.kind() == std::io::ErrorKind::WouldBlock {
                    format!("Another DemoTracer process is writing this output: {error}")
                } else {
                    error.to_string()
                },
                &lock_path,
            )
        })?;

    recover_output_state(file_ops, final_root, &backup_root)?;
    if let Some(metadata) = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_inspect_failed", error.to_string(), final_root)
    })? {
        require_normal_output_directory(&metadata, final_root)?;
        if overwrite == OverwriteModeDto::Deny {
            return Err(output_exists_error(final_root));
        }
    }

    let staging_root = create_unique_staging_root(file_ops, output_dir, &demo_id)?;
    let value = match build(&staging_root) {
        Ok(value) => value,
        Err(error) => {
            cleanup_staging(file_ops, &staging_root);
            return Err(error);
        }
    };

    let marker_path = staging_root.join(OUTPUT_COMPLETION_MARKER);
    if let Err(error) = file_ops.write_new_file(&marker_path, OUTPUT_COMPLETION_MARKER_CONTENT) {
        cleanup_staging(file_ops, &staging_root);
        return Err(CommandErrorDto::at_path(
            "completion_marker_failed",
            error.to_string(),
            marker_path,
        ));
    }

    let backup_cleanup_warning =
        match promote_staged_output(file_ops, &staging_root, final_root, &backup_root, overwrite) {
            Ok(warning) => warning,
            Err(error) => {
                cleanup_staging(file_ops, &staging_root);
                return Err(error);
            }
        };
    Ok(OutputTransaction {
        value,
        backup_cleanup_warning,
    })
}

fn recover_output_state<O: OutputFileOps>(
    file_ops: &mut O,
    final_root: &Path,
    backup_root: &Path,
) -> CommandResult<()> {
    let final_metadata = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_recovery_failed", error.to_string(), final_root)
    })?;
    let backup_metadata = file_ops.metadata(backup_root).map_err(|error| {
        CommandErrorDto::at_path("output_recovery_failed", error.to_string(), backup_root)
    })?;
    match (final_metadata, backup_metadata) {
        (None, Some(backup_metadata)) => {
            require_normal_output_directory(&backup_metadata, backup_root)?;
            file_ops.rename(backup_root, final_root).map_err(|error| {
                CommandErrorDto::at_path("output_recovery_failed", error.to_string(), backup_root)
            })
        }
        (Some(final_metadata), Some(backup_metadata)) => {
            require_normal_output_directory(&final_metadata, final_root)?;
            require_normal_output_directory(&backup_metadata, backup_root)?;
            let marker_path = final_root.join(OUTPUT_COMPLETION_MARKER);
            let marker_matches = file_ops
                .file_contents_equal(&marker_path, OUTPUT_COMPLETION_MARKER_CONTENT)
                .map_err(|error| {
                    CommandErrorDto::at_path(
                        "output_recovery_failed",
                        error.to_string(),
                        &marker_path,
                    )
                })?;
            if !marker_matches {
                return Err(CommandErrorDto::at_path(
                    "ambiguous_output_state",
                    "Both the output and its backup exist, but the output has no valid completion marker. Refusing to discard either copy.",
                    final_root,
                ));
            }
            file_ops.remove_dir_all(backup_root).map_err(|error| {
                CommandErrorDto::at_path(
                    "output_backup_cleanup_failed",
                    error.to_string(),
                    backup_root,
                )
            })
        }
        _ => Ok(()),
    }
}

fn require_normal_output_directory(metadata: &fs::Metadata, path: &Path) -> CommandResult<()> {
    if metadata.file_type().is_dir() && !catalog::is_symlink_or_reparse(metadata) {
        Ok(())
    } else {
        Err(CommandErrorDto::at_path(
            "output_not_directory",
            "Output transaction state contains a non-directory or symbolic link.",
            path,
        ))
    }
}

fn create_unique_staging_root<O: OutputFileOps>(
    file_ops: &mut O,
    output_dir: &Path,
    demo_id: &str,
) -> CommandResult<PathBuf> {
    for _ in 0..32 {
        let sequence = NEXT_STAGING_NONCE.fetch_add(1, Ordering::Relaxed);
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let nonce = format!("{timestamp:x}{sequence:x}");
        let staging_root =
            output_dir.join(format!(".{demo_id}.tmp.{}.{nonce}", std::process::id()));
        match file_ops.create_dir(&staging_root) {
            Ok(()) => return Ok(staging_root),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(CommandErrorDto::at_path(
                    "output_stage_failed",
                    error.to_string(),
                    staging_root,
                ));
            }
        }
    }
    Err(CommandErrorDto::at_path(
        "output_stage_failed",
        "Could not reserve a unique staging directory.",
        output_dir,
    ))
}

fn promote_staged_output<O: OutputFileOps>(
    file_ops: &mut O,
    staging_root: &Path,
    final_root: &Path,
    backup_root: &Path,
    overwrite: OverwriteModeDto,
) -> CommandResult<Option<String>> {
    let staging_metadata = file_ops.metadata(staging_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), staging_root)
    })?;
    let Some(staging_metadata) = staging_metadata else {
        return Err(CommandErrorDto::at_path(
            "output_promote_failed",
            "The staging directory disappeared before promotion.",
            staging_root,
        ));
    };
    require_normal_output_directory(&staging_metadata, staging_root)?;

    let final_metadata = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), final_root)
    })?;
    let Some(final_metadata) = final_metadata else {
        file_ops.rename(staging_root, final_root).map_err(|error| {
            CommandErrorDto::at_path("output_promote_failed", error.to_string(), staging_root)
        })?;
        return Ok(None);
    };
    require_normal_output_directory(&final_metadata, final_root)?;
    if overwrite == OverwriteModeDto::Deny {
        return Err(output_exists_error(final_root));
    }
    if file_ops
        .metadata(backup_root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_promote_failed", error.to_string(), backup_root)
        })?
        .is_some()
    {
        return Err(CommandErrorDto::at_path(
            "ambiguous_output_state",
            "A backup appeared while preparing output promotion. Refusing to overwrite it.",
            backup_root,
        ));
    }

    file_ops.rename(final_root, backup_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), final_root)
    })?;
    if let Err(promote_error) = file_ops.rename(staging_root, final_root) {
        if let Err(rollback_error) = file_ops.rename(backup_root, final_root) {
            return Err(CommandErrorDto::at_path(
                "output_rollback_failed",
                format!(
                    "Could not promote staged output ({promote_error}); restoring the previous output also failed ({rollback_error})."
                ),
                backup_root,
            ));
        }
        return Err(CommandErrorDto::at_path(
            "output_promote_failed",
            format!("Could not promote staged output; the previous output was restored: {promote_error}"),
            staging_root,
        ));
    }

    Ok(file_ops.remove_dir_all(backup_root).err().map(|error| {
        format!(
            "New output is ready, but the previous-output backup could not be removed ({}): {error}",
            backup_root.display()
        )
    }))
}

fn cleanup_staging<O: OutputFileOps>(file_ops: &mut O, staging_root: &Path) {
    if matches!(
        file_ops.metadata(staging_root),
        Ok(Some(metadata)) if metadata.file_type().is_dir()
    ) {
        let _ = file_ops.remove_dir_all(staging_root);
    }
}

fn summarize_conversion(
    report: ConversionReport,
    validated_files: usize,
    voice_requested: bool,
    voice_sidecars: usize,
    cosmetic_requested: bool,
    sticker_requested: bool,
    charm_requested: bool,
) -> ConversionSummaryDto {
    let output_bytes = directory_size_bytes(&report.root).unwrap_or(0);
    let rounds = report
        .manifest
        .rounds
        .iter()
        .map(|round| RoundFileCountDto {
            round: round.round,
            files: round.files,
        })
        .collect::<Vec<_>>();
    let first_exported_round = rounds.iter().map(|round| round.round).min();
    let players = summarize_exported_players(&report.manifest.files);
    let cosmetic_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics
                .as_ref()
                .is_some_and(|cosmetics| !cosmetics.is_empty())
        })
        .count();
    let sticker_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics.as_ref().is_some_and(|cosmetics| {
                cosmetics
                    .weapons
                    .iter()
                    .any(|weapon| !weapon.stickers.is_empty())
            })
        })
        .count();
    let charm_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics.as_ref().is_some_and(|cosmetics| {
                cosmetics
                    .weapons
                    .iter()
                    .any(|weapon| !weapon.charms.is_empty())
            })
        })
        .count();
    let preset = if cosmetic_files == 0 {
        None
    } else if sticker_files > 0 || charm_files > 0 {
        Some("full".to_string())
    } else {
        Some("basic".to_string())
    };
    let commands = build_commands(
        &report.manifest_path,
        first_exported_round,
        voice_sidecars,
        preset.as_deref(),
    );

    ConversionSummaryDto {
        root: report.root.display().to_string(),
        manifest_path: report.manifest_path.display().to_string(),
        files_written: report.files_written,
        validated_files,
        output_bytes: output_bytes.to_string(),
        rounds_exported: rounds.len(),
        first_exported_round,
        rounds,
        players,
        voice: VoiceSummaryDto {
            requested: voice_requested,
            sidecars: voice_sidecars,
        },
        cosmetics: CosmeticSummaryDto {
            requested: Some(cosmetic_requested),
            sticker_requested: Some(sticker_requested),
            charm_requested: Some(charm_requested),
            files: cosmetic_files,
            sticker_files,
            charm_files,
            preset,
        },
        commands,
    }
}

struct PlayerAccumulator {
    first_round: u32,
    first_side: String,
    name: String,
    rounds: BTreeSet<u32>,
    files: usize,
    scoreboard_round: u32,
    scoreboard: Option<ManifestPlayerScoreboardWire>,
}

fn summarize_exported_players(files: &[ConvertedFile]) -> Vec<PlayerSummaryDto> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    for file in files {
        let player = players
            .entry(file.steam_id)
            .or_insert_with(|| PlayerAccumulator {
                first_round: file.round,
                first_side: file.side.clone(),
                name: if file.player_name.is_empty() {
                    file.steam_id.to_string()
                } else {
                    file.player_name.clone()
                },
                rounds: BTreeSet::new(),
                files: 0,
                scoreboard_round: file.round,
                scoreboard: file.scoreboard.as_ref().map(|scoreboard| {
                    ManifestPlayerScoreboardWire {
                        player_color: scoreboard.player_color.clone(),
                        score: scoreboard.score,
                        kills: scoreboard.kills,
                        deaths: scoreboard.deaths,
                        assists: scoreboard.assists,
                        mvps: scoreboard.mvps,
                    }
                }),
            });
        if file.round < player.first_round
            || (file.round == player.first_round
                && side_rank(&file.side) < side_rank(&player.first_side))
        {
            player.first_round = file.round;
            player.first_side = file.side.clone();
        }
        if player.name == file.steam_id.to_string() && !file.player_name.is_empty() {
            player.name = file.player_name.clone();
        }
        player.rounds.insert(file.round);
        player.files += 1;
        if file.scoreboard.is_some() && file.round >= player.scoreboard_round {
            player.scoreboard_round = file.round;
            player.scoreboard =
                file.scoreboard
                    .as_ref()
                    .map(|scoreboard| ManifestPlayerScoreboardWire {
                        player_color: scoreboard.player_color.clone(),
                        score: scoreboard.score,
                        kills: scoreboard.kills,
                        deaths: scoreboard.deaths,
                        assists: scoreboard.assists,
                        mvps: scoreboard.mvps,
                    });
        }
    }

    let mut summaries = players
        .into_iter()
        .map(|(steam_id, player)| PlayerSummaryDto {
            team: team_index_from_first_side(&player.first_side),
            steam_id: steam_id.to_string(),
            name: player.name,
            rounds: player.rounds.len(),
            files: player.files,
            side: Some(player.first_side),
            player_color: player
                .scoreboard
                .as_ref()
                .and_then(|value| value.player_color.clone()),
            match_team: None,
            team_name: None,
            score: player.scoreboard.as_ref().and_then(|value| value.score),
            kills: player.scoreboard.as_ref().and_then(|value| value.kills),
            deaths: player.scoreboard.as_ref().and_then(|value| value.deaths),
            assists: player.scoreboard.as_ref().and_then(|value| value.assists),
            mvps: player.scoreboard.as_ref().and_then(|value| value.mvps),
            details: None,
        })
        .collect::<Vec<_>>();
    summaries.sort_by(|left, right| {
        (left.team, left.steam_id.as_str()).cmp(&(right.team, right.steam_id.as_str()))
    });
    summaries
}

fn side_rank(side: &str) -> u8 {
    if side.eq_ignore_ascii_case("t") {
        0
    } else if side.eq_ignore_ascii_case("ct") {
        1
    } else {
        2
    }
}

fn team_index_from_first_side(side: &str) -> usize {
    if side.eq_ignore_ascii_case("t") {
        1
    } else if side.eq_ignore_ascii_case("ct") {
        2
    } else {
        3
    }
}

fn build_commands(
    manifest_path: &Path,
    first_round: Option<u32>,
    voice_sidecars: usize,
    cosmetic_preset: Option<&str>,
) -> CommandSummaryDto {
    let round = first_round.unwrap_or(0);
    let manifest = console_quote_path(manifest_path);
    let go_round = format!("dtr_go round \"{manifest}\" {round}");
    let go_sequence = format!("dtr_go seq \"{manifest}\" {round}");
    CommandSummaryDto {
        go_round: go_round.clone(),
        go_sequence: go_sequence.clone(),
        round: command_with_prefixes(&go_round, voice_sidecars, None),
        sequence: command_with_prefixes(&go_sequence, voice_sidecars, None),
        cosmetic_round: cosmetic_preset
            .map(|preset| command_with_prefixes(&go_round, voice_sidecars, Some(preset))),
        cosmetic_sequence: cosmetic_preset
            .map(|preset| command_with_prefixes(&go_sequence, voice_sidecars, Some(preset))),
    }
}

fn command_with_prefixes(
    command: &str,
    voice_sidecars: usize,
    cosmetic_preset: Option<&str>,
) -> String {
    let mut prefixes = Vec::new();
    if voice_sidecars > 0 {
        prefixes.push("dtr_voice_auto on".to_string());
    }
    if let Some(preset) = cosmetic_preset {
        prefixes.push(format!("dtr_cosmetics {preset}"));
    }
    if prefixes.is_empty() {
        command.to_string()
    } else {
        format!("{}; {command}", prefixes.join("; "))
    }
}

fn console_quote_path(path: &Path) -> String {
    path.display().to_string().replace('"', "\\\"")
}

fn directory_size_bytes(path: &Path) -> std::io::Result<u64> {
    let metadata = fs::metadata(path)?;
    if metadata.is_file() {
        return Ok(metadata.len());
    }
    let mut total = 0_u64;
    for entry in fs::read_dir(path)? {
        total = total.saturating_add(directory_size_bytes(&entry?.path()).unwrap_or(0));
    }
    Ok(total)
}

fn emit(channel: &Channel<TaskEvent>, event: TaskEvent) {
    let _ = channel.send(event);
}

fn emit_phase(channel: &Channel<TaskEvent>, phase: TaskPhase) {
    emit(channel, TaskEvent::Phase { phase });
}

fn emit_log(channel: &Channel<TaskEvent>, level: LogLevel, message: impl Into<String>) {
    emit(
        channel,
        TaskEvent::Log {
            level,
            message: message.into(),
        },
    );
}

fn replay_input_warning(summary: &BrowserReplayInputSummary) -> Option<String> {
    (summary.status == BrowserReplayInputStatus::Limited).then(|| {
        format!(
            "Source demo has no usable player command or action input (command rows {}/{}, action rows {}). DTR can still use the snapshot data that is present, but firing, grenade release, bomb interaction, and subtick timing cannot be faithfully replayed.",
            summary.command_rows, summary.player_rows, summary.action_rows
        )
    })
}

fn emit_demo_source(channel: &Channel<TaskEvent>, source: &DemoSourceSet) {
    if source.is_segmented() {
        emit_log(
            channel,
            LogLevel::Info,
            format!(
                "Detected {}-part demo series {}; merging it as one match",
                source.parts.len(),
                source.logical_stem
            ),
        );
    }
    for (index, part) in source.parts.iter().enumerate() {
        emit_log(
            channel,
            LogLevel::Info,
            format!(
                "Reading part {}/{}: {}",
                index + 1,
                source.parts.len(),
                part.path.display()
            ),
        );
    }
}

fn open_folder_path(path: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        Command::new("explorer.exe").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(target_os = "macos")]
    {
        Command::new("open").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(all(unix, not(target_os = "macos")))]
    {
        Command::new("xdg-open").arg(path).spawn()?;
        Ok(())
    }
}

fn reveal_file_path(path: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        Command::new("explorer.exe")
            .arg("/select,")
            .arg(path)
            .spawn()?;
        return Ok(());
    }

    #[cfg(target_os = "macos")]
    {
        Command::new("open").arg("-R").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(all(unix, not(target_os = "macos")))]
    {
        let parent = path.parent().unwrap_or(path);
        Command::new("xdg-open").arg(parent).spawn()?;
        Ok(())
    }
}

fn open_external_url(url: &str) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        Command::new("rundll32.exe")
            .arg("url.dll,FileProtocolHandler")
            .arg(url)
            .spawn()?;
        return Ok(());
    }

    #[cfg(target_os = "macos")]
    {
        Command::new("open").arg(url).spawn()?;
        return Ok(());
    }

    #[cfg(all(unix, not(target_os = "macos")))]
    {
        Command::new("xdg-open").arg(url).spawn()?;
        Ok(())
    }
}

pub fn run() {
    tauri::Builder::default()
        .register_uri_scheme_protocol("steam-avatar", |context, request| {
            steam_profile::serve_cached_asset(
                context.app_handle().path().app_local_data_dir().ok(),
                &request,
            )
        })
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .setup(|app| {
            let activity_root = app.path().app_local_data_dir()?.join("logs");
            let activity = ActivityLogState::new(activity_root)
                .map_err(|error| std::io::Error::other(error.message))?;
            let _ = activity.maintain();
            let _ = activity.append(
                ActivityLogLevel::Info,
                "app",
                format!("CS2 DemoTracer {} started", env!("CARGO_PKG_VERSION")),
            );
            let gsi = GsiState::new(activity.clone());
            gsi.start();
            app.manage(activity);
            app.manage(gsi);
            if let (Some(window), Some(icon)) = (
                app.get_webview_window("main"),
                app.default_window_icon().cloned(),
            ) {
                window.set_icon(icon)?;
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() == "main" && matches!(event, tauri::WindowEvent::Destroyed) {
                window.app_handle().exit(0);
            }
        })
        .manage(AppState::default())
        .manage(batch::BatchState::default())
        .manage(telemetry::TelemetryState::default())
        .invoke_handler(tauri::generate_handler![
            choose_demo,
            choose_demos,
            choose_manifest,
            read_workspace_background,
            choose_workspace_background,
            clear_workspace_background,
            load_gui_preferences,
            save_gui_preferences,
            load_library_avatar,
            choose_output_dir,
            default_library_dir,
            choose_library_dir,
            choose_demo_source_dir,
            choose_cs2_dir,
            detect_cs2_installations,
            inspect_cs2_install,
            playback_release_status,
            playback_update_status,
            choose_playback_bundle,
            install_playback_bundle,
            install_latest_playback_bundle,
            rollback_playback_install,
            load_server_config,
            validate_server_config,
            save_server_config,
            choose_demo_batch_dir,
            scan_demo_folder,
            start_batch_import,
            resume_batch_import,
            read_batch_import,
            list_batch_imports,
            cancel_batch_import,
            preflight_demo_source,
            analyze_demo,
            cancel_analysis,
            preflight_output,
            read_manifest,
            save_archive_note,
            scan_demo_library,
            delete_archive,
            refresh_archive_metadata,
            resolve_archive_source,
            refresh_library_metadata,
            import_archives,
            convert_demo,
            open_output,
            reveal_output,
            open_external,
            start_inventory_simulator_batch,
            set_inventory_simulator_panel,
            load_steam_profiles,
            list_activity_logs,
            append_activity_log,
            maintain_activity_logs,
            clear_activity_logs,
            open_activity_log_directory,
            configure_gsi,
            gsi_status,
            telemetry::configure_telemetry,
            telemetry::submit_telemetry
        ])
        .run(tauri::generate_context!())
        .expect("failed to run CS2 DemoTracer desktop app");
}

#[cfg(test)]
mod tests {
    use super::*;
    use cs2_demotracer::model::{
        ConversionManifest, ConvertedRound, EconomyClass, ReplayCosmetics, ReplayItemCosmetic,
        ReplayLoadout, ReplayWeaponCharm, ReplayWeaponCosmetic, ReplayWeaponSticker, TeamEconomy,
    };

    #[test]
    fn interactive_analysis_keeps_optional_export_evidence() {
        assert!(INTERACTIVE_ANALYSIS_READ_OPTIONS.collect_voice);
        assert!(INTERACTIVE_ANALYSIS_READ_OPTIONS.collect_cosmetics);
    }

    #[test]
    fn workspace_background_accepts_bounded_png_dimensions() {
        let mut png = vec![0_u8; 24];
        png[..8].copy_from_slice(b"\x89PNG\r\n\x1a\n");
        png[12..16].copy_from_slice(b"IHDR");
        png[16..20].copy_from_slice(&1920_u32.to_be_bytes());
        png[20..24].copy_from_slice(&1080_u32.to_be_bytes());
        assert_eq!(png_dimensions(&png).unwrap(), (1920, 1080));

        png[16..20].copy_from_slice(&0_u32.to_be_bytes());
        assert_eq!(
            png_dimensions(&png).unwrap_err().code,
            "workspace_background_dimensions_invalid"
        );
    }

    #[test]
    fn demo_preflight_finds_an_existing_archive_by_content_hash() {
        let temp = ManifestTestDir::new("demo-preflight");
        let demo_path = temp.write_file("source/random-download-name.dem", b"demo-content");
        let demo_sha256 = demo_content_sha256(&demo_path).unwrap();
        let manifest_path = temp.write_file(
            "library/de_mirage/match/manifest.json",
            &serde_json::to_vec_pretty(&serde_json::json!({
                "demo_path": "random-download-name.dem",
                "demo_id": "match-test",
                "demo_sha256": demo_sha256,
                "map": "de_mirage",
                "tick_rate": 64.0,
                "abi": DEMOTRACER_ABI,
                "dtr_format_version": DTR_FORMAT_VERSION,
                "files": [{
                    "path": "round01/t/player.dtr",
                    "round": 1,
                    "side": "t",
                    "steam_id": 1,
                    "player_name": "player"
                }]
            }))
            .unwrap(),
        );
        let request = PreflightDemoSourceRequest {
            path: demo_path.display().to_string(),
            library_roots: vec![temp.path.join("library").display().to_string()],
        };

        let result = preflight_demo_source_for(&request).unwrap();

        assert_eq!(result.demo_sha256, demo_content_sha256(&demo_path).unwrap());
        assert_eq!(result.source_path, demo_path.display().to_string());
        assert_eq!(result.matches.len(), 1);
        assert_eq!(
            result.matches[0].manifest_path,
            manifest_path.display().to_string()
        );
    }

    #[test]
    fn external_urls_are_limited_to_exact_supported_targets() {
        let profile = "https://steamcommunity.com/profiles/76561198147750283";
        let inspect = concat!(
            "steam://run/730/en/+csgo_econ_action_preview%20",
            "0018A62720C04E280638B9F5C2F10340E706411C0C20"
        );
        assert_eq!(validate_external_url(profile).unwrap(), profile);
        assert_eq!(validate_external_url(inspect).unwrap(), inspect);
        for credit_url in [
            "https://github.com/unicbm/demotracer",
            "https://github.com/unicbm/demotracer/releases",
            "https://github.com/unicbm",
            "https://github.com/ed0ard",
            "https://github.com/Newbie046",
            "https://github.com/XBribo",
            "https://github.com/Misaka17032",
            "https://github.com/T1mLuk0",
            "https://github.com/ianlucas",
            "https://github.com/LaihoE",
            "https://github.com/XBribo/CS2-Bot-Controller",
            "https://github.com/XBribo/CS2-Bot-Hider",
            "https://github.com/ianlucas/cs2-inventory-simulator",
            "https://github.com/ianlucas/cs2-lib",
            "https://github.com/ianlucas/cs2-lib-inspect",
            "https://github.com/LaihoE/demoparser",
        ] {
            assert_eq!(validate_external_url(credit_url).unwrap(), credit_url);
        }
        let payload = "0018830420B804280638D8968FE203408E015A2DE58D83E58FA4E9A38EE6B581E4BB8AE59CA8E6ADA4EFBC8CE4B887E9878CE58A9FE5908DE88EABE694BEE4BC9102C1C58A";
        let legacy_inspect =
            format!("steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20{payload}");
        assert_eq!(
            validate_external_url(&legacy_inspect).unwrap(),
            format!("steam://run/730/en/+csgo_econ_action_preview%20{payload}")
        );

        for rejected in [
            "https://example.com/76561198147750283",
            "https://steamcommunity.com/profiles/76561198147750283/edit",
            "steam://run/730/en/+csgo_econ_action_preview%20not-hex",
            "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%2000112233445",
            "steam://rungame/730/1/+csgo_econ_action_preview%20001122334455",
            "https://inventory.cstrike.app/craft?share=valid",
            "https://github.com/unicbm?tab=repositories",
            "https://github.com/unicbm/unapproved-repository",
            "https://github.com/example/example",
        ] {
            assert_eq!(
                validate_external_url(rejected).unwrap_err().code,
                "external_url_not_allowed"
            );
        }

        let oversized = format!(
            "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20{}",
            "00".repeat(118)
        );
        assert_eq!(
            validate_external_url(&oversized).unwrap_err().code,
            "external_url_not_allowed"
        );
    }

    fn round(round: u32, status: RoundStatus) -> cs2_demotracer::model::RoundSummary {
        cs2_demotracer::model::RoundSummary {
            round,
            start_tick: 100,
            end_tick: 740,
            duration_seconds: 10.0,
            t_players: 5,
            ct_players: 5,
            total_players: 10,
            valid_rows: 100,
            status,
            problems: if status == RoundStatus::Recommended {
                Vec::new()
            } else {
                vec!["test warning".to_string()]
            },
        }
    }

    fn analysis() -> DemoAnalysis {
        DemoAnalysis {
            demo_path: "match.dem".to_string(),
            demo_stem: "match".to_string(),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            row_count: 1000,
            rounds: vec![
                round(1, RoundStatus::Recommended),
                round(2, RoundStatus::Partial),
                round(3, RoundStatus::Suspicious),
            ],
        }
    }

    fn request() -> ConvertDemoRequest {
        ConvertDemoRequest {
            analysis_id: "analysis-1".to_string(),
            output_dir: "output".to_string(),
            selected_rounds: vec![1],
            include_suspicious: false,
            full_round: false,
            side: Side::Both,
            subtick_mode: SubtickMode::Auto,
            max_round_seconds: default_max_round_seconds(),
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_voice: true,
            export_cosmetics: false,
            export_stickers: true,
            export_charms: true,
            cosmetic_consent: None,
            overwrite: OverwriteModeDto::Deny,
        }
    }

    fn converted_file(
        round: u32,
        side: &str,
        steam_id: u64,
        name: &str,
        cosmetics: Option<ReplayCosmetics>,
    ) -> ConvertedFile {
        ConvertedFile {
            path: format!("round{round:02}/{side}/{steam_id}.dtr"),
            round,
            side: side.to_string(),
            steam_id,
            player_name: name.to_string(),
            ticks: 100,
            subticks: 0,
            play_start_tick_index: 0,
            first_weapon_def_index: -1,
            preload_weapon_def_indices: Vec::new(),
            hifi_event_count: 0,
            inventory_snapshot_count: 0,
            loadout: ReplayLoadout::default(),
            music_kit_id: None,
            scoreboard_flair: None,
            cosmetics,
            view: None,
            scoreboard: None,
        }
    }

    fn converted_round(round: u32, files: usize) -> ConvertedRound {
        ConvertedRound {
            round,
            recording_start_tick: 90,
            start_tick: 100,
            end_tick: 740,
            original_end_tick: 740,
            bomb_planted_tick: None,
            bomb_planted_seconds_after_live: None,
            freeze_preroll_ticks: 10,
            duration_seconds: 10.0,
            pistol_round: false,
            cut_reason: None,
            t_economy: TeamEconomy {
                side: "t".to_string(),
                class: EconomyClass::Full,
                ..TeamEconomy::default()
            },
            ct_economy: TeamEconomy {
                side: "ct".to_string(),
                class: EconomyClass::Full,
                ..TeamEconomy::default()
            },
            scoreboard: None,
            chat_messages: Vec::new(),
            files,
        }
    }

    struct ManifestTestDir {
        path: PathBuf,
    }

    impl ManifestTestDir {
        fn new(label: &str) -> Self {
            let unique = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "cs2-demotracer-manifest-{label}-{}-{unique}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self { path }
        }

        fn write_file(&self, relative: &str, bytes: &[u8]) -> PathBuf {
            let path = self
                .path
                .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, bytes).unwrap();
            path
        }

        fn write_dtr(&self, relative: &str, round: u32, side: &str, steam_id: u64) -> PathBuf {
            let path = self
                .path
                .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            let mut recording = cs2_demotracer::dtr::Cs2Rec::default();
            recording.header.map = "de_mirage".to_string();
            recording.header.round = round;
            recording.header.side = if side.eq_ignore_ascii_case("t") { 2 } else { 3 };
            recording.header.steam_id = steam_id;
            recording
                .ticks
                .push(cs2_demotracer::dtr::ReplayTick::default());
            cs2_demotracer::dtr::write_rec_file(&path, &recording).unwrap();
            path
        }

        fn write_manifest(&self, value: serde_json::Value) -> PathBuf {
            let path = self.path.join("manifest.json");
            fs::write(&path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();
            path
        }
    }

    impl Drop for ManifestTestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.path);
        }
    }

    fn write_delete_test_archive(root: &Path) -> PathBuf {
        fs::create_dir_all(root.join("round00/t")).unwrap();
        fs::write(root.join("round00/t/player.dtr"), b"test dtr").unwrap();
        let manifest_path = root.join("manifest.json");
        fs::write(
            &manifest_path,
            serde_json::to_vec_pretty(&serde_json::json!({
                "demo_path": "source.dem",
                "demo_id": "test-demo",
                "demo_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "map": "de_mirage",
                "tick_rate": 64.0,
                "abi": DEMOTRACER_ABI,
                "dtr_format_version": DTR_FORMAT_VERSION,
                "files": [{
                    "round": 0,
                    "side": "t",
                    "steam_id": 1,
                    "player_name": "bot"
                }]
            }))
            .unwrap(),
        )
        .unwrap();
        manifest_path
    }

    #[test]
    fn archive_delete_removes_only_the_archive_folder() {
        let temp = ManifestTestDir::new("delete-archive");
        let library = temp.path.join("library");
        let archive = library.join("de_mirage/test-demo");
        let manifest = write_delete_test_archive(&archive);
        let source_demo = temp.write_file("source.dem", b"original demo");

        delete_archive_for(&DeleteArchiveRequest {
            manifest_path: manifest.display().to_string(),
            library_roots: vec![library.display().to_string()],
        })
        .unwrap();

        assert!(!archive.exists());
        assert_eq!(fs::read(source_demo).unwrap(), b"original demo");
    }

    #[test]
    fn archive_delete_rejects_an_archive_containing_a_demo() {
        let temp = ManifestTestDir::new("delete-archive-demo-guard");
        let library = temp.path.join("library");
        let archive = library.join("de_mirage/test-demo");
        let manifest = write_delete_test_archive(&archive);
        let nested_demo = archive.join("source.dem.zst");
        fs::write(&nested_demo, b"must survive").unwrap();

        let error = delete_archive_for(&DeleteArchiveRequest {
            manifest_path: manifest.display().to_string(),
            library_roots: vec![library.display().to_string()],
        })
        .unwrap_err();

        assert_eq!(error.code, "archive_contains_demo");
        assert!(archive.exists());
        assert_eq!(fs::read(nested_demo).unwrap(), b"must survive");
    }

    #[test]
    fn archive_delete_rejects_a_manifest_outside_the_library() {
        let temp = ManifestTestDir::new("delete-archive-library-guard");
        let library = temp.path.join("library");
        fs::create_dir_all(&library).unwrap();
        let archive = temp.path.join("outside/test-demo");
        let manifest = write_delete_test_archive(&archive);

        let error = delete_archive_for(&DeleteArchiveRequest {
            manifest_path: manifest.display().to_string(),
            library_roots: vec![library.display().to_string()],
        })
        .unwrap_err();

        assert_eq!(error.code, "archive_outside_library");
        assert!(archive.exists());
    }

    #[derive(Default)]
    struct FaultInjectingOutputFileOps {
        rename_calls: usize,
        fail_rename_call: Option<usize>,
        fail_remove_path: Option<PathBuf>,
    }

    impl OutputFileOps for FaultInjectingOutputFileOps {
        fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>> {
            path_metadata(path)
        }

        fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool> {
            file_contents_equal(path, expected)
        }

        fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
            fs::create_dir_all(path)
        }

        fn create_dir(&mut self, path: &Path) -> std::io::Result<()> {
            fs::create_dir(path)
        }

        fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()> {
            self.rename_calls += 1;
            if self.fail_rename_call == Some(self.rename_calls) {
                return Err(std::io::Error::other(format!(
                    "injected rename failure {}",
                    self.rename_calls
                )));
            }
            fs::rename(from, to)
        }

        fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
            if self.fail_remove_path.as_deref() == Some(path) {
                return Err(std::io::Error::other("injected remove failure"));
            }
            fs::remove_dir_all(path)
        }

        fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()> {
            let mut file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)?;
            std::io::Write::write_all(&mut file, bytes)
        }
    }

    fn output_transaction_paths(temp: &ManifestTestDir) -> (PathBuf, PathBuf, PathBuf) {
        let output_dir = temp.path.join("output");
        let final_root = output_dir.join("match-aabbccddeeff");
        let backup_root = output_dir.join(".match-aabbccddeeff.backup");
        (output_dir, final_root, backup_root)
    }

    fn write_transaction_file(root: &Path, name: &str, contents: &[u8]) {
        fs::create_dir_all(root).unwrap();
        fs::write(root.join(name), contents).unwrap();
    }

    fn transaction_file(root: &Path, name: &str) -> Vec<u8> {
        fs::read(root.join(name)).unwrap()
    }

    fn assert_no_staging_directories(output_dir: &Path) {
        let has_staging = fs::read_dir(output_dir).unwrap().any(|entry| {
            entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .contains(".tmp.")
        });
        assert!(!has_staging, "the current transaction staging root leaked");
    }

    fn manifest_file(path: &str, round: u32, side: &str, steam_id: u64) -> serde_json::Value {
        serde_json::json!({
            "path": path,
            "round": round,
            "side": side,
            "steam_id": steam_id,
            "player_name": format!("Player {steam_id}")
        })
    }

    fn manifest_json(
        rounds: Vec<serde_json::Value>,
        files: Vec<serde_json::Value>,
    ) -> serde_json::Value {
        serde_json::json!({
            "demo_path": "C:\\private\\match.dem",
            "demo_id": "match-aabbccddeeff",
            "demo_sha256": "aa".repeat(32),
            "map": "de_mirage",
            "tick_rate": 64.0,
            "abi": DEMOTRACER_ABI,
            "format_version": DTR_FORMAT_VERSION,
            "rounds": rounds,
            "files": files
        })
    }

    fn manifest_round(round: u32, files: usize) -> serde_json::Value {
        serde_json::json!({
            "round": round,
            "duration_seconds": 42.5,
            "pistol_round": round == 0,
            "cut_reason": null,
            "t_economy": {
                "side": "t",
                "players": 5,
                "round_start_equipment_value": 12000,
                "equipment_value_total": 24000,
                "money_saved_total": 1000,
                "cash_spent_this_round": 8000,
                "class": "full"
            },
            "ct_economy": {
                "side": "ct",
                "players": 5,
                "round_start_equipment_value": 13000,
                "equipment_value_total": 25000,
                "money_saved_total": 1200,
                "cash_spent_this_round": 9000,
                "class": "full"
            },
            "files": files
        })
    }

    fn issue_codes(result: &ManifestArchiveDto) -> BTreeSet<&str> {
        result
            .issues
            .iter()
            .map(|issue| issue.code.as_str())
            .collect()
    }

    #[test]
    fn analysis_dto_selects_recommended_and_partial_rounds() {
        let dto = analysis_dto(
            "analysis-1",
            Path::new("C:/demos/match.dem"),
            "match-aabbccddeeff",
            &analysis(),
        );
        assert!(dto.rounds[0].selected_by_default);
        assert!(dto.rounds[1].selected_by_default);
        assert_eq!(dto.rounds[1].status, "partial");
        assert!(!dto.rounds[2].selected_by_default);
        assert_eq!(dto.rounds[2].status, "suspicious");
    }

    #[test]
    fn limited_replay_input_produces_an_actionable_parse_warning() {
        let summary = BrowserReplayInputSummary {
            status: BrowserReplayInputStatus::Limited,
            player_rows: 1_190_657,
            ..BrowserReplayInputSummary::default()
        };

        let warning = replay_input_warning(&summary).expect("limited input warning");
        assert!(warning.contains("command rows 0/1190657"));
        assert!(warning.contains("Source demo"));
        assert!(replay_input_warning(&BrowserReplayInputSummary {
            status: BrowserReplayInputStatus::Available,
            ..BrowserReplayInputSummary::default()
        })
        .is_none());
    }

    #[test]
    fn preflight_output_reports_backend_root_and_existing_state_without_creating_it() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let output_dir = std::env::temp_dir().join(format!(
            "cs2-demotracer-preflight-{}-{unique}",
            std::process::id()
        ));
        let parsed = Arc::new(ParsedDemo {
            stem: "match".to_string(),
            demo_sha256: "aabbccddeeff".repeat(6),
            map: "de_mirage".to_string(),
            ..ParsedDemo::default()
        });
        let browser = analyze_browser_demo(&parsed, AnalysisOptions::default());
        let archive_id = archive_info::archive_directory_name(&parsed, &browser);
        let cached = CachedDemo {
            analysis_id: "analysis-1".to_string(),
            parsed,
            analysis: analysis(),
            browser,
            analysis_options: AnalysisOptions::default(),
            archive_id: archive_id.clone(),
            source_modified_at_ms: None,
            source_size_bytes: None,
        };
        fs::create_dir_all(&output_dir).unwrap();

        let first = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        let expected_parent = canonicalize_public_path(&output_dir)
            .unwrap()
            .join("de_mirage");
        let expected_root = expected_parent.join(&archive_id);
        assert_eq!(PathBuf::from(&first.root), expected_root);
        assert!(!first.exists);
        assert!(output_dir.is_dir());

        fs::create_dir_all(&expected_root).unwrap();
        fs::write(
            expected_root.join("manifest.json"),
            serde_json::to_vec(&serde_json::json!({
                "demo_path": "match.dem",
                "demo_id": archive_id,
                "demo_sha256": cached.parsed.demo_sha256,
                "map": "de_mirage",
                "tick_rate": 64.0,
                "abi": DEMOTRACER_ABI,
                "format_version": DTR_FORMAT_VERSION,
                "rounds": [],
                "files": [{
                    "round": 1,
                    "side": "t",
                    "steam_id": 1,
                    "player_name": "Player"
                }]
            }))
            .unwrap(),
        )
        .unwrap();
        let second = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        assert!(second.exists);

        fs::remove_dir_all(&expected_root).unwrap();
        let backup_root = expected_parent.join(format!(".{archive_id}.backup"));
        fs::create_dir_all(&backup_root).unwrap();
        let recovered = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        assert!(recovered.exists);
        fs::remove_dir_all(output_dir).unwrap();
    }

    #[test]
    fn map_directory_links_are_rejected_before_output_staging() {
        let temp = ManifestTestDir::new("map-link");
        let library_root = temp.path.join("library");
        let outside = temp.path.join("outside");
        fs::create_dir_all(&library_root).unwrap();
        fs::create_dir_all(&outside).unwrap();
        let linked_map = library_root.join("de_mirage");

        #[cfg(unix)]
        if std::os::unix::fs::symlink(&outside, &linked_map).is_err() {
            return;
        }
        #[cfg(windows)]
        if std::os::windows::fs::symlink_dir(&outside, &linked_map).is_err() {
            return;
        }

        let canonical_library = canonical_normal_library_root(&library_root).unwrap();
        let error = checked_library_map_directory(&canonical_library, "de_mirage").unwrap_err();
        assert_eq!(error.code, "map_directory_invalid");
        assert!(outside.read_dir().unwrap().next().is_none());
    }

    #[test]
    fn preflight_extends_hash_when_the_readable_path_is_occupied() {
        let temp = ManifestTestDir::new("hash-collision");
        let output_dir = temp.path.join("library");
        let parsed = Arc::new(ParsedDemo {
            stem: "match".to_string(),
            demo_sha256: "abcdef1234567890".repeat(4),
            map: "de_mirage".to_string(),
            ..ParsedDemo::default()
        });
        let browser = analyze_browser_demo(&parsed, AnalysisOptions::default());
        let archive_id = archive_info::archive_directory_name(&parsed, &browser);
        let cached = CachedDemo {
            analysis_id: "analysis-collision".to_string(),
            parsed,
            analysis: analysis(),
            browser,
            analysis_options: AnalysisOptions::default(),
            archive_id: archive_id.clone(),
            source_modified_at_ms: None,
            source_size_bytes: None,
        };
        fs::create_dir_all(output_dir.join("de_mirage").join(&archive_id)).unwrap();

        let preflight = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();

        assert!(Path::new(&preflight.root)
            .file_name()
            .unwrap()
            .to_string_lossy()
            .ends_with("--abcdef1234567890"));
        assert!(!preflight.exists);
    }

    #[test]
    fn archive_import_copies_into_map_library_and_is_idempotent() {
        let temp = ManifestTestDir::new("archive-import");
        let source_root = temp.path.join("scattered");
        let source_archive = source_root.join("legacy-output");
        let replay_path = source_archive.join("round00").join("t").join("a.dtr");
        fs::create_dir_all(replay_path.parent().unwrap()).unwrap();
        let mut recording = cs2_demotracer::dtr::Cs2Rec::default();
        recording.header.map = "de_mirage".to_string();
        recording.header.round = 0;
        recording.header.side = 2;
        recording.header.steam_id = 101;
        recording
            .ticks
            .push(cs2_demotracer::dtr::ReplayTick::default());
        cs2_demotracer::dtr::write_rec_file(&replay_path, &recording).unwrap();
        fs::write(
            source_archive.join("manifest.json"),
            serde_json::to_vec_pretty(&manifest_json(
                vec![manifest_round(0, 1)],
                vec![manifest_file("round00/t/a.dtr", 0, "t", 101)],
            ))
            .unwrap(),
        )
        .unwrap();
        fs::write(source_archive.join("conversion.log"), b"portable log").unwrap();
        fs::write(
            source_archive.join(OUTPUT_COMPLETION_MARKER),
            b"legacy marker",
        )
        .unwrap();

        let library_root = temp.path.join("library");
        let request = ImportArchivesRequest {
            library_root: library_root.display().to_string(),
            source_root: source_root.display().to_string(),
        };
        let first = import_archives_for(&request).unwrap();

        assert_eq!(first.archives_found, 1);
        assert_eq!(first.archives_imported, 1);
        assert_eq!(first.duplicates_skipped, 0);
        assert_eq!(first.archives_rejected, 0);
        let imported_root = library_root.join("de_mirage").join("match--aaaaaaaaaaaa");
        assert!(imported_root.join("manifest.json").is_file());
        assert!(imported_root.join("round00/t/a.dtr").is_file());
        assert_eq!(
            fs::read(imported_root.join("conversion.log")).unwrap(),
            b"portable log"
        );
        assert_eq!(
            fs::read(imported_root.join(OUTPUT_COMPLETION_MARKER)).unwrap(),
            OUTPUT_COMPLETION_MARKER_CONTENT
        );
        assert!(source_archive.join("manifest.json").is_file());
        assert_eq!(
            fs::read(source_archive.join(OUTPUT_COMPLETION_MARKER)).unwrap(),
            b"legacy marker"
        );

        let second = import_archives_for(&request).unwrap();
        assert_eq!(second.archives_imported, 0);
        assert_eq!(second.duplicates_skipped, 1);
        assert_eq!(second.archives_rejected, 0);
    }

    #[test]
    fn build_and_validation_failures_preserve_existing_output() {
        for code in ["conversion_failed", "validation_failed"] {
            let temp = ManifestTestDir::new(code);
            let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
            write_transaction_file(&final_root, "sentinel.txt", b"old output");
            let mut file_ops = FaultInjectingOutputFileOps::default();

            let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
                &output_dir,
                &final_root,
                OverwriteModeDto::Replace,
                &mut file_ops,
                |staging_root| {
                    write_transaction_file(staging_root, "partial.dtr", b"partial");
                    Err(CommandErrorDto::new(code, "injected failure"))
                },
            );

            assert_eq!(result.unwrap_err().code, code);
            assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
            assert!(!backup_root.exists());
            assert_no_staging_directories(&output_dir);
        }
    }

    #[test]
    fn output_transaction_rejects_a_second_target_writer_before_build() {
        let temp = ManifestTestDir::new("output-lock");
        let (output_dir, final_root, _) = output_transaction_paths(&temp);
        fs::create_dir_all(&output_dir).unwrap();
        let lock_path = output_lock_path(&output_dir, &final_root).unwrap();
        let _lock = crate::target_lock::TargetFileLock::acquire(&lock_path).unwrap();
        let mut file_ops = FaultInjectingOutputFileOps::default();
        let build_called = std::cell::Cell::new(false);

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |_| {
                build_called.set(true);
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_locked");
        assert!(!build_called.get());
    }

    #[test]
    fn completion_marker_failure_preserves_existing_output() {
        let temp = ManifestTestDir::new("marker-failure");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, OUTPUT_COMPLETION_MARKER, b"collision");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "completion_marker_failed");
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn first_rename_failure_leaves_existing_output_in_place() {
        let temp = ManifestTestDir::new("rename-one");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_rename_call: Some(1),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_promote_failed");
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn second_rename_failure_restores_existing_output() {
        let temp = ManifestTestDir::new("rename-two");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_rename_call: Some(2),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_promote_failed");
        assert_eq!(file_ops.rename_calls, 3);
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn backup_cleanup_failure_keeps_valid_new_output_and_reports_warning() {
        let temp = ManifestTestDir::new("backup-cleanup");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_remove_path: Some(backup_root.clone()),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        )
        .unwrap();

        assert!(result.backup_cleanup_warning.is_some());
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert!(final_root.join(OUTPUT_COMPLETION_MARKER).is_file());
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn startup_recovery_restores_backup_before_deny_check() {
        let temp = ManifestTestDir::new("startup-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();
        let build_called = std::cell::Cell::new(false);

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Deny,
            &mut file_ops,
            |_| {
                build_called.set(true);
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_exists");
        assert!(!build_called.get());
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
    }

    #[test]
    fn ambiguous_output_and_backup_without_marker_fail_closed() {
        let temp = ManifestTestDir::new("ambiguous-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"new-unknown");
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |_| unreachable!("ambiguous state must fail before staging"),
        );

        assert_eq!(result.unwrap_err().code, "ambiguous_output_state");
        assert_eq!(
            transaction_file(&final_root, "sentinel.txt"),
            b"new-unknown"
        );
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
    }

    #[test]
    fn incomplete_or_wrong_completion_markers_fail_closed() {
        let cases = vec![
            ("empty", Vec::new()),
            (
                "truncated",
                OUTPUT_COMPLETION_MARKER_CONTENT[..OUTPUT_COMPLETION_MARKER_CONTENT.len() - 1]
                    .to_vec(),
            ),
            ("wrong", vec![b'X'; OUTPUT_COMPLETION_MARKER_CONTENT.len()]),
        ];
        for (label, marker) in cases {
            let temp = ManifestTestDir::new(label);
            let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
            write_transaction_file(&final_root, "candidate.txt", b"new-unknown");
            fs::write(final_root.join(OUTPUT_COMPLETION_MARKER), marker).unwrap();
            write_transaction_file(&backup_root, "sentinel.txt", b"old output");
            let mut file_ops = FaultInjectingOutputFileOps::default();

            let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
                &output_dir,
                &final_root,
                OverwriteModeDto::Replace,
                &mut file_ops,
                |_| unreachable!("invalid marker must fail before staging"),
            );

            assert_eq!(result.unwrap_err().code, "ambiguous_output_state");
            assert_eq!(
                transaction_file(&final_root, "candidate.txt"),
                b"new-unknown"
            );
            assert_eq!(
                transaction_file(&backup_root, "sentinel.txt"),
                b"old output"
            );
        }
    }

    #[test]
    fn completed_output_allows_stale_backup_cleanup_before_deny() {
        let temp = ManifestTestDir::new("completed-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "candidate.txt", b"new output");
        fs::write(
            final_root.join(OUTPUT_COMPLETION_MARKER),
            OUTPUT_COMPLETION_MARKER_CONTENT,
        )
        .unwrap();
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Deny,
            &mut file_ops,
            |_| unreachable!("deny must stop before staging"),
        );

        assert_eq!(result.unwrap_err().code, "output_exists");
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert!(!backup_root.exists());
    }

    #[test]
    fn stale_backup_cleanup_failure_aborts_without_touching_completed_output() {
        let temp = ManifestTestDir::new("stale-cleanup-failure");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "candidate.txt", b"new output");
        fs::write(
            final_root.join(OUTPUT_COMPLETION_MARKER),
            OUTPUT_COMPLETION_MARKER_CONTENT,
        )
        .unwrap();
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_remove_path: Some(backup_root.clone()),
            ..FaultInjectingOutputFileOps::default()
        };

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |_| unreachable!("recovery failure must stop before staging"),
        );

        assert_eq!(result.unwrap_err().code, "output_backup_cleanup_failed");
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
    }

    #[test]
    fn staged_progress_never_exposes_temporary_paths_or_finishes_early() {
        let final_root = Path::new("output/match-aabbccddeeff");
        let progress = public_conversion_progress(
            ConversionProgress::ArtifactsWritingStarted {
                root: "output/.match-aabbccddeeff.tmp.1.nonce".to_string(),
                artifacts: 4,
            },
            final_root,
        )
        .unwrap();
        assert_eq!(
            progress,
            ConversionProgressDto::ArtifactsWritingStarted {
                root: final_root.display().to_string(),
                artifacts: 4,
            }
        );
        assert!(public_conversion_progress(
            ConversionProgress::Finished {
                root: "output/.match-aabbccddeeff.tmp.1.nonce".to_string(),
                manifest_path: "output/.match-aabbccddeeff.tmp.1.nonce/manifest.json".to_string(),
                files_written: 3,
            },
            final_root,
        )
        .is_none());
    }

    #[test]
    fn progress_serializes_steam_id_as_a_string() {
        let progress = ConversionProgressDto::from(ConversionProgress::PlayerWritten {
            round: 1,
            steam_id: 76_561_198_012_345_678,
            player_name: "Player".to_string(),
            side: "t".to_string(),
            path: "round01/t/player.dtr".to_string(),
            ticks: 100,
            subticks: 2,
        });
        let json = serde_json::to_value(progress).unwrap();
        assert_eq!(json["steamId"], "76561198012345678");
    }

    #[test]
    fn suspicious_rounds_require_explicit_opt_in() {
        let selected = BTreeSet::from([3]);
        let error = validate_round_selection(&analysis(), &selected, false).unwrap_err();
        assert_eq!(error.code, "suspicious_rounds_not_allowed");
        validate_round_selection(&analysis(), &selected, true).unwrap();
    }

    #[test]
    fn partial_rounds_do_not_require_suspicious_opt_in() {
        let selected = BTreeSet::from([2]);
        validate_round_selection(&analysis(), &selected, false).unwrap();
    }

    #[test]
    fn conversion_rejects_analysis_option_drift() {
        let parsed = Arc::new(ParsedDemo {
            stem: "match".to_string(),
            demo_sha256: "ab".repeat(32),
            map: "de_mirage".to_string(),
            ..ParsedDemo::default()
        });
        let analysis_options = AnalysisOptions::default();
        let browser = analyze_browser_demo(&parsed, analysis_options);
        let cached = CachedDemo {
            analysis_id: "analysis-options".to_string(),
            parsed,
            analysis: browser.analysis.clone(),
            browser,
            analysis_options,
            archive_id: "match-aabbccddeeff".to_string(),
            source_modified_at_ms: None,
            source_size_bytes: None,
        };
        let mut changed = request();
        changed.max_round_seconds += 1.0;

        let error = prepare_conversion_with_cosmetics(
            &changed,
            &cached,
            CosmeticFlags {
                cosmetics: false,
                stickers: false,
                charms: false,
            },
        )
        .err()
        .expect("analysis option drift must fail");
        assert_eq!(error.code, "analysis_options_changed");
    }

    #[test]
    fn cosmetics_require_exact_confirmation_phrase() {
        let mut request = request();
        request.export_cosmetics = true;
        request.cosmetic_consent = Some(CosmeticConsentDto {
            phrase: "close enough".to_string(),
        });
        assert_eq!(
            validate_cosmetic_request(&request).unwrap_err().code,
            "cosmetic_consent_required"
        );

        request.cosmetic_consent.as_mut().unwrap().phrase =
            COSMETIC_CONFIRMATION_PHRASE.to_string();
        let flags = validate_cosmetic_request(&request).unwrap();
        assert!(flags.cosmetics && flags.stickers && flags.charms);
    }

    #[test]
    fn disabled_cosmetics_gate_detail_flags() {
        let flags = validate_cosmetic_request(&request()).unwrap();
        assert!(!flags.cosmetics && !flags.stickers && !flags.charms);
    }

    #[test]
    fn cosmetic_detail_restriction_preserves_non_cosmetic_player_evidence() {
        let mut players: Vec<BrowserPlayerSummary> = serde_json::from_value(serde_json::json!([
            {
                "name": "Player",
                "steamId": "76561198012345678",
                "side": "CT",
                "team": "a",
                "teamName": "Alpha",
                "score": 12,
                "kills": 8,
                "deaths": 6,
                "assists": 2,
                "mvps": 1,
                "rounds": 12,
                "rows": 100,
                "details": {
                    "headshotKills": 4,
                    "totalDamage": 900,
                    "statsRounds": 12,
                    "crosshairCodes": ["CSGO-AAAAA"],
                    "viewmodels": [{ "fov": 68.0 }],
                    "cosmetics": [{
                        "kind": "weapon",
                        "itemDefIndex": 16,
                        "stickers": [{
                            "slot": 0,
                            "stickerId": 10,
                            "wear": 0.0,
                            "offsetX": 0.0,
                            "offsetY": 0.0
                        }],
                        "charms": [{
                            "slot": 0,
                            "charmId": 20,
                            "offsetX": 0.0,
                            "offsetY": 0.0,
                            "offsetZ": 0.0
                        }],
                        "inspectCommand": "csgo_econ_action_preview 00",
                        "inspectUrl": "steam://rungame/example"
                    }]
                }
            }
        ]))
        .unwrap();

        restrict_player_cosmetic_details(&mut players, true, false, true);
        let details = players[0].details.as_ref().unwrap();
        assert_eq!(details.headshot_kills, Some(4));
        assert_eq!(details.crosshair_codes, ["CSGO-AAAAA"]);
        assert!(details.cosmetics[0].stickers.is_empty());
        assert_eq!(details.cosmetics[0].charms.len(), 1);
        assert!(details.cosmetics[0].inspect_command.is_none());
        assert!(details.cosmetics[0].inspect_url.is_none());

        restrict_player_cosmetic_details(&mut players, false, true, true);
        let details = players[0].details.as_ref().unwrap();
        assert!(details.cosmetics.is_empty());
        assert_eq!(details.total_damage, Some(900));
        assert_eq!(details.viewmodels.len(), 1);
    }

    #[test]
    fn source_demo_resolution_reuses_a_recorded_matching_path() {
        let temp = ManifestTestDir::new("source-pointer");
        let source = temp.write_file("original.dem", b"local demo bytes");
        let expected = sha256_hex(b"local demo bytes");

        let resolved =
            resolve_source_demo(&expected, source.to_str(), "original.dem", None).unwrap();

        assert_eq!(resolved.primary_path(), source);
    }

    #[test]
    fn source_demo_resolution_accepts_any_part_of_a_matching_series() {
        let temp = ManifestTestDir::new("source-series");
        let first = temp.write_file("match-p1.dem", b"part one");
        let second = temp.write_file("match-p2.dem", b"part two");
        let source = resolve_demo_source(&second).unwrap();
        let expected = demo_source_sha256(&source).unwrap();

        let resolved = resolve_source_demo(&expected, second.to_str(), "match.dem", None).unwrap();

        assert_eq!(resolved.primary_path(), first);
        assert_eq!(resolved.parts.len(), 2);
    }

    #[test]
    fn source_demo_resolution_preserves_a_legacy_raw_part_hash() {
        let temp = ManifestTestDir::new("legacy-source-series");
        temp.write_file("match-p1.dem", b"part one");
        let second = temp.write_file("match-p2.dem", b"part two");
        let expected = sha256_hex(b"part two");

        let resolved =
            resolve_source_demo(&expected, second.to_str(), "match-p2.dem", None).unwrap();

        assert_eq!(resolved.primary_path(), second);
        assert_eq!(resolved.parts.len(), 1);
    }

    #[test]
    fn source_demo_resolution_returns_a_relocatable_hint_only_when_needed() {
        let missing = PathBuf::from(r"C:\Moved\original.dem");
        let error = resolve_source_demo(&"aa".repeat(32), missing.to_str(), "original.dem", None)
            .unwrap_err();

        assert_eq!(error.code, "source_demo_unavailable");
        assert_eq!(error.path.as_deref(), missing.to_str());
    }

    #[test]
    fn source_demo_resolution_never_accepts_a_wrong_hash() {
        let temp = ManifestTestDir::new("source-hash-mismatch");
        let source = temp.write_file("wrong.dem", b"wrong demo");
        let error = resolve_source_demo(&"aa".repeat(32), None, "original.dem", source.to_str())
            .unwrap_err();

        assert_eq!(error.code, "metadata_demo_hash_mismatch");
        assert_eq!(error.path.as_deref(), source.to_str());
    }

    #[test]
    fn archive_source_resolution_uses_the_manifest_hash() {
        let temp = ManifestTestDir::new("archive-source-resolution");
        let source = temp.write_file("original.dem", b"matching local demo");
        let mut manifest = manifest_json(
            vec![manifest_round(0, 1)],
            vec![manifest_file("round00/t/player.dtr", 0, "t", 1)],
        );
        manifest["demo_path"] = serde_json::json!("original.dem");
        manifest["demo_sha256"] = serde_json::json!(sha256_hex(b"matching local demo"));
        let manifest_path = temp.write_manifest(manifest);

        let resolved = resolve_archive_source_for(&manifest_path, source.to_str()).unwrap();

        assert_eq!(PathBuf::from(resolved.source_path), source);
        assert_eq!(
            archive_info::read_demo_source_path(&temp.path, &sha256_hex(b"matching local demo"))
                .as_deref(),
            source.to_str()
        );
    }

    #[test]
    fn library_refresh_returns_verified_current_sources_to_the_machine_index() {
        let temp = ManifestTestDir::new("verified-current-source");
        let source = temp.write_file("source.dem", b"current source demo");
        let demo_sha256 = sha256_hex(b"current source demo");
        let mut manifest = manifest_json(
            vec![manifest_round(0, 1)],
            vec![manifest_file("round00/t/player.dtr", 0, "t", 1)],
        );
        manifest["demo_path"] = serde_json::json!("source.dem");
        manifest["demo_sha256"] = serde_json::json!(demo_sha256.clone());
        temp.write_manifest(manifest);
        temp.write_file(
            "demo-info.json",
            &serde_json::to_vec(&serde_json::json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "source-aabbccddeeff",
                "demoSha256": demo_sha256.clone(),
                "displayName": "Current source",
                "sourceFileName": "source.dem",
                "sourceFileDateIsApproximate": false,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 0.0,
                "durationEvidence": "observedTicks",
                "scoreEvidence": "unavailable",
                "players": [],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "test",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        );
        archive_info::write_demo_source_pointer(&temp.path, &demo_sha256, &source).unwrap();

        let result = refresh_library_metadata_for(&RefreshLibraryMetadataRequest {
            library_root: temp.path.display().to_string(),
            demo_root: None,
            source_paths: BTreeMap::new(),
        })
        .unwrap();

        assert_eq!(result.archives_current, 1);
        assert_eq!(
            result.source_paths.get(&demo_sha256).map(String::as_str),
            source.to_str()
        );
    }

    #[test]
    fn metadata_refresh_preserves_matching_conversion_intent() {
        let temp = ManifestTestDir::new("preserve-conversion-intent");
        let manifest_path = temp.write_manifest(manifest_json(Vec::new(), Vec::new()));
        let manifest_sha256 = sha256_hex(&fs::read(&manifest_path).unwrap());
        temp.write_file(
            "demo-info.json",
            &serde_json::to_vec(&serde_json::json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": 3,
                "demoId": "match-aabbccddeeff",
                "demoSha256": "aa".repeat(32),
                "displayName": "Match",
                "sourceFileName": "match.dem",
                "sourceFileDateIsApproximate": false,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 0.0,
                "durationEvidence": "observedTicks",
                "scoreEvidence": "unavailable",
                "players": [],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "conversion": {
                    "converterVersion": "0.7.2",
                    "selectedRounds": [0, 1],
                    "side": "both",
                    "fullRound": false,
                    "includeSuspicious": false,
                    "freezePrerollSeconds": 10.0,
                    "voice": true,
                    "cosmetics": true,
                    "stickers": false,
                    "charms": false,
                    "manifestSha256": manifest_sha256
                },
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "conversion",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        );

        let conversion =
            preserved_conversion_for_manifest(&temp.path, &"aa".repeat(32), &manifest_path)
                .unwrap();

        assert!(matches!(
            archive_info::read_demo_info(&temp.path, &"aa".repeat(32)),
            archive_info::DemoInfoRead::Current(_)
        ));
        assert!(conversion.voice);
        assert!(conversion.cosmetics);
        assert_eq!(conversion.selected_rounds, vec![0, 1]);
    }

    #[test]
    fn metadata_refresh_preserves_unbound_intent_without_claiming_manifest_binding() {
        let temp = ManifestTestDir::new("migrate-unbound-conversion-intent");
        let manifest_path = temp.write_manifest(manifest_json(Vec::new(), Vec::new()));
        temp.write_file(
            "demo-info.json",
            &serde_json::to_vec(&serde_json::json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "match-aabbccddeeff",
                "demoSha256": "aa".repeat(32),
                "displayName": "Match",
                "sourceFileName": "match.dem",
                "sourceFileDateIsApproximate": false,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 0.0,
                "durationEvidence": "observedTicks",
                "scoreEvidence": "unavailable",
                "players": [],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "conversion": {
                    "converterVersion": "0.7.1",
                    "selectedRounds": [3, 4],
                    "side": "both",
                    "fullRound": true,
                    "includeSuspicious": false,
                    "freezePrerollSeconds": 10.0,
                    "voice": false,
                    "cosmetics": false,
                    "stickers": false,
                    "charms": false
                },
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.1",
                    "reason": "conversion",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        );

        let conversion =
            preserved_conversion_for_manifest(&temp.path, &"aa".repeat(32), &manifest_path)
                .unwrap();

        assert_eq!(conversion.selected_rounds, vec![3, 4]);
        assert!(conversion.full_round);
        assert!(conversion.manifest_sha256.is_none());
        assert!(conversion.manifest_bytes.is_none());
    }

    #[test]
    fn commands_use_exported_round_and_expected_prefix_order() {
        let commands = build_commands(
            Path::new("output/demo/manifest.json"),
            Some(7),
            2,
            Some("full"),
        );
        assert_eq!(
            commands.go_sequence,
            "dtr_go seq \"output/demo/manifest.json\" 7"
        );
        assert_eq!(
            commands.round,
            "dtr_voice_auto on; dtr_go round \"output/demo/manifest.json\" 7"
        );
        assert_eq!(
            commands.cosmetic_sequence.as_deref(),
            Some(
                "dtr_voice_auto on; dtr_cosmetics full; dtr_go seq \"output/demo/manifest.json\" 7"
            )
        );
    }

    #[test]
    fn player_summary_preserves_large_steam_ids_as_strings() {
        let steam_id = 76_561_198_012_345_678;
        let files = vec![
            converted_file(1, "t", steam_id, "Player", None),
            converted_file(2, "ct", steam_id, "Player", None),
        ];
        let players = summarize_exported_players(&files);
        assert_eq!(players[0].steam_id, "76561198012345678");
        assert_eq!(players[0].rounds, 2);
        assert_eq!(players[0].team, 1);
    }

    #[test]
    fn conversion_summary_reports_full_cosmetic_preset() {
        let cosmetics = ReplayCosmetics {
            weapons: vec![ReplayWeaponCosmetic {
                weapon_def_index: 7,
                paint_kit: 1,
                seed: 2,
                wear: 0.1,
                quality: None,
                stattrak_counter: None,
                original_owner_steam_id: None,
                item_account_id: None,
                item_id: None,
                custom_name: None,
                inspect: None,
                stickers: vec![ReplayWeaponSticker {
                    slot: 0,
                    sticker_id: 10,
                    wear: 0.0,
                    offset_x: 0.0,
                    offset_y: 0.0,
                    scale: None,
                    rotation: None,
                }],
                charms: vec![ReplayWeaponCharm {
                    slot: 0,
                    charm_id: 20,
                    offset_x: 0.0,
                    offset_y: 0.0,
                    offset_z: 0.0,
                    seed: None,
                    highlight: None,
                    sticker_id: None,
                }],
            }],
            knife: Some(ReplayItemCosmetic {
                item_def_index: Some(500),
                paint_kit: 1,
                seed: 2,
                seed_known: None,
                wear: 0.1,
                custom_name: None,
                inspect: None,
            }),
            glove: None,
            agent: None,
        };
        let manifest = ConversionManifest {
            demo_path: "match.dem".to_string(),
            demo_id: "match-aabbccddeeff".to_string(),
            demo_sha256: "aa".repeat(32),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            abi: 17,
            format_version: DTR_FORMAT_VERSION,
            avatar_overrides: Vec::new(),
            rounds: vec![converted_round(7, 1)],
            files: vec![converted_file(
                7,
                "t",
                76_561_198_012_345_678,
                "Player",
                Some(cosmetics),
            )],
        };
        let report = ConversionReport {
            root: PathBuf::from("output/demo"),
            manifest_path: PathBuf::from("output/demo/manifest.json"),
            files_written: 1,
            manifest,
        };
        let summary = summarize_conversion(report, 1, true, 1, true, true, true);
        assert_eq!(summary.cosmetics.preset.as_deref(), Some("full"));
        assert_eq!(summary.cosmetics.requested, Some(true));
        assert_eq!(summary.cosmetics.sticker_requested, Some(true));
        assert_eq!(summary.cosmetics.charm_requested, Some(true));
        assert_eq!(summary.first_exported_round, Some(7));
        assert_eq!(summary.players[0].steam_id, "76561198012345678");
    }

    #[test]
    fn read_manifest_uses_files_as_round_truth_and_summarizes_safe_artifacts() {
        let temp = ManifestTestDir::new("valid");
        temp.write_dtr("round00/t/a.dtr", 0, "t", 76_561_198_012_345_678);
        temp.write_dtr("round00/ct/b.dtr", 0, "ct", 76_561_198_012_345_679);
        temp.write_file("voice/round00.dtv", b"voice");
        let mut first = manifest_file("round00/t/a.dtr", 0, "t", 76_561_198_012_345_678);
        first["cosmetics"] = serde_json::json!({
            "weapons": [{ "stickers": [{ "slot": 0, "sticker_id": 10 }] }]
        });
        first["ticks"] = serde_json::json!(2400);
        first["subticks"] = serde_json::json!(80);
        first["hifi_event_count"] = serde_json::json!(12);
        first["scoreboard"] = serde_json::json!({
            "player_color": "yellow", "score": 31, "kills": 20, "deaths": 10, "assists": 5, "mvps": 3
        });
        let mut round = manifest_round(0, 2);
        round["scoreboard"] = serde_json::json!({
            "t_score": 1, "ct_score": 0, "t_team_name": "Alpha", "ct_team_name": "Bravo"
        });
        round["bomb_planted_seconds_after_live"] = serde_json::json!(37.25);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![round, manifest_round(99, 1)],
            vec![
                first,
                manifest_file("round00/ct/b.dtr", 0, "ct", 76_561_198_012_345_679),
            ],
        ));
        let manifest_sha256 = sha256_hex(&fs::read(&manifest_path).unwrap());
        temp.write_file(
            "demo-info.json",
            &serde_json::to_vec_pretty(&serde_json::json!({
                "schemaVersion": 1,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "match-aabbccddeeff",
                "demoSha256": "aa".repeat(32),
                "displayName": "Alpha vs Bravo",
                "sourceFileName": "match.dem",
                "sourceFilePath": "C:\\Demos\\match.dem",
                "sourceFileModifiedAtMs": 1_725_000_000_000_u64,
                "sourceFileDateIsApproximate": true,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 1800.0,
                "durationEvidence": "demoFileInfo",
                "demoPatchVersion": 14228,
                "demoVersionName": "1.42.2.8",
                "demoSource": { "name": "Faceit", "evidence": "serverName" },
                "score": {
                    "teamA": { "score": 13, "name": "Alpha" },
                    "teamB": { "score": 8, "name": "Bravo" },
                    "status": "final"
                },
                "scoreEvidence": "roundEndEvents",
                "players": [{
                    "name": "Alpha One",
                    "steamId": "76561198012345678",
                    "side": "t",
                    "team": "a",
                    "teamName": "Alpha",
                    "score": 31,
                    "kills": 20,
                    "deaths": 10,
                    "assists": 5,
                    "mvps": 3,
                    "rounds": 21,
                    "rows": 120000
                }],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "conversion": {
                    "converterVersion": "0.7.2",
                    "selectedRounds": [0],
                    "side": "both",
                    "fullRound": false,
                    "includeSuspicious": false,
                    "freezePrerollSeconds": 20.0,
                    "voice": true,
                    "cosmetics": true,
                    "stickers": true,
                    "charms": false,
                    "manifestSha256": manifest_sha256
                },
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "test",
                    "generatedAtMs": 1_725_000_000_000_u64
                }
            }))
            .unwrap(),
        );

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(result.playable);
        assert_eq!(result.compatibility, "current");
        assert_eq!(result.demo_path, "match.dem");
        assert_eq!(result.total_files, 2);
        assert_eq!(result.playable_files, 2);
        assert_eq!(result.players[0].steam_id, "76561198012345678");
        assert_eq!(result.players[0].name, "Alpha One");
        assert_eq!(result.players[0].player_color.as_deref(), Some("yellow"));
        assert_eq!(result.players[0].kills, Some(20));
        assert_eq!(result.display_name, "Alpha vs Bravo");
        assert_eq!(result.metadata_status, "current");
        assert_eq!(result.source_path.as_deref(), Some(r"C:\Demos\match.dem"));
        assert_eq!(result.demo_source.as_ref().unwrap().name, "Faceit");
        assert_eq!(result.score.as_ref().unwrap().team_a.score, 13);
        assert_eq!(result.voice.requested, Some(true));
        assert_eq!(result.voice.rounds, vec![0]);
        assert_eq!(result.cosmetics.requested, Some(true));
        assert_eq!(result.cosmetics.sticker_requested, Some(true));
        assert_eq!(result.cosmetics.charm_requested, Some(false));
        assert_eq!(result.cosmetics.preset.as_deref(), Some("full"));
        assert_eq!(result.rounds.len(), 1);
        assert_eq!(result.rounds[0].round, 0);
        assert_eq!(result.rounds[0].t_files, 1);
        assert_eq!(result.rounds[0].ct_files, 1);
        assert_eq!(result.rounds[0].t_steam_ids, ["76561198012345678"]);
        assert_eq!(result.rounds[0].ct_steam_ids, ["76561198012345679"]);
        assert_eq!(result.rounds[0].cosmetic_files, 1);
        assert_eq!(result.rounds[0].sticker_files, 1);
        assert_eq!(result.rounds[0].charm_files, 0);
        assert_eq!(result.rounds[0].ticks, 2400);
        assert_eq!(result.rounds[0].subticks, 80);
        assert_eq!(result.rounds[0].hifi_events, 12);
        assert_eq!(result.rounds[0].bomb_planted_seconds, Some(37.25));
        assert_eq!(result.rounds[0].scoreboard.as_ref().unwrap().t_score, 1);
        assert_eq!(result.rounds[0].sequence_length, 1);
        assert!(result.rounds[0].available);
        assert!(result.rounds[0]
            .commands
            .go_round
            .ends_with(&format!("\"{}\" 0", manifest_path.display())));
        assert!(result.output_bytes.parse::<u64>().unwrap() > 0);
        assert!(issue_codes(&result).contains("manifest_round_without_files"));
    }

    #[test]
    fn read_manifest_marks_round_unavailable_when_any_declared_file_is_missing() {
        let temp = ManifestTestDir::new("missing");
        temp.write_dtr("round07/t/a.dtr", 7, "t", 101);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(7, 3), manifest_round(8, 1)],
            vec![
                manifest_file("round07/t/a.dtr", 7, "t", 101),
                manifest_file("round07/ct/missing.dtr", 7, "ct", 102),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.voice.requested, None);
        assert_eq!(result.cosmetics.requested, None);
        assert_eq!(result.total_files, 2);
        assert_eq!(result.playable_files, 1);
        assert_eq!(result.rounds.len(), 1);
        assert!(!result.rounds[0].available);
        assert_eq!(result.rounds[0].sequence_length, 0);
        assert!(result.rounds[0]
            .commands
            .go_sequence
            .ends_with(&format!("\"{}\" 7", manifest_path.display())));
        let codes = issue_codes(&result);
        assert!(codes.contains("manifest_file_missing"));
        assert!(codes.contains("manifest_round_file_count_mismatch"));
        assert!(codes.contains("manifest_round_without_files"));
    }

    #[test]
    fn read_manifest_disables_sequence_across_an_unavailable_round() {
        let temp = ManifestTestDir::new("sequence-gap");
        temp.write_dtr("round01/t/a.dtr", 1, "t", 101);
        temp.write_dtr("round03/ct/c.dtr", 3, "ct", 103);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![
                manifest_round(1, 1),
                manifest_round(2, 1),
                manifest_round(3, 1),
            ],
            vec![
                manifest_file("round01/t/a.dtr", 1, "t", 101),
                manifest_file("round02/t/missing.dtr", 2, "t", 102),
                manifest_file("round03/ct/c.dtr", 3, "ct", 103),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(result.playable);
        assert_eq!(result.rounds.len(), 3);
        assert!(result.rounds[0].available);
        assert_eq!(result.rounds[0].sequence_length, 0);
        assert!(!result.rounds[1].available);
        assert_eq!(result.rounds[1].sequence_length, 0);
        assert!(result.rounds[2].available);
        assert_eq!(result.rounds[2].sequence_length, 1);
    }

    #[test]
    fn read_manifest_materializes_a_missing_round_number_as_an_unavailable_gap() {
        let temp = ManifestTestDir::new("sequence-number-gap");
        temp.write_dtr("round10/t/a.dtr", 10, "t", 101);
        temp.write_dtr("round12/ct/c.dtr", 12, "ct", 103);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(10, 1), manifest_round(12, 1)],
            vec![
                manifest_file("round10/t/a.dtr", 10, "t", 101),
                manifest_file("round12/ct/c.dtr", 12, "ct", 103),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(result.playable);
        assert_eq!(result.rounds.len(), 3);
        assert_eq!(result.rounds[0].round, 10);
        assert!(result.rounds[0].available);
        assert_eq!(result.rounds[0].sequence_length, 0);
        assert_eq!(result.rounds[1].round, 11);
        assert!(!result.rounds[1].available);
        assert_eq!(result.rounds[1].files, 0);
        assert_eq!(result.rounds[1].sequence_length, 0);
        assert_eq!(result.rounds[2].round, 12);
        assert!(result.rounds[2].available);
        assert_eq!(result.rounds[2].sequence_length, 1);
        assert!(issue_codes(&result).contains("manifest_round_gap"));
    }

    #[test]
    fn read_manifest_rejects_corrupt_dtr_payloads() {
        let temp = ManifestTestDir::new("corrupt-dtr");
        temp.write_file("round04/t/a.dtr", b"not a replay");
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(4, 1)],
            vec![manifest_file("round04/t/a.dtr", 4, "t", 401)],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.playable_files, 0);
        assert_eq!(result.output_bytes, "0");
        assert!(issue_codes(&result).contains("manifest_file_invalid_dtr"));
    }

    #[test]
    fn read_manifest_reports_duplicate_escape_and_invalid_side_paths() {
        let temp = ManifestTestDir::new("unsafe");
        temp.write_dtr("round01/t/a.dtr", 1, "t", 201);
        temp.write_dtr("round01/t/b.dtr", 1, "t", 203);
        temp.write_dtr("round02/t/c.dtr", 2, "t", 205);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(1, 4), manifest_round(2, 1)],
            vec![
                manifest_file("round01/t/a.dtr", 1, "t", 201),
                manifest_file("round01/t/a.dtr", 1, "t", 202),
                manifest_file("round01/t/b.dtr", 1, "spectator", 203),
                manifest_file("../escape.dtr", 1, "ct", 204),
                manifest_file("round02/t/c.dtr", 2, "t", 205),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.playable_files, 2);
        assert!(result.rounds.iter().all(|round| !round.available));
        let codes = issue_codes(&result);
        assert!(codes.contains("manifest_file_path_duplicate"));
        assert!(codes.contains("manifest_file_target_duplicate"));
        assert!(codes.contains("manifest_file_side_invalid"));
        assert!(codes.contains("manifest_file_path_escape"));
    }

    #[test]
    fn read_manifest_honors_format_alias_and_rejects_newer_abi() {
        let temp = ManifestTestDir::new("versions");
        temp.write_dtr("round03/t/a.dtr", 3, "t", 301);
        let mut value = manifest_json(
            vec![manifest_round(3, 1)],
            vec![manifest_file("round03/t/a.dtr", 3, "t", 301)],
        );
        value["abi"] = serde_json::json!(15);
        value["format_version"] = serde_json::json!(999);
        value["dtr_format_version"] = serde_json::json!(6);
        let manifest_path = temp.write_manifest(value.clone());
        let supported = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(supported.compatibility, "supported");
        assert_eq!(supported.format_version, 6);
        assert!(supported.playable);

        value["dtr_format_version"] = serde_json::json!(0);
        temp.write_manifest(value.clone());
        let fallback = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(fallback.format_version, 999);
        assert_eq!(fallback.compatibility, "unsupported");
        assert!(issue_codes(&fallback).contains("manifest_format_unsupported"));

        value["dtr_format_version"] = serde_json::json!(6);
        value["abi"] = serde_json::json!(DEMOTRACER_ABI + 1);
        temp.write_manifest(value);
        let unsupported = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(unsupported.compatibility, "unsupported");
        assert!(!unsupported.playable);
        assert!(issue_codes(&unsupported).contains("manifest_abi_unsupported"));
    }

    #[test]
    fn read_manifest_rejects_unrelated_json() {
        let temp = ManifestTestDir::new("unrelated-json");
        let path = temp.write_manifest(serde_json::json!({ "name": "not a manifest" }));
        let error = read_manifest_for(&path.display().to_string()).unwrap_err();
        assert_eq!(error.code, "manifest_schema_invalid");
    }
}
