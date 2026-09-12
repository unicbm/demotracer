/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use super::*;
use crate::diagnostics::ReceiptFileWire;
use std::io::Write;

const CONTROLLER: &str =
    "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImpl.dll";
const OLD_HIDER: &str =
    "addons/counterstrikesharp/plugins/DemoTracerBotHider/DemoTracerBotHider.dll";
const USER_CONFIG: &str = "addons/counterstrikesharp/plugins/DemoTracerBotHider/settings.json";
const RECORDING: &str = "addons/counterstrikesharp/plugins/BotControllerImpl/recordings/user.json";

struct InstallFixture(PathBuf);

impl InstallFixture {
    fn new() -> Self {
        let fixture =
            Self(std::env::temp_dir().join(format!("dtr-install-{}", uuid::Uuid::new_v4())));
        fixture.write("gameinfo.gi", b"GameInfo");
        fixture
    }

    fn game(&self) -> PathBuf {
        self.0.join("cs2/game/csgo")
    }
    fn local(&self) -> PathBuf {
        self.0.join("local")
    }
    fn write(&self, path: &str, bytes: &[u8]) {
        let target = self.game().join(path);
        fs::create_dir_all(target.parent().unwrap()).unwrap();
        fs::write(target, bytes).unwrap();
    }
    fn install(&self, version: &str) -> CommandResult<PlaybackInstallResultDto> {
        install_package_bytes(
            &self.local(),
            self.game().to_str().unwrap(),
            &package_bytes(version),
            version,
            "test",
        )
    }
    fn rollback(&self) -> CommandResult<PlaybackInstallResultDto> {
        rollback_latest(&self.local(), self.game().to_str().unwrap())
    }
    fn state_path(&self) -> PathBuf {
        install_state_path(&self.local(), &self.game())
    }
    fn snapshot(&self) -> BTreeMap<String, String> {
        collect_normal_files(&self.game(), MAX_ZIP_ENTRIES)
            .unwrap()
            .into_iter()
            .map(|path| {
                (
                    normalized_receipt_path(
                        &path.strip_prefix(self.game()).unwrap().to_string_lossy(),
                    ),
                    sha256_hex(&fs::read(path).unwrap()),
                )
            })
            .collect()
    }
}

