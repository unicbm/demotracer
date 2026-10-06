/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

pub(crate) const INSTALL_RECEIPT_RELATIVE_PATH: &str = "addons/demotracer-install.v1.json";
const MAX_TEXT_FILE_BYTES: u64 = 4 * 1024 * 1024;
pub(crate) const MAX_RECEIPT_FILES: usize = 256;
pub(crate) const MAX_RECEIPT_FILE_BYTES: u64 = 128 * 1024 * 1024;
pub(crate) const REQUIRED_RECEIPT_PATHS: &[&str] = &[
    "addons/dtr-controller/bin/win64/dtr-controller.dll",
    "addons/dtr-controller/gamedata.json",
    "addons/metamod/dtr-controller.vdf",
    "addons/dtr-hider/bin/win64/dtr-hider.dll",
    "addons/dtr-hider/gamedata.json",
    "addons/counterstrikesharp/plugins/demotracer/demotracer.dll",
    "addons/counterstrikesharp/shared/demotracerapi/demotracerapi.dll",
    "addons/counterstrikesharp/plugins/demotracer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/dtrhider/dtrhider.dll",
    "addons/counterstrikesharp/shared/dtrhiderapi/dtrhiderapi.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/botrandomizer.dll",
    "addons/counterstrikesharp/plugins/botrandomizer/cosmetic_catalog.json",
    "addons/counterstrikesharp/plugins/botrandomizer/cs2-lib-econ-index.v1.json",
    "addons/counterstrikesharp/plugins/botrandomizer/charm_placements.json",
    "addons/counterstrikesharp/shared/botrandomizerapi/botrandomizerapi.dll",
    "addons/counterstrikesharp/shared/0harmony/0harmony.dll",
];
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Cs2InstallCandidateDto {
    pub path: String,
    pub game_csgo_path: String,
    pub source: String,
    pub label: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct InstallReceiptWire {
    pub(crate) schema_version: u32,
    pub(crate) product: String,
    pub(crate) bundle_version: String,
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
    pub(crate) inventory_plan_schema: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub(crate) inventory_plan_reader: Option<DtrReaderContractWire>,
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
    #[serde(default)]
    pub(crate) native_library: String,
    pub(crate) abi_major: i32,
    pub(crate) min_abi_minor: i32,
    #[serde(default)]
    pub(crate) movement_intent_version: i32,
    #[serde(default)]
    pub(crate) replay_tick_bytes: u32,
    #[serde(default)]
    pub(crate) replay_tick_event_tail: String,
    pub(crate) required_capabilities_hex: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) struct BotHiderContractWire {
    #[serde(default)]
    pub(crate) native_library: String,
    #[serde(default)]
    pub(crate) managed_assembly: String,
    #[serde(default)]
    pub(crate) managed_api: String,
    #[serde(default)]
    pub(crate) capability: String,
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

#[derive(Debug)]
pub(crate) struct InstallPaths {
    pub(crate) cs2_root: PathBuf,
    pub(crate) game_csgo: PathBuf,
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

pub(crate) fn resolve_install_paths(input: &Path) -> CommandResult<InstallPaths> {
    if input.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "cs2_path_empty",
            "Choose or enter a local CS2 folder before installing playback components.",
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
    // Installation must enforce the complete package contract.
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
    if normalized_path.starts_with("addons/dtr-controller/")
        || normalized_path == "addons/metamod/dtr-controller.vdf"
        || normalized_path.starts_with("addons/counterstrikesharp/plugins/dtrcontroller/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/dtrcontrollerapi/")
    {
        Some("bot_controller")
    } else if normalized_path.starts_with("addons/dtr-hider/")
        || normalized_path == "addons/metamod/dtr-hider.vdf"
    {
        Some("bot_hider_native")
    } else if normalized_path.starts_with("addons/counterstrikesharp/plugins/dtrhider/")
        || normalized_path.starts_with("addons/counterstrikesharp/shared/dtrhiderapi/")
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

fn path_key(path: &Path) -> String {
    path.display()
        .to_string()
        .replace('\\', "/")
        .to_ascii_lowercase()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    struct TempTree(PathBuf);

    impl TempTree {
        fn cs2() -> Self {
            let nonce = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock")
                .as_nanos();
            let root = std::env::temp_dir().join(format!(
                "cs2-demotracer-install-{}-{nonce}",
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
        assert!(checked_receipt_relative_path("addons/dtr-controller/a.dll").is_ok());
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
            receipt_component("addons/dtr-controller/bin/win64/dtr-controller.dll"),
            Some("bot_controller")
        );
        assert_eq!(
            receipt_component("addons/counterstrikesharp/plugins/dtrhider/dtrhider.dll"),
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
}
