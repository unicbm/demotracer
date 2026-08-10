/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::diagnostics::{
    checked_receipt_relative_path, embedded_playback_contract, fresh_runtime_plugin_version,
    normalized_receipt_path, receipt_component, resolve_install_paths, InstallReceiptWire,
    INSTALL_RECEIPT_RELATIVE_PATH, MAX_RECEIPT_FILES, MAX_RECEIPT_FILE_BYTES,
    REQUIRED_RECEIPT_PATHS,
};
use crate::{http_client, CommandErrorDto, CommandResult};
use base64::Engine;
use cs2_demotracer::demo_id::sha256_hex;
use minisign_verify::{PublicKey, Signature};
use semver::Version;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::io::{Cursor, Read};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Manager};

const MAX_PLAYBACK_PACKAGE_BYTES: usize = 256 * 1024 * 1024;
const MAX_RELEASE_MANIFEST_BYTES: usize = 128 * 1024;
const MAX_RELEASE_SIGNATURE_BYTES: usize = 16 * 1024;
const RELEASE_REQUEST_TIMEOUT_MS: i32 = 15_000;
const PLAYBACK_RELEASE_MANIFEST_URL: &str =
    "https://releases.detr.site/channels/stable/latest.json";
const PLAYBACK_RELEASE_BASE_URL: &str = "https://releases.detr.site";
const PLAYBACK_SIGNING_PUBLIC_KEY: &str =
    include_str!("../../../../tooling/release/updater-public-key.txt");
