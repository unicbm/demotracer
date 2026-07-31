/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::rec_writer::read_rec_file;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

pub fn validate_dtr_path(input: &Path) -> crate::Result<usize> {
    validate_public_artifacts(input)?;
    let mut count = 0_usize;
    for path in collect_dtr_files(input)? {
        let rec = read_rec_file(&path)?;
        if rec.ticks.is_empty() {
            return Err(crate::Error::InvalidRec(format!(
                "{} has no ticks",
                path.display()
            )));
        }
        count += 1;
    }
    if count == 0 {
        return Err(crate::Error::InvalidDemo(format!(
            "no .dtr files found under {}",
            input.display()
        )));
    }
    Ok(count)
}

fn collect_dtr_files(root: &Path) -> crate::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    collect_recursively(root, &mut out)?;
    Ok(out)
}

fn validate_public_artifacts(input: &Path) -> crate::Result<()> {
    let pack_root = if input.is_file() {
        input.parent().unwrap_or_else(|| Path::new("."))
    } else {
        input
    };
    for path in collect_files(input)? {
        if let Some(reason) = forbidden_public_artifact_reason(&path) {
            return Err(crate::Error::InvalidDemo(format!(
                "{reason} must not be included in output packs: {}",
                path.display()
            )));
        }

        if is_manifest_json(&path) {
            let text = read_manifest_text(&path)?;
            let json = parse_manifest_json(&path, &text)?;
            validate_manifest_demo_paths(&path, &json)?;
            validate_manifest_artifact_paths(pack_root, &path, &json)?;
        }
    }
    Ok(())
}

fn forbidden_public_artifact_reason(path: &Path) -> Option<&'static str> {
    if crate::demo_reader::is_supported_demo_path(path) {
        return Some("raw demo file");
    }
    let ext = path.extension()?.to_str()?.to_ascii_lowercase();
    match ext.as_str() {
        "cs2rec" => Some("raw replay dump"),
        "csv" | "parquet" => Some("debug trace/data dump"),
        _ => None,
    }
}

fn collect_files(input: &Path) -> crate::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    collect_files_recursively(input, &mut out)?;
    Ok(out)
}

fn collect_files_recursively(path: &Path, out: &mut Vec<PathBuf>) -> crate::Result<()> {
    if path.is_file() {
        out.push(path.to_path_buf());
        return Ok(());
    }
    let entries = fs::read_dir(path).map_err(|e| crate::io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| crate::io_error(path, e))?;
        collect_files_recursively(&entry.path(), out)?;
    }
    Ok(())
}

fn is_manifest_json(path: &Path) -> bool {
    let Some(name) = path.file_name().and_then(|value| value.to_str()) else {
        return false;
    };
    name.ends_with(".json") || name.ends_with(".json.br")
}

fn read_manifest_text(path: &Path) -> crate::Result<String> {
    if !path
        .file_name()
        .and_then(|value| value.to_str())
        .is_some_and(|name| name.ends_with(".json.br"))
    {
        return fs::read_to_string(path).map_err(|e| crate::io_error(path, e));
    }

    let file = fs::File::open(path).map_err(|e| crate::io_error(path, e))?;
    let mut decompressor = brotli::Decompressor::new(file, 4096);
    let mut text = String::new();
    decompressor.read_to_string(&mut text).map_err(|e| {
        crate::Error::InvalidDemo(format!(
            "{} could not be decompressed as Brotli JSON manifest: {e}",
            path.display()
        ))
    })?;
    Ok(text)
}

fn parse_manifest_json(path: &Path, text: &str) -> crate::Result<serde_json::Value> {
    serde_json::from_str(text).map_err(|e| {
        crate::Error::InvalidDemo(format!("{} contains invalid JSON: {e}", path.display()))
    })
}