impl Drop for InstallFixture {
    fn drop(&mut self) {
        assert_eq!(self.0.parent(), Some(std::env::temp_dir().as_path()));
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn package_bytes(version: &str) -> Vec<u8> {
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default();
    let files = REQUIRED_RECEIPT_PATHS
        .iter()
        .map(|path| {
            let content = format!("{version}:{path}").into_bytes();
            zip.start_file(*path, options).unwrap();
            zip.write_all(&content).unwrap();
            ReceiptFileWire {
                path: path.to_string(),
                component: receipt_component(path).unwrap().to_string(),
                size: content.len() as u64,
                sha256: sha256_hex(&content),
            }
        })
        .collect();
    let receipt = InstallReceiptWire {
        schema_version: 1,
        product: "CS2 DemoTracer Playback Bundle".to_string(),
        bundle_version: version.to_string(),
        git_commit: None,
        platform: "windows-x64".to_string(),
        compatibility: embedded_playback_contract().unwrap(),
        files,
    };
    zip.start_file(INSTALL_RECEIPT_RELATIVE_PATH, options)
        .unwrap();
    zip.write_all(&serde_json::to_vec(&receipt).unwrap())
        .unwrap();
    zip.finish().unwrap().into_inner()
}

#[test]
fn provider_migration_and_rollback_preserve_user_data_and_original_code() {
    let fixture = InstallFixture::new();
    fixture.write(CONTROLLER, b"upstream controller");
    fixture.write(OLD_HIDER, b"old DTR hider");
    fixture.write(USER_CONFIG, b"user settings");
    fixture.write(RECORDING, b"user recording");
    fixture.write(
        "addons/counterstrikesharp/plugins/Unrelated/plugin.dll",
        b"unrelated",
    );
    let original = fixture.snapshot();
    fixture.install("1.2.2").unwrap();
    assert!(!fixture.game().join(OLD_HIDER).exists());
    assert_ne!(
        fs::read(fixture.game().join(CONTROLLER)).unwrap(),
        b"upstream controller"
    );
    assert_eq!(
        fs::read(fixture.game().join(USER_CONFIG)).unwrap(),
        b"user settings"
    );
    assert_eq!(
        fs::read(fixture.game().join(RECORDING)).unwrap(),
        b"user recording"
    );
    fixture.rollback().unwrap();
    assert_eq!(fixture.snapshot(), original);
    assert!(!fixture.state_path().exists());
}

#[test]
fn later_external_overwrite_blocks_rollback_and_repair_preserves_that_baseline() {
    let fixture = InstallFixture::new();
    fixture.install("1.2.2").unwrap();
    fixture.write(CONTROLLER, b"later upstream installation");
    let overwritten = fixture.snapshot();
    assert_eq!(
        fixture.rollback().unwrap_err().code,
        "playback_modified_since_install"
    );
    assert_eq!(fixture.snapshot(), overwritten);
    fixture.install("1.2.3").unwrap();
    fixture.rollback().unwrap();
    assert_eq!(fixture.snapshot(), overwritten);
}

#[test]
fn interrupted_apply_restores_previous_files_and_rollback_state() {
    let fixture = InstallFixture::new();
    fixture.install("1.2.2").unwrap();
    let original = fixture.snapshot();
    let state = fs::read(fixture.state_path()).unwrap();
    let payload = fixture.0.join("payload");
    let bytes = package_bytes("1.2.3");
    let package = extract_and_validate_package(&bytes, &payload, "1.2.3").unwrap();
    // Invalidate a later source only after validation: earlier entries have
    // already been replaced when this copy fails.
    fs::remove_file(package.payload_root.join(&package.receipt.files[2].path)).unwrap();
    let paths = resolve_install_paths(&fixture.game()).unwrap();
    assert!(apply_validated_package(&fixture.local(), &paths, package, &bytes, "test").is_err());
    assert_eq!(fixture.snapshot(), original);
    assert_eq!(fs::read(fixture.state_path()).unwrap(), state);
    fixture.rollback().unwrap();
}

#[test]
fn missing_backup_is_detected_before_rollback_changes_any_installed_file() {
    let fixture = InstallFixture::new();
    fixture.write(CONTROLLER, b"upstream controller");
    fixture.write(OLD_HIDER, b"old DTR hider");
    let installed = fixture.install("1.2.2").unwrap();
    let original = fixture.snapshot();
    fs::remove_file(
        Path::new(&installed.backup_path)
            .join("files")
            .join(CONTROLLER.to_ascii_lowercase()),
    )
    .unwrap();
    assert!(fixture.rollback().is_err());
    assert_eq!(fixture.snapshot(), original);
    assert!(fixture.state_path().is_file());
}

#[test]
fn failure_to_publish_install_state_rolls_back_payload_and_removes_temporary_files() {
    let fixture = InstallFixture::new();
    fixture.write(CONTROLLER, b"upstream controller");
    let original = fixture.snapshot();
    // A directory cannot be atomically replaced by the rollback-state file.
    fs::create_dir_all(fixture.state_path()).unwrap();
    assert!(fixture.install("1.2.2").is_err());
    assert_eq!(fixture.snapshot(), original);
    let state_parent = fixture.state_path().parent().unwrap().to_path_buf();
    assert_eq!(fs::read_dir(state_parent).unwrap().count(), 1);
}

#[test]
fn install_rejects_changed_event_semantics_even_when_tick_size_and_abi_match() {
    let fixture = InstallFixture::new();
    let bytes = package_bytes("1.2.2");
    let package =
        extract_and_validate_package(&bytes, &fixture.0.join("payload"), "1.2.2").unwrap();
    let mut receipt = package.receipt;
    receipt.compatibility.bot_controller.replay_tick_event_tail = "drop_events".to_string();
    assert_eq!(
        validate_receipt_contract(&receipt, "1.2.2")
            .unwrap_err()
            .code,
        "playback_receipt_contract_mismatch"
    );
    // Old receipts remain readable for migration, while missing new fields
    // cannot pass this build's compatibility check.
    let mut old = serde_json::to_value(&receipt).unwrap();
    let controller = old["compatibility"]["bot_controller"]
        .as_object_mut()
        .unwrap();
    for key in [
        "public_control_api",
        "replay_tick_bytes",
        "replay_tick_event_tail",
        "managed_provider_version",
    ] {
        controller.remove(key);
    }
    let old: InstallReceiptWire = serde_json::from_value(old).unwrap();
    assert!(validate_receipt_contract(&old, "1.2.2").is_err());
}