const MAX_EXTRACTED_PACKAGE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_ZIP_ENTRIES: usize = 512;
const INSTALL_STATE_SCHEMA: u32 = 1;
const INSTALL_STATE_DIRECTORY: &str = "playback-installs-v1";
const STAGING_DIRECTORY: &str = "playback-staging-v1";
const BACKUP_DIRECTORY: &str = "playback-backups-v1";
const LEGACY_PROVIDER_DIRECTORIES: &[&str] = &[
    "addons/counterstrikesharp/plugins/BotControllerImpl",
    "addons/counterstrikesharp/plugins/BotHiderImpl",
];

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaybackReleaseStatusDto {
    pub app_version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub loaded_plugin_version: Option<String>,
    pub can_rollback: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaybackUpdateStatusDto {
    pub latest_version: String,
    pub update_available: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notes: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaybackInstallResultDto {
    pub version: String,
    pub installed_files: usize,
    pub removed_legacy_files: usize,
    pub backup_path: String,
    pub game_csgo_path: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct InstallState {
    schema_version: u32,
    cs2_root: String,
    game_csgo_path: String,
    installed_version: String,
    installed_at_ms: u64,
    source: String,
    package_sha256: String,
    backup_path: String,
    entries: Vec<InstallStateEntry>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct InstallStateEntry {
    relative_path: String,
    had_original: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    installed_sha256: Option<String>,
}

#[derive(Debug)]
struct ValidatedPackage {
    payload_root: PathBuf,
    receipt: InstallReceiptWire,
}

#[derive(Clone, Debug, Deserialize)]
struct RemoteReleaseManifest {
    version: String,
    #[serde(default)]
    notes: Option<String>,
    #[serde(default)]
    playback: Option<RemotePlaybackAsset>,
}

#[derive(Clone, Debug, Deserialize)]
struct RemotePlaybackAsset {
    version: String,
    url: String,
    signature: String,
    sha256: String,
}

#[derive(Clone, Debug)]
struct ValidatedRemotePlaybackRelease {
    version: String,
    notes: Option<String>,
    url: String,
    signature: String,
    sha256: String,
}

#[tauri::command]
pub(crate) async fn playback_release_status(
    app: AppHandle,
    cs2_path: Option<String>,
) -> CommandResult<PlaybackReleaseStatusDto> {
    let local_data = app_local_data_dir(&app)?;
    let app_version = app
        .config()
        .version
        .clone()
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string());
    tauri::async_runtime::spawn_blocking(move || {
        local_playback_status(&local_data, cs2_path.as_deref(), app_version)
    })
    .await
    .map_err(|error| CommandErrorDto::new("playback_status_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn playback_update_status(
    cs2_path: Option<String>,
) -> CommandResult<PlaybackUpdateStatusDto> {
    tauri::async_runtime::spawn_blocking(move || {
        let current_version = local_playback_version(cs2_path.as_deref())?;
        let release = fetch_remote_playback_release()?;
        Ok(PlaybackUpdateStatusDto {
            update_available: playback_update_available(
                current_version.as_deref(),
                &release.version,
            ),
            latest_version: release.version,
            notes: release.notes,
        })
    })
    .await
    .map_err(|error| CommandErrorDto::new("playback_update_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn choose_playback_bundle(
    initial_path: Option<String>,
) -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new()
            .set_title("Choose a DemoTracer CSS bundle")
            .add_filter("DemoTracer CSS bundle", &["zip"]);
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
        dialog.pick_file().map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
pub(crate) async fn install_playback_bundle(
    app: AppHandle,
    cs2_path: String,
    package_path: String,
) -> CommandResult<PlaybackInstallResultDto> {
    let local_data = app_local_data_dir(&app)?;
    tauri::async_runtime::spawn_blocking(move || {
        ensure_cs2_is_stopped()?;
        let package_path = PathBuf::from(package_path.trim());
        let metadata = fs::metadata(&package_path).map_err(|error| {
            CommandErrorDto::at_path(
                "playback_package_unreadable",
                error.to_string(),
                &package_path,
            )
        })?;
        if !metadata.is_file()
            || metadata.len() == 0
            || metadata.len() > MAX_PLAYBACK_PACKAGE_BYTES as u64
        {
            return Err(CommandErrorDto::at_path(
                "playback_package_invalid",
                "Playback bundle must be a non-empty ZIP below 256 MiB.",
                &package_path,
            ));
        }
        let package = fs::read(&package_path).map_err(|error| {
            CommandErrorDto::at_path(
                "playback_package_unreadable",
                error.to_string(),
                &package_path,
            )
        })?;
        let expected_version = package_receipt_version(&package)?;
        install_package_bytes(
            &local_data,
            &cs2_path,
            &package,
            &expected_version,
            &format!("file:{}", package_path.display()),
        )
    })
    .await
    .map_err(|error| CommandErrorDto::new("playback_install_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn install_latest_playback_bundle(
    app: AppHandle,
    cs2_path: String,
) -> CommandResult<PlaybackInstallResultDto> {
    let local_data = app_local_data_dir(&app)?;
    tauri::async_runtime::spawn_blocking(move || {
        ensure_cs2_is_stopped()?;
        let release = fetch_remote_playback_release()?;
        let package = http_client::get_https(
            &release.url,
            MAX_PLAYBACK_PACKAGE_BYTES,
            RELEASE_REQUEST_TIMEOUT_MS,
        )
        .map_err(|error| CommandErrorDto::new("playback_update_download_failed", error))?;
        if !sha256_hex(&package).eq_ignore_ascii_case(&release.sha256) {
            return Err(CommandErrorDto::new(
                "playback_update_hash_mismatch",
                "Downloaded playback bundle does not match the signed release metadata.",
            ));
        }
        verify_playback_package_signature(&package, &release.signature)?;
        install_package_bytes(
            &local_data,
            &cs2_path,
            &package,
            &release.version,
            &format!("release:{}", release.url),
        )
    })
    .await
    .map_err(|error| CommandErrorDto::new("playback_update_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn rollback_playback_install(
    app: AppHandle,
    cs2_path: String,
) -> CommandResult<PlaybackInstallResultDto> {
    let local_data = app_local_data_dir(&app)?;
    tauri::async_runtime::spawn_blocking(move || {
        ensure_cs2_is_stopped()?;
        rollback_latest(&local_data, &cs2_path)
    })
    .await
    .map_err(|error| CommandErrorDto::new("playback_rollback_worker_failed", error.to_string()))?
}

fn app_local_data_dir(app: &AppHandle) -> CommandResult<PathBuf> {
    app.path().app_local_data_dir().map_err(|error| {
        CommandErrorDto::new(
            "local_data_unavailable",
            format!("Local app data is unavailable: {error}"),
        )
    })
}

fn local_playback_status(
    local_data: &Path,
    cs2_path: Option<&str>,
    app_version: String,
) -> CommandResult<PlaybackReleaseStatusDto> {
    let mut current_version = None;
    let mut loaded_plugin_version = None;
    let mut can_rollback = false;
    if let Some(cs2_path) = cs2_path.map(str::trim).filter(|path| !path.is_empty()) {
        let paths = resolve_install_paths(Path::new(cs2_path))?;
        current_version = read_installed_receipt(&paths.game_csgo)
            .ok()
            .flatten()
            .map(|receipt| receipt.bundle_version);
        loaded_plugin_version = fresh_runtime_plugin_version(&paths.game_csgo);
        can_rollback = install_state_path(local_data, &paths.game_csgo).is_file();
    }
    Ok(PlaybackReleaseStatusDto {
        app_version,
        current_version,
        loaded_plugin_version,
        can_rollback,
    })
}

fn local_playback_version(cs2_path: Option<&str>) -> CommandResult<Option<String>> {
    let Some(cs2_path) = cs2_path.map(str::trim).filter(|path| !path.is_empty()) else {
        return Ok(None);
    };
    let paths = resolve_install_paths(Path::new(cs2_path))?;
    Ok(read_installed_receipt(&paths.game_csgo)
        .ok()
        .flatten()
        .map(|receipt| receipt.bundle_version)
        .or_else(|| fresh_runtime_plugin_version(&paths.game_csgo)))
}

fn playback_update_available(current: Option<&str>, latest: &str) -> bool {
    let Ok(latest) = Version::parse(latest.trim().trim_start_matches('v')) else {
        return false;
    };
    current
        .and_then(|value| Version::parse(value.trim().trim_start_matches('v')).ok())
        .is_none_or(|current| current < latest)
}

fn fetch_remote_playback_release() -> CommandResult<ValidatedRemotePlaybackRelease> {
    let bytes = http_client::get_https(
        PLAYBACK_RELEASE_MANIFEST_URL,
        MAX_RELEASE_MANIFEST_BYTES,
        RELEASE_REQUEST_TIMEOUT_MS,
    )
    .map_err(|error| CommandErrorDto::new("playback_update_check_failed", error))?;
    let manifest: RemoteReleaseManifest = serde_json::from_slice(&bytes).map_err(|error| {
        CommandErrorDto::new("playback_update_manifest_invalid", error.to_string())
    })?;
    validate_remote_playback_release(manifest)
}

fn validate_remote_playback_release(
    manifest: RemoteReleaseManifest,
) -> CommandResult<ValidatedRemotePlaybackRelease> {
    let release_version = parse_version(&manifest.version)?;
    let playback = manifest.playback.ok_or_else(|| {
        CommandErrorDto::new(
            "playback_update_unavailable",
            "The stable channel does not publish a playback bundle yet.",
        )
    })?;
    let playback_version = parse_version(&playback.version)?;
    if release_version != playback_version {
        return Err(CommandErrorDto::new(
            "playback_update_manifest_invalid",
            "Playback release version does not match the desktop release channel.",
        ));
    }
    let version = playback_version.to_string();
    let expected_url =
        format!("{PLAYBACK_RELEASE_BASE_URL}/releases/v{version}/demotracer-css-v{version}.zip");
    if playback.url != expected_url {
        return Err(CommandErrorDto::new(
            "playback_update_manifest_invalid",
            "Playback release URL is outside the immutable DemoTracer release path.",
        ));
    }
    validate_sha256_text(&playback.sha256).map_err(|_| {
        CommandErrorDto::new(
            "playback_update_manifest_invalid",
            "Playback release SHA-256 is missing or invalid.",
        )
    })?;
    if decode_playback_signature(&playback.signature, "playback_update_manifest_invalid").is_err() {
        return Err(CommandErrorDto::new(
            "playback_update_manifest_invalid",
            "Playback release signature is missing or invalid.",
        ));
    }
    Ok(ValidatedRemotePlaybackRelease {
        version,
        notes: manifest.notes,
        url: playback.url,
        signature: playback.signature,
        sha256: playback.sha256.to_ascii_lowercase(),
    })
}

fn verify_playback_package_signature(bytes: &[u8], signature: &str) -> CommandResult<()> {
    let decoded_key = base64::engine::general_purpose::STANDARD
        .decode(PLAYBACK_SIGNING_PUBLIC_KEY.trim())
        .map_err(|error| CommandErrorDto::new("playback_signing_key_invalid", error.to_string()))?;
    let decoded_key = std::str::from_utf8(&decoded_key)
        .map_err(|error| CommandErrorDto::new("playback_signing_key_invalid", error.to_string()))?;
    let public_key = PublicKey::decode(decoded_key)
        .map_err(|error| CommandErrorDto::new("playback_signing_key_invalid", error.to_string()))?;
    let signature = decode_playback_signature(signature, "playback_update_signature_invalid")?;
    public_key
        .verify(bytes, &signature, false)
        .map_err(|error| {
            CommandErrorDto::new("playback_update_signature_invalid", error.to_string())
        })
}

fn decode_playback_signature(value: &str, error_code: &str) -> CommandResult<Signature> {
    let value = value.trim();
    if value.is_empty() || value.len() > MAX_RELEASE_SIGNATURE_BYTES {
        return Err(CommandErrorDto::new(
            error_code,
            "Playback release signature is missing or invalid.",
        ));
    }
    let decoded = base64::engine::general_purpose::STANDARD
        .decode(value)
        .map_err(|_| {
            CommandErrorDto::new(
                error_code,
                "Playback release signature is missing or invalid.",
            )
        })?;
    let decoded = std::str::from_utf8(&decoded).map_err(|_| {
        CommandErrorDto::new(
            error_code,
            "Playback release signature is missing or invalid.",
        )
    })?;
    Signature::decode(decoded).map_err(|_| {
        CommandErrorDto::new(
            error_code,
            "Playback release signature is missing or invalid.",
        )
    })
}

fn validate_sha256_text(value: &str) -> CommandResult<()> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(CommandErrorDto::new(
            "playback_receipt_invalid",
            "Playback receipt SHA-256 must contain exactly 64 hexadecimal characters.",
        ));
    }
    Ok(())
}

fn parse_version(value: &str) -> CommandResult<Version> {
    Version::parse(value.trim().trim_start_matches('v')).map_err(|error| {
        CommandErrorDto::new(
            "playback_version_invalid",
            format!("Playback release version is not valid SemVer: {error}"),
        )
    })
}

fn package_receipt_version(bytes: &[u8]) -> CommandResult<String> {
    let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).map_err(|error| {
        CommandErrorDto::new("playback_package_invalid", format!("Invalid ZIP: {error}"))
    })?;
    if archive.len() > MAX_ZIP_ENTRIES {
        return Err(CommandErrorDto::new(
            "playback_package_invalid",
            "Playback ZIP contains too many entries.",
        ));
    }
    let receipt_suffix = normalized_receipt_path(INSTALL_RECEIPT_RELATIVE_PATH);
    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| CommandErrorDto::new("playback_package_invalid", error.to_string()))?;
        let Some(path) = entry.enclosed_name() else {
            continue;
        };
        if normalized_receipt_path(&path.to_string_lossy()).ends_with(&receipt_suffix) {
            if entry.size() > MAX_RECEIPT_FILE_BYTES {
                break;
            }
            let mut bytes = Vec::with_capacity(entry.size() as usize);
            entry.read_to_end(&mut bytes).map_err(|error| {
                CommandErrorDto::new("playback_package_invalid", error.to_string())
            })?;
            let receipt: InstallReceiptWire = serde_json::from_slice(&bytes).map_err(|error| {
                CommandErrorDto::new("playback_receipt_invalid", error.to_string())
            })?;
            validate_receipt_contract(&receipt, &receipt.bundle_version)?;
            return Ok(receipt.bundle_version);
        }
    }
    Err(CommandErrorDto::new(
        "playback_receipt_missing",
        "Playback ZIP does not contain addons/demotracer-install.v1.json.",
    ))
}

fn install_package_bytes(
    local_data: &Path,
    cs2_path: &str,
    bytes: &[u8],
    expected_version: &str,
    source: &str,
) -> CommandResult<PlaybackInstallResultDto> {
    let paths = resolve_install_paths(Path::new(cs2_path.trim()))?;
    let nonce = format!("{}-{}", now_ms(), std::process::id());
    let staging_root = local_data.join(STAGING_DIRECTORY).join(&nonce);
    fs::create_dir_all(&staging_root).map_err(|error| {
        CommandErrorDto::at_path("playback_staging_failed", error.to_string(), &staging_root)
    })?;
    let result = (|| {
        let package = extract_and_validate_package(bytes, &staging_root, expected_version)?;
        apply_validated_package(local_data, &paths, package, bytes, source)
    })();
    let _ = fs::remove_dir_all(&staging_root);
    result
}

fn extract_and_validate_package(
    bytes: &[u8],
    staging_root: &Path,
    expected_version: &str,
) -> CommandResult<ValidatedPackage> {
    let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).map_err(|error| {
        CommandErrorDto::new("playback_package_invalid", format!("Invalid ZIP: {error}"))
    })?;
    if archive.len() == 0 || archive.len() > MAX_ZIP_ENTRIES {
        return Err(CommandErrorDto::new(
            "playback_package_invalid",
            "Playback ZIP has an invalid entry count.",
        ));
    }
    let mut extracted_bytes = 0_u64;
    let mut receipt_paths = Vec::new();
    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| CommandErrorDto::new("playback_package_invalid", error.to_string()))?;
        let enclosed = entry.enclosed_name().ok_or_else(|| {
            CommandErrorDto::new(
                "playback_package_unsafe",
                "Playback ZIP contains an unsafe path.",
            )
        })?;
        if enclosed.components().count() > 16 {
            return Err(CommandErrorDto::new(
                "playback_package_unsafe",
                "Playback ZIP path nesting is too deep.",
            ));
        }
        if entry
            .unix_mode()
            .is_some_and(|mode| mode & 0o170000 == 0o120000)
        {
            return Err(CommandErrorDto::new(
                "playback_package_unsafe",
                "Playback ZIP cannot contain symbolic links.",
            ));
        }
        let output = staging_root.join(&enclosed);
        if entry.is_dir() {
            fs::create_dir_all(&output).map_err(|error| {
                CommandErrorDto::at_path("playback_extract_failed", error.to_string(), &output)
            })?;
            continue;
        }
        if entry.size() > MAX_RECEIPT_FILE_BYTES {
            return Err(CommandErrorDto::new(
                "playback_package_too_large",
                format!("Playback ZIP entry is too large: {}", entry.name()),
            ));
        }
        extracted_bytes = extracted_bytes.saturating_add(entry.size());
        if extracted_bytes > MAX_EXTRACTED_PACKAGE_BYTES {
            return Err(CommandErrorDto::new(
                "playback_package_too_large",
                "Playback ZIP expands beyond 512 MiB.",
            ));
        }
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent).map_err(|error| {
                CommandErrorDto::at_path("playback_extract_failed", error.to_string(), parent)
            })?;
        }
        let mut file = fs::File::create(&output).map_err(|error| {
            CommandErrorDto::at_path("playback_extract_failed", error.to_string(), &output)
        })?;
        std::io::copy(&mut entry, &mut file).map_err(|error| {
            CommandErrorDto::at_path("playback_extract_failed", error.to_string(), &output)
        })?;
        let normalized = normalized_receipt_path(&enclosed.to_string_lossy());
        if normalized.ends_with(&normalized_receipt_path(INSTALL_RECEIPT_RELATIVE_PATH)) {
            receipt_paths.push(output);
        }
    }
    if receipt_paths.len() != 1 {
        return Err(CommandErrorDto::new(
            "playback_receipt_invalid",
            "Playback ZIP must contain exactly one install receipt.",
        ));
    }
    let receipt_path = receipt_paths.remove(0);
    let addons = receipt_path.parent().ok_or_else(|| {
        CommandErrorDto::new(
            "playback_receipt_invalid",
            "Install receipt has no addons parent.",
        )
    })?;
    if !addons
        .file_name()
        .is_some_and(|name| name.to_string_lossy().eq_ignore_ascii_case("addons"))
    {
        return Err(CommandErrorDto::new(
            "playback_receipt_invalid",
            "Install receipt is not directly under an addons directory.",
        ));
    }
    let payload_root = addons.parent().ok_or_else(|| {
        CommandErrorDto::new("playback_receipt_invalid", "Playback payload has no root.")
    })?;
    let receipt_bytes = fs::read(&receipt_path).map_err(|error| {
        CommandErrorDto::at_path("playback_receipt_invalid", error.to_string(), &receipt_path)
    })?;
    let receipt: InstallReceiptWire = serde_json::from_slice(&receipt_bytes)
        .map_err(|error| CommandErrorDto::new("playback_receipt_invalid", error.to_string()))?;
    validate_receipt_contract(&receipt, expected_version)?;
    validate_payload_files(payload_root, &receipt)?;
    Ok(ValidatedPackage {
        payload_root: payload_root.to_path_buf(),
        receipt,
    })
}

fn validate_receipt_contract(
    receipt: &InstallReceiptWire,
    expected_version: &str,
) -> CommandResult<()> {
    if receipt.schema_version != 1
        || receipt.product != "CS2 DemoTracer Playback Bundle"
        || receipt.platform != "windows-x64"
        || parse_version(&receipt.bundle_version)? != parse_version(expected_version)?
        || receipt.compatibility
            != embedded_playback_contract()
                .map_err(|error| CommandErrorDto::new("embedded_contract_invalid", error))?
    {
        return Err(CommandErrorDto::new(
            "playback_receipt_contract_mismatch",
            "Playback package receipt does not match this desktop build and requested version.",
        ));
    }
    if receipt.files.is_empty() || receipt.files.len() > MAX_RECEIPT_FILES {
        return Err(CommandErrorDto::new(
            "playback_receipt_invalid",
            "Playback package receipt has an invalid file count.",
        ));
    }
    Ok(())
}

fn validate_payload_files(payload_root: &Path, receipt: &InstallReceiptWire) -> CommandResult<()> {
    let mut recorded = BTreeSet::new();
    for file in &receipt.files {
        let relative = checked_receipt_relative_path(&file.path)
            .map_err(|error| CommandErrorDto::new("playback_receipt_invalid", error))?;
        let normalized = normalized_receipt_path(&file.path);
        if !recorded.insert(normalized.clone())
            || receipt_component(&normalized) != Some(file.component.as_str())
            || file.size > MAX_RECEIPT_FILE_BYTES
        {
            return Err(CommandErrorDto::new(
                "playback_receipt_invalid",
                format!("Playback receipt entry is invalid: {}", file.path),
            ));
        }
        validate_sha256_text(&file.sha256)?;
        let path = payload_root.join(relative);
        let metadata = fs::symlink_metadata(&path).map_err(|error| {
            CommandErrorDto::at_path("playback_payload_invalid", error.to_string(), &path)
        })?;
        if !metadata.is_file() || metadata.file_type().is_symlink() || metadata.len() != file.size {
            return Err(CommandErrorDto::at_path(
                "playback_payload_invalid",
                "Playback payload file type or size differs from its receipt.",
                &path,
            ));
        }
        let bytes = fs::read(&path).map_err(|error| {
            CommandErrorDto::at_path("playback_payload_invalid", error.to_string(), &path)
        })?;
        if !sha256_hex(&bytes).eq_ignore_ascii_case(&file.sha256) {
            return Err(CommandErrorDto::at_path(
                "playback_payload_invalid",
                "Playback payload SHA-256 differs from its receipt.",
                &path,
            ));
        }
    }
    for required in REQUIRED_RECEIPT_PATHS {
        if !recorded.contains(*required) {
            return Err(CommandErrorDto::new(
                "playback_receipt_invalid",
                format!("Playback receipt omits required file: {required}"),
            ));
        }
    }
    Ok(())
}

fn apply_validated_package(
    local_data: &Path,
    paths: &crate::diagnostics::InstallPaths,
    package: ValidatedPackage,
    package_bytes: &[u8],
    source: &str,
) -> CommandResult<PlaybackInstallResultDto> {
    let backup_root = local_data.join(BACKUP_DIRECTORY).join(format!(
        "{}-{}",
        now_ms(),
        safe_version_label(&package.receipt.bundle_version)
    ));
    fs::create_dir_all(&backup_root).map_err(|error| {
        CommandErrorDto::at_path("playback_backup_failed", error.to_string(), &backup_root)
    })?;

    let mut affected = BTreeMap::<String, Option<String>>::new();
    for file in &package.receipt.files {
        affected.insert(
            normalized_receipt_path(&file.path),
            Some(file.sha256.to_ascii_lowercase()),
        );
    }
    affected.insert(
        normalized_receipt_path(INSTALL_RECEIPT_RELATIVE_PATH),
        Some(sha256_hex(
            &fs::read(
                package
                    .payload_root
                    .join(checked_receipt_relative_path(INSTALL_RECEIPT_RELATIVE_PATH).unwrap()),
            )
            .map_err(|error| CommandErrorDto::new("playback_receipt_invalid", error.to_string()))?,
        )),
    );

    if let Ok(Some(previous)) = read_installed_receipt(&paths.game_csgo) {
        for file in previous.files {
            let Ok(relative) = checked_receipt_relative_path(&file.path) else {
                continue;
            };
            let normalized = normalized_receipt_path(&file.path);
            if affected.contains_key(&normalized) {
                continue;
            }
            let target = paths.game_csgo.join(relative);
            if target.is_file() && file_matches(&target, file.size, &file.sha256) {
                affected.insert(normalized, None);
            }
        }
    }

    let mut legacy_files = BTreeSet::new();
    for relative in LEGACY_PROVIDER_DIRECTORIES {
        let directory = paths.game_csgo.join(path_from_public(relative));
        if directory.is_dir() {
            ensure_no_reparse_below(&paths.game_csgo, &directory)?;
            for file in collect_normal_files(&directory, MAX_ZIP_ENTRIES)? {
                let relative = file.strip_prefix(&paths.game_csgo).map_err(|_| {
                    CommandErrorDto::new("playback_legacy_path_invalid", "Legacy path escaped CS2.")
                })?;
                let normalized = normalized_receipt_path(&relative.to_string_lossy());
                legacy_files.insert(normalized.clone());
                affected.entry(normalized).or_insert(None);
            }
        }
    }

    let mut state_entries = Vec::with_capacity(affected.len());
    for (relative, installed_sha256) in &affected {
        let target = paths.game_csgo.join(path_from_public(relative));
        ensure_no_reparse_below(&paths.game_csgo, &target)?;
        let had_original = target.is_file();
        if had_original {
            let metadata = fs::symlink_metadata(&target).map_err(|error| {
                CommandErrorDto::at_path("playback_backup_failed", error.to_string(), &target)
            })?;
            if !metadata.is_file() || metadata.file_type().is_symlink() {
                return Err(CommandErrorDto::at_path(
                    "playback_target_unsafe",
                    "Refusing to replace a non-normal target file.",
                    &target,
                ));
            }
            copy_with_parents(
                &target,
                &backup_root.join("files").join(path_from_public(relative)),
            )?;
        }
        state_entries.push(InstallStateEntry {
            relative_path: relative.clone(),
            had_original,
            installed_sha256: installed_sha256.clone(),
        });
    }

    let install_result = (|| {
        for file in &package.receipt.files {
            let relative = checked_receipt_relative_path(&file.path)
                .map_err(|error| CommandErrorDto::new("playback_receipt_invalid", error))?;
            replace_file(
                &package.payload_root.join(&relative),
                &paths.game_csgo.join(&relative),
            )?;
        }
        replace_file(
            &package
                .payload_root
                .join(path_from_public(INSTALL_RECEIPT_RELATIVE_PATH)),
            &paths
                .game_csgo
                .join(path_from_public(INSTALL_RECEIPT_RELATIVE_PATH)),
        )?;

        for (relative, installed_sha256) in &affected {
            if installed_sha256.is_none() {
                let path = paths.game_csgo.join(path_from_public(relative));
                if path.is_file() {
                    fs::remove_file(&path).map_err(|error| {
                        CommandErrorDto::at_path(
                            "playback_cleanup_failed",
                            error.to_string(),
                            &path,
                        )
                    })?;
                }
            }
        }
        for relative in LEGACY_PROVIDER_DIRECTORIES {
            remove_empty_tree(&paths.game_csgo.join(path_from_public(relative)));
        }

        let state = InstallState {
            schema_version: INSTALL_STATE_SCHEMA,
            cs2_root: paths.cs2_root.display().to_string(),
            game_csgo_path: paths.game_csgo.display().to_string(),
            installed_version: package.receipt.bundle_version.clone(),
            installed_at_ms: now_ms(),
            source: source.to_string(),
            package_sha256: sha256_hex(package_bytes),
            backup_path: backup_root.display().to_string(),
            entries: state_entries.clone(),
        };
        let state_bytes = serde_json::to_vec_pretty(&state)
            .map_err(|error| CommandErrorDto::new("playback_state_failed", error.to_string()))?;
        let state_path = install_state_path(local_data, &paths.game_csgo);
        write_atomic(&state_path, &state_bytes)?;
        write_atomic(&backup_root.join("install-state.v1.json"), &state_bytes)?;
        Ok(())
    })();

    if let Err(error) = install_result {
        let _ = restore_entries(&paths.game_csgo, &backup_root, &state_entries);
        return Err(error);
    }

    Ok(PlaybackInstallResultDto {
        version: package.receipt.bundle_version,
        installed_files: package.receipt.files.len() + 1,
        removed_legacy_files: legacy_files.len(),
        backup_path: backup_root.display().to_string(),
        game_csgo_path: paths.game_csgo.display().to_string(),
    })
}

fn rollback_latest(local_data: &Path, cs2_path: &str) -> CommandResult<PlaybackInstallResultDto> {
    let paths = resolve_install_paths(Path::new(cs2_path.trim()))?;
    let state_path = install_state_path(local_data, &paths.game_csgo);
    let state_bytes = fs::read(&state_path).map_err(|error| {
        CommandErrorDto::at_path(
            "playback_rollback_unavailable",
            error.to_string(),
            &state_path,
        )
    })?;
    let state: InstallState = serde_json::from_slice(&state_bytes)
        .map_err(|error| CommandErrorDto::new("playback_state_invalid", error.to_string()))?;
    if state.schema_version != INSTALL_STATE_SCHEMA
        || !same_path(&state.game_csgo_path, &paths.game_csgo)
        || state.entries.len() > MAX_ZIP_ENTRIES
    {
        return Err(CommandErrorDto::new(
            "playback_state_invalid",
            "Saved rollback state does not match this CS2 installation.",
        ));
    }
    let backup_root = PathBuf::from(&state.backup_path);
    let expected_backup_parent = local_data.join(BACKUP_DIRECTORY);
    let backup_canonical = backup_root.canonicalize().map_err(|error| {
        CommandErrorDto::at_path(
            "playback_rollback_unavailable",
            error.to_string(),
            &backup_root,
        )
    })?;
    let parent_canonical = expected_backup_parent.canonicalize().map_err(|error| {
        CommandErrorDto::at_path(
            "playback_rollback_unavailable",
            error.to_string(),
            &expected_backup_parent,
        )
    })?;
    if !backup_canonical.starts_with(&parent_canonical) {
        return Err(CommandErrorDto::new(
            "playback_state_invalid",
            "Saved rollback backup is outside DemoTracer local data.",
        ));
    }

    for entry in &state.entries {
        checked_receipt_relative_path(&entry.relative_path)
            .map_err(|error| CommandErrorDto::new("playback_state_invalid", error))?;
        let target = paths.game_csgo.join(path_from_public(&entry.relative_path));
        ensure_no_reparse_below(&paths.game_csgo, &target)?;
        match &entry.installed_sha256 {
            Some(expected) => {
                if !target.is_file()
                    || !sha256_hex(&fs::read(&target).map_err(|error| {
                        CommandErrorDto::at_path(
                            "playback_rollback_check_failed",
                            error.to_string(),
                            &target,
                        )
                    })?)
                    .eq_ignore_ascii_case(expected)
                {
                    return Err(CommandErrorDto::at_path(
                        "playback_modified_since_install",
                        "A managed file changed after installation; rollback stopped to avoid data loss.",
                        &target,
                    ));
                }
            }
            None if target.exists() => {
                return Err(CommandErrorDto::at_path(
                    "playback_modified_since_install",
                    "A removed legacy path was recreated; rollback stopped to avoid data loss.",
                    &target,
                ));
            }
            None => {}
        }
    }

    restore_entries(&paths.game_csgo, &backup_root, &state.entries)?;
    fs::remove_file(&state_path).map_err(|error| {
        CommandErrorDto::at_path(
            "playback_state_cleanup_failed",
            error.to_string(),
            &state_path,
        )
    })?;
    Ok(PlaybackInstallResultDto {
        version: state.installed_version,
        installed_files: state.entries.len(),
        removed_legacy_files: 0,
        backup_path: backup_root.display().to_string(),
        game_csgo_path: paths.game_csgo.display().to_string(),
    })
}

fn restore_entries(
    game_csgo: &Path,
    backup_root: &Path,
    entries: &[InstallStateEntry],
) -> CommandResult<()> {
    for entry in entries {
        let relative = checked_receipt_relative_path(&entry.relative_path)
            .map_err(|error| CommandErrorDto::new("playback_state_invalid", error))?;
        let target = game_csgo.join(&relative);
        ensure_no_reparse_below(game_csgo, &target)?;
        if target.is_file() {
            fs::remove_file(&target).map_err(|error| {
                CommandErrorDto::at_path("playback_rollback_failed", error.to_string(), &target)
            })?;
        }
        if entry.had_original {
            let source = backup_root.join("files").join(&relative);
            ensure_no_reparse_below(backup_root, &source)?;
            copy_with_parents(&source, &target)?;
        }
    }
    Ok(())
}

fn read_installed_receipt(game_csgo: &Path) -> Result<Option<InstallReceiptWire>, String> {
    let path = game_csgo.join(path_from_public(INSTALL_RECEIPT_RELATIVE_PATH));
    if !path.is_file() {
        return Ok(None);
    }
    let metadata = fs::metadata(&path).map_err(|error| error.to_string())?;
    if metadata.len() > MAX_RECEIPT_FILE_BYTES {
        return Err("installed receipt is too large".to_string());
    }
    let receipt = serde_json::from_slice::<InstallReceiptWire>(
        &fs::read(&path).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    Ok(Some(receipt))
}

fn install_state_path(local_data: &Path, game_csgo: &Path) -> PathBuf {
    let key = sha256_hex(normalized_receipt_path(&game_csgo.to_string_lossy()).as_bytes());
    local_data
        .join(INSTALL_STATE_DIRECTORY)
        .join(format!("{key}.json"))
}

fn write_atomic(path: &Path, bytes: &[u8]) -> CommandResult<()> {
    let parent = path.parent().ok_or_else(|| {
        CommandErrorDto::new("playback_state_failed", "State path has no parent.")
    })?;
    fs::create_dir_all(parent).map_err(|error| {
        CommandErrorDto::at_path("playback_state_failed", error.to_string(), parent)
    })?;
    let temporary = parent.join(format!(
        ".{}.tmp-{}",
        path.file_name().unwrap_or_default().to_string_lossy(),
        now_ms()
    ));
    fs::write(&temporary, bytes).map_err(|error| {
        CommandErrorDto::at_path("playback_state_failed", error.to_string(), &temporary)
    })?;
    if path.exists() {
        fs::remove_file(path).map_err(|error| {
            CommandErrorDto::at_path("playback_state_failed", error.to_string(), path)
        })?;
    }
    fs::rename(&temporary, path)
        .map_err(|error| CommandErrorDto::at_path("playback_state_failed", error.to_string(), path))
}

fn replace_file(source: &Path, destination: &Path) -> CommandResult<()> {
    let parent = destination.parent().ok_or_else(|| {
        CommandErrorDto::new("playback_install_failed", "Install path has no parent.")
    })?;
    fs::create_dir_all(parent).map_err(|error| {
        CommandErrorDto::at_path("playback_install_failed", error.to_string(), parent)
    })?;
    let temporary = parent.join(format!(".demotracer-new-{}", now_ms()));
    fs::copy(source, &temporary).map_err(|error| {
        CommandErrorDto::at_path("playback_install_failed", error.to_string(), &temporary)
    })?;
    if destination.exists() {
        fs::remove_file(destination).map_err(|error| {
            CommandErrorDto::at_path("playback_install_failed", error.to_string(), destination)
        })?;
    }
    fs::rename(&temporary, destination).map_err(|error| {
        CommandErrorDto::at_path("playback_install_failed", error.to_string(), destination)
    })
}

fn copy_with_parents(source: &Path, destination: &Path) -> CommandResult<()> {
    let parent = destination.parent().ok_or_else(|| {
        CommandErrorDto::new("playback_copy_failed", "Copy destination has no parent.")
    })?;
    fs::create_dir_all(parent).map_err(|error| {
        CommandErrorDto::at_path("playback_copy_failed", error.to_string(), parent)
    })?;
    fs::copy(source, destination).map(|_| ()).map_err(|error| {
        CommandErrorDto::at_path("playback_copy_failed", error.to_string(), destination)
    })
}

fn file_matches(path: &Path, size: u64, sha256: &str) -> bool {
    fs::metadata(path).ok().is_some_and(|metadata| {
        metadata.is_file()
            && metadata.len() == size
            && fs::read(path)
                .ok()
                .is_some_and(|bytes| sha256_hex(&bytes).eq_ignore_ascii_case(sha256.trim()))
    })
}

fn collect_normal_files(root: &Path, max_files: usize) -> CommandResult<Vec<PathBuf>> {
    let mut pending = vec![root.to_path_buf()];
    let mut files = Vec::new();
    while let Some(directory) = pending.pop() {
        for entry in fs::read_dir(&directory).map_err(|error| {
            CommandErrorDto::at_path("playback_legacy_scan_failed", error.to_string(), &directory)
        })? {
            let entry = entry.map_err(|error| {
                CommandErrorDto::at_path(
                    "playback_legacy_scan_failed",
                    error.to_string(),
                    &directory,
                )
            })?;
            let metadata = fs::symlink_metadata(entry.path()).map_err(|error| {
                CommandErrorDto::at_path(
                    "playback_legacy_scan_failed",
                    error.to_string(),
                    entry.path(),
                )
            })?;
            if metadata.file_type().is_symlink() {
                return Err(CommandErrorDto::at_path(
                    "playback_target_unsafe",
                    "Legacy provider directory contains a link or reparse point.",
                    entry.path(),
                ));
            }
            if metadata.is_dir() {
                pending.push(entry.path());
            } else if metadata.is_file() {
                files.push(entry.path());
                if files.len() > max_files {
                    return Err(CommandErrorDto::new(
                        "playback_legacy_scan_failed",
                        "Legacy provider directory contains too many files.",
                    ));
                }
            }
        }
    }
    Ok(files)
}

fn ensure_no_reparse_below(root: &Path, path: &Path) -> CommandResult<()> {
    let relative = path.strip_prefix(root).map_err(|_| {
        CommandErrorDto::at_path(
            "playback_target_unsafe",
            "Playback target is outside the selected CS2 directory.",
            path,
        )
    })?;
    let root_metadata = fs::symlink_metadata(root).map_err(|error| {
        CommandErrorDto::at_path("playback_target_unsafe", error.to_string(), root)
    })?;
    if !root_metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&root_metadata) {
        return Err(CommandErrorDto::at_path(
            "playback_target_unsafe",
            "Playback target root is not a normal directory.",
            root,
        ));
    }

    let mut current = root.to_path_buf();
    let mut components = relative.components().peekable();
    while let Some(component) = components.next() {
        let std::path::Component::Normal(segment) = component else {
            return Err(CommandErrorDto::at_path(
                "playback_target_unsafe",
                "Playback target contains an unsafe path component.",
                path,
            ));
        };
        current.push(segment);
        match fs::symlink_metadata(&current) {
            Ok(metadata) => {
                if crate::catalog::is_symlink_or_reparse(&metadata) {
                    return Err(CommandErrorDto::at_path(
                        "playback_target_unsafe",
                        "Playback target crosses a link or junction.",
                        &current,
                    ));
                }
                if components.peek().is_some() && !metadata.is_dir() {
                    return Err(CommandErrorDto::at_path(
                        "playback_target_unsafe",
                        "Playback target parent is not a directory.",
                        &current,
                    ));
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(error) => {
                return Err(CommandErrorDto::at_path(
                    "playback_target_unsafe",
                    error.to_string(),
                    &current,
                ));
            }
        }
    }
    Ok(())
}

fn remove_empty_tree(root: &Path) {
    if !root.is_dir() {
        return;
    }
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    let children = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect::<Vec<_>>();
    for child in children {
        if child.is_dir() {
            remove_empty_tree(&child);
        }
    }
    if fs::read_dir(root)
        .ok()
        .is_some_and(|mut entries| entries.next().is_none())
    {
        let _ = fs::remove_dir(root);
    }
}

fn path_from_public(value: &str) -> PathBuf {
    value.split(['/', '\\']).collect()
}

fn same_path(value: &str, path: &Path) -> bool {
    normalized_receipt_path(value) == normalized_receipt_path(&path.to_string_lossy())
}

fn safe_version_label(value: &str) -> String {
    value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '.' | '-' | '_') {
                character
            } else {
                '_'
            }
        })
        .take(64)
        .collect()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or_default()
}

