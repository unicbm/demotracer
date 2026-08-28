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
const MAX_PLUGIN_DIRECTORIES: usize = 256;
const MAX_PLUGIN_DLLS: usize = 64;
pub(crate) const REQUIRED_RECEIPT_PATHS: &[&str] = &[
    "addons/botcontroller/bin/win64/botcontroller.dll",
    "addons/botcontroller/gamedata.json",
    "addons/metamod/botcontroller.vdf",
    "addons/bothider/bin/win64/bothider.dll",
    "addons/bothider/gamedata.json",
    "addons/metamod/bothider.vdf",
    "addons/counterstrikesharp/plugins/demotracer/demotracer.dll",
    "addons/counterstrikesharp/shared/demotracerapi/demotracerapi.dll",
    "addons/counterstrikesharp/plugins/demotracer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/demotracerbothider/demotracerbothider.dll",
    "addons/counterstrikesharp/shared/demotracerbothiderapi/demotracerbothiderapi.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/botrandomizer.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/cosmetic_catalog.json",
    "addons/counterstrikesharp/plugins/botrandomizer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/botrandomizer/charm_placements.json",
    "addons/counterstrikesharp/shared/botrandomizerapi/botrandomizerapi.dll",
    "addons/counterstrikesharp/shared/0harmony/0harmony.dll",
];
const BOT_IMPROVER_142_CONTROLLER_SHA256: &str =
    "84b28ba57246f5b8ae97f248fa3012f8bdc32a036fb9f22f255071c0aea05da3";
const BOT_IMPROVER_142_CONTROLLER_BYTES: u64 = 1_917_440;
const BOT_IMPROVER_142_HIDER_SHA256: &str =
    "7eaa9abd55888d67e961e833f3244570221ba4778626a44c21ad5277649aab67";
const BOT_IMPROVER_142_HIDER_BYTES: u64 = 1_534_976;
const BOT_IMPROVER_141_HIDER_SHA256: &str =
    "e420b634e19707bf8f3aa099ccdeb7493ff6755f1f01138a6e607c80cf72ccdf";
const BOT_IMPROVER_141_HIDER_BYTES: u64 = 1_450_496;
const BOT_IMPROVER_142_CONTROLLER_IMPL_SHA256: &str =
    "92d6d1ee346289ff9b06acf8edbf693f5bc383fd8287867516ad04fab57c66bd";
const BOT_IMPROVER_142_CONTROLLER_IMPL_BYTES: u64 = 16_896;

const BOT_IMPROVER_PLUGIN_NAMES: &[&str] = &[
    "botai",
    "botbuy",
    "botcontrollerimpl",
    "bothiderimpl",
    "botrandomizer",
    "botstate",
    "botteams",
    "rounddamagerecap",
];

const BOT_IMPROVER_BEHAVIOR_PLUGIN_NAMES: &[&str] = &[
    "botai",
    "botbuy",
    "botrandomizer",
    "botstate",
    "botteams",
    "rounddamagerecap",
];