fn validate_manifest_demo_paths(path: &Path, value: &serde_json::Value) -> crate::Result<()> {
    match value {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if key == "demo_path" {
                    if let Some(text) = value.as_str() {
                        if is_local_demo_path(text) {
                            return Err(crate::Error::InvalidDemo(format!(
                                "{} contains local demo_path {:?}",
                                path.display(),
                                text
                            )));
                        }
                    }
                }
                validate_manifest_demo_paths(path, value)?;
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                validate_manifest_demo_paths(path, item)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_manifest_artifact_paths(
    pack_root: &Path,
    manifest_path: &Path,
    value: &serde_json::Value,
) -> crate::Result<()> {
    match value {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if key == "avatar_overrides" {
                    validate_manifest_avatar_override_paths(pack_root, manifest_path, value)?;
                    continue;
                }
                if let Some(kind) = manifest_artifact_kind(key) {
                    if let Some(text) = value.as_str() {
                        validate_manifest_artifact_path(pack_root, manifest_path, key, kind, text)?;
                    }
                }
                validate_manifest_artifact_paths(pack_root, manifest_path, value)?;
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                validate_manifest_artifact_paths(pack_root, manifest_path, item)?;
            }
        }
        _ => {}
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ManifestArtifactKind {
    Dtr,
    AvatarImage,
}

fn manifest_artifact_kind(key: &str) -> Option<ManifestArtifactKind> {
    match key {
        "path" => Some(ManifestArtifactKind::Dtr),
        _ => None,
    }
}

fn validate_manifest_artifact_path(
    pack_root: &Path,
    manifest_path: &Path,
    key: &str,
    kind: ManifestArtifactKind,
    value: &str,
) -> crate::Result<()> {
    if value.trim().is_empty() {
        return Err(crate::Error::InvalidDemo(format!(
            "{} contains empty {key}",
            manifest_path.display()
        )));
    }
    if is_absolute_manifest_artifact_path(value) {
        return Err(crate::Error::InvalidDemo(format!(
            "{} contains absolute {key} {:?}",
            manifest_path.display(),
            value
        )));
    }

    let manifest_dir = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let full =
        normalize_path(&manifest_dir.join(value.replace('\\', std::path::MAIN_SEPARATOR_STR)));
    let root = normalize_path(pack_root);
    if !path_is_under_root(&full, &root) {
        return Err(crate::Error::InvalidDemo(format!(
            "{} contains {key} outside output pack {:?}",
            manifest_path.display(),
            value
        )));
    }
    validate_manifest_artifact_extension(manifest_path, key, kind, value, &full)?;
    if !full.exists() {
        return Err(crate::Error::InvalidDemo(format!(
            "{} contains missing {key} target {:?}",
            manifest_path.display(),
            value
        )));
    }
    Ok(())
}

fn validate_manifest_artifact_extension(
    manifest_path: &Path,
    key: &str,
    kind: ManifestArtifactKind,
    value: &str,
    full: &Path,
) -> crate::Result<()> {
    match kind {
        ManifestArtifactKind::Dtr => {
            if !has_dtr_extension(full) {
                return Err(crate::Error::InvalidDemo(format!(
                    "{} contains {key} target with unsupported extension {:?}: expected .dtr",
                    manifest_path.display(),
                    value
                )));
            }
        }
        ManifestArtifactKind::AvatarImage => {
            if !has_avatar_image_extension(full) {
                return Err(crate::Error::InvalidDemo(format!(
                    "{} contains {key} target with unsupported extension {:?}: expected .png, .jpg, .jpeg, or .bin",
                    manifest_path.display(),
                    value
                )));
            }
        }
    }
    Ok(())
}

fn validate_manifest_avatar_override_paths(
    pack_root: &Path,
    manifest_path: &Path,
    value: &serde_json::Value,
) -> crate::Result<()> {
    match value {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if key == "path" {
                    if let Some(text) = value.as_str() {
                        validate_manifest_artifact_path(
                            pack_root,
                            manifest_path,
                            "avatar_overrides[].path",
                            ManifestArtifactKind::AvatarImage,
                            text,
                        )?;
                    }
                } else {
                    validate_manifest_artifact_paths(pack_root, manifest_path, value)?;
                }
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                validate_manifest_avatar_override_paths(pack_root, manifest_path, item)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn has_dtr_extension(path: &Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .is_some_and(|ext| ext.eq_ignore_ascii_case("dtr"))
}

fn has_avatar_image_extension(path: &Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .is_some_and(|ext| {
            matches!(
                ext.to_ascii_lowercase().as_str(),
                "png" | "jpg" | "jpeg" | "bin"
            )
        })
}

fn is_absolute_manifest_artifact_path(value: &str) -> bool {
    Path::new(value).is_absolute()
        || value.starts_with('/')
        || value.starts_with('\\')
        || value.contains(':')
}

fn normalize_path(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => match out.components().next_back() {
                Some(std::path::Component::Normal(_)) => {
                    out.pop();
                }
                Some(std::path::Component::ParentDir) | None => {
                    out.push("..");
                }
                Some(std::path::Component::Prefix(_))
                | Some(std::path::Component::RootDir)
                | Some(std::path::Component::CurDir) => {}
            },
            other => out.push(other.as_os_str()),
        }
    }
    out
}

fn path_is_under_root(path: &Path, root: &Path) -> bool {
    path == root || path.starts_with(root)
}

fn is_local_demo_path(value: &str) -> bool {
    value.contains('\\') || value.contains('/') || value.contains(':')
}

fn collect_recursively(path: &Path, out: &mut Vec<PathBuf>) -> crate::Result<()> {
    if path.is_file() {
        if path.extension().and_then(|e| e.to_str()) == Some("dtr") {
            out.push(path.to_path_buf());
        }
        return Ok(());
    }
    let entries = std::fs::read_dir(path).map_err(|e| crate::io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| crate::io_error(path, e))?;
        collect_recursively(&entry.path(), out)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn local_demo_path_detector_rejects_directories_and_drive_letters() {
        assert!(is_local_demo_path(r"C:\demos\match.dem"));
        assert!(is_local_demo_path("C:/demos/match.dem"));
        assert!(is_local_demo_path("/home/user/match.dem"));
        assert!(is_local_demo_path("demos/match.dem"));
        assert!(!is_local_demo_path("match.dem"));
    }

    #[test]
    fn public_artifact_hygiene_rejects_raw_and_debug_dumps() {
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("match.dem")),
            Some("raw demo file")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("FACEIT-MATCH.DEM.ZST")),
            Some("raw demo file")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("round.cs2rec")),
            Some("raw replay dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("utility.csv")),
            Some("debug trace/data dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("ticks.parquet")),
            Some("debug trace/data dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("conversion.log")),
            None
        );
    }

    #[test]
    fn public_artifact_hygiene_scans_output_pack_files() {
        let temp = tempfile::tempdir().unwrap();
        let trace = temp.path().join("debug_trace.csv");
        fs::write(&trace, b"slot,tick").unwrap();

        let err = validate_public_artifacts(temp.path()).unwrap_err();

        assert!(err.to_string().contains("debug trace/data dump"));
    }

    #[test]
    fn validate_rejects_inputs_without_dtr_files() {
        let temp = tempfile::tempdir().unwrap();

        let err = validate_dtr_path(temp.path()).unwrap_err();

        assert!(err.to_string().contains("no .dtr files"));
    }

    #[test]
    fn manifest_hygiene_reports_invalid_json_path() {
        let temp = tempfile::tempdir().unwrap();
        let manifest_path = temp.path().join("manifest.json");
        fs::write(&manifest_path, "{").unwrap();

        let err = validate_public_artifacts(temp.path()).unwrap_err();

        assert!(err.to_string().contains("manifest.json"));
        assert!(err.to_string().contains("contains invalid JSON"));
    }

    #[test]
    fn manifest_hygiene_reports_invalid_brotli_manifest_path() {
        let temp = tempfile::tempdir().unwrap();
        let manifest_path = temp.path().join("manifest.json.br");
        fs::write(&manifest_path, b"not brotli").unwrap();

        let err = validate_public_artifacts(temp.path()).unwrap_err();

        assert!(err.to_string().contains("manifest.json.br"));
        assert!(err.to_string().contains("could not be decompressed"));
    }

    #[test]
    fn manifest_hygiene_rejects_nested_local_demo_path() {
        let manifest = json!({
            "files": [],
            "metadata": [
                { "demo_path": r"C:\demos\match.dem" }
            ]
        });

        let err = validate_manifest_demo_paths(Path::new("manifest.json"), &manifest).unwrap_err();

        assert!(err.to_string().contains("contains local demo_path"));
    }

    #[test]
    fn manifest_hygiene_allows_sanitized_demo_path() {
        let manifest = json!({
            "demo_path": "match.dem",
            "files": []
        });

        validate_manifest_demo_paths(Path::new("manifest.json"), &manifest).unwrap();
    }

    #[test]
    fn manifest_hygiene_allows_avatar_override_images_inside_pack() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let manifest_path = pack.join("manifest.json");
        let replay_path = pack.join("round01/t/player.dtr");
        let avatar_path = pack.join("avatars/avatar.png");
        fs::create_dir_all(replay_path.parent().unwrap()).unwrap();
        fs::create_dir_all(avatar_path.parent().unwrap()).unwrap();
        fs::write(&replay_path, b"dtr").unwrap();
        fs::write(&avatar_path, b"png").unwrap();
        fs::write(
            &manifest_path,
            serde_json::to_string(&json!({
                "files": [
                    { "path": "round01/t/player.dtr" }
                ],
                "avatar_overrides": [
                    { "steam_id": 76561198000000001_u64, "path": "avatars/avatar.png" }
                ]
            }))
            .unwrap(),
        )
        .unwrap();

        validate_public_artifacts(pack).unwrap();
    }

    #[test]
    fn manifest_hygiene_allows_relative_parent_pack_inputs() {
        let cwd = std::env::current_dir().unwrap();
        let parent = cwd.parent().unwrap();
        let temp = tempfile::tempdir_in(parent).unwrap();
        let pack = temp.path().join("pack");
        let manifest_path = pack.join("manifest.json");
        let replay_path = pack.join("round01/t/player.dtr");
        let avatar_path = pack.join("avatars/avatar.png");
        fs::create_dir_all(replay_path.parent().unwrap()).unwrap();
        fs::create_dir_all(avatar_path.parent().unwrap()).unwrap();
        fs::write(&replay_path, b"dtr").unwrap();
        fs::write(&avatar_path, b"png").unwrap();
        fs::write(
            &manifest_path,
            serde_json::to_string(&json!({
                "files": [
                    { "path": "round01/t/player.dtr" }
                ],
                "avatar_overrides": [
                    { "steam_id": 76561198000000001_u64, "path": "avatars/avatar.png" }
                ]
            }))
            .unwrap(),
        )
        .unwrap();

        let relative_pack = PathBuf::from("..")
            .join(temp.path().file_name().unwrap())
            .join("pack");
        validate_public_artifacts(&relative_pack).unwrap();
    }

    #[test]
    fn manifest_hygiene_rejects_avatar_override_non_image_targets() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let avatar_path = pack.join("avatars/avatar.txt");
        fs::create_dir_all(avatar_path.parent().unwrap()).unwrap();
        fs::write(&avatar_path, b"not an avatar").unwrap();
        let manifest_path = pack.join("manifest.json");
        let manifest = json!({
            "avatar_overrides": [
                { "steam_id": 76561198000000001_u64, "path": "avatars/avatar.txt" }
            ]
        });

        let err = validate_manifest_artifact_paths(pack, &manifest_path, &manifest).unwrap_err();

        assert!(err.to_string().contains("expected .png"));
    }

    #[test]
    fn manifest_hygiene_rejects_missing_artifact_targets() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let manifest_path = pack.join("manifest.json");
        let manifest = json!({
            "files": [
                { "path": "round01/t/missing.dtr" }
            ]
        });

        let err = validate_manifest_artifact_paths(pack, &manifest_path, &manifest).unwrap_err();

        assert!(err.to_string().contains("missing path target"));
    }

    #[test]
    fn manifest_hygiene_rejects_path_artifacts_with_non_dtr_extensions() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let target = pack.join("round01/t/not-a-replay.txt");
        fs::create_dir_all(target.parent().unwrap()).unwrap();
        fs::write(&target, b"not a replay").unwrap();
        let manifest_path = pack.join("manifest.json");
        let manifest = json!({
            "files": [
                { "path": "round01/t/not-a-replay.txt" }
            ]
        });

        let err = validate_manifest_artifact_paths(pack, &manifest_path, &manifest).unwrap_err();

        assert!(err.to_string().contains("expected .dtr"));
    }

    #[test]
    fn manifest_hygiene_rejects_artifact_paths_outside_pack() {
        let manifest = json!({
            "files": [
                { "path": "../../../outside.dtr" }
            ]
        });

        let err = validate_manifest_artifact_paths(
            Path::new("pack"),
            Path::new("pack/replays/demo-a/manifest.json"),
            &manifest,
        )
        .unwrap_err();

        assert!(err.to_string().contains("outside output pack"));
    }

    #[test]
    fn manifest_hygiene_rejects_absolute_artifact_paths() {
        let manifest = json!({
            "files": [
                { "path": r"C:\demos\round01.dtr" }
            ]
        });

        let err = validate_manifest_artifact_paths(
            Path::new("pack"),
            Path::new("pack/manifest.json"),
            &manifest,
        )
        .unwrap_err();

        assert!(err.to_string().contains("absolute path"));
    }
}