#[cfg(windows)]
fn ensure_cs2_is_stopped() -> CommandResult<()> {
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };

    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot == INVALID_HANDLE_VALUE {
            return Err(CommandErrorDto::new(
                "process_check_failed",
                "Could not verify whether CS2 is running.",
            ));
        }
        let mut entry = PROCESSENTRY32W::default();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut found = Process32FirstW(snapshot, &mut entry) != 0;
        let mut running = false;
        while found {
            let length = entry
                .szExeFile
                .iter()
                .position(|value| *value == 0)
                .unwrap_or(entry.szExeFile.len());
            if String::from_utf16_lossy(&entry.szExeFile[..length]).eq_ignore_ascii_case("cs2.exe")
            {
                running = true;
                break;
            }
            found = Process32NextW(snapshot, &mut entry) != 0;
        }
        CloseHandle(snapshot);
        if running {
            return Err(CommandErrorDto::new(
                "cs2_running",
                "Close CS2 and any local CS2 server before installing or rolling back playback components.",
            ));
        }
    }
    Ok(())
}

#[cfg(not(windows))]
fn ensure_cs2_is_stopped() -> CommandResult<()> {
    Err(CommandErrorDto::new(
        "unsupported_platform",
        "Playback component management is supported only on Windows.",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_version_labels_cannot_escape_backup_directory() {
        assert_eq!(safe_version_label("../v1.0.0/beta"), ".._v1.0.0_beta");
    }

    #[test]
    fn playback_update_comparison_is_semver_aware() {
        assert!(playback_update_available(Some("1.0.9"), "1.0.10"));
        assert!(!playback_update_available(Some("1.0.10"), "1.0.10"));
        assert!(!playback_update_available(Some("1.1.0"), "1.0.10"));
        assert!(playback_update_available(None, "1.0.10"));
    }

    #[test]
    fn playback_signature_uses_the_tauri_base64_wrapper() {
        let wrapped = "dW50cnVzdGVkIGNvbW1lbnQ6IHNpZ25hdHVyZSBmcm9tIG1pbmlzaWduIHNlY3JldCBrZXkKUlVRZjZMUkNHQTlpNTU5cjNnN1YxcU55SkRBcEdpcDhNZnFjYWRJZ1Q5Q3VoVjNFTWhIb04xbUdUa1VpZEYvejdTcmxRZ1hkeThvZmpiN2JOSkp5bERPb2NyQ284S0x6WndvPQp0cnVzdGVkIGNvbW1lbnQ6IHRpbWVzdGFtcDoxNTU2MTkzMzM1YHRmaWxlOnRlc3QKeS9yVXcyeTgvaE9VWWpaVTcxZUhwL1dvMUtaNDBmR3kyVkpFRGwzNFhNSk0rVFg0OFNzLzE3dTNJdklmYlZSMUZrWlpTTkNpc1FidVFZK2JId2hFQmc9PQ==";

        assert!(decode_playback_signature(wrapped, "invalid").is_ok());
        assert!(decode_playback_signature("untrusted comment", "invalid").is_err());
    }

    #[test]
    fn playback_manifest_pins_the_signed_asset_to_the_release_origin() {
        let manifest = RemoteReleaseManifest {
            version: "1.0.11".to_string(),
            notes: Some("fix".to_string()),
            playback: Some(RemotePlaybackAsset {
                version: "1.0.11".to_string(),
                url: "https://evil.example/demotracer-css-v1.0.11.zip".to_string(),
                signature: "not a signature".to_string(),
                sha256: "0".repeat(64),
            }),
        };

        let error = validate_remote_playback_release(manifest).unwrap_err();
        assert_eq!(error.code, "playback_update_manifest_invalid");
        assert!(error.message.contains("immutable DemoTracer release path"));
    }

    #[test]
    fn legacy_stable_manifest_without_playback_is_not_a_security_failure() {
        let manifest: RemoteReleaseManifest =
            serde_json::from_str(r#"{"version":"1.0.10","notes":"desktop only","platforms":{}}"#)
                .unwrap();

        let error = validate_remote_playback_release(manifest).unwrap_err();
        assert_eq!(error.code, "playback_update_unavailable");
    }
}