const KNOWN_COSMETIC_PLUGIN_NAMES: &[&str] =
    &["cs2-weaponpaints", "cs2_weaponpaints", "weaponpaints"];

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) enum DiagnosticStatus {
    Pass,
    Warning,
    Error,
    Unverified,
    NotApplicable,
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
    pub requested_path: String,
    pub cs2_root: String,
    pub game_csgo_path: String,
    pub overall: DiagnosticStatus,
    pub runtime_verification: String,
    pub checks: Vec<DiagnosticCheckDto>,
    pub plugins: Vec<CssPluginDto>,
    pub conflicts: Vec<DiagnosticConflictDto>,
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

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct CssPluginDto {
    pub name: String,
    pub directory: String,
    pub assembly_files: Vec<String>,
    pub classification: String,
    pub runtime_state: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DiagnosticConflictDto {
    pub rule_id: String,
    pub severity: String,
    pub confidence: String,
    pub title: String,
    pub summary: String,
    pub evidence_path: String,
    pub affected_features: Vec<String>,
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
    pub manifest_abi: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_controller_abi: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_controller_minor: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_hider_api: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bot_randomizer_api: Option<i32>,
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
    pub(crate) dtr_reader: DtrReaderContractWire,
    pub(crate) bot_controller: BotControllerContractWire,
    pub(crate) bot_hider: BotHiderContractWire,
    pub(crate) bot_randomizer: BotRandomizerContractWire,
    pub(crate) demotracer: DemoTracerContractWire,
    pub(crate) counterstrikesharp: CounterStrikeSharpContractWire,
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
    pub(crate) required_capabilities_hex: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct BotHiderContractWire {
    pub(crate) api: i32,
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
    cosmetics: RuntimeCosmeticsWire,
    loaded_css_plugin_directories: Vec<String>,
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

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeCosmeticsWire {
    alignment_enabled: bool,
    weapons_enabled: bool,
    knives_enabled: bool,
    gloves_enabled: bool,
    names_enabled: bool,
    agents_enabled: bool,
    stickers_enabled: bool,
    charms_enabled: bool,
    preserve_native_enabled: bool,
}

#[derive(Debug)]
pub(crate) struct InstallPaths {
    pub(crate) cs2_root: PathBuf,
    pub(crate) game_csgo: PathBuf,
}

#[derive(Debug, Default)]
struct ReceiptAudit {
    summary: InstallReceiptSummaryDto,
    component_mismatches: BTreeSet<String>,
}

#[derive(Debug, Default)]
struct RuntimeAudit {
    verification: String,
    plugin_version: Option<String>,
    counter_strike_sharp_version: Option<String>,
    loaded_plugin_directories: Option<BTreeSet<String>>,
    cosmetics: Option<RuntimeCosmeticsWire>,
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

    checks.push(DiagnosticCheckDto {
        id: "cs2.path".to_string(),
        group: "cs2".to_string(),
        status: DiagnosticStatus::Pass,
        title: "CS2 game directory".to_string(),
        summary: "The selected path resolves to a local game/csgo directory.".to_string(),
        expected: Some("game/csgo/gameinfo.gi".to_string()),
        actual: Some(game_csgo.display().to_string()),
        evidence_path: Some(game_csgo.join("gameinfo.gi").display().to_string()),
        action: None,
    });

    let executable = paths
        .cs2_root
        .join("game")
        .join("bin")
        .join("win64")
        .join("cs2.exe");
    checks.push(single_file_check(
        "cs2.executable",
        "cs2",
        "CS2 Windows executable",
        &paths.cs2_root,
        &executable,
    ));

    checks.push(metamod_files_check(game_csgo));
    checks.push(metamod_gameinfo_check(game_csgo));
    let mut runtime_audit = inspect_runtime_health(game_csgo);
    checks.push(counterstrikesharp_check(
        game_csgo,
        runtime_audit.counter_strike_sharp_version.as_deref(),
    ));

    checks.push(required_files_check(
        "demotracer.botController",
        "demotracer",
        "DemoTracer BotController runtime",
        game_csgo,
        &[
            "addons/BotController/bin/win64/BotController.dll",
            "addons/BotController/gamedata.json",
            "addons/metamod/BotController.vdf",
        ],
    ));
    checks.push(required_files_check(
        "demotracer.botHiderNative",
        "demotracer",
        "DemoTracer BotHider native runtime",
        game_csgo,
        &[
            "addons/BotHider/bin/win64/BotHider.dll",
            "addons/BotHider/gamedata.json",
            "addons/metamod/BotHider.vdf",
        ],
    ));
    checks.push(required_files_check(
        "demotracer.plugin",
        "demotracer",
        "DemoTracer CounterStrikeSharp plugin",
        game_csgo,
        &[
            "addons/counterstrikesharp/plugins/DemoTracer/DemoTracer.dll",
            "addons/counterstrikesharp/shared/DemoTracerApi/DemoTracerApi.dll",
            "addons/counterstrikesharp/plugins/DemoTracer/cs2-lib-econ-index.v1.json",
        ],
    ));
    checks.push(required_files_check(
        "demotracer.botHiderPlugin",
        "demotracer",
        "DemoTracer BotHider managed provider",
        game_csgo,
        &[
            "addons/counterstrikesharp/plugins/DemoTracerBotHider/DemoTracerBotHider.dll",
            "addons/counterstrikesharp/shared/DemoTracerBotHiderApi/DemoTracerBotHiderApi.dll",
            "addons/counterstrikesharp/shared/0Harmony/0Harmony.dll",
        ],
    ));
    checks.push(json_files_check(game_csgo));
    checks.push(vdf_targets_check(game_csgo));
    checks.append(&mut runtime_audit.checks);

    let receipt_audit = inspect_install_receipt(game_csgo, &mut checks);
    let plugins = scan_css_plugins(
        game_csgo,
        &mut checks,
        runtime_audit.loaded_plugin_directories.as_ref(),
    );
    checks.push(bot_improver_behavior_check(&plugins, &receipt_audit));
    let conflicts = detect_conflicts(game_csgo, &plugins, &receipt_audit, &runtime_audit);
    let overall = overall_status(&checks, &conflicts);

    Ok(EnvironmentDiagnosticReportDto {
        checked_at_ms: now_ms(),
        requested_path: requested_path.trim().to_string(),
        cs2_root: paths.cs2_root.display().to_string(),
        game_csgo_path: game_csgo.display().to_string(),
        overall,
        runtime_verification: runtime_audit.verification,
        checks,
        plugins,
        conflicts,
        receipt: receipt_audit.summary,
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
    let metamod_root = game_csgo.join("addons").join("metamod");
    let win64 = metamod_root.join("bin").join("win64");
    let native_present = is_normal_directory_below(game_csgo, &win64)
        && fs::read_dir(&win64)
            .ok()
            .into_iter()
            .flatten()
            .filter_map(Result::ok)
            .any(|entry| {
                let path = entry.path();
                is_normal_file_below(game_csgo, &path)
                    && path.extension().is_some_and(|extension| {
                        extension.to_string_lossy().eq_ignore_ascii_case("dll")
                    })
            });
    DiagnosticCheckDto {
        id: "metamod.files".to_string(),
        group: "dependencies".to_string(),
        status: if native_present {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "Metamod:Source files".to_string(),
        summary: if native_present {
            "Metamod's Windows runtime directory is present.".to_string()
        } else {
            "Metamod's Windows runtime files were not found.".to_string()
        },
        expected: Some("addons/metamod/bin/win64/*.dll".to_string()),
        actual: Some(if native_present { "present" } else { "missing" }.to_string()),
        evidence_path: Some(win64.display().to_string()),
        action: (!native_present)
            .then(|| "Install a current Metamod:Source build for CS2.".to_string()),
    }
}

fn metamod_gameinfo_check(game_csgo: &Path) -> DiagnosticCheckDto {
    let gameinfo = game_csgo.join("gameinfo.gi");
    let wired = read_small_text_below(game_csgo, &gameinfo, MAX_TEXT_FILE_BYTES)
        .ok()
        .is_some_and(|text| text.to_ascii_lowercase().contains("csgo/addons/metamod"));
    DiagnosticCheckDto {
        id: "metamod.gameinfo".to_string(),
        group: "dependencies".to_string(),
        status: if wired {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Warning
        },
        title: "Metamod loader entry".to_string(),
        summary: if wired {
            "gameinfo.gi contains the Metamod search path.".to_string()
        } else {
            "Metamod files may exist, but its gameinfo.gi loader entry was not confirmed."
                .to_string()
        },
        expected: Some("Game csgo/addons/metamod".to_string()),
        actual: Some(if wired { "present" } else { "not confirmed" }.to_string()),
        evidence_path: Some(gameinfo.display().to_string()),
        action: (!wired)
            .then(|| "Re-run the Metamod installation steps for this CS2 tree.".to_string()),
    }
}

fn counterstrikesharp_check(game_csgo: &Path, runtime_version: Option<&str>) -> DiagnosticCheckDto {
    let root = game_csgo.join("addons").join("counterstrikesharp");
    let vdf = game_csgo
        .join("addons")
        .join("metamod")
        .join("counterstrikesharp.vdf");
    let present =
        is_normal_directory_below(game_csgo, &root) && is_normal_file_below(game_csgo, &vdf);
    let expected_version = embedded_playback_contract()
        .ok()
        .map(|contract| contract.counterstrikesharp.minimum_version)
        .unwrap_or_else(|| "1.0.371".to_string());
    let version_compatible = runtime_version
        .is_some_and(|version| version_tuple(version) >= version_tuple(&expected_version));
    DiagnosticCheckDto {
        id: "counterStrikeSharp.runtime".to_string(),
        group: "dependencies".to_string(),
        status: if present && version_compatible {
            DiagnosticStatus::Pass
        } else if present && runtime_version.is_some() {
            DiagnosticStatus::Error
        } else if present {
            DiagnosticStatus::Unverified
        } else {
            DiagnosticStatus::Error
        },
        title: "CounterStrikeSharp runtime".to_string(),
        summary: if present && version_compatible {
            "CounterStrikeSharp is installed and a fresh DemoTracer heartbeat proves that its loaded host version meets the required contract."
                .to_string()
        } else if present && runtime_version.is_some() {
            "The loaded CounterStrikeSharp host is older than DemoTracer's required version."
                .to_string()
        } else if present {
            "CounterStrikeSharp is installed; its loaded state and exact version are not proven by files alone.".to_string()
        } else {
            "CounterStrikeSharp or its Metamod loader file is missing.".to_string()
        },
        expected: Some(format!("CounterStrikeSharp {expected_version} or newer")),
        actual: Some(
            if present && version_compatible {
                runtime_version.unwrap_or("unknown")
            } else if present && runtime_version.is_some() {
                runtime_version.unwrap_or("unknown")
            } else if present {
                "installed, version unverified"
            } else {
                "missing"
            }
            .to_string(),
        ),
        evidence_path: Some(root.display().to_string()),
        action: if !present || (runtime_version.is_some() && !version_compatible) {
            Some(format!(
                "Install CounterStrikeSharp {expected_version} or newer."
            ))
        } else {
            None
        },
    }
}

fn inspect_runtime_health(game_csgo: &Path) -> RuntimeAudit {
    let path = join_public_relative(game_csgo, RUNTIME_HEALTH_RELATIVE_PATH);
    if !is_normal_file_below(game_csgo, &path) {
        return runtime_audit_without_live_evidence(
            "unavailable",
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
                "unknown",
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
                "unknown",
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
        || health.loaded_css_plugin_directories.len() > MAX_PLUGIN_DIRECTORIES
    {
        return runtime_audit_without_live_evidence(
            "unknown",
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
            "unknown",
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
            "notRunning",
            DiagnosticStatus::NotApplicable,
            "DemoTracer recorded a clean runtime stop. Installed files can still be inspected, but no plugin is currently proven active.",
            "stopped",
            &path,
            Some("Start the local replay server and inspect again for live ABI and plugin evidence."),
        );
    }
    if age_ms > MAX_RUNTIME_HEALTH_AGE_MS {
        return runtime_audit_without_live_evidence(
            "notRunning",
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

    let mut loaded_plugin_directories = BTreeSet::new();
    for name in &health.loaded_css_plugin_directories {
        let trimmed = name.trim();
        if trimmed.is_empty()
            || trimmed.len() > 128
            || trimmed
                .chars()
                .any(|character| matches!(character, '/' | '\\' | ':'))
            || matches!(trimmed, "." | "..")
        {
            return runtime_audit_without_live_evidence(
                "unknown",
                DiagnosticStatus::Warning,
                "The runtime heartbeat contains an unsafe CSS plugin directory name.",
                "invalid plugin inventory",
                &path,
                Some("Restart DemoTracer and inspect again."),
            );
        }
        loaded_plugin_directories.insert(trimmed.to_ascii_lowercase());
    }

    let expected = match embedded_playback_contract() {
        Ok(expected) => expected,
        Err(error) => {
            return runtime_audit_without_live_evidence(
                "unknown",
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
    checks.push(runtime_cosmetics_check(&path, &health.cosmetics));

    RuntimeAudit {
        verification: "verified".to_string(),
        plugin_version: Some(health.plugin_version),
        counter_strike_sharp_version: Some(health.counter_strike_sharp_version),
        loaded_plugin_directories: Some(loaded_plugin_directories),
        cosmetics: Some(health.cosmetics),
        checks,
    }
}

pub(crate) fn fresh_runtime_plugin_version(game_csgo: &Path) -> Option<String> {
    inspect_runtime_health(game_csgo).plugin_version
}

fn runtime_audit_without_live_evidence(
    verification: &str,
    status: DiagnosticStatus,
    summary: &str,
    actual: &str,
    path: &Path,
    action: Option<&str>,
) -> RuntimeAudit {
    RuntimeAudit {
        verification: verification.to_string(),
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

fn runtime_cosmetics_check(path: &Path, cosmetics: &RuntimeCosmeticsWire) -> DiagnosticCheckDto {
    let enabled = [
        ("master", cosmetics.alignment_enabled),
        ("weapons", cosmetics.weapons_enabled),
        ("knives", cosmetics.knives_enabled),
        ("gloves", cosmetics.gloves_enabled),
        ("names", cosmetics.names_enabled),
        ("agents", cosmetics.agents_enabled),
        ("stickers", cosmetics.stickers_enabled),
        ("charms", cosmetics.charms_enabled),
        ("preserve-native", cosmetics.preserve_native_enabled),
    ]
    .into_iter()
    .filter_map(|(name, enabled)| enabled.then_some(name))
    .collect::<Vec<_>>();
    DiagnosticCheckDto {
        id: "runtime.cosmetics".to_string(),
        group: "runtime".to_string(),
        status: DiagnosticStatus::Pass,
        title: "Live DemoTracer cosmetic alignment settings".to_string(),
        summary: if enabled.is_empty() {
            "DemoTracer cosmetic, sticker, charm, and agent alignment are currently off."
                .to_string()
        } else {
            format!("Runtime-enabled cosmetic features: {}.", enabled.join(", "))
        },
        expected: None,
        actual: Some(if enabled.is_empty() {
            "all off".to_string()
        } else {
            enabled.join(", ")
        }),
        evidence_path: Some(path.display().to_string()),
        action: None,
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

fn single_file_check(
    id: &str,
    group: &str,
    title: &str,
    root: &Path,
    path: &Path,
) -> DiagnosticCheckDto {
    let present = is_normal_file_below(root, path);
    DiagnosticCheckDto {
        id: id.to_string(),
        group: group.to_string(),
        status: if present {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::NotApplicable
        },
        title: title.to_string(),
        summary: if present {
            "File is present."
        } else {
            "No client executable was found; this can be valid for a dedicated replay-server tree."
        }
        .to_string(),
        expected: Some(path.display().to_string()),
        actual: Some(if present { "present" } else { "missing" }.to_string()),
        evidence_path: Some(path.display().to_string()),
        action: None,
    }
}

fn json_files_check(game_csgo: &Path) -> DiagnosticCheckDto {
    let files = [
        "addons/BotController/gamedata.json",
        "addons/BotHider/gamedata.json",
        "addons/counterstrikesharp/plugins/DemoTracer/cs2-lib-econ-index.v1.json",
    ];
    let invalid = files
        .iter()
        .filter_map(|relative| {
            let path = join_public_relative(game_csgo, relative);
            if !is_normal_file_below(game_csgo, &path) {
                return None;
            }
            match read_small_text_below(game_csgo, &path, MAX_TEXT_FILE_BYTES) {
                Ok(text) => serde_json::from_str::<serde_json::Value>(&text)
                    .err()
                    .map(|error| format!("{relative}: {error}")),
                Err(error) => Some(format!("{relative}: {error}")),
            }
        })
        .collect::<Vec<_>>();
    DiagnosticCheckDto {
        id: "demotracer.json".to_string(),
        group: "demotracer".to_string(),
        status: if invalid.is_empty() {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "DemoTracer JSON data".to_string(),
        summary: if invalid.is_empty() {
            "Installed DemoTracer JSON data is syntactically valid.".to_string()
        } else {
            invalid.join("; ")
        },
        expected: Some("Valid JSON for installed gamedata and econ index files".to_string()),
        actual: Some(
            if invalid.is_empty() {
                "valid"
            } else {
                "invalid"
            }
            .to_string(),
        ),
        evidence_path: Some(game_csgo.join("addons").display().to_string()),
        action: (!invalid.is_empty()).then(|| {
            "Replace the damaged files from one complete DemoTracer playback bundle.".to_string()
        }),
    }
}

fn vdf_targets_check(game_csgo: &Path) -> DiagnosticCheckDto {
    let expected = [
        (
            "addons/metamod/BotController.vdf",
            "addons/botcontroller/bin/win64/botcontroller",
        ),
        (
            "addons/metamod/BotHider.vdf",
            "addons/bothider/bin/win64/bothider",
        ),
    ];
    let mut invalid = Vec::new();
    for (relative, target) in expected {
        let path = join_public_relative(game_csgo, relative);
        let ok = read_small_text_below(game_csgo, &path, MAX_TEXT_FILE_BYTES)
            .ok()
            .is_some_and(|text| {
                text.replace('\\', "/")
                    .to_ascii_lowercase()
                    .contains(target)
            });
        if !ok {
            invalid.push(relative);
        }
    }
    DiagnosticCheckDto {
        id: "demotracer.vdfTargets".to_string(),
        group: "demotracer".to_string(),
        status: if invalid.is_empty() {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Error
        },
        title: "DemoTracer Metamod loader targets".to_string(),
        summary: if invalid.is_empty() {
            "BotController and BotHider VDF files point at DemoTracer's expected native paths."
                .to_string()
        } else {
            format!(
                "Missing or unexpected loader target: {}",
                invalid.join(", ")
            )
        },
        expected: Some("DemoTracer BotController and BotHider native paths".to_string()),
        actual: Some(
            if invalid.is_empty() {
                "matching"
            } else {
                "not matching"
            }
            .to_string(),
        ),
        evidence_path: Some(
            game_csgo
                .join("addons")
                .join("metamod")
                .display()
                .to_string(),
        ),
        action: (!invalid.is_empty()).then(|| {
            "Reinstall DemoTracer's VDF and matching native runtime together.".to_string()
        }),
    }
}

fn inspect_install_receipt(game_csgo: &Path, checks: &mut Vec<DiagnosticCheckDto>) -> ReceiptAudit {
    let receipt_path = join_public_relative(game_csgo, INSTALL_RECEIPT_RELATIVE_PATH);
    if !is_normal_file_below(game_csgo, &receipt_path) {
        checks.push(DiagnosticCheckDto {
            id: "demotracer.receipt".to_string(),
            group: "demotracer".to_string(),
            status: DiagnosticStatus::Unverified,
            title: "DemoTracer install receipt".to_string(),
            summary: "This is a legacy or unverified install. Component vendor and exact ABI cannot be proven from file names alone.".to_string(),
            expected: Some(INSTALL_RECEIPT_RELATIVE_PATH.to_string()),
            actual: Some("missing".to_string()),
            evidence_path: Some(receipt_path.display().to_string()),
            action: Some("Install a current complete DemoTracer playback bundle to enable vendor-integrity checks.".to_string()),
        });
        return ReceiptAudit::default();
    }

    let mut audit = ReceiptAudit {
        summary: InstallReceiptSummaryDto {
            found: true,
            path: Some(receipt_path.display().to_string()),
            ..InstallReceiptSummaryDto::default()
        },
        ..ReceiptAudit::default()
    };
    let text = match read_small_text_below(game_csgo, &receipt_path, MAX_TEXT_FILE_BYTES) {
        Ok(text) => text,
        Err(error) => {
            checks.push(receipt_error_check(&receipt_path, error));
            audit.summary.verified = Some(false);
            return audit;
        }
    };
    let receipt = match serde_json::from_str::<InstallReceiptWire>(&text) {
        Ok(receipt) => receipt,
        Err(error) => {
            checks.push(receipt_error_check(&receipt_path, error.to_string()));
            audit.summary.verified = Some(false);
            return audit;
        }
    };
    audit.summary.bundle_version = Some(receipt.bundle_version.clone());
    audit.summary.manifest_abi = Some(receipt.compatibility.manifest_abi);
    audit.summary.bot_controller_abi = Some(receipt.compatibility.bot_controller.abi_major);
    audit.summary.bot_controller_minor = Some(receipt.compatibility.bot_controller.min_abi_minor);
    audit.summary.bot_hider_api = Some(receipt.compatibility.bot_hider.api);
    audit.summary.bot_randomizer_api = Some(receipt.compatibility.bot_randomizer.api);
    audit.summary.demo_tracer_api = Some(receipt.compatibility.demotracer.companion_api);

    let contract_errors = contract_errors(&receipt);
    let mut integrity_errors = Vec::new();
    if receipt.files.len() > MAX_RECEIPT_FILES {
        audit.summary.files_mismatched = receipt.files.len();
        integrity_errors.push(format!(
            "receipt lists too many files ({})",
            receipt.files.len()
        ));
    } else {
        let mut recorded_paths = BTreeSet::new();
        for file in &receipt.files {
            let normalized_path = normalized_receipt_path(&file.path);
            if !recorded_paths.insert(normalized_path.clone()) {
                audit.summary.files_mismatched += 1;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!("duplicate receipt path: {}", file.path));
                }
                continue;
            }
            let expected_component = receipt_component(&normalized_path);
            let mut mismatched = false;
            if expected_component.is_some_and(|component| component != file.component) {
                mismatched = true;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!(
                        "{} component {} is not {}",
                        file.path,
                        file.component,
                        expected_component.unwrap_or("unknown")
                    ));
                }
                if let Some(component) = expected_component {
                    audit.component_mismatches.insert(component.to_string());
                }
            }
            audit.summary.files_checked += 1;
            match verify_receipt_file(game_csgo, file) {
                Ok(()) => {}
                Err(error) => {
                    mismatched = true;
                    audit.component_mismatches.insert(
                        expected_component
                            .unwrap_or(file.component.as_str())
                            .to_string(),
                    );
                    if integrity_errors.len() < 12 {
                        integrity_errors.push(error);
                    }
                }
            }
            if mismatched {
                audit.summary.files_mismatched += 1;
            }
        }
        for required in REQUIRED_RECEIPT_PATHS {
            if !recorded_paths.contains(*required) {
                audit.summary.files_mismatched += 1;
                if integrity_errors.len() < 12 {
                    integrity_errors.push(format!("receipt omits required file: {required}"));
                }
                if let Some(component) = receipt_component(required) {
                    audit.component_mismatches.insert(component.to_string());
                }
            }
        }
    }
    let verified = contract_errors.is_empty() && integrity_errors.is_empty();
    audit.summary.verified = Some(verified);
    checks.push(DiagnosticCheckDto {
        id: "demotracer.receipt".to_string(),
        group: "demotracer".to_string(),
        status: if verified { DiagnosticStatus::Pass } else { DiagnosticStatus::Error },
        title: "DemoTracer install receipt".to_string(),
        summary: if verified {
            format!(
                "Bundle {} matches the receipt metadata and all {} recorded file hashes. This proves package integrity; loaded ABI/API compatibility is verified separately by the runtime heartbeat.",
                receipt.bundle_version, audit.summary.files_checked
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
            audit.summary.files_mismatched
        )),
        evidence_path: Some(receipt_path.display().to_string()),
        action: (!verified).then(|| "A component was mixed, replaced, or damaged. Reinstall one complete DemoTracer playback bundle before replay.".to_string()),
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

fn contract_errors(receipt: &InstallReceiptWire) -> Vec<String> {
    let mut errors = Vec::new();
    let expected = match embedded_playback_contract() {
        Ok(expected) => expected,
        Err(error) => return vec![error],
    };
    let actual = &receipt.compatibility;
    if receipt.schema_version != 1 || actual.schema_version != expected.schema_version {
        errors.push("unsupported receipt or compatibility schema".to_string());
    }
    if receipt.product != expected.product || actual.product != expected.product {
        errors.push("receipt product is not CS2 DemoTracer".to_string());
    }
    if receipt.platform != expected.platform || actual.platform != expected.platform {
        errors.push(format!(
            "platform {} is not {}",
            receipt.platform, expected.platform
        ));
    }
    if actual.manifest_abi != expected.manifest_abi {
        errors.push(format!(
            "manifest ABI {} is not {}",
            actual.manifest_abi, expected.manifest_abi
        ));
    }
    if actual.dtr_writer != expected.dtr_writer
        || actual.dtr_reader.min > expected.dtr_writer
        || actual.dtr_reader.max < expected.dtr_writer
    {
        errors
            .push("installed DTR writer/reader contract does not cover the GUI writer".to_string());
    }
    if actual.bot_controller.abi_major != expected.bot_controller.abi_major
        || actual.bot_controller.min_abi_minor < expected.bot_controller.min_abi_minor
    {
        errors.push(format!(
            "BotController ABI {}/{} is incompatible with required {}/{}+",
            actual.bot_controller.abi_major,
            actual.bot_controller.min_abi_minor,
            expected.bot_controller.abi_major,
            expected.bot_controller.min_abi_minor
        ));
    }
    if actual
        .bot_controller
        .required_capabilities_hex
        .to_ascii_lowercase()
        != expected
            .bot_controller
            .required_capabilities_hex
            .to_ascii_lowercase()
    {
        errors.push("BotController required capability contract differs".to_string());
    }
    if actual.bot_hider.api != expected.bot_hider.api {
        errors.push(format!(
            "BotHider API {} is not {}",
            actual.bot_hider.api, expected.bot_hider.api
        ));
    }
    if actual.bot_randomizer != expected.bot_randomizer {
        errors.push(format!(
            "BotRandomizer {}/API {} is not {}/API {}",
            actual.bot_randomizer.provider_version,
            actual.bot_randomizer.api,
            expected.bot_randomizer.provider_version,
            expected.bot_randomizer.api
        ));
    }
    if actual.demotracer.companion_api != expected.demotracer.companion_api {
        errors.push(format!(
            "DemoTracer API {} is not {}",
            actual.demotracer.companion_api, expected.demotracer.companion_api
        ));
    }
    if actual.counterstrikesharp.target_framework != expected.counterstrikesharp.target_framework {
        errors.push("CounterStrikeSharp target framework differs".to_string());
    }
    if version_tuple(&actual.counterstrikesharp.minimum_version)
        < version_tuple(&expected.counterstrikesharp.minimum_version)
    {
        errors.push(format!(
            "CounterStrikeSharp minimum {} is older than {}",
            actual.counterstrikesharp.minimum_version, expected.counterstrikesharp.minimum_version
        ));
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
    {
        Some("bot_controller")
    } else if normalized_path.starts_with("addons/bothider/")
        || normalized_path == "addons/metamod/bothider.vdf"
    {
        Some("bot_hider_native")
    } else if normalized_path.starts_with("addons/counterstrikesharp/plugins/demotracerbothider/")
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

fn scan_css_plugins(
    game_csgo: &Path,
    checks: &mut Vec<DiagnosticCheckDto>,
    loaded_plugin_directories: Option<&BTreeSet<String>>,
) -> Vec<CssPluginDto> {
    let root = game_csgo
        .join("addons")
        .join("counterstrikesharp")
        .join("plugins");
    if !is_normal_directory_below(game_csgo, &root) {
        checks.push(DiagnosticCheckDto {
            id: "plugins.inventory".to_string(),
            group: "plugins".to_string(),
            status: DiagnosticStatus::NotApplicable,
            title: "CounterStrikeSharp plugin inventory".to_string(),
            summary: "No CounterStrikeSharp plugins directory is available to scan.".to_string(),
            expected: None,
            actual: Some("not available".to_string()),
            evidence_path: Some(root.display().to_string()),
            action: None,
        });
        return Vec::new();
    }
    let mut directories = fs::read_dir(&root)
        .ok()
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect::<Vec<_>>();
    directories.sort();
    directories.truncate(MAX_PLUGIN_DIRECTORIES);
    let mut plugins = Vec::new();
    for directory in directories {
        let Ok(metadata) = fs::symlink_metadata(&directory) else {
            continue;
        };
        if !metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&metadata) {
            continue;
        }
        let name = directory
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        if name.is_empty() {
            continue;
        }
        let mut assembly_files = fs::read_dir(&directory)
            .ok()
            .into_iter()
            .flatten()
            .filter_map(Result::ok)
            .map(|entry| entry.path())
            .filter(|path| {
                is_normal_file_below(game_csgo, path)
                    && path.extension().is_some_and(|extension| {
                        extension.to_string_lossy().eq_ignore_ascii_case("dll")
                    })
            })
            .filter_map(|path| {
                path.file_name()
                    .map(|name| name.to_string_lossy().into_owned())
            })
            .collect::<Vec<_>>();
        assembly_files.sort();
        assembly_files.truncate(MAX_PLUGIN_DLLS);
        if assembly_files.is_empty() {
            continue;
        }
        plugins.push(CssPluginDto {
            classification: classify_plugin(&assembly_files).to_string(),
            runtime_state: match loaded_plugin_directories {
                Some(loaded) if loaded.contains(&name.to_ascii_lowercase()) => "loaded",
                Some(_) => "notLoaded",
                None => "unknown",
            }
            .to_string(),
            name,
            directory: directory.display().to_string(),
            assembly_files,
        });
    }
    checks.push(DiagnosticCheckDto {
        id: "plugins.inventory".to_string(),
        group: "plugins".to_string(),
        status: DiagnosticStatus::Pass,
        title: "CounterStrikeSharp plugin inventory".to_string(),
        summary: format!(
            "Found {} plugin directories. File presence does not prove that a plugin is loaded.",
            plugins.len()
        ),
        expected: None,
        actual: Some(format!("{} directories", plugins.len())),
        evidence_path: Some(root.display().to_string()),
        action: None,
    });
    plugins
}

fn plugin_assembly_identities(plugin: &CssPluginDto) -> BTreeSet<String> {
    plugin
        .assembly_files
        .iter()
        .filter_map(|file| Path::new(file).file_stem())
        .map(|stem| stem.to_string_lossy().to_ascii_lowercase())
        .collect()
}

fn plugin_has_identity(plugin: &CssPluginDto, identity: &str) -> bool {
    plugin_assembly_identities(plugin).contains(&identity.to_ascii_lowercase())
}

fn plugin_dll_path(plugin: &CssPluginDto, identity: &str) -> Option<PathBuf> {
    plugin.assembly_files.iter().find_map(|file| {
        Path::new(file)
            .file_stem()
            .is_some_and(|stem| stem.to_string_lossy().eq_ignore_ascii_case(identity))
            .then(|| Path::new(&plugin.directory).join(file))
    })
}

fn classify_plugin(assembly_files: &[String]) -> &'static str {
    let identities = assembly_files
        .iter()
        .filter_map(|file| Path::new(file).file_stem())
        .map(|stem| stem.to_string_lossy().to_ascii_lowercase())
        .collect::<BTreeSet<_>>();
    if identities
        .iter()
        .any(|identity| matches!(identity.as_str(), "demotracer" | "demotracerbothider"))
    {
        "demotracer"
    } else if identities.contains("botrandomizer") {
        "dependency"
    } else if identities
        .iter()
        .any(|identity| matches!(identity.as_str(), "raytraceimpl" | "raytrace"))
    {
        "dependency"
    } else if identities.iter().any(|identity| {
        matches!(
            identity.as_str(),
            "bothider" | "bothiderimpl" | "botcontrollerimpl" | "botrandomizer"
        ) || KNOWN_COSMETIC_PLUGIN_NAMES.contains(&identity.as_str())
    }) {
        "potentialConflict"
    } else {
        "unknown"
    }
}

fn detect_conflicts(
    game_csgo: &Path,
    plugins: &[CssPluginDto],
    receipt: &ReceiptAudit,
    runtime: &RuntimeAudit,
) -> Vec<DiagnosticConflictDto> {
    let mut conflicts = Vec::new();
    let bundled_bot_randomizer_verified = receipt.summary.verified == Some(true)
        && !receipt
            .component_mismatches
            .contains("bot_randomizer_managed");
    let bundled_bot_randomizer_directory = game_csgo
        .join("addons/counterstrikesharp/plugins/BotRandomizer")
        .to_string_lossy()
        .replace('\\', "/");
    let is_bundled_bot_randomizer = |plugin: &CssPluginDto| {
        bundled_bot_randomizer_verified
            && plugin
                .directory
                .replace('\\', "/")
                .eq_ignore_ascii_case(&bundled_bot_randomizer_directory)
    };
    let names = plugins
        .iter()
        .flat_map(plugin_assembly_identities)
        .collect::<BTreeSet<_>>();

    let controller_path = game_csgo.join("addons/BotController/bin/win64/BotController.dll");
    let hider_path = game_csgo.join("addons/BotHider/bin/win64/BotHider.dll");
    let controller_impl_path = plugins
        .iter()
        .find_map(|plugin| plugin_dll_path(plugin, "botcontrollerimpl"));
    let known_improver_controller = matches_file_fingerprint(
        game_csgo,
        &controller_path,
        BOT_IMPROVER_142_CONTROLLER_BYTES,
        BOT_IMPROVER_142_CONTROLLER_SHA256,
    );
    let known_improver_hider_142 = matches_file_fingerprint(
        game_csgo,
        &hider_path,
        BOT_IMPROVER_142_HIDER_BYTES,
        BOT_IMPROVER_142_HIDER_SHA256,
    );
    let known_improver_hider_141 = matches_file_fingerprint(
        game_csgo,
        &hider_path,
        BOT_IMPROVER_141_HIDER_BYTES,
        BOT_IMPROVER_141_HIDER_SHA256,
    );
    let known_improver_hider = known_improver_hider_142 || known_improver_hider_141;

    if known_improver_controller || known_improver_hider {
        let mut matched = Vec::new();
        if known_improver_controller {
            matched.push("BotController 0.5.2 / ABI 14");
        }
        if known_improver_hider_142 {
            matched.push("BotHider 0.3.1 (v1.4.2 package)");
        }
        if known_improver_hider_141 {
            matched.push("BotHider 0.2.0 (v1.4.1 package)");
        }
        conflicts.push(DiagnosticConflictDto {
            rule_id: "cs2_bot_improver_known_native_vendor".to_string(),
            severity: "error".to_string(),
            confidence: "certain".to_string(),
            title: "CS2-Bot-Improver native vendor files are installed".to_string(),
            summary: format!(
                "Exact known CS2-Bot-Improver release fingerprints were found: {}. Its native vendor set is not the DemoTracer contract; the v1.4.2 BotController specifically uses ABI 14 instead of DemoTracer's ABI 18/minor 33. Reinstall DemoTracer's complete playback bundle, then keep only compatible post-handoff behavior plugins.",
                matched.join(", ")
            ),
            evidence_path: if known_improver_controller {
                controller_path.display().to_string()
            } else {
                hider_path.display().to_string()
            },
            affected_features: vec![
                "native replay runtime".to_string(),
                "post-handoff bot AI".to_string(),
                "bot identity".to_string(),
            ],
        });
    }

    if names.contains("botcontrollerimpl") {
        let known_abi14_bridge = controller_impl_path.as_deref().is_some_and(|path| {
            matches_file_fingerprint(
                game_csgo,
                path,
                BOT_IMPROVER_142_CONTROLLER_IMPL_BYTES,
                BOT_IMPROVER_142_CONTROLLER_IMPL_SHA256,
            )
        });
        conflicts.push(DiagnosticConflictDto {
            rule_id: "cs2_bot_improver_controller_bridge".to_string(),
            severity: "warning".to_string(),
            confidence: if known_abi14_bridge { "certain" } else { "high" }.to_string(),
            title: if known_abi14_bridge {
                "CS2-Bot-Improver's ABI 14 BotController bridge is installed"
            } else {
                "A second BotController CounterStrikeSharp bridge is installed"
            }
            .to_string(),
            summary: if known_abi14_bridge {
                "This exact BotControllerImpl build expects ABI 14 and disables itself against DemoTracer's ABI 18 runtime, so Improver behavior plugins cannot obtain their botcontroller:api dependency. Remove BotControllerImpl when using DemoTracer's bundled native runtime."
            } else {
                "BotControllerImpl uses the same managed capability surface as post-handoff behavior plugins. Its ABI contract could not be proven; verify that it explicitly supports DemoTracer BotController ABI 18/minor 33 before use."
            }
            .to_string(),
            evidence_path: controller_impl_path
                .as_deref()
                .map(|path| path.display().to_string())
                .unwrap_or_else(|| game_csgo.join("addons/counterstrikesharp/plugins").display().to_string()),
            affected_features: vec![
                "post-handoff bot AI".to_string(),
                "native replay runtime".to_string(),
            ],
        });
    }

    for plugin in plugins {
        if ["bothider", "bothiderimpl"]
            .iter()
            .any(|identity| plugin_has_identity(plugin, identity))
        {
            conflicts.push(DiagnosticConflictDto {
                rule_id: "duplicate_bot_hider_publisher".to_string(),
                severity: "error".to_string(),
                confidence: "high".to_string(),
                title: "A second BotHider presentation publisher is installed".to_string(),
                summary: "DemoTracerBotHider must be the sole BotHider CounterStrikeSharp presentation publisher.".to_string(),
                evidence_path: plugin.directory.clone(),
                affected_features: vec!["identity".to_string(), "crosshair".to_string(), "bot ownership".to_string()],
            });
        }
    }

    for plugin in plugins {
        let identities = plugin_assembly_identities(plugin);
        if identities
            .iter()
            .any(|identity| KNOWN_COSMETIC_PLUGIN_NAMES.contains(&identity.as_str()))
            && plugin.runtime_state != "notLoaded"
        {
            let alignment_enabled = runtime.cosmetics.as_ref().is_some_and(|cosmetics| {
                cosmetics.alignment_enabled
                    && (cosmetics.weapons_enabled
                        || cosmetics.knives_enabled
                        || cosmetics.gloves_enabled
                        || cosmetics.names_enabled
                        || cosmetics.agents_enabled
                        || cosmetics.stickers_enabled
                        || cosmetics.charms_enabled)
            });
            let runtime_loaded = plugin.runtime_state == "loaded";
            conflicts.push(DiagnosticConflictDto {
                rule_id: "known_cosmetic_writer".to_string(),
                severity: "warning".to_string(),
                confidence: if runtime_loaded && alignment_enabled {
                    "certain"
                } else if runtime_loaded {
                    "high"
                } else {
                    "medium"
                }
                .to_string(),
                title: format!("{} may write the same bot cosmetic state", plugin.name),
                summary: if runtime_loaded && alignment_enabled {
                    "A fresh DemoTracer heartbeat shows this plugin assembly loaded while DemoTracer cosmetic alignment is enabled. Both may write the same replay-bot state."
                        .to_string()
                } else if runtime_loaded {
                    "A fresh DemoTracer heartbeat shows this plugin assembly loaded. DemoTracer cosmetic alignment is currently off; enabling it may create competing bot inventory or presentation writes."
                        .to_string()
                } else {
                    "This known cosmetic writer is installed, but current loaded state is unverified. It can conflict when DemoTracer cosmetic, sticker, charm, knife, glove, or agent alignment is enabled."
                        .to_string()
                },
                evidence_path: plugin.directory.clone(),
                affected_features: vec!["cosmetics".to_string(), "agents".to_string()],
            });
        }
        if identities.contains("botrandomizer")
            && plugin.runtime_state != "notLoaded"
            && !is_bundled_bot_randomizer(plugin)
        {
            let runtime_loaded = plugin.runtime_state == "loaded";
            let agent_alignment_enabled = runtime
                .cosmetics
                .as_ref()
                .is_some_and(|cosmetics| cosmetics.alignment_enabled && cosmetics.agents_enabled);
            conflicts.push(DiagnosticConflictDto {
                rule_id: "cs2_bot_improver_bot_randomizer".to_string(),
                severity: "warning".to_string(),
                confidence: if runtime_loaded && agent_alignment_enabled {
                    "certain"
                } else if runtime_loaded {
                    "high"
                } else {
                    "medium"
                }
                .to_string(),
                title: "CS2-Bot-Improver BotRandomizer is installed".to_string(),
                summary: if runtime_loaded && agent_alignment_enabled {
                    "A fresh heartbeat shows an additional BotRandomizer loaded while DemoTracer's bundled provider is accepting replay plans. Two cosmetic providers can write the same bot presentation state; keep only the bundled provider."
                        .to_string()
                } else if runtime_loaded {
                    "A fresh heartbeat shows an additional BotRandomizer loaded. It can compete with DemoTracer's bundled provider for bot agent, music, inventory, or model state."
                        .to_string()
                } else {
                    "An additional BotRandomizer is installed, but loaded state is unverified. Remove it so the bundled replay-plan provider remains the sole cosmetic writer."
                        .to_string()
                },
                evidence_path: plugin.directory.clone(),
                affected_features: vec!["agents".to_string(), "music kits".to_string()],
            });
        }
    }

    let external_bot_randomizer_present = plugins.iter().any(|plugin| {
        plugin_has_identity(plugin, "botrandomizer") && !is_bundled_bot_randomizer(plugin)
    });
    let improver_plugins = names
        .iter()
        .filter(|name| BOT_IMPROVER_PLUGIN_NAMES.contains(&name.as_str()))
        .filter(|name| name.as_str() != "botrandomizer" || external_bot_randomizer_present)
        .cloned()
        .collect::<Vec<_>>();
    let native_present = is_normal_file_below(game_csgo, &controller_path)
        || is_normal_file_below(game_csgo, &hider_path);
    if !improver_plugins.is_empty()
        && native_present
        && !known_improver_controller
        && !known_improver_hider
    {
        let native_mismatch = receipt.component_mismatches.contains("bot_controller")
            || receipt.component_mismatches.contains("bot_hider_native");
        let conflict = if native_mismatch {
            Some((
                "cs2_bot_improver_native_vendor_mismatch",
                "error",
                "BotController or BotHider no longer matches the DemoTracer vendor set",
                "CS2-Bot-Improver plugins are present and DemoTracer's install receipt proves that a native runtime file was replaced or mixed. The two projects vendor different BotController/BotHider builds.",
            ))
        } else if receipt.summary.verified != Some(true) {
            Some((
                "cs2_bot_improver_native_vendor_unverified",
                "warning",
                "CS2-Bot-Improver and unverified BotController/BotHider files coexist",
                "Both projects use BotController/BotHider names, but this legacy install has no DemoTracer receipt. File names alone cannot establish which vendor build is installed.",
            ))
        } else {
            None
        };
        if let Some((rule_id, severity, title, summary)) = conflict {
            conflicts.push(DiagnosticConflictDto {
                rule_id: rule_id.to_string(),
                severity: severity.to_string(),
                confidence: if improver_plugins.len() >= 2 {
                    "high"
                } else {
                    "medium"
                }
                .to_string(),
                title: title.to_string(),
                summary: format!(
                    "{summary} Detected Improver modules: {}.",
                    improver_plugins.join(", ")
                ),
                evidence_path: game_csgo.join("addons").display().to_string(),
                affected_features: vec![
                    "post-handoff bot AI".to_string(),
                    "native replay runtime".to_string(),
                    "bot identity".to_string(),
                ],
            });
        }
    }
    conflicts
}

fn bot_improver_behavior_check(
    plugins: &[CssPluginDto],
    receipt: &ReceiptAudit,
) -> DiagnosticCheckDto {
    let names = plugins
        .iter()
        .flat_map(plugin_assembly_identities)
        .collect::<BTreeSet<_>>();
    let behavior_plugins = names
        .iter()
        .filter(|name| BOT_IMPROVER_BEHAVIOR_PLUGIN_NAMES.contains(&name.as_str()))
        .cloned()
        .collect::<Vec<_>>();
    let legacy_bridge_present = names.iter().any(|name| {
        matches!(
            name.as_str(),
            "botcontrollerimpl" | "bothider" | "bothiderimpl"
        )
    });
    let supported_static_shape = !behavior_plugins.is_empty()
        && !legacy_bridge_present
        && receipt.summary.verified == Some(true);
    DiagnosticCheckDto {
        id: "compatibility.botImproverBehaviorOnly".to_string(),
        group: "compatibility".to_string(),
        status: if behavior_plugins.is_empty() {
            DiagnosticStatus::NotApplicable
        } else if supported_static_shape {
            DiagnosticStatus::Pass
        } else {
            DiagnosticStatus::Unverified
        },
        title: "CS2-Bot-Improver post-handoff behavior plugins".to_string(),
        summary: if behavior_plugins.is_empty() {
            "No known CS2-Bot-Improver behavior-only modules were found.".to_string()
        } else if supported_static_shape {
            format!(
                "A behavior-only combination is installed while DemoTracer's BotController/BotHider vendor set remains intact: {}. Loaded state still requires runtime evidence.",
                behavior_plugins.join(", ")
            )
        } else {
            format!(
                "Behavior modules are present, but a clean DemoTracer-native plus behavior-only combination was not proven: {}.",
                behavior_plugins.join(", ")
            )
        },
        expected: Some(
            "DemoTracer native receipt intact; no BotControllerImpl/BotHiderImpl; behavior plugins only"
                .to_string(),
        ),
        actual: Some(if behavior_plugins.is_empty() {
            "not installed".to_string()
        } else if supported_static_shape {
            "supported static layout".to_string()
        } else {
            "layout not proven".to_string()
        }),
        evidence_path: plugins
            .iter()
            .find(|plugin| {
                plugin_assembly_identities(plugin).iter().any(|identity| {
                    BOT_IMPROVER_BEHAVIOR_PLUGIN_NAMES.contains(&identity.as_str())
                })
            })
            .map(|plugin| plugin.directory.clone()),
        action: (!behavior_plugins.is_empty() && !supported_static_shape).then(|| {
            "Keep DemoTracer's complete native bundle and install only compatible behavior plugins for post-handoff enhancement."
                .to_string()
        }),
    }
}

fn matches_file_fingerprint(
    root: &Path,
    path: &Path,
    expected_size: u64,
    expected_sha256: &str,
) -> bool {
    let Ok(metadata) = metadata_below_without_reparse(root, path) else {
        return false;
    };
    if !metadata.is_file()
        || metadata.len() != expected_size
        || metadata.len() > MAX_RECEIPT_FILE_BYTES
    {
        return false;
    }
    fs::read(path)
        .ok()
        .is_some_and(|bytes| sha256_hex(&bytes).eq_ignore_ascii_case(expected_sha256))
}

fn overall_status(
    checks: &[DiagnosticCheckDto],
    conflicts: &[DiagnosticConflictDto],
) -> DiagnosticStatus {
    if checks
        .iter()
        .any(|check| check.status == DiagnosticStatus::Error)
        || conflicts
            .iter()
            .any(|conflict| conflict.severity == "error")
    {
        DiagnosticStatus::Error
    } else if checks
        .iter()
        .any(|check| check.status == DiagnosticStatus::Warning)
        || !conflicts.is_empty()
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

fn is_normal_directory_below(root: &Path, path: &Path) -> bool {
    metadata_below_without_reparse(root, path)
        .ok()
        .is_some_and(|metadata| metadata.is_dir())
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

fn version_tuple(value: &str) -> (u32, u32, u32, u32) {
    let mut parts = value
        .split(|character: char| !character.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .take(4)
        .map(|part| part.parse::<u32>().unwrap_or(0));
    (
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
    )
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
    fn compares_numeric_versions() {
        assert!(version_tuple("1.0.371") > version_tuple("1.0.99"));
        assert_eq!(version_tuple("v1.2"), (1, 2, 0, 0));
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
    fn installed_legacy_bot_hider_is_reported_as_a_conflict() {
        let tree = TempTree::cs2();
        let plugin = tree
            .game_csgo()
            .join("addons/counterstrikesharp/plugins/BotHiderImpl");
        fs::create_dir_all(&plugin).expect("create plugin directory");
        fs::write(plugin.join("BotHiderImpl.dll"), b"fixture").expect("write plugin fixture");

        let report = inspect_cs2_install_for(tree.root().to_str().expect("UTF-8 test path"))
            .expect("inspect fixture");
        assert!(report
            .conflicts
            .iter()
            .any(|conflict| conflict.rule_id == "duplicate_bot_hider_publisher"));
        assert!(report
            .plugins
            .iter()
            .any(|plugin| plugin.name.eq_ignore_ascii_case("BotHiderImpl")));
    }

    #[test]
    fn empty_plugin_directory_is_not_treated_as_a_bot_hider() {
        let tree = TempTree::cs2();
        fs::create_dir_all(
            tree.game_csgo()
                .join("addons/counterstrikesharp/plugins/BotHiderImpl"),
        )
        .expect("create empty plugin directory");

        let report = inspect_cs2_install_for(tree.root().to_str().expect("UTF-8 test path"))
            .expect("inspect fixture");
        assert!(!report
            .conflicts
            .iter()
            .any(|conflict| conflict.rule_id == "duplicate_bot_hider_publisher"));
        assert!(!report
            .plugins
            .iter()
            .any(|plugin| plugin.name.eq_ignore_ascii_case("BotHiderImpl")));
    }

    #[test]
    fn assembly_identity_detects_renamed_bot_hider_directory() {
        let tree = TempTree::cs2();
        let plugin = tree
            .game_csgo()
            .join("addons/counterstrikesharp/plugins/RenamedPlugin");
        fs::create_dir_all(&plugin).expect("create renamed plugin directory");
        fs::write(plugin.join("BotHiderImpl.dll"), b"fixture").expect("write plugin fixture");

        let report = inspect_cs2_install_for(tree.root().to_str().expect("UTF-8 test path"))
            .expect("inspect fixture");
        assert!(report.conflicts.iter().any(|conflict| {
            conflict.rule_id == "duplicate_bot_hider_publisher"
                && conflict.evidence_path.contains("RenamedPlugin")
        }));
    }

    #[test]
    fn loaded_counterstrikesharp_version_is_compared_to_the_contract() {
        let tree = TempTree::cs2();
        let css_root = tree.game_csgo().join("addons/counterstrikesharp");
        let css_vdf = tree
            .game_csgo()
            .join("addons/metamod/counterstrikesharp.vdf");
        fs::create_dir_all(&css_root).expect("create CSS root");
        fs::create_dir_all(css_vdf.parent().expect("CSS VDF parent")).expect("create Metamod root");
        fs::write(&css_vdf, b"fixture").expect("write CSS VDF");

        assert_eq!(
            counterstrikesharp_check(&tree.game_csgo(), Some("1.0.370.0")).status,
            DiagnosticStatus::Error
        );
        assert_eq!(
            counterstrikesharp_check(&tree.game_csgo(), Some("1.0.371.0")).status,
            DiagnosticStatus::Pass
        );
    }

    #[test]
    fn receipt_components_are_derived_from_paths_not_labels() {
        assert_eq!(
            receipt_component("addons/botcontroller/bin/win64/botcontroller.dll"),
            Some("bot_controller")
        );
        assert_eq!(
            receipt_component(
                "addons/counterstrikesharp/plugins/demotracerbothider/demotracerbothider.dll"
            ),
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

    #[test]
    fn fresh_runtime_heartbeat_proves_live_contracts_and_loaded_plugins() {
        let tree = TempTree::cs2();
        let health_path = join_public_relative(&tree.game_csgo(), RUNTIME_HEALTH_RELATIVE_PATH);
        fs::create_dir_all(health_path.parent().expect("health parent"))
            .expect("create health directory");
        let health = serde_json::json!({
            "schemaVersion": 1,
            "writtenAtMs": now_ms(),
            "running": true,
            "pluginVersion": "0.8.0",
            "demoTracerApi": 7,
            "counterStrikeSharpVersion": "1.0.371.0",
            "botController": {
                "abiMajor": 18,
                "abiMinor": 35,
                "capabilities": "0x1ffff",
                "buildId": "fixture",
                "compatible": true,
                "requiredCapabilities": {
                    "mask": "0x141ff",
                    "present": true,
                    "missing": "0x0"
                }
            },
            "botHider": {
                "providerApi": 1,
                "connected": true,
                "draining": false,
                "available": true
            },
            "botRandomizer": {
                "providerApi": 2,
                "ready": true,
                "draining": false,
                "replayPlanPrebuildAvailable": true,
                "available": true
            },
            "cosmetics": {
                "alignmentEnabled": false,
                "weaponsEnabled": false,
                "knivesEnabled": false,
                "glovesEnabled": false,
                "namesEnabled": false,
                "agentsEnabled": false,
                "stickersEnabled": false,
                "charmsEnabled": false,
                "preserveNativeEnabled": false
            },
            "loadedCssPluginDirectories": ["DemoTracer", "BotAI"]
        });
        fs::write(
            &health_path,
            serde_json::to_vec_pretty(&health).expect("serialize heartbeat"),
        )
        .expect("write heartbeat");

        let audit = inspect_runtime_health(&tree.game_csgo());
        assert_eq!(audit.verification, "verified");
        assert!(audit
            .loaded_plugin_directories
            .as_ref()
            .is_some_and(|plugins| plugins.contains("botai")));
        assert!(audit.checks.iter().any(|check| {
            check.id == "runtime.botController" && check.status == DiagnosticStatus::Pass
        }));
        assert!(audit.checks.iter().any(|check| {
            check.id == "runtime.botHider" && check.status == DiagnosticStatus::Pass
        }));
        assert!(audit.checks.iter().any(|check| {
            check.id == "runtime.botRandomizer" && check.status == DiagnosticStatus::Pass
        }));
    }

    #[test]
    fn stale_runtime_heartbeat_never_claims_plugins_are_loaded() {
        let tree = TempTree::cs2();
        let health_path = join_public_relative(&tree.game_csgo(), RUNTIME_HEALTH_RELATIVE_PATH);
        fs::create_dir_all(health_path.parent().expect("health parent"))
            .expect("create health directory");
        let stale = serde_json::json!({
            "schemaVersion": 1,
            "writtenAtMs": now_ms().saturating_sub(MAX_RUNTIME_HEALTH_AGE_MS + 1),
            "running": true,
            "pluginVersion": "0.8.0",
            "demoTracerApi": 7,
            "counterStrikeSharpVersion": "1.0.371.0",
            "botController": {
                "abiMajor": 18,
                "abiMinor": 35,
                "capabilities": "0x1ffff",
                "buildId": "fixture",
                "compatible": true,
                "requiredCapabilities": { "mask": "0x141ff", "present": true, "missing": "0x0" }
            },
            "botHider": { "providerApi": 1, "connected": true, "draining": false, "available": true },
            "botRandomizer": { "providerApi": 2, "ready": true, "draining": false, "replayPlanPrebuildAvailable": true, "available": true },
            "cosmetics": {
                "alignmentEnabled": false,
                "weaponsEnabled": false,
                "knivesEnabled": false,
                "glovesEnabled": false,
                "namesEnabled": false,
                "agentsEnabled": false,
                "stickersEnabled": false,
                "charmsEnabled": false,
                "preserveNativeEnabled": false
            },
            "loadedCssPluginDirectories": ["WeaponPaints"]
        });
        fs::write(
            &health_path,
            serde_json::to_vec(&stale).expect("serialize heartbeat"),
        )
        .expect("write heartbeat");

        let audit = inspect_runtime_health(&tree.game_csgo());
        assert_eq!(audit.verification, "notRunning");
        assert!(audit.loaded_plugin_directories.is_none());
    }
}
