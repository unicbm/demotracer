/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use cs2_demotracer::demo_id::sha256_hex;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

pub(crate) const INSTALL_RECEIPT_RELATIVE_PATH: &str = "addons/demotracer-install.v1.json";
const RUNTIME_HEALTH_RELATIVE_PATH: &str =
    "addons/counterstrikesharp/plugins/DemoTracer/demotracer-runtime.v1.json";
const MAX_TEXT_FILE_BYTES: u64 = 4 * 1024 * 1024;
const MAX_RUNTIME_HEALTH_BYTES: u64 = 1024 * 1024;
const MAX_RUNTIME_HEALTH_AGE_MS: u64 = 30_000;
const MAX_RUNTIME_HEALTH_FUTURE_SKEW_MS: u64 = 60_000;
pub(crate) const MAX_RECEIPT_FILES: usize = 256;
pub(crate) const MAX_RECEIPT_FILE_BYTES: u64 = 128 * 1024 * 1024;
pub(crate) const REQUIRED_RECEIPT_PATHS: &[&str] = &[
    "addons/botcontroller/bin/win64/botcontroller.dll",
    "addons/botcontroller/gamedata.json",
    "addons/metamod/botcontroller.vdf",
    "addons/counterstrikesharp/plugins/botcontrollerimpl/botcontrollerimpl.dll",
    "addons/counterstrikesharp/shared/botcontrollerapi/botcontrollerapi.dll",
    "addons/bothider/bin/win64/bothider.dll",
    "addons/bothider/gamedata.json",
    "addons/metamod/bothider.vdf",
    "addons/counterstrikesharp/plugins/demotracer/demotracer.dll",
    "addons/counterstrikesharp/shared/demotracerapi/demotracerapi.dll",
    "addons/counterstrikesharp/plugins/demotracer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/bothiderimpl/bothiderimpl.dll",
    "addons/counterstrikesharp/shared/demotracerbothiderapi/demotracerbothiderapi.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/botrandomizer.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/cosmetic_catalog.json",
    "addons/counterstrikesharp/plugins/botrandomizer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/botrandomizer/charm_placements.json",
    "addons/counterstrikesharp/shared/botrandomizerapi/botrandomizerapi.dll",
    "addons/counterstrikesharp/shared/0harmony/0harmony.dll",
];
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) enum DiagnosticStatus {
    Pass,
    Warning,
    Error,
    Unverified,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Cs2InstallCandidateDto {
    pub path: String,
    pub game_csgo_path: String,
    pub source: String,
    pub label: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EnvironmentDiagnosticReportDto {
    pub checked_at_ms: u64,
    pub cs2_root: String,
    pub overall: DiagnosticStatus,
    pub checks: Vec<DiagnosticCheckDto>,
    pub receipt: InstallReceiptSummaryDto,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DiagnosticCheckDto {
    pub id: String,
    pub group: String,
    pub status: DiagnosticStatus,
    pub title: String,
    pub summary: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expected: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub actual: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub evidence_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct InstallReceiptSummaryDto {
    pub found: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bundle_version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_controller_abi: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_controller_minor: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_hider_api: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_tracer_api: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub verified: Option<bool>,
    pub files_checked: usize,
    pub files_mismatched: usize,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct InstallReceiptWire {
    pub(crate) schema_version: u32,
    pub(crate) product: String,
    pub(crate) bundle_version: String,
    #[allow(dead_code)]
    pub(crate) git_commit: Option<String>,
    pub(crate) platform: String,
    pub(crate) compatibility: PlaybackContractWire,
    pub(crate) files: Vec<ReceiptFileWire>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct ReceiptFileWire {
    pub(crate) path: String,
    pub(crate) component: String,
    pub(crate) size: u64,
    pub(crate) sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct PlaybackContractWire {
    pub(crate) schema_version: u32,
    pub(crate) product: String,
    pub(crate) platform: String,
    pub(crate) manifest_abi: i32,
    pub(crate) dtr_writer: u32,
    #[serde(default)]
    pub(crate) dtr_section_writer_codec: String,
    #[serde(default)]
    pub(crate) dtr_section_zstd_level: i32,
    pub(crate) dtr_reader: DtrReaderContractWire,
    pub(crate) bot_controller: BotControllerContractWire,
    pub(crate) bot_hider: BotHiderContractWire,
    pub(crate) bot_randomizer: BotRandomizerContractWire,
    pub(crate) demotracer: DemoTracerContractWire,
    pub(crate) counterstrikesharp: CounterStrikeSharpContractWire,
    #[serde(default)]
    pub(crate) hook_runtime: Option<HookRuntimeContractWire>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct HookRuntimeContractWire {
    pub(crate) backend: String,
    pub(crate) metamod_minimum_build: u32,
    pub(crate) metamod_plugin_api: u32,
    pub(crate) metamod_source_commit: String,
    pub(crate) khook_source_commit: String,
    pub(crate) counterstrikesharp_source_commit: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct DtrReaderContractWire {
    pub(crate) min: u32,
    pub(crate) max: u32,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct BotControllerContractWire {
    pub(crate) abi_major: i32,
    pub(crate) min_abi_minor: i32,
    #[serde(default)]
    pub(crate) movement_intent_version: i32,
    #[serde(default)]
    pub(crate) public_control_api: i32,
    #[serde(default)]
    pub(crate) replay_tick_bytes: u32,
    #[serde(default)]
    pub(crate) replay_tick_event_tail: String,
    #[serde(default)]
    pub(crate) managed_provider_version: String,
    pub(crate) required_capabilities_hex: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct BotHiderContractWire {
    pub(crate) api: i32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub(crate) clan_tag_max_utf8_bytes: Option<u32>,
    #[serde(default)]
    pub(crate) native_abi: i32,
    #[serde(default)]
    pub(crate) native_slot_bytes: u32,
    #[serde(default)]
    pub(crate) native_provider_version: String,
    #[serde(default)]
    pub(crate) managed_provider_version: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct BotRandomizerContractWire {
    pub(crate) api: i32,
    pub(crate) provider_version: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct DemoTracerContractWire {
    pub(crate) companion_api: i32,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct CounterStrikeSharpContractWire {
    pub(crate) minimum_version: String,
    pub(crate) target_framework: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeHealthWire {
    schema_version: u32,
    written_at_ms: u64,
    running: bool,
    plugin_version: String,
    demo_tracer_api: i32,
    counter_strike_sharp_version: String,
    bot_controller: RuntimeBotControllerWire,
    bot_hider: RuntimeBotHiderWire,
    bot_randomizer: RuntimeBotRandomizerWire,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeBotControllerWire {
    abi_major: i32,
    abi_minor: i32,
    capabilities: String,
    build_id: String,
    compatible: bool,
    required_capabilities: RuntimeRequiredCapabilitiesWire,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeRequiredCapabilitiesWire {
    mask: String,
    present: bool,
    missing: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeBotHiderWire {
    provider_api: Option<i32>,
    connected: bool,
    draining: bool,
    available: bool,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeBotRandomizerWire {
    provider_api: Option<i32>,
    ready: bool,
    draining: bool,
    replay_plan_prebuild_available: bool,
    available: bool,
}

#[derive(Debug)]
pub(crate) struct InstallPaths {
    pub(crate) cs2_root: PathBuf,
    pub(crate) game_csgo: PathBuf,
}

#[derive(Debug, Default)]
struct RuntimeAudit {
    plugin_version: Option<String>,
    checks: Vec<DiagnosticCheckDto>,
}

#[tauri::command]
pub(crate) async fn choose_cs2_dir(initial_path: Option<String>) -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new().set_title("Choose a local CS2 or server folder");
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
pub(crate) async fn detect_cs2_installations() -> CommandResult<Vec<Cs2InstallCandidateDto>> {
    tauri::async_runtime::spawn_blocking(detect_cs2_installations_for)
        .await
        .map_err(|error| CommandErrorDto::new("cs2_detection_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn inspect_cs2_install(
    path: String,
) -> CommandResult<EnvironmentDiagnosticReportDto> {
    tauri::async_runtime::spawn_blocking(move || inspect_cs2_install_for(&path))
        .await
        .map_err(|error| CommandErrorDto::new("cs2_inspection_worker_failed", error.to_string()))?
}

fn detect_cs2_installations_for() -> CommandResult<Vec<Cs2InstallCandidateDto>> {
    let mut steam_roots = registry_steam_roots();
    for variable in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Some(value) = std::env::var_os(variable) {
            steam_roots.push(PathBuf::from(value).join("Steam"));
        }
    }
    steam_roots.push(PathBuf::from(r"C:\Program Files (x86)\Steam"));

    let mut candidates = Vec::new();
    let mut seen = BTreeSet::new();
    for steam_root in unique_existing_directories(steam_roots) {
        for library in steam_library_roots(&steam_root) {
            let manifest_path = library.join("steamapps").join("appmanifest_730.acf");
            let Ok(text) = read_small_text(&manifest_path, MAX_TEXT_FILE_BYTES) else {
                continue;
            };
            let Some(install_dir) = vdf_value(&text, "installdir") else {
                continue;
            };
            let cs2_root = library.join("steamapps").join("common").join(install_dir);
            let Ok(paths) = resolve_install_paths(&cs2_root) else {
                continue;
            };
            let key = path_key(&paths.game_csgo);
            if !seen.insert(key) {
                continue;
            }
            let drive = paths
                .cs2_root
                .components()
                .next()
                .map(|component| component.as_os_str().to_string_lossy().into_owned())
                .unwrap_or_else(|| "Steam".to_string());
            candidates.push(Cs2InstallCandidateDto {
                path: paths.cs2_root.display().to_string(),
                game_csgo_path: paths.game_csgo.display().to_string(),
                source: "steam".to_string(),
                label: format!("Counter-Strike 2 ({drive})"),
            });
        }
    }
    candidates.sort_by(|left, right| left.game_csgo_path.cmp(&right.game_csgo_path));
    Ok(candidates)
}

fn inspect_cs2_install_for(requested_path: &str) -> CommandResult<EnvironmentDiagnosticReportDto> {
    let paths = resolve_install_paths(Path::new(requested_path.trim()))?;
    let mut checks = Vec::new();
    let game_csgo = &paths.game_csgo;

    checks.push(metamod_files_check(game_csgo));
    checks.push(counterstrikesharp_files_check(game_csgo));
    checks.append(&mut inspect_runtime_health(game_csgo).checks);
    let receipt = inspect_install_receipt(game_csgo, &mut checks);
    Ok(EnvironmentDiagnosticReportDto {
        checked_at_ms: now_ms(),
        cs2_root: paths.cs2_root.display().to_string(),
        overall: overall_status(&checks),
        checks,
        receipt,
    })
}

pub(crate) fn resolve_install_paths(input: &Path) -> CommandResult<InstallPaths> {
    if input.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "cs2_path_empty",
            "Choose or enter a local CS2 folder before scanning.",
        ));
    }
    if !input.is_absolute() {
        return Err(CommandErrorDto::at_path(
            "cs2_path_not_absolute",
            "The CS2 path must be absolute.",
            input,
        ));
    }
    let metadata = fs::symlink_metadata(input).map_err(|error| {
        CommandErrorDto::at_path("cs2_path_unavailable", error.to_string(), input)
    })?;
    if !metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "cs2_path_not_normal_directory",
            "The selected CS2 path must be a normal local directory, not a link or junction.",
            input,
        ));
    }

    let candidates = [
        input.to_path_buf(),
        input.join("csgo"),
        input.join("game").join("csgo"),
    ];
    let game_csgo = candidates
        .into_iter()
        .find(|candidate| is_normal_file_below(input, &candidate.join("gameinfo.gi")))
        .ok_or_else(|| {
            CommandErrorDto::at_path(
                "cs2_game_directory_not_found",
                "The selected folder does not contain game/csgo/gameinfo.gi.",
                input,
            )
        })?;
    let metadata = fs::symlink_metadata(&game_csgo).map_err(|error| {
        CommandErrorDto::at_path(
            "cs2_game_directory_unavailable",
            error.to_string(),
            &game_csgo,
        )
    })?;
    if crate::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "cs2_game_directory_reparse_point",
            "The resolved game/csgo folder cannot be a link or junction.",
            &game_csgo,
        ));
    }

    let cs2_root = game_csgo
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .unwrap_or_else(|| input.to_path_buf());
    Ok(InstallPaths {
        cs2_root,
        game_csgo,
    })
}

fn metamod_files_check(game_csgo: &Path) -> DiagnosticCheckDto {
    let mut check = required_files_check(
        "metamod.files",
        "dependencies",
        "Metamod:Source files",
        game_csgo,
        &[
            "addons/metamod/bin/win64/server.dll",
            "addons/metamod/bin/win64/metamod.2.cs2.dll",
        ],
    );
    if check.status == DiagnosticStatus::Error {
        check.action = Some(
            "Install Metamod:Source for CS2 using the playback server requirements.".to_string(),
        );
    }
    check
}

fn counterstrikesharp_files_check(game_csgo: &Path) -> DiagnosticCheckDto {
    let mut check = required_files_check(
        "counterStrikeSharp.files",
        "dependencies",
        "CounterStrikeSharp files",
        game_csgo,
        &[
            "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll",
            "addons/counterstrikesharp/api/CounterStrikeSharp.API.dll",
            "addons/metamod/counterstrikesharp.vdf",
        ],
    );
    if check.status == DiagnosticStatus::Error {
        check.action =
            Some("Install CounterStrikeSharp using the playback server requirements.".to_string());
    }
    check
}

fn inspect_runtime_health(game_csgo: &Path) -> RuntimeAudit {
    let path = join_public_relative(game_csgo, RUNTIME_HEALTH_RELATIVE_PATH);
    if !is_normal_file_below(game_csgo, &path) {
        return runtime_audit_without_live_evidence(
            DiagnosticStatus::Unverified,
            "No DemoTracer runtime heartbeat has been written in this CS2 tree.",
            "missing",
            &path,
            Some("Start the local replay server with DemoTracer loaded, wait a few seconds, then inspect again."),
        );
    }

    let text = match read_small_text_below(game_csgo, &path, MAX_RUNTIME_HEALTH_BYTES) {
        Ok(text) => text,
        Err(error) => {
            return runtime_audit_without_live_evidence(
                DiagnosticStatus::Warning,
                &format!("The DemoTracer runtime heartbeat could not be read: {error}"),
                "unreadable",
                &path,
                Some("Restart DemoTracer and inspect again. The GUI never loads the runtime DLL directly."),
            );
        }
    };
    let health = match serde_json::from_str::<RuntimeHealthWire>(&text) {
        Ok(health) => health,
        Err(error) => {
            return runtime_audit_without_live_evidence(
                DiagnosticStatus::Warning,
                &format!("The DemoTracer runtime heartbeat is invalid JSON: {error}"),
                "invalid",
                &path,
                Some("Restart DemoTracer and inspect again."),
            );
        }
    };
    if health.schema_version != 1
        || health.plugin_version.trim().is_empty()
        || health.plugin_version.len() > 64
        || health.counter_strike_sharp_version.trim().is_empty()
        || health.counter_strike_sharp_version.len() > 64
        || version_tuple(&health.counter_strike_sharp_version).is_none()
    {
        return runtime_audit_without_live_evidence(
            DiagnosticStatus::Warning,
            "The DemoTracer runtime heartbeat has an unsupported or invalid schema.",
            "unsupported",
            &path,
            Some("Update the desktop GUI and playback bundle as one matching release."),
        );
    }

    let now = now_ms();
    if health.written_at_ms > now.saturating_add(MAX_RUNTIME_HEALTH_FUTURE_SKEW_MS) {
        return runtime_audit_without_live_evidence(
            DiagnosticStatus::Warning,
            "The heartbeat timestamp is too far in the future to use as live evidence.",
            "clock mismatch",
            &path,
            Some("Check the Windows clock, restart DemoTracer, and inspect again."),
        );
    }
    let age_ms = now.saturating_sub(health.written_at_ms);
    if !health.running {
        return runtime_audit_without_live_evidence(
            DiagnosticStatus::Unverified,
            "DemoTracer recorded a clean runtime stop. Installed files can still be inspected, but no plugin is currently proven active.",
            "stopped",
            &path,
            Some("Start the local replay server and inspect again for live ABI and plugin evidence."),
        );
    }
    if age_ms > MAX_RUNTIME_HEALTH_AGE_MS {
        return runtime_audit_without_live_evidence(
            DiagnosticStatus::Unverified,
            &format!(
                "The most recent running heartbeat is stale ({} seconds old).",
                age_ms / 1000
            ),
            "stale",
            &path,
            Some("Confirm the local replay server is running, then inspect again."),
        );
    }

    let expected = match embedded_playback_contract() {
        Ok(expected) => expected,
        Err(error) => {
            return runtime_audit_without_live_evidence(
                DiagnosticStatus::Error,
                &error,
                "embedded contract invalid",
                &path,
                None,
            );
        }
    };
    let capabilities = parse_hex_mask(&health.bot_controller.capabilities);
    let reported_required_mask = parse_hex_mask(&health.bot_controller.required_capabilities.mask);
    let reported_missing = parse_hex_mask(&health.bot_controller.required_capabilities.missing);
    let expected_required_mask = parse_hex_mask(&expected.bot_controller.required_capabilities_hex);
    let controller_compatible = capabilities
        .zip(reported_required_mask)
        .zip(reported_missing)
        .zip(expected_required_mask)
        .is_some_and(
            |(((capabilities, reported_mask), reported_missing), expected_mask)| {
                health.bot_controller.compatible
                    && health.bot_controller.abi_major == expected.bot_controller.abi_major
                    && health.bot_controller.abi_minor >= expected.bot_controller.min_abi_minor
                    && reported_mask == expected_mask
                    && health.bot_controller.required_capabilities.present
                    && reported_missing == 0
                    && capabilities & expected_mask == expected_mask
                    && health.demo_tracer_api == expected.demotracer.companion_api
            },
        );
    let hider_compatible = health.bot_hider.available
        && health.bot_hider.connected
        && !health.bot_hider.draining
        && health.bot_hider.provider_api == Some(expected.bot_hider.api);
    let randomizer_compatible = health.bot_randomizer.available
        && health.bot_randomizer.ready
        && !health.bot_randomizer.draining
        && health.bot_randomizer.replay_plan_prebuild_available
        && health.bot_randomizer.provider_api == Some(expected.bot_randomizer.api);

    let mut checks = vec![DiagnosticCheckDto {
        id: "runtime.heartbeat".to_string(),
        group: "runtime".to_string(),
        status: DiagnosticStatus::Pass,
        title: "DemoTracer live heartbeat".to_string(),
        summary: format!(
            "A fresh DemoTracer {} heartbeat was written {} seconds ago.",
            health.plugin_version,
            age_ms / 1000
        ),
        expected: Some(format!(
            "schema 1, running, no older than {} seconds",
            MAX_RUNTIME_HEALTH_AGE_MS / 1000
        )),
        actual: Some("fresh and running".to_string()),
        evidence_path: Some(path.display().to_string()),
        action: None,
    }];
    checks.push(DiagnosticCheckDto {
        id: "runtime.botController".to_string(),
        group: "runtime".to_string(),
        status: if controller_compatible {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "Live BotController contract".to_string(),
        summary: if controller_compatible {
            "The loaded BotController satisfies DemoTracer's ABI, minor, capability, and companion API contract."
                .to_string()
        } else {
            "The loaded BotController does not satisfy DemoTracer's runtime contract.".to_string()
        },
        expected: Some(format!(
            "ABI {}/{}+, capabilities {}, DemoTracer API {}",
            expected.bot_controller.abi_major,
            expected.bot_controller.min_abi_minor,
            expected.bot_controller.required_capabilities_hex,
            expected.demotracer.companion_api
        )),
        actual: Some(format!(
            "ABI {}/{}, capabilities {}, build {}, DemoTracer API {}",
            health.bot_controller.abi_major,
            health.bot_controller.abi_minor,
            health.bot_controller.capabilities,
            health.bot_controller.build_id,
            health.demo_tracer_api
        )),
        evidence_path: Some(path.display().to_string()),
        action: (!controller_compatible).then(|| {
            "Stop the server and reinstall one complete DemoTracer playback bundle; do not copy BotController from another bot package."
                .to_string()
        }),
    });
    checks.push(DiagnosticCheckDto {
        id: "runtime.botHider".to_string(),
        group: "runtime".to_string(),
        status: if hider_compatible {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "Live BotHider provider".to_string(),
        summary: if hider_compatible {
            "The versioned DemoTracer BotHider provider is connected and available.".to_string()
        } else {
            "The required DemoTracer BotHider provider is unavailable, disconnected, draining, or on the wrong API."
                .to_string()
        },
        expected: Some(format!(
            "API {}, connected, available, not draining",
            expected.bot_hider.api
        )),
        actual: Some(format!(
            "API {}, connected={}, available={}, draining={}",
            health
                .bot_hider
                .provider_api
                .map(|value| value.to_string())
                .unwrap_or_else(|| "unavailable".to_string()),
            health.bot_hider.connected,
            health.bot_hider.available,
            health.bot_hider.draining
        )),
        evidence_path: Some(path.display().to_string()),
        action: (!hider_compatible).then(|| {
            "Verify DemoTracerBotHider is the only BotHider presentation provider and reinstall the matching bundle if needed."
                .to_string()
        }),
    });
    checks.push(DiagnosticCheckDto {
        id: "runtime.botRandomizer".to_string(),
        group: "runtime".to_string(),
        status: if randomizer_compatible {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "Live BotRandomizer replay-plan provider".to_string(),
        summary: if randomizer_compatible {
            "The bundled cosmetic provider accepts complete replay plans and can prebuild item views."
                .to_string()
        } else {
            "The required replay cosmetic provider is unavailable, on the wrong API, or cannot prebuild item views."
                .to_string()
        },
        expected: Some(format!(
            "API {}, ready, replay prebuild available, not draining",
            expected.bot_randomizer.api
        )),
        actual: Some(format!(
            "API {}, ready={}, prebuild={}, available={}, draining={}",
            health
                .bot_randomizer
                .provider_api
                .map(|value| value.to_string())
                .unwrap_or_else(|| "unavailable".to_string()),
            health.bot_randomizer.ready,
            health.bot_randomizer.replay_plan_prebuild_available,
            health.bot_randomizer.available,
            health.bot_randomizer.draining
        )),
        evidence_path: Some(path.display().to_string()),
        action: (!randomizer_compatible).then(|| {
            "Reinstall the complete DemoTracer playback bundle so BotRandomizer and BotRandomizerApi match DemoTracer."
                .to_string()
        }),
    });

    let css_compatible = version_tuple(&health.counter_strike_sharp_version)
        .zip(version_tuple(&expected.counterstrikesharp.minimum_version))
        .is_some_and(|(actual, required)| actual >= required);
    checks.push(DiagnosticCheckDto {
        id: "runtime.counterStrikeSharpApi".to_string(),
        group: "runtime".to_string(),
        status: if css_compatible { DiagnosticStatus::Pass } else { DiagnosticStatus::Error },
        title: "Reported CounterStrikeSharp API version".to_string(),
        summary: "Compares the API version in the fresh heartbeat with the required minimum. This does not identify the host's hook backend.".to_string(),
        expected: Some(format!("API {}+", expected.counterstrikesharp.minimum_version)),
        actual: Some(health.counter_strike_sharp_version),
        evidence_path: Some(path.display().to_string()),
        action: (!css_compatible).then(|| "Update CounterStrikeSharp using the playback server requirements.".to_string()),
    });

    RuntimeAudit {
        plugin_version: Some(health.plugin_version),
        checks,
    }
}

pub(crate) fn fresh_runtime_plugin_version(game_csgo: &Path) -> Option<String> {
    inspect_runtime_health(game_csgo).plugin_version
}

fn runtime_audit_without_live_evidence(
    status: DiagnosticStatus,
    summary: &str,
    actual: &str,
    path: &Path,
    action: Option<&str>,
) -> RuntimeAudit {
    RuntimeAudit {
        checks: vec![DiagnosticCheckDto {
            id: "runtime.heartbeat".to_string(),
            group: "runtime".to_string(),
            status,
            title: "DemoTracer live heartbeat".to_string(),
            summary: summary.to_string(),
            expected: Some(format!(
                "running heartbeat no older than {} seconds",
                MAX_RUNTIME_HEALTH_AGE_MS / 1000
            )),
            actual: Some(actual.to_string()),
            evidence_path: Some(path.display().to_string()),
            action: action.map(str::to_string),
        }],
        ..RuntimeAudit::default()
    }
}

fn parse_hex_mask(value: &str) -> Option<u64> {
    let trimmed = value.trim();
    let digits = trimmed
        .strip_prefix("0x")
        .or_else(|| trimmed.strip_prefix("0X"))
        .unwrap_or(trimmed);
    (!digits.is_empty())
        .then(|| u64::from_str_radix(digits, 16).ok())
        .flatten()
}

fn required_files_check(
    id: &str,
    group: &str,
    title: &str,
    root: &Path,
    relative_paths: &[&str],
) -> DiagnosticCheckDto {
    let missing = relative_paths
        .iter()
        .filter(|relative| !is_normal_file_below(root, &join_public_relative(root, relative)))
        .copied()
        .collect::<Vec<_>>();
    DiagnosticCheckDto {
        id: id.to_string(),
        group: group.to_string(),
        status: if missing.is_empty() { DiagnosticStatus::Pass } else { DiagnosticStatus::Error },
        title: title.to_string(),
        summary: if missing.is_empty() {
            format!("All {} required files are present.", relative_paths.len())
        } else {
            format!("Missing: {}", missing.join(", "))
        },
        expected: Some(relative_paths.join(", ")),
        actual: Some(format!("{} of {} present", relative_paths.len() - missing.len(), relative_paths.len())),
        evidence_path: missing
            .first()
            .map(|relative| join_public_relative(root, relative).display().to_string())
            .or_else(|| relative_paths.first().map(|relative| join_public_relative(root, relative).display().to_string())),
        action: (!missing.is_empty()).then(|| "Reinstall one complete DemoTracer playback bundle; do not mix native files from another bot package.".to_string()),
    }
}

fn inspect_install_receipt(
    game_csgo: &Path,
    checks: &mut Vec<DiagnosticCheckDto>,
) -> InstallReceiptSummaryDto {
    let receipt_path = join_public_relative(game_csgo, INSTALL_RECEIPT_RELATIVE_PATH);
    if !is_normal_file_below(game_csgo, &receipt_path) {
        checks.push(DiagnosticCheckDto {
            id: "demotracer.receipt".to_string(),
            group: "demotracer".to_string(),
            status: DiagnosticStatus::Unverified,
            title: "DemoTracer install receipt".to_string(),
            summary: "No install receipt was found. The package contract and file integrity have not been verified.".to_string(),
            expected: Some(INSTALL_RECEIPT_RELATIVE_PATH.to_string()),
            actual: Some("missing".to_string()),
            evidence_path: Some(receipt_path.display().to_string()),
            action: Some("Install a complete matching DemoTracer playback bundle to enable receipt and file-hash checks.".to_string()),
        });
        checks.push(required_files_check(
            "demotracer.files",
            "demotracer",
            "DemoTracer bundle files (no receipt)",
            game_csgo,
            REQUIRED_RECEIPT_PATHS,
        ));
        return InstallReceiptSummaryDto::default();
    }

    let mut audit = InstallReceiptSummaryDto {
        found: true,
        path: Some(receipt_path.display().to_string()),
        ..InstallReceiptSummaryDto::default()
    };
    let text = match read_small_text_below(game_csgo, &receipt_path, MAX_TEXT_FILE_BYTES) {
        Ok(text) => text,
        Err(error) => {
            checks.push(receipt_error_check(&receipt_path, error));
            audit.verified = Some(false);
            return audit;
        }
    };
    let receipt = match serde_json::from_str::<InstallReceiptWire>(&text) {
        Ok(receipt) => receipt,
        Err(error) => {
            checks.push(receipt_error_check(&receipt_path, error.to_string()));
            audit.verified = Some(false);
            return audit;
        }
    };
    audit.bundle_version = Some(receipt.bundle_version.clone());
    audit.bot_controller_abi = Some(receipt.compatibility.bot_controller.abi_major);
    audit.bot_controller_minor = Some(receipt.compatibility.bot_controller.min_abi_minor);
    audit.bot_hider_api = Some(receipt.compatibility.bot_hider.api);
    audit.demo_tracer_api = Some(receipt.compatibility.demotracer.companion_api);

    let contract_errors = receipt_contract_errors(&receipt);
    let mut integrity_errors = Vec::new();
    if receipt.files.len() > MAX_RECEIPT_FILES {
        audit.files_mismatched = receipt.files.len();
        integrity_errors.push(format!(
            "receipt lists too many files ({})",
            receipt.files.len()
        ));
    } else {
        let mut recorded_paths = BTreeSet::new();
        for file in &receipt.files {
            let normalized_path = normalized_receipt_path(&file.path);
            if !recorded_paths.insert(normalized_path.clone()) {
                audit.files_mismatched += 1;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!("duplicate receipt path: {}", file.path));
                }
                continue;
            }
            let expected_component = receipt_component(&normalized_path);
            let mut mismatched = false;
            if expected_component != Some(file.component.as_str()) {
                mismatched = true;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!(
                        "{} component {} is not {}",
                        file.path,
                        file.component,
                        expected_component.unwrap_or("unknown")
                    ));
                }
            }
            audit.files_checked += 1;
            match verify_receipt_file(game_csgo, file) {
                Ok(()) => {}
                Err(error) => {
                    mismatched = true;
                    if integrity_errors.len() < 12 {
                        integrity_errors.push(error);
                    }
                }
            }
            if mismatched {
                audit.files_mismatched += 1;
            }
        }
        for required in REQUIRED_RECEIPT_PATHS {
            if !recorded_paths.contains(*required) {
                audit.files_mismatched += 1;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!("receipt omits required file: {required}"));
                }
            }
        }
    }
    let verified = contract_errors.is_empty() && integrity_errors.is_empty();
    audit.verified = Some(verified);
    checks.push(DiagnosticCheckDto {
        id: "demotracer.receipt".to_string(),
        group: "demotracer".to_string(),
        status: if verified { DiagnosticStatus::Pass } else { DiagnosticStatus::Error },
        title: "DemoTracer install receipt".to_string(),
        summary: if verified {
            format!(
                "Bundle {} matches the receipt metadata and all {} recorded file hashes. This proves package integrity; loaded ABI/API compatibility is verified separately by the runtime heartbeat.",
                receipt.bundle_version, audit.files_checked
            )
        } else {
            contract_errors
                .into_iter()
                .chain(integrity_errors)
                .collect::<Vec<_>>()
                .join("; ")
        },
        expected: embedded_playback_contract().ok().map(|expected| {
            format!(
                "DemoTracer ABI {}/minor {}+, BotHider API {}, matching component hashes",
                expected.bot_controller.abi_major,
                expected.bot_controller.min_abi_minor,
                expected.bot_hider.api
            )
        }),
        actual: Some(format!(
            "ABI {}/{}, BotHider API {}, mismatched files {}",
            receipt.compatibility.bot_controller.abi_major,
            receipt.compatibility.bot_controller.min_abi_minor,
            receipt.compatibility.bot_hider.api,
            audit.files_mismatched
        )),
        evidence_path: Some(receipt_path.display().to_string()),
        action: (!verified).then(|| "The receipt or recorded files do not match this desktop build. Reinstall a complete matching playback bundle to refresh both.".to_string()),
    });
    audit
}

fn receipt_error_check(path: &Path, message: String) -> DiagnosticCheckDto {
    DiagnosticCheckDto {
        id: "demotracer.receipt".to_string(),
        group: "demotracer".to_string(),
        status: DiagnosticStatus::Error,
        title: "DemoTracer install receipt".to_string(),
        summary: format!("The install receipt could not be read: {message}"),
        expected: Some("Valid demotracer-install.v1.json".to_string()),
        actual: Some("invalid".to_string()),
        evidence_path: Some(path.display().to_string()),
        action: Some("Reinstall one complete DemoTracer playback bundle.".to_string()),
    }
}

pub(crate) fn receipt_contract_errors(receipt: &InstallReceiptWire) -> Vec<String> {
    let expected = match embedded_playback_contract() {
        Ok(expected) => expected,
        Err(error) => return vec![error],
    };
    let mut errors = Vec::new();
    if receipt.schema_version != 1 {
        errors.push("unsupported install receipt schema".to_string());
    }
    if receipt.product != expected.product || receipt.platform != expected.platform {
        errors.push("receipt product or platform differs from this desktop build".to_string());
    }
    // This is a matched playback bundle, not a probe of the installed host.
    // Installation and inspection must enforce the same package contract.
    if receipt.compatibility != expected {
        errors.push("playback bundle contract differs from this desktop build".to_string());
    }
    errors
}

pub(crate) fn embedded_playback_contract() -> Result<PlaybackContractWire, String> {
    serde_json::from_str::<PlaybackContractWire>(include_str!(
        "../../../../shared/contracts/playback-contract.v1.json"
    ))
    .map_err(|error| format!("embedded compatibility contract is invalid: {error}"))
}

fn verify_receipt_file(game_csgo: &Path, file: &ReceiptFileWire) -> Result<(), String> {
    let relative = checked_receipt_relative_path(&file.path)?;
    let path = game_csgo.join(relative);
    let metadata = metadata_below_without_reparse(game_csgo, &path)
        .map_err(|error| format!("{}: {error}", file.path))?;
    if !metadata.is_file() {
        return Err(format!("{} is not a normal file", file.path));
    }
    if metadata.len() != file.size {
        return Err(format!("{} size differs", file.path));
    }
    if metadata.len() > MAX_RECEIPT_FILE_BYTES {
        return Err(format!("{} exceeds the diagnostic size limit", file.path));
    }
    let bytes = fs::read(&path).map_err(|error| format!("{}: {error}", file.path))?;
    if !sha256_hex(&bytes).eq_ignore_ascii_case(file.sha256.trim()) {
        return Err(format!("{} hash differs", file.path));
    }
    Ok(())
}

pub(crate) fn checked_receipt_relative_path(value: &str) -> Result<PathBuf, String> {
    let mut output = PathBuf::new();
    for segment in value.split(['/', '\\']) {
        if segment.is_empty() || segment == "." || segment == ".." || segment.contains(':') {
            return Err(format!("unsafe receipt path: {value}"));
        }
        output.push(segment);
    }
    if output.components().next().is_none()
        || !output.components().next().is_some_and(|component| {
            component
                .as_os_str()
                .to_string_lossy()
                .eq_ignore_ascii_case("addons")
        })
    {
        return Err(format!("receipt path is outside addons: {value}"));
    }
    Ok(output)
}

pub(crate) fn normalized_receipt_path(value: &str) -> String {
    value.replace('\\', "/").to_ascii_lowercase()
}

pub(crate) fn receipt_component(normalized_path: &str) -> Option<&'static str> {
    if normalized_path.starts_with("addons/botcontroller/")
        || normalized_path == "addons/metamod/botcontroller.vdf"
        || normalized_path.starts_with("addons/counterstrikesharp/plugins/botcontrollerimpl/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/botcontrollerapi/")
    {
        Some("bot_controller")
    } else if normalized_path.starts_with("addons/bothider/")
        || normalized_path == "addons/metamod/bothider.vdf"
    {
        Some("bot_hider_native")
    } else if normalized_path.starts_with("addons/counterstrikesharp/plugins/bothiderimpl/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/demotracerbothiderapi/")
    {
        Some("bot_hider_managed")
    } else if normalized_path.starts_with("addons/counterstrikesharp/plugins/demotracer/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/demotracerapi/")
    {
        Some("demotracer")
    } else if normalized_path.starts_with("addons/counterstrikesharp/plugins/botrandomizer/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/botrandomizerapi/")
    {
        Some("bot_randomizer_managed")
    } else if normalized_path.starts_with("addons/") {
        Some("shared_dependency")
    } else {
        None
    }
}

fn overall_status(checks: &[DiagnosticCheckDto]) -> DiagnosticStatus {
    if checks
        .iter()
        .any(|check| check.status == DiagnosticStatus::Error)
    {
        DiagnosticStatus::Error
    } else if checks
        .iter()
        .any(|check| check.status == DiagnosticStatus::Warning)
    {
        DiagnosticStatus::Warning
    } else if checks
        .iter()
        .any(|check| check.status == DiagnosticStatus::Unverified)
    {
        DiagnosticStatus::Unverified
    } else {
        DiagnosticStatus::Pass
    }
}

fn unique_existing_directories(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut seen = BTreeSet::new();
    paths
        .into_iter()
        .filter(|path| path.is_dir())
        .filter(|path| seen.insert(path_key(path)))
        .collect()
}

fn steam_library_roots(steam_root: &Path) -> Vec<PathBuf> {
    let mut roots = vec![steam_root.to_path_buf()];
    let library_file = steam_root.join("steamapps").join("libraryfolders.vdf");
    if let Ok(text) = read_small_text(&library_file, MAX_TEXT_FILE_BYTES) {
        for line in text.lines() {
            let tokens = quoted_tokens(line);
            if tokens.len() >= 2 && tokens[0].eq_ignore_ascii_case("path") {
                roots.push(PathBuf::from(&tokens[1]));
            }
        }
    }
    unique_existing_directories(roots)
}

#[cfg(windows)]
fn registry_steam_roots() -> Vec<PathBuf> {
    let queries = [
        (r"HKCU\Software\Valve\Steam", "SteamPath"),
        (r"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
    ];
    let mut roots = Vec::new();
    for (key, value_name) in queries {
        let Ok(output) = Command::new("reg.exe")
            .args(["query", key, "/v", value_name])
            .output()
        else {
            continue;
        };
        if !output.status.success() {
            continue;
        }
        let stdout = String::from_utf8_lossy(&output.stdout);
        for line in stdout.lines() {
            let lower = line.to_ascii_lowercase();
            let Some(type_index) = lower.find("reg_sz") else {
                continue;
            };
            if !lower[..type_index].contains(&value_name.to_ascii_lowercase()) {
                continue;
            }
            let value = line[type_index + "reg_sz".len()..].trim();
            if !value.is_empty() {
                roots.push(PathBuf::from(value.replace('/', "\\")));
            }
        }
    }
    roots
}

#[cfg(not(windows))]
fn registry_steam_roots() -> Vec<PathBuf> {
    Vec::new()
}

fn vdf_value(text: &str, key: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let tokens = quoted_tokens(line);
        (tokens.len() >= 2 && tokens[0].eq_ignore_ascii_case(key)).then(|| tokens[1].clone())
    })
}

fn quoted_tokens(line: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    let mut escaped = false;
    for character in line.chars() {
        if !quoted {
            if character == '"' {
                quoted = true;
                current.clear();
            }
            continue;
        }
        if escaped {
            current.push(character);
            escaped = false;
        } else if character == '\\' {
            escaped = true;
        } else if character == '"' {
            tokens.push(current.clone());
            quoted = false;
        } else {
            current.push(character);
        }
    }
    tokens
}

fn read_small_text(path: &Path, max_bytes: u64) -> Result<String, String> {
    let metadata = fs::symlink_metadata(path).map_err(|error| error.to_string())?;
    if !metadata.is_file() || crate::catalog::is_symlink_or_reparse(&metadata) {
        return Err("not a normal file".to_string());
    }
    if metadata.len() > max_bytes {
        return Err(format!("file is larger than {max_bytes} bytes"));
    }
    fs::read_to_string(path).map_err(|error| error.to_string())
}

fn metadata_below_without_reparse(root: &Path, path: &Path) -> Result<fs::Metadata, String> {
    let relative = path
        .strip_prefix(root)
        .map_err(|_| format!("path is outside the selected root: {}", path.display()))?;
    let mut current = root.to_path_buf();
    let mut metadata = fs::symlink_metadata(&current).map_err(|error| error.to_string())?;
    if crate::catalog::is_symlink_or_reparse(&metadata) {
        return Err(format!(
            "path crosses a link or junction: {}",
            current.display()
        ));
    }

    let mut components = relative.components().peekable();
    while let Some(component) = components.next() {
        let std::path::Component::Normal(segment) = component else {
            return Err(format!(
                "path contains an unsafe component: {}",
                path.display()
            ));
        };
        if !metadata.is_dir() {
            return Err(format!(
                "path component is not a directory: {}",
                current.display()
            ));
        }
        current.push(segment);
        metadata = fs::symlink_metadata(&current).map_err(|error| error.to_string())?;
        if crate::catalog::is_symlink_or_reparse(&metadata) {
            return Err(format!(
                "path crosses a link or junction: {}",
                current.display()
            ));
        }
        if components.peek().is_some() && !metadata.is_dir() {
            return Err(format!(
                "path component is not a directory: {}",
                current.display()
            ));
        }
    }
    Ok(metadata)
}

fn is_normal_file_below(root: &Path, path: &Path) -> bool {
    metadata_below_without_reparse(root, path)
        .ok()
        .is_some_and(|metadata| metadata.is_file())
}

fn read_small_text_below(root: &Path, path: &Path, max_bytes: u64) -> Result<String, String> {
    let metadata = metadata_below_without_reparse(root, path)?;
    if !metadata.is_file() {
        return Err("not a normal file".to_string());
    }
    if metadata.len() > max_bytes {
        return Err(format!("file is larger than {max_bytes} bytes"));
    }
    fs::read_to_string(path).map_err(|error| error.to_string())
}

fn join_public_relative(root: &Path, relative: &str) -> PathBuf {
    relative
        .split(['/', '\\'])
        .filter(|segment| !segment.is_empty())
        .fold(root.to_path_buf(), |path, segment| path.join(segment))
}

fn path_key(path: &Path) -> String {
    path.display()
        .to_string()
        .replace('\\', "/")
        .to_ascii_lowercase()
}

fn version_tuple(value: &str) -> Option<(u32, u32, u32, u32)> {
    let mut parts = [0; 4];
    let mut count = 0;
    for (index, part) in value.split('.').enumerate() {
        if index >= parts.len()
            || part.is_empty()
            || !part.bytes().all(|byte| byte.is_ascii_digit())
        {
            return None;
        }
        parts[index] = part.parse().ok()?;
        count += 1;
    }
    (count >= 3).then_some((parts[0], parts[1], parts[2], parts[3]))
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempTree(PathBuf);

    impl TempTree {
        fn cs2() -> Self {
            let nonce = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock")
                .as_nanos();
            let root = std::env::temp_dir().join(format!(
                "cs2-demotracer-diagnostics-{}-{nonce}",
                std::process::id()
            ));
            fs::create_dir_all(root.join("game/csgo")).expect("create test CS2 tree");
            fs::write(root.join("game/csgo/gameinfo.gi"), b"GameInfo\n").expect("write gameinfo");
            Self(root)
        }

        fn root(&self) -> &Path {
            &self.0
        }

        fn game_csgo(&self) -> PathBuf {
            self.0.join("game/csgo")
        }
    }

    impl Drop for TempTree {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn parses_vdf_escaped_paths() {
        let text = r#""libraryfolders"
{
    "0" { "path" "Example Library\\Steam" }
}
"#;
        let tokens = quoted_tokens(text.lines().nth(2).unwrap());
        assert_eq!(tokens, vec!["0", "path", r"Example Library\Steam"]);
    }

    #[test]
    fn rejects_receipt_paths_outside_addons() {
        assert!(checked_receipt_relative_path("addons/BotController/a.dll").is_ok());
        assert!(checked_receipt_relative_path("addons/../outside.dll").is_err());
        assert!(checked_receipt_relative_path("C:/outside.dll").is_err());
    }

    #[test]
    fn rejects_legacy_hook_runtime_receipts() {
        let contract = embedded_playback_contract().unwrap();
        let receipt = InstallReceiptWire {
            schema_version: 1,
            product: contract.product.clone(),
            bundle_version: "test".to_string(),
            git_commit: None,
            platform: contract.platform.clone(),
            compatibility: contract,
            files: vec![],
        };
        assert!(receipt_contract_errors(&receipt).is_empty());
        let mut json = serde_json::to_value(&receipt).unwrap();
        let roundtrip: InstallReceiptWire = serde_json::from_value(json.clone()).unwrap();
        assert_eq!(roundtrip, receipt);
        json["compatibility"]
            .as_object_mut()
            .unwrap()
            .remove("hook_runtime");
        let legacy: InstallReceiptWire = serde_json::from_value(json).unwrap();
        assert!(receipt_contract_errors(&legacy)
            .iter()
            .any(|error| error.contains("bundle contract")));
        let mut old_native = receipt;
        old_native.compatibility.bot_controller.min_abi_minor = 42;
        assert!(receipt_contract_errors(&old_native)
            .iter()
            .any(|error| error.contains("bundle contract")));
    }

    #[test]
    fn compares_numeric_versions() {
        assert!(version_tuple("1.0.371") > version_tuple("1.0.99"));
        assert_eq!(version_tuple("1.0.375.0"), Some((1, 0, 375, 0)));
        for invalid in [
            "unknown",
            "v1.2",
            "not-1.0.999",
            "1.0.375-PR",
            "1.0.375.0.1",
            "1..375",
        ] {
            assert_eq!(version_tuple(invalid), None, "{invalid}");
        }
    }

    #[test]
    fn resolves_install_root_and_game_csgo_without_global_discovery() {
        let tree = TempTree::cs2();
        let from_root = resolve_install_paths(tree.root()).expect("resolve CS2 root");
        let from_game = resolve_install_paths(&tree.game_csgo()).expect("resolve game/csgo");
        assert_eq!(from_root.game_csgo, tree.game_csgo());
        assert_eq!(from_game.cs2_root, tree.root());
    }

    #[test]
    fn receipt_components_are_derived_from_paths_not_labels() {
        assert_eq!(
            receipt_component("addons/botcontroller/bin/win64/botcontroller.dll"),
            Some("bot_controller")
        );
        assert_eq!(
            receipt_component("addons/counterstrikesharp/plugins/bothiderimpl/bothiderimpl.dll"),
            Some("bot_hider_managed")
        );
        assert_eq!(
            receipt_component("addons/counterstrikesharp/shared/demotracerapi/demotracerapi.dll"),
            Some("demotracer")
        );
        assert_eq!(
            receipt_component("addons/counterstrikesharp/plugins/botrandomizer/botrandomizer.dll"),
            Some("bot_randomizer_managed")
        );
    }

    fn healthy_heartbeat() -> serde_json::Value {
        let contract = embedded_playback_contract().unwrap();
        serde_json::json!({
            "schemaVersion": 1,
            "writtenAtMs": now_ms(),
            "running": true,
            "pluginVersion": "0.8.0",
            "demoTracerApi": contract.demotracer.companion_api,
            "counterStrikeSharpVersion": contract.counterstrikesharp.minimum_version,
            "botController": {
                "abiMajor": contract.bot_controller.abi_major,
                "abiMinor": contract.bot_controller.min_abi_minor,
                "capabilities": contract.bot_controller.required_capabilities_hex,
                "buildId": "fixture",
                "compatible": true,
                "requiredCapabilities": {
                    "mask": contract.bot_controller.required_capabilities_hex,
                    "present": true,
                    "missing": "0x0"
                }
            },
            "botHider": {
                "providerApi": contract.bot_hider.api, "connected": true,
                "draining": false, "available": true
            },
            "botRandomizer": {
                "providerApi": contract.bot_randomizer.api, "ready": true, "draining": false,
                "replayPlanPrebuildAvailable": true, "available": true
            }
        })
    }

    fn write_receipt_fixture(tree: &TempTree) -> InstallReceiptWire {
        let contract = embedded_playback_contract().unwrap();
        let bytes = b"fixture";
        let files = REQUIRED_RECEIPT_PATHS
            .iter()
            .map(|relative| {
                let path = join_public_relative(&tree.game_csgo(), relative);
                fs::create_dir_all(path.parent().unwrap()).unwrap();
                fs::write(path, bytes).unwrap();
                ReceiptFileWire {
                    path: relative.to_string(),
                    component: receipt_component(relative).unwrap().to_string(),
                    size: bytes.len() as u64,
                    sha256: sha256_hex(bytes),
                }
            })
            .collect();
        let receipt = InstallReceiptWire {
            schema_version: 1,
            product: contract.product.clone(),
            bundle_version: "1.3.0".to_string(),
            git_commit: None,
            platform: contract.platform.clone(),
            compatibility: contract,
            files,
        };
        fs::write(
            join_public_relative(&tree.game_csgo(), INSTALL_RECEIPT_RELATIVE_PATH),
            serde_json::to_vec(&receipt).unwrap(),
        )
        .unwrap();
        receipt
    }

    #[test]
    fn empty_install_has_no_successful_file_checks() {
        let tree = TempTree::cs2();
        let report = inspect_cs2_install_for(tree.root().to_str().unwrap()).unwrap();
        assert_eq!(report.overall, DiagnosticStatus::Error);
        assert!(report
            .checks
            .iter()
            .all(|check| check.status != DiagnosticStatus::Pass));
        assert!(
            report
                .checks
                .iter()
                .any(|check| check.id == "demotracer.files"
                    && check.status == DiagnosticStatus::Error)
        );
    }

    #[test]
    fn matching_receipt_replaces_duplicate_file_probes_and_still_detects_damage() {
        let tree = TempTree::cs2();
        write_receipt_fixture(&tree);
        let mut checks = Vec::new();
        let receipt = inspect_install_receipt(&tree.game_csgo(), &mut checks);
        assert_eq!(receipt.verified, Some(true));
        assert_eq!(receipt.files_checked, REQUIRED_RECEIPT_PATHS.len());
        assert_eq!(checks.len(), 1);
        assert_eq!(checks[0].id, "demotracer.receipt");

        // Same length, different bytes: integrity still checks hashes.
        fs::write(
            join_public_relative(&tree.game_csgo(), REQUIRED_RECEIPT_PATHS[0]),
            b"changed",
        )
        .unwrap();
        checks.clear();
        let damaged = inspect_install_receipt(&tree.game_csgo(), &mut checks);
        assert_eq!(damaged.verified, Some(false));
        assert_eq!(damaged.files_mismatched, 1);
        assert_eq!(checks[0].status, DiagnosticStatus::Error);
        assert!(checks[0].summary.contains("hash differs"));
    }

    #[test]
    fn receipt_inspection_rejects_files_outside_the_bundle_components() {
        let tree = TempTree::cs2();
        let mut receipt = write_receipt_fixture(&tree);
        let bytes = b"unrelated";
        let relative = "addons/another-plugin.dll";
        fs::write(join_public_relative(&tree.game_csgo(), relative), bytes).unwrap();
        receipt.files.push(ReceiptFileWire {
            path: relative.to_string(),
            component: "demotracer".to_string(),
            size: bytes.len() as u64,
            sha256: sha256_hex(bytes),
        });
        fs::write(
            join_public_relative(&tree.game_csgo(), INSTALL_RECEIPT_RELATIVE_PATH),
            serde_json::to_vec(&receipt).unwrap(),
        )
        .unwrap();
        let mut checks = Vec::new();
        assert_eq!(
            inspect_install_receipt(&tree.game_csgo(), &mut checks).verified,
            Some(false)
        );
        assert_eq!(checks[0].status, DiagnosticStatus::Error);
    }

    #[test]
    fn receipt_contract_roundtrip_preserves_every_declared_requirement() {
        let source: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../shared/contracts/playback-contract.v1.json"
        ))
        .unwrap();
        assert_eq!(
            serde_json::to_value(embedded_playback_contract().unwrap()).unwrap(),
            source
        );
    }

    fn write_heartbeat(tree: &TempTree, health: &serde_json::Value) {
        let path = join_public_relative(&tree.game_csgo(), RUNTIME_HEALTH_RELATIVE_PATH);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, serde_json::to_vec(health).unwrap()).unwrap();
    }

    #[test]
    fn fresh_runtime_heartbeat_checks_only_the_reported_contracts() {
        let tree = TempTree::cs2();
        write_heartbeat(&tree, &healthy_heartbeat());
        let audit = inspect_runtime_health(&tree.game_csgo());
        assert_eq!(overall_status(&audit.checks), DiagnosticStatus::Pass);
        assert!(audit
            .checks
            .iter()
            .all(|check| check.status == DiagnosticStatus::Pass));
    }

    #[test]
    fn failing_live_contracts_never_claim_verification() {
        let tree = TempTree::cs2();
        for (pointer, value) in [
            ("/botController/abiMinor", serde_json::json!(0)),
            ("/botController/abiMajor", serde_json::json!(0)),
            ("/botController/compatible", serde_json::json!(false)),
            ("/botController/capabilities", serde_json::json!("0x0")),
            (
                "/botController/requiredCapabilities/missing",
                serde_json::json!("0x1"),
            ),
            ("/botHider/connected", serde_json::json!(false)),
            ("/botHider/providerApi", serde_json::json!(0)),
            (
                "/botRandomizer/replayPlanPrebuildAvailable",
                serde_json::json!(false),
            ),
            ("/botRandomizer/draining", serde_json::json!(true)),
            ("/demoTracerApi", serde_json::json!(0)),
        ] {
            let mut health = healthy_heartbeat();
            *health.pointer_mut(pointer).unwrap() = value;
            write_heartbeat(&tree, &health);
            let audit = inspect_runtime_health(&tree.game_csgo());
            assert_eq!(
                overall_status(&audit.checks),
                DiagnosticStatus::Error,
                "{pointer}"
            );
        }
    }

    #[test]
    fn missing_stale_stopped_and_future_heartbeats_do_not_prove_runtime_compatibility() {
        let tree = TempTree::cs2();
        assert_eq!(
            overall_status(&inspect_runtime_health(&tree.game_csgo()).checks),
            DiagnosticStatus::Unverified
        );
        for (pointer, value, actual) in [
            (
                "/writtenAtMs",
                serde_json::json!(now_ms().saturating_sub(MAX_RUNTIME_HEALTH_AGE_MS + 1000)),
                "stale",
            ),
            ("/running", serde_json::json!(false), "stopped"),
            (
                "/writtenAtMs",
                serde_json::json!(now_ms() + MAX_RUNTIME_HEALTH_FUTURE_SKEW_MS + 60_000),
                "clock mismatch",
            ),
            (
                "/counterStrikeSharpVersion",
                serde_json::json!("unknown-1.0.999"),
                "unsupported",
            ),
        ] {
            let mut health = healthy_heartbeat();
            *health.pointer_mut(pointer).unwrap() = value;
            write_heartbeat(&tree, &health);
            let audit = inspect_runtime_health(&tree.game_csgo());
            assert_eq!(audit.checks[0].actual.as_deref(), Some(actual), "{pointer}");
            assert!(audit.plugin_version.is_none());
            assert!(audit
                .checks
                .iter()
                .all(|check| check.status != DiagnosticStatus::Pass));
        }
    }

    #[test]
    fn old_css_api_cannot_verify_live_runtime() {
        let tree = TempTree::cs2();
        let mut health = healthy_heartbeat();
        health["counterStrikeSharpVersion"] = serde_json::json!("0.0.1");
        write_heartbeat(&tree, &health);
        assert_eq!(
            overall_status(&inspect_runtime_health(&tree.game_csgo()).checks),
            DiagnosticStatus::Error
        );
    }

    #[test]
    fn arbitrary_dlls_do_not_satisfy_metamod_file_requirements() {
        let tree = TempTree::cs2();
        let directory = tree.game_csgo().join("addons/metamod/bin/win64");
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("unrelated.dll"), b"fixture").unwrap();
        assert_eq!(
            metamod_files_check(&tree.game_csgo()).status,
            DiagnosticStatus::Error
        );
        for name in ["server.dll", "metamod.2.cs2.dll"] {
            fs::write(directory.join(name), b"fixture").unwrap();
        }
        assert_eq!(
            metamod_files_check(&tree.game_csgo()).status,
            DiagnosticStatus::Pass
        );
    }

    #[test]
    fn plugin_names_and_dll_names_do_not_change_compatibility_verdicts() {
        let tree = TempTree::cs2();
        let before = inspect_cs2_install_for(tree.root().to_str().unwrap()).unwrap();
        for name in ["WeaponPaints", "BotAI", "BotControllerImpl", "RenamedHider"] {
            let directory = tree
                .game_csgo()
                .join("addons/counterstrikesharp/plugins")
                .join(name);
            fs::create_dir_all(&directory).unwrap();
            fs::write(directory.join("BotHiderImpl.dll"), b"fixture").unwrap();
        }
        let after = inspect_cs2_install_for(tree.root().to_str().unwrap()).unwrap();
        assert_eq!(before.overall, after.overall);
        assert_eq!(
            serde_json::to_value(before.checks).unwrap(),
            serde_json::to_value(after.checks).unwrap()
        );
    }
}
