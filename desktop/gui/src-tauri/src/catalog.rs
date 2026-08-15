/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use super::{
    archive_info::{read_demo_info, DemoArchiveInfo, DemoInfoRead},
    CommandErrorDto, CommandResult, MAX_MANIFEST_BYTES, MIN_SUPPORTED_DTR_FORMAT_VERSION,
    MIN_SUPPORTED_MANIFEST_ABI,
};
use cs2_demotracer::demo_id::sha256_hex;
use cs2_demotracer::demo_reader::is_supported_demo_path;
use cs2_demotracer::model::{DEMOTRACER_ABI, DTR_FORMAT_VERSION};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, Metadata};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;

const MAX_SCAN_DEPTH: usize = 8;
const MAX_LIBRARY_MANIFESTS: usize = 2048;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryScanDto {
    pub root: String,
    pub entries: Vec<LibraryEntryDto>,
    pub skipped: Vec<LibraryScanSkippedDto>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryScanSkippedDto {
    pub path: String,
    pub message: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryEntryDto {
    pub root: String,
    pub manifest_path: String,
    pub demo_path: String,
    pub demo_id: String,
    pub demo_sha256: String,
    pub display_name: Option<String>,
    pub note: Option<String>,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    pub compatibility: String,
    pub modified_at_ms: u64,
    pub rounds: usize,
    pub files: usize,
    pub players: Vec<LibraryPlayerDto>,
    pub avatar_overrides: Vec<LibraryAvatarOverrideDto>,
    /// Latest round scoreboard snapshot available in this archive. This is a
    /// lightweight library preview, not a freshly calculated match result.
    pub score: Option<LibraryScoreDto>,
    pub score_is_snapshot: bool,
    pub metadata_status: String,
    pub source_path: Option<String>,
    pub source_available: bool,
    pub source_modified_at_ms: Option<u64>,
    pub played_at: Option<String>,
    pub source_size_bytes: Option<u64>,
    pub duration_seconds: Option<f32>,
    pub demo_patch_version: Option<i32>,
    pub demo_version_name: Option<String>,
    pub server_name: Option<String>,
    pub demo_source: Option<cs2_demotracer::browser_analysis::BrowserDemoSource>,
    pub converter_version: Option<String>,
    pub series: Option<LibrarySeriesDto>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryAvatarOverrideDto {
    pub steam_id: String,
    pub path: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibrarySeriesDto {
    pub id: String,
    pub order: usize,
    pub map_count: usize,
    pub evidence: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryPlayerDto {
    pub steam_id: String,
    pub name: String,
    pub side: String,
    pub player_color: Option<String>,
    /// Legacy manifests only describe a per-round side, not a stable team.
    pub team: String,
    pub team_name: Option<String>,
    pub rounds: usize,
    pub files: usize,
    /// Latest per-player scoreboard snapshot available in files[].
    pub score: Option<i32>,
    pub kills: Option<u32>,
    pub deaths: Option<u32>,
    pub assists: Option<u32>,
    pub mvps: Option<u32>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryScoreDto {
    pub round: u32,
    pub team_a: LibraryTeamScoreDto,
    pub team_b: LibraryTeamScoreDto,
    pub status: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryTeamScoreDto {
    pub score: u32,
    pub name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct LibraryManifestWire {
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
    #[serde(default)]
    avatar_overrides: Vec<LibraryAvatarOverrideWire>,
    rounds: Option<Vec<LibraryRoundWire>>,
    files: Option<Vec<LibraryFileWire>>,
}

#[derive(Clone, Debug, Deserialize)]
struct LibraryAvatarOverrideWire {
    steam_id: Option<u64>,
    path: Option<String>,
    sha256: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
struct LibraryRoundWire {
    round: Option<u32>,
    scoreboard: Option<LibraryRoundScoreboardWire>,
}

#[derive(Clone, Debug, Deserialize)]
struct LibraryRoundScoreboardWire {
    t_score: Option<u32>,
    ct_score: Option<u32>,
    t_team_name: Option<String>,
    ct_team_name: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
struct LibraryFileWire {
    round: Option<u32>,
    side: Option<String>,
    steam_id: Option<u64>,
    player_name: Option<String>,
    scoreboard: Option<LibraryPlayerScoreboardWire>,
}

#[derive(Clone, Debug, Default, Deserialize)]
struct LibraryPlayerScoreboardWire {
    player_color: Option<String>,
    score: Option<i32>,
    kills: Option<u32>,
    deaths: Option<u32>,
    assists: Option<u32>,
    mvps: Option<u32>,
}

#[derive(Debug, Default)]
struct PlayerAccumulator {
    name: String,
    latest_round: Option<u32>,
    latest_side: String,
    rounds: BTreeSet<u32>,
    files: usize,
    scoreboard_round: Option<u32>,
    scoreboard: Option<LibraryPlayerScoreboardWire>,
}

pub(crate) fn scan_demo_library_for(root: &str) -> CommandResult<LibraryScanDto> {
    let trimmed = root.trim();
    if trimmed.is_empty() {
        return Err(CommandErrorDto::new(
            "library_root_invalid",
            "Choose a demo library folder before scanning.",
        ));
    }

    let root_path = PathBuf::from(trimmed);
    let root_metadata = fs::symlink_metadata(&root_path).map_err(|error| {
        let code = if error.kind() == std::io::ErrorKind::NotFound {
            "library_root_not_found"
        } else {
            "library_root_inspect_failed"
        };
        CommandErrorDto::at_path(code, error.to_string(), &root_path)
    })?;
    if is_symlink_or_reparse(&root_metadata) {
        return Err(CommandErrorDto::at_path(
            "library_root_reparse_point",
            "The demo library root cannot be a symbolic link or junction.",
            &root_path,
        ));
    }
    if !root_metadata.is_dir() {
        return Err(CommandErrorDto::at_path(
            "library_root_not_directory",
            "The selected demo library path is not a directory.",
            &root_path,
        ));
    }

    let mut manifest_paths = Vec::new();
    let mut skipped = Vec::new();
    collect_manifest_paths(&root_path, 0, &mut manifest_paths, &mut skipped)?;

    let mut entries = Vec::new();
    for manifest_path in manifest_paths {
        match summarize_manifest(&manifest_path) {
            Ok(entry) => entries.push(entry),
            Err(message) => skipped.push(LibraryScanSkippedDto {
                path: manifest_path.display().to_string(),
                message,
            }),
        }
    }
    entries.sort_by(|left, right| left.manifest_path.cmp(&right.manifest_path));
    assign_strict_hltv_series(&mut entries);

    Ok(LibraryScanDto {
        root: root_path.display().to_string(),
        entries,
        skipped,
    })
}

fn collect_manifest_paths(
    directory: &Path,
    depth: usize,
    manifests: &mut Vec<PathBuf>,
    skipped: &mut Vec<LibraryScanSkippedDto>,
) -> CommandResult<bool> {
    let read_dir = match fs::read_dir(directory) {
        Ok(read_dir) => read_dir,
        Err(error) if depth == 0 => {
            return Err(CommandErrorDto::at_path(
                "library_root_read_failed",
                error.to_string(),
                directory,
            ));
        }
        Err(error) => {
            skipped.push(LibraryScanSkippedDto {
                path: directory.display().to_string(),
                message: format!("Directory could not be scanned: {error}"),
            });
            return Ok(false);
        }
    };

    let mut paths = Vec::new();
    for entry in read_dir {
        match entry {
            Ok(entry) => paths.push(entry.path()),
            Err(error) => skipped.push(LibraryScanSkippedDto {
                path: directory.display().to_string(),
                message: format!("Directory entry could not be read: {error}"),
            }),
        }
    }
    paths.sort();

    let mut child_directories = Vec::new();
    for path in paths {
        let metadata = match fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) => {
                if is_manifest_name(&path) {
                    skipped.push(LibraryScanSkippedDto {
                        path: path.display().to_string(),
                        message: format!("Manifest metadata could not be read: {error}"),
                    });
                }
                continue;
            }
        };
        if is_symlink_or_reparse(&metadata) {
            continue;
        }
        if metadata.is_file() && is_manifest_name(&path) {
            if manifests.len() == MAX_LIBRARY_MANIFESTS {
                skipped.push(LibraryScanSkippedDto {
                    path: directory.display().to_string(),
                    message: format!("Scan stopped after {MAX_LIBRARY_MANIFESTS} manifest files."),
                });
                return Ok(true);
            }
            manifests.push(path);
        } else if metadata.is_dir()
            && depth < MAX_SCAN_DEPTH
            && !is_internal_transaction_directory(&path)
        {
            child_directories.push(path);
        }
    }

    for child in child_directories {
        if collect_manifest_paths(&child, depth + 1, manifests, skipped)? {
            return Ok(true);
        }
    }
    Ok(false)
}

fn is_manifest_name(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.eq_ignore_ascii_case("manifest.json"))
}

fn is_internal_transaction_directory(path: &Path) -> bool {
    let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
        return false;
    };
    name.starts_with('.') && (name.ends_with(".backup") || name.contains(".tmp."))
}

pub(crate) fn is_symlink_or_reparse(metadata: &Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }

    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0400;
        metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }

    #[cfg(not(windows))]
    {
        false
    }
}

pub(crate) fn summarize_manifest(path: &Path) -> Result<LibraryEntryDto, String> {
    let mut file =
        File::open(path).map_err(|error| format!("Manifest could not be opened: {error}"))?;
    let metadata = file
        .metadata()
        .map_err(|error| format!("Manifest metadata could not be read: {error}"))?;
    if !metadata.is_file() {
        return Err("Manifest path is not a regular file.".to_string());
    }
    if metadata.len() > MAX_MANIFEST_BYTES {
        return Err(format!(
            "Manifest is larger than the {} byte scan limit.",
            MAX_MANIFEST_BYTES
        ));
    }

    let mut text = String::new();
    file.by_ref()
        .take(MAX_MANIFEST_BYTES + 1)
        .read_to_string(&mut text)
        .map_err(|error| format!("Manifest is not readable UTF-8 JSON: {error}"))?;
    if text.len() as u64 > MAX_MANIFEST_BYTES {
        return Err(format!(
            "Manifest is larger than the {} byte scan limit.",
            MAX_MANIFEST_BYTES
        ));
    }
    let manifest: LibraryManifestWire = serde_json::from_str(&text)
        .map_err(|error| format!("Manifest JSON is invalid: {error}"))?;
    let manifest_sha256 = sha256_hex(text.as_bytes());

    let LibraryManifestWire {
        demo_path,
        demo_id,
        demo_sha256,
        map,
        tick_rate,
        abi,
        format_version,
        dtr_format_version,
        avatar_overrides,
        rounds,
        files,
    } = manifest;
    let files = match files {
        Some(files) if !files.is_empty() => files,
        Some(_) => return Err("Per-demo manifest contains no replay files.".to_string()),
        None => {
            return Err("JSON is not a per-demo replay manifest (files[] is missing).".to_string());
        }
    };
    let rounds = rounds.unwrap_or_default();
    let abi = abi.unwrap_or(0);
    let format_version = dtr_format_version
        .filter(|version| *version != 0)
        .or(format_version)
        .unwrap_or(0);
    let compatibility = manifest_compatibility(abi, format_version);
    let archive_root = path.parent().unwrap_or_else(|| Path::new("."));
    let demo_id = if demo_id.trim().is_empty() {
        archive_root
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default()
    } else {
        demo_id
    };

    let score = latest_score_snapshot(&rounds);
    let mut round_ids = rounds
        .iter()
        .filter_map(|round| round.round)
        .collect::<BTreeSet<_>>();
    for file in &files {
        if let Some(round) = file.round {
            round_ids.insert(round);
        }
    }

    let dedicated_source_path =
        super::archive_info::read_demo_source_path(archive_root, &demo_sha256);
    let source_path = dedicated_source_path.clone().or_else(|| {
        super::archive_info::read_matching_demo_info(archive_root, &demo_sha256)
            .and_then(|info| info.source_file_path.clone())
    });
    let source_available = source_path
        .as_deref()
        .is_some_and(source_demo_path_is_available);
    let mut entry = LibraryEntryDto {
        root: archive_root.display().to_string(),
        manifest_path: path.display().to_string(),
        demo_path,
        demo_id,
        demo_sha256,
        display_name: None,
        note: super::archive_info::read_archive_note(archive_root),
        map,
        tick_rate: tick_rate.unwrap_or(0.0),
        abi,
        format_version,
        compatibility,
        modified_at_ms: modified_at_ms(&metadata),
        rounds: round_ids.len(),
        files: files.len(),
        players: summarize_players(&files),
        avatar_overrides: summarize_avatar_overrides(avatar_overrides),
        score_is_snapshot: score.is_some(),
        score,
        metadata_status: "missing".to_string(),
        source_path,
        source_available,
        source_modified_at_ms: None,
        played_at: None,
        source_size_bytes: None,
        duration_seconds: None,
        demo_patch_version: None,
        demo_version_name: None,
        server_name: None,
        demo_source: None,
        converter_version: None,
        series: None,
    };
    match read_demo_info(archive_root, &entry.demo_sha256) {
        DemoInfoRead::Current(info)
            if info.map.eq_ignore_ascii_case(entry.map.trim())
                && info.manifest_abi == entry.abi
                && info.dtr_format_version == entry.format_version
                && info
                    .conversion
                    .as_ref()
                    .and_then(|conversion| conversion.manifest_sha256.as_deref())
                    .is_none_or(|expected| expected.eq_ignore_ascii_case(&manifest_sha256)) =>
        {
            apply_demo_info(
                &mut entry,
                *info,
                &manifest_sha256,
                dedicated_source_path.is_none(),
            )
        }
        DemoInfoRead::Current(_) => entry.metadata_status = "stale".to_string(),
        DemoInfoRead::Missing => {}
        DemoInfoRead::Stale => entry.metadata_status = "stale".to_string(),
        DemoInfoRead::Invalid => entry.metadata_status = "invalid".to_string(),
    }
    Ok(entry)
}

fn summarize_avatar_overrides(
    avatars: Vec<LibraryAvatarOverrideWire>,
) -> Vec<LibraryAvatarOverrideDto> {
    let mut seen = BTreeSet::new();
    avatars
        .into_iter()
        .filter_map(|avatar| {
            let steam_id = avatar.steam_id.unwrap_or(0);
            let path = avatar.path.unwrap_or_default().trim().replace('\\', "/");
            let sha256 = avatar
                .sha256
                .unwrap_or_default()
                .trim()
                .to_ascii_lowercase();
            let path_valid = !path.is_empty()
                && !path.starts_with('/')
                && !path.contains(':')
                && !Path::new(&path).components().any(|component| {
                    matches!(
                        component,
                        std::path::Component::ParentDir
                            | std::path::Component::RootDir
                            | std::path::Component::Prefix(_)
                    )
                });
            let digest_valid =
                sha256.len() == 64 && sha256.bytes().all(|byte| byte.is_ascii_hexdigit());
            if steam_id == 0 || !path_valid || !digest_valid || !seen.insert(steam_id) {
                return None;
            }
            Some(LibraryAvatarOverrideDto {
                steam_id: steam_id.to_string(),
                path,
                sha256,
            })
        })
        .collect()
}

fn apply_demo_info(
    entry: &mut LibraryEntryDto,
    info: DemoArchiveInfo,
    manifest_sha256: &str,
    allow_source_path_fallback: bool,
) {
    let converter_version = info
        .conversion
        .as_ref()
        .filter(|conversion| {
            conversion
                .manifest_sha256
                .as_deref()
                .is_some_and(|expected| expected.eq_ignore_ascii_case(manifest_sha256))
        })
        .map(|conversion| conversion.converter_version.clone());
    let archived_players = entry
        .players
        .iter()
        .map(|player| (player.steam_id.clone(), player.clone()))
        .collect::<BTreeMap<_, _>>();
    entry.players = info
        .players
        .into_iter()
        .map(|player| {
            let archived = archived_players.get(&player.steam_id);
            LibraryPlayerDto {
                steam_id: player.steam_id,
                name: player.name,
                side: player.side,
                player_color: player
                    .player_color
                    .or_else(|| archived.and_then(|value| value.player_color.clone())),
                team: player.team,
                team_name: player.team_name,
                rounds: player.rounds,
                files: archived.map_or(0, |value| value.files),
                score: player.score,
                kills: player.kills,
                deaths: player.deaths,
                assists: player.assists,
                mvps: player.mvps,
            }
        })
        .collect();
    entry.display_name = Some(info.display_name);
    entry.score = info.score.map(|score| LibraryScoreDto {
        round: 0,
        team_a: LibraryTeamScoreDto {
            score: score.team_a.score,
            name: score.team_a.name,
        },
        team_b: LibraryTeamScoreDto {
            score: score.team_b.score,
            name: score.team_b.name,
        },
        status: score.status,
    });
    entry.score_is_snapshot = false;
    entry.metadata_status = "current".to_string();
    if allow_source_path_fallback && info.source_file_path.is_some() {
        entry.source_path = info.source_file_path;
    }
    entry.source_available = entry
        .source_path
        .as_deref()
        .is_some_and(source_demo_path_is_available);
    entry.source_modified_at_ms = info.source_file_modified_at_ms;
    entry.played_at = info.played_at;
    entry.source_size_bytes = info.source_file_size_bytes;
    entry.duration_seconds = Some(info.duration_seconds);
    entry.demo_patch_version = info.demo_patch_version;
    entry.demo_version_name = info.demo_version_name;
    entry.server_name = info.server_name;
    entry.demo_source = info.demo_source;
    entry.converter_version = converter_version;
}

fn assign_strict_hltv_series(entries: &mut [LibraryEntryDto]) {
    let mut candidates = BTreeMap::<String, Vec<(usize, usize)>>::new();
    for (index, entry) in entries.iter().enumerate() {
        let Some(source_path) = entry.source_path.as_deref() else {
            continue;
        };
        let provider = entry
            .demo_source
            .as_ref()
            .map(|source| source.name.trim().to_ascii_lowercase());
        let bundle_path_evidence = strict_hltv_bundle_path(source_path);
        if provider
            .as_deref()
            .is_some_and(|name| name != "hltv" && !bundle_path_evidence)
        {
            continue;
        }
        let Some((file_key, order)) = strict_series_file_key(source_path, &entry.map) else {
            continue;
        };
        let Some(parent) = Path::new(source_path).parent() else {
            continue;
        };
        let key = format!(
            "{}\0{}",
            parent
                .to_string_lossy()
                .replace('\\', "/")
                .to_ascii_lowercase(),
            file_key
        );
        candidates.entry(key).or_default().push((index, order));
    }

    for (key, mut group) in candidates {
        if !(2..=5).contains(&group.len()) {
            continue;
        }
        group.sort_by_key(|(_, order)| *order);
        if group
            .iter()
            .enumerate()
            .any(|(position, (_, order))| *order != position + 1)
        {
            continue;
        }
        if !group.iter().any(|(index, _)| {
            let entry = &entries[*index];
            entry
                .demo_source
                .as_ref()
                .is_some_and(|source| source.name.trim().eq_ignore_ascii_case("hltv"))
                || entry
                    .source_path
                    .as_deref()
                    .is_some_and(strict_hltv_bundle_path)
        }) {
            continue;
        }
        let maps = group
            .iter()
            .map(|(index, _)| entries[*index].map.trim().to_ascii_lowercase())
            .collect::<BTreeSet<_>>();
        if maps.len() != group.len() {
            continue;
        }

        let hashes = group
            .iter()
            .map(|(index, _)| entries[*index].demo_sha256.trim().to_ascii_lowercase())
            .collect::<Vec<_>>()
            .join(",");
        let id = format!(
            "series-{}",
            &sha256_hex(format!("{key}\0{hashes}").as_bytes())[..20]
        );
        let map_count = group.len();
        for (index, order) in group {
            entries[index].series = Some(LibrarySeriesDto {
                id: id.clone(),
                order,
                map_count,
                evidence: "hltvFilenameSequence".to_string(),
            });
        }
    }
}

fn strict_hltv_bundle_path(source_path: &str) -> bool {
    let Some(parent_name) = Path::new(source_path)
        .parent()
        .and_then(Path::file_name)
        .and_then(|name| name.to_str())
    else {
        return false;
    };
    parent_name
        .to_ascii_lowercase()
        .split(|character: char| !character.is_ascii_alphanumeric())
        .any(|token| matches!(token, "bo1" | "bo3" | "bo5"))
}

fn strict_series_file_key(source_path: &str, map: &str) -> Option<(String, usize)> {
    let file_name = Path::new(source_path).file_name()?.to_str()?;
    let lower = file_name.to_ascii_lowercase();
    let stem = lower
        .strip_suffix(".dem.zst")
        .or_else(|| lower.strip_suffix(".dem"))?;
    let normalized = stem
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() {
                character
            } else {
                '-'
            }
        })
        .collect::<String>();
    let tokens = normalized
        .split('-')
        .filter(|token| !token.is_empty())
        .collect::<Vec<_>>();
    let (position, order) = tokens.iter().enumerate().find_map(|(position, token)| {
        let digits = token
            .strip_prefix("map")
            .or_else(|| token.strip_prefix('m'))?;
        let order = digits.parse::<usize>().ok()?;
        (1..=5).contains(&order).then_some((position, order))
    })?;
    let prefix = &tokens[..position];
    let suffix = &tokens[position + 1..];
    if prefix.len() < 3 || !prefix.iter().any(|token| *token == "vs") || suffix.is_empty() {
        return None;
    }
    let lower_map = map.trim().to_ascii_lowercase();
    let map_key = lower_map
        .strip_prefix("de_")
        .unwrap_or(&lower_map)
        .chars()
        .filter(|character| character.is_ascii_alphanumeric())
        .collect::<String>();
    let suffix_key = suffix
        .iter()
        .flat_map(|token| token.chars())
        .filter(|character| character.is_ascii_alphanumeric())
        .collect::<String>();
    if map_key.is_empty() || !suffix_key.contains(&map_key) {
        return None;
    }
    Some((prefix.join("-"), order))
}

fn source_demo_path_is_available(value: &str) -> bool {
    let path = Path::new(value);
    path.is_file() && is_supported_demo_path(path)
}

fn manifest_compatibility(abi: i32, format_version: u32) -> String {
    let abi_supported = abi == 0 || (MIN_SUPPORTED_MANIFEST_ABI..=DEMOTRACER_ABI).contains(&abi);
    let format_supported = format_version == 0
        || (MIN_SUPPORTED_DTR_FORMAT_VERSION..=DTR_FORMAT_VERSION).contains(&format_version);
    if !abi_supported || !format_supported {
        "unsupported"
    } else if abi == 0 || format_version == 0 {
        "legacy"
    } else if abi == DEMOTRACER_ABI && format_version == DTR_FORMAT_VERSION {
        "current"
    } else {
        "supported"
    }
    .to_string()
}

fn latest_score_snapshot(rounds: &[LibraryRoundWire]) -> Option<LibraryScoreDto> {
    rounds
        .iter()
        .filter_map(|round| {
            let round_number = round.round?;
            let scoreboard = round.scoreboard.as_ref()?;
            Some((
                round_number,
                LibraryScoreDto {
                    round: round_number,
                    team_a: LibraryTeamScoreDto {
                        score: scoreboard.t_score?,
                        name: clean_optional_string(scoreboard.t_team_name.as_deref()),
                    },
                    team_b: LibraryTeamScoreDto {
                        score: scoreboard.ct_score?,
                        name: clean_optional_string(scoreboard.ct_team_name.as_deref()),
                    },
                    status: "snapshot".to_string(),
                },
            ))
        })
        .max_by_key(|(round, _)| *round)
        .map(|(_, score)| score)
}

fn summarize_players(files: &[LibraryFileWire]) -> Vec<LibraryPlayerDto> {
    let mut players = BTreeMap::<u64, PlayerAccumulator>::new();
    for file in files {
        let Some(steam_id) = file.steam_id.filter(|steam_id| *steam_id != 0) else {
            continue;
        };
        let player = players.entry(steam_id).or_default();
        player.files += 1;

        if let Some(round) = file.round {
            player.rounds.insert(round);
            if player.latest_round.is_none_or(|latest| round > latest) {
                player.latest_round = Some(round);
                player.latest_side = normalize_side(file.side.as_deref());
                if let Some(name) = clean_optional_string(file.player_name.as_deref()) {
                    player.name = name;
                }
            } else if player.name.is_empty() {
                if let Some(name) = clean_optional_string(file.player_name.as_deref()) {
                    player.name = name;
                }
            }

            if file.scoreboard.is_some()
                && player.scoreboard_round.is_none_or(|latest| round > latest)
            {
                player.scoreboard_round = Some(round);
                player.scoreboard = file.scoreboard.clone();
            }
        } else if player.name.is_empty() {
            if let Some(name) = clean_optional_string(file.player_name.as_deref()) {
                player.name = name;
            }
        }
    }

    let mut summaries = players
        .into_iter()
        .map(|(steam_id, player)| {
            let scoreboard = player.scoreboard.unwrap_or_default();
            LibraryPlayerDto {
                steam_id: steam_id.to_string(),
                name: if player.name.is_empty() {
                    steam_id.to_string()
                } else {
                    player.name
                },
                side: player.latest_side,
                player_color: scoreboard.player_color,
                team: String::new(),
                team_name: None,
                rounds: player.rounds.len(),
                files: player.files,
                score: scoreboard.score,
                kills: scoreboard.kills,
                deaths: scoreboard.deaths,
                assists: scoreboard.assists,
                mvps: scoreboard.mvps,
            }
        })
        .collect::<Vec<_>>();
    summaries.sort_by(|left, right| {
        (side_rank(&left.side), left.steam_id.as_str())
            .cmp(&(side_rank(&right.side), right.steam_id.as_str()))
    });
    summaries
}

fn normalize_side(side: Option<&str>) -> String {
    match side
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "t" => "t".to_string(),
        "ct" => "ct".to_string(),
        _ => String::new(),
    }
}

fn side_rank(side: &str) -> u8 {
    match side {
        "t" => 0,
        "ct" => 1,
        _ => 2,
    }
}

fn clean_optional_string(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn modified_at_ms(metadata: &Metadata) -> u64 {
    metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT_TEST_DIRECTORY: AtomicU64 = AtomicU64::new(1);

    struct TestDirectory {
        path: PathBuf,
    }

    impl TestDirectory {
        fn new(name: &str) -> Self {
            let nonce = NEXT_TEST_DIRECTORY.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "cs2-demotracer-catalog-{name}-{}-{nonce}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self { path }
        }

        fn write_manifest(&self, relative_root: &str, value: &serde_json::Value) -> PathBuf {
            let root = self.path.join(relative_root);
            fs::create_dir_all(&root).unwrap();
            let path = root.join("manifest.json");
            fs::write(&path, serde_json::to_vec(value).unwrap()).unwrap();
            path
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.path);
        }
    }

    fn valid_manifest() -> serde_json::Value {
        json!({
            "demo_path": "match.dem",
            "demo_id": "match-a1b2c3d4",
            "demo_sha256": "a1b2c3d4",
            "map": "de_mirage",
            "tick_rate": 64.0,
            "abi": DEMOTRACER_ABI,
            "format_version": DTR_FORMAT_VERSION,
            "rounds": [
                {
                    "round": 1,
                    "scoreboard": {
                        "t_score": 0,
                        "ct_score": 0,
                        "t_team_name": "Alpha",
                        "ct_team_name": "Bravo"
                    }
                },
                {
                    "round": 12,
                    "scoreboard": {
                        "t_score": 7,
                        "ct_score": 4,
                        "t_team_name": "Bravo",
                        "ct_team_name": "Alpha"
                    }
                }
            ],
            "files": [
                {
                    "path": "round01/t/76561198000000001_alpha.dtr",
                    "round": 1,
                    "side": "t",
                    "steam_id": 76561198000000001_u64,
                    "player_name": "alpha",
                    "scoreboard": { "score": 2, "kills": 1, "deaths": 0, "assists": 1, "mvps": 0 }
                },
                {
                    "path": "round12/ct/76561198000000001_alpha.dtr",
                    "round": 12,
                    "side": "ct",
                    "steam_id": 76561198000000001_u64,
                    "player_name": "alpha",
                    "scoreboard": { "player_color": "blue", "score": 25, "kills": 18, "deaths": 7, "assists": 4, "mvps": 3 }
                },
                {
                    "path": "round12/t/76561198000000002_bravo.dtr",
                    "round": 12,
                    "side": "t",
                    "steam_id": 76561198000000002_u64,
                    "player_name": "bravo",
                    "scoreboard": { "score": 20, "kills": 14, "deaths": 9, "assists": 2, "mvps": 1 }
                }
            ]
        })
    }

    #[test]
    fn scans_lightweight_archive_summaries_and_uses_latest_snapshots() {
        let directory = TestDirectory::new("summary");
        directory.write_manifest("output/match", &valid_manifest());

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries.len(), 1);
        assert!(scan.skipped.is_empty());
        let entry = &scan.entries[0];
        assert_eq!(entry.demo_id, "match-a1b2c3d4");
        assert_eq!(entry.map, "de_mirage");
        assert_eq!(entry.compatibility, "current");
        assert_eq!(entry.rounds, 2);
        assert_eq!(entry.files, 3);
        let score = entry.score.as_ref().unwrap();
        assert_eq!(score.round, 12);
        assert_eq!(score.team_a.score, 7);
        assert_eq!(score.team_b.name.as_deref(), Some("Alpha"));
        assert_eq!(score.status, "snapshot");
        let alpha = entry
            .players
            .iter()
            .find(|player| player.name == "alpha")
            .unwrap();
        assert_eq!(alpha.side, "ct");
        assert_eq!(alpha.rounds, 2);
        assert_eq!(alpha.files, 2);
        assert_eq!(alpha.player_color.as_deref(), Some("blue"));
        assert_eq!(alpha.kills, Some(18));
        assert_eq!(alpha.deaths, Some(7));
        assert_eq!(alpha.assists, Some(4));
        assert!(entry.modified_at_ms > 0);
        assert!(!entry.root.contains(".dtr"));
    }

    #[test]
    fn scan_exposes_only_safe_avatar_override_evidence() {
        let directory = TestDirectory::new("avatar-evidence");
        let mut manifest = valid_manifest();
        manifest["avatar_overrides"] = json!([
            {
                "steam_id": 76561198000000001_u64,
                "path": "avatars/team.png",
                "sha256": "ab".repeat(32)
            },
            {
                "steam_id": 76561198000000002_u64,
                "path": "../outside.png",
                "sha256": "cd".repeat(32)
            }
        ]);
        directory.write_manifest("output/match", &manifest);

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries[0].avatar_overrides.len(), 1);
        assert_eq!(
            scan.entries[0].avatar_overrides[0].steam_id,
            "76561198000000001"
        );
        assert_eq!(scan.entries[0].avatar_overrides[0].path, "avatars/team.png");
    }

    #[test]
    fn scan_exposes_the_local_archive_note() {
        let directory = TestDirectory::new("archive-note");
        let manifest = directory.write_manifest("output/match", &valid_manifest());
        crate::archive_info::write_archive_note(manifest.parent().unwrap(), "Decider map").unwrap();

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries[0].note.as_deref(), Some("Decider map"));
    }

    #[test]
    fn current_demo_info_replaces_legacy_snapshot_with_stable_metadata() {
        let directory = TestDirectory::new("demo-info");
        let manifest_path = directory.write_manifest("output/de_mirage/match", &valid_manifest());
        let archive_root = manifest_path.parent().unwrap();
        let manifest_sha256 = sha256_hex(&fs::read(&manifest_path).unwrap());
        fs::write(
            archive_root.join(crate::archive_info::DEMO_INFO_FILE_NAME),
            serde_json::to_vec_pretty(&json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "match-a1b2c3d4",
                "demoSha256": "a1b2c3d4",
                "displayName": "Alpha vs Bravo",
                "sourceFileName": "match.dem",
                "sourceFilePath": "C:\\Demos\\match.dem",
                "sourceFileModifiedAtMs": 123456_u64,
                "sourceFileSizeBytes": 999_u64,
                "sourceFileDateIsApproximate": true,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 1800.5,
                "durationEvidence": "demoFileInfo",
                "demoSource": { "name": "Faceit", "evidence": "serverName" },
                "score": {
                    "teamA": { "score": 13, "name": "Alpha" },
                    "teamB": { "score": 9, "name": "Bravo" },
                    "status": "final"
                },
                "scoreEvidence": "roundEndEvents",
                "players": [{
                    "name": "alpha",
                    "steamId": "76561198000000001",
                    "side": "CT",
                    "team": "a",
                    "teamName": "Alpha",
                    "kills": 22,
                    "deaths": 14,
                    "assists": 6,
                    "rounds": 22,
                    "rows": 1000
                }],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "conversion": {
                    "converterVersion": "0.7.2",
                    "selectedRounds": [1, 12],
                    "side": "both",
                    "fullRound": false,
                    "includeSuspicious": false,
                    "freezePrerollSeconds": 20.0,
                    "voice": false,
                    "cosmetics": false,
                    "stickers": false,
                    "charms": false,
                    "manifestSha256": manifest_sha256
                },
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "conversion",
                    "generatedAtMs": 123456_u64
                }
            }))
            .unwrap(),
        )
        .unwrap();

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();
        let entry = &scan.entries[0];
        assert_eq!(entry.metadata_status, "current");
        assert_eq!(entry.source_path.as_deref(), Some(r"C:\Demos\match.dem"));
        assert!(!entry.score_is_snapshot);
        assert_eq!(entry.display_name.as_deref(), Some("Alpha vs Bravo"));
        assert_eq!(entry.score.as_ref().unwrap().team_a.score, 13);
        assert_eq!(entry.score.as_ref().unwrap().team_b.score, 9);
        assert_eq!(entry.players[0].team, "a");
        assert_eq!(entry.players[0].kills, Some(22));
        assert_eq!(entry.duration_seconds, Some(1800.5));
        assert_eq!(entry.demo_source.as_ref().unwrap().name, "Faceit");
    }

    #[test]
    fn changed_manifest_invalidates_bound_demo_info() {
        let directory = TestDirectory::new("changed-manifest");
        let manifest_path = directory.write_manifest("output/match", &valid_manifest());
        let archive_root = manifest_path.parent().unwrap();
        fs::write(
            archive_root.join(crate::archive_info::DEMO_INFO_FILE_NAME),
            serde_json::to_vec(&json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "match-a1b2c3d4",
                "demoSha256": "a1b2c3d4",
                "displayName": "Stale bound metadata",
                "sourceFileName": "match.dem",
                "sourceFileDateIsApproximate": false,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 1800.0,
                "durationEvidence": "demoFileInfo",
                "score": {
                    "teamA": { "score": 13, "name": "Alpha" },
                    "teamB": { "score": 9, "name": "Bravo" },
                    "status": "final"
                },
                "scoreEvidence": "roundEndEvents",
                "players": [],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "conversion": {
                    "converterVersion": "0.7.2",
                    "selectedRounds": [1, 12],
                    "side": "both",
                    "fullRound": false,
                    "includeSuspicious": false,
                    "freezePrerollSeconds": 20.0,
                    "voice": false,
                    "cosmetics": false,
                    "stickers": false,
                    "charms": false,
                    "manifestSha256": "00".repeat(32)
                },
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "conversion",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        )
        .unwrap();

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();
        let entry = &scan.entries[0];
        assert_eq!(entry.metadata_status, "stale");
        assert_eq!(entry.display_name, None);
        assert!(entry.score_is_snapshot);
        assert_eq!(entry.score.as_ref().unwrap().status, "snapshot");
    }

    #[test]
    fn mismatched_demo_info_is_stale_and_cannot_override_manifest() {
        let directory = TestDirectory::new("stale-demo-info");
        let manifest_path = directory.write_manifest("output/match", &valid_manifest());
        let archive_root = manifest_path.parent().unwrap();
        fs::write(
            archive_root.join(crate::archive_info::DEMO_INFO_FILE_NAME),
            serde_json::to_vec(&json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION,
                "demoId": "other",
                "demoSha256": "different",
                "displayName": "Other",
                "sourceFileName": "other.dem",
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
                    "reason": "conversion",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        )
        .unwrap();

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();
        assert_eq!(scan.entries[0].metadata_status, "stale");
        assert_eq!(scan.entries[0].score.as_ref().unwrap().status, "snapshot");
        assert!(scan.entries[0].source_path.is_none());
    }

    #[test]
    fn previous_analysis_revision_keeps_compatible_archive_metadata() {
        let directory = TestDirectory::new("old-analysis-revision");
        let manifest_path = directory.write_manifest("output/match", &valid_manifest());
        let archive_root = manifest_path.parent().unwrap();
        fs::write(
            archive_root.join(crate::archive_info::DEMO_INFO_FILE_NAME),
            serde_json::to_vec(&json!({
                "schemaVersion": crate::archive_info::DEMO_INFO_SCHEMA_VERSION,
                "analysisRevision": crate::archive_info::DEMO_INFO_ANALYSIS_REVISION - 1,
                "demoId": "match-a1b2c3d4",
                "demoSha256": "a1b2c3d4",
                "displayName": "Compatible old score",
                "sourceFileName": "match.dem",
                "sourceFilePath": "C:\\Demos\\match.dem",
                "sourceFileDateIsApproximate": false,
                "playedAt": null,
                "map": "de_mirage",
                "tickRate": 64.0,
                "durationSeconds": 1200.0,
                "durationEvidence": "observedTicks",
                "score": {
                    "teamA": { "score": 14, "name": "Alpha" },
                    "teamB": { "score": 9, "name": "Bravo" },
                    "status": "final"
                },
                "scoreEvidence": "roundEndEvents",
                "players": [],
                "manifestAbi": DEMOTRACER_ABI,
                "dtrFormatVersion": DTR_FORMAT_VERSION,
                "generatedBy": {
                    "app": "CS2 DemoTracer",
                    "version": "0.7.2",
                    "reason": "conversion",
                    "generatedAtMs": 1
                }
            }))
            .unwrap(),
        )
        .unwrap();

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();
        let entry = &scan.entries[0];
        assert_eq!(entry.metadata_status, "current");
        assert_eq!(entry.source_path.as_deref(), Some(r"C:\Demos\match.dem"));
        assert_eq!(entry.score.as_ref().unwrap().status, "final");
        assert_eq!(entry.display_name.as_deref(), Some("Compatible old score"));
    }

    #[test]
    fn skips_bad_and_unrelated_manifests_without_failing_the_scan() {
        let directory = TestDirectory::new("skips");
        directory.write_manifest("valid", &valid_manifest());
        let invalid_root = directory.path.join("invalid");
        fs::create_dir_all(&invalid_root).unwrap();
        fs::write(invalid_root.join("manifest.json"), b"{ definitely not json").unwrap();
        directory.write_manifest("unrelated", &json!({ "name": "not a replay manifest" }));

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries.len(), 1);
        assert_eq!(scan.skipped.len(), 2);
        assert!(scan
            .skipped
            .iter()
            .any(|item| item.message.contains("invalid")));
        assert!(scan
            .skipped
            .iter()
            .any(|item| item.message.contains("files[] is missing")));
    }

    #[test]
    fn enforces_the_directory_depth_limit() {
        let directory = TestDirectory::new("depth");
        directory.write_manifest("one/two/three/four/five/six/seven/eight", &valid_manifest());
        directory.write_manifest(
            "one/two/three/four/five/six/seven/eight/nine",
            &valid_manifest(),
        );

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries.len(), 1);
        assert!(scan.entries[0].manifest_path.contains("eight"));
    }

    #[test]
    fn skips_internal_output_transaction_directories() {
        let directory = TestDirectory::new("transactions");
        directory.write_manifest("match-a1b2c3d4", &valid_manifest());
        directory.write_manifest(".match-a1b2c3d4.backup", &valid_manifest());
        directory.write_manifest(".match-a1b2c3d4.tmp.123.nonce", &valid_manifest());

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert_eq!(scan.entries.len(), 1);
        let archive_name = Path::new(&scan.entries[0].manifest_path)
            .parent()
            .and_then(Path::file_name)
            .and_then(|name| name.to_str());
        assert_eq!(archive_name, Some("match-a1b2c3d4"));
    }

    #[test]
    fn reports_invalid_roots_as_structured_errors() {
        let directory = TestDirectory::new("invalid-root");
        let file = directory.path.join("not-a-directory.txt");
        fs::write(&file, b"not a directory").unwrap();

        let error = scan_demo_library_for(file.to_str().unwrap()).unwrap_err();

        assert_eq!(error.code, "library_root_not_directory");
        assert_eq!(error.path.as_deref(), file.to_str());
    }

    #[test]
    fn does_not_follow_directory_symlinks_or_junction_like_reparse_points() {
        let directory = TestDirectory::new("symlink");
        let external = TestDirectory::new("symlink-target");
        external.write_manifest("archive", &valid_manifest());
        let link = directory.path.join("linked");

        #[cfg(unix)]
        if std::os::unix::fs::symlink(&external.path, &link).is_err() {
            return;
        }
        #[cfg(windows)]
        if std::os::windows::fs::symlink_dir(&external.path, &link).is_err() {
            return;
        }

        let scan = scan_demo_library_for(directory.path.to_str().unwrap()).unwrap();

        assert!(scan.entries.is_empty());
    }

    #[test]
    fn hltv_series_file_key_requires_ordered_map_suffix() {
        assert_eq!(
            strict_series_file_key(r"C:\demos\og-vs-spirit-m1-cache.dem", "de_cache"),
            Some(("og-vs-spirit".to_string(), 1))
        );
        assert_eq!(
            strict_series_file_key(r"C:\demos\og-vs-spirit-m3-ancient.dem.zst", "de_ancient"),
            Some(("og-vs-spirit".to_string(), 3))
        );
        assert_eq!(
            strict_series_file_key(r"C:\demos\og-vs-spirit-m2-dust2.dem", "de_mirage"),
            None
        );
        assert_eq!(
            strict_series_file_key(r"C:\demos\random-hash.dem", "de_cache"),
            None
        );
    }

    #[test]
    fn hltv_bundle_path_uses_the_downloaded_series_directory() {
        assert!(strict_hltv_bundle_path(
            r"C:\demos\blast-bounty-og-vs-spirit-bo3-P0hojm\og-vs-spirit-m1-cache.dem"
        ));
        assert!(!strict_hltv_bundle_path(
            r"C:\demos\og-vs-spirit-m1-cache.dem"
        ));
    }

    fn strict_series_entry(
        source_path: &str,
        map: &str,
        demo_sha256: &str,
        modified_at_ms: u64,
    ) -> LibraryEntryDto {
        LibraryEntryDto {
            root: "archive".to_string(),
            manifest_path: format!("archive/{demo_sha256}/manifest.json"),
            demo_path: "demo.dem".to_string(),
            demo_id: demo_sha256.to_string(),
            demo_sha256: demo_sha256.to_string(),
            display_name: Some("OG vs Spirit".to_string()),
            note: None,
            map: map.to_string(),
            tick_rate: 64.0,
            abi: DEMOTRACER_ABI,
            format_version: DTR_FORMAT_VERSION,
            compatibility: "current".to_string(),
            modified_at_ms,
            rounds: 24,
            files: 10,
            players: (0..10)
                .map(|index| LibraryPlayerDto {
                    steam_id: (76_561_198_000_000_000_u64 + index).to_string(),
                    name: format!("Player {index}"),
                    side: if index < 5 { "t" } else { "ct" }.to_string(),
                    player_color: None,
                    team: if index < 5 { "a" } else { "b" }.to_string(),
                    team_name: None,
                    rounds: 24,
                    files: 24,
                    score: None,
                    kills: None,
                    deaths: None,
                    assists: None,
                    mvps: None,
                })
                .collect(),
            avatar_overrides: Vec::new(),
            score: None,
            score_is_snapshot: false,
            metadata_status: "current".to_string(),
            source_path: Some(source_path.to_string()),
            source_available: true,
            source_modified_at_ms: Some(modified_at_ms),
            played_at: None,
            source_size_bytes: Some(1),
            duration_seconds: Some(1.0),
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            demo_source: Some(cs2_demotracer::browser_analysis::BrowserDemoSource {
                name: "HLTV".to_string(),
                evidence: "fileName".to_string(),
            }),
            converter_version: Some("1.0.0".to_string()),
            series: None,
        }
    }

    #[test]
    fn strict_hltv_series_accepts_lineup_changes_after_map_one() {
        let mut entries = vec![
            strict_series_entry(
                r"C:\demos\og-vs-spirit-m1-cache.dem",
                "de_cache",
                "a",
                10_000,
            ),
            strict_series_entry(
                r"C:\demos\og-vs-spirit-m2-dust2.dem",
                "de_dust2",
                "b",
                20_000,
            ),
            strict_series_entry(
                r"C:\demos\og-vs-spirit-m3-ancient.dem",
                "de_ancient",
                "c",
                30_000,
            ),
        ];
        // A previously imported map may predate source classification. The
        // remaining maps still prove the HLTV lane for the shared selection.
        entries[0].demo_source = None;
        assign_strict_hltv_series(&mut entries);
        let series_ids = entries
            .iter()
            .map(|entry| entry.series.as_ref().map(|series| series.id.clone()))
            .collect::<BTreeSet<_>>();
        assert_eq!(series_ids.len(), 1);
        assert!(series_ids.iter().next().unwrap().is_some());
        assert_eq!(
            entries
                .iter()
                .map(|entry| entry.series.as_ref().unwrap().order)
                .collect::<Vec<_>>(),
            vec![1, 2, 3]
        );

        entries[2].series = None;
        entries[2].players[0].steam_id = "76561199999999999".to_string();
        for entry in &mut entries[..2] {
            entry.series = None;
        }
        assign_strict_hltv_series(&mut entries);
        assert!(entries.iter().all(|entry| entry.series.is_some()));
    }

    #[test]
    fn strict_hltv_series_accepts_event_server_names_inside_a_bo_bundle() {
        let parent = r"C:\demos\blast-bounty-og-vs-spirit-bo3-P0hojm";
        let mut entries = vec![
            strict_series_entry(
                &format!(r"{parent}\og-vs-spirit-m1-cache.dem"),
                "de_cache",
                "a",
                10_000,
            ),
            strict_series_entry(
                &format!(r"{parent}\og-vs-spirit-m2-dust2.dem"),
                "de_dust2",
                "b",
                20_000,
            ),
            strict_series_entry(
                &format!(r"{parent}\og-vs-spirit-m3-ancient.dem"),
                "de_ancient",
                "c",
                30_000,
            ),
        ];
        for entry in &mut entries {
            entry.demo_source.as_mut().unwrap().name = "BLAST".to_string();
        }

        assign_strict_hltv_series(&mut entries);

        assert!(entries.iter().all(|entry| entry.series.is_some()));
    }

    #[test]
    fn strict_hltv_series_ignores_download_time_skew() {
        let parent =
            r"C:\demos\iem-cologne-major-2026-spirit-vs-falcons-bo3-wEPPcZigNVs8eZdF9kjqcz";
        let mut entries = vec![
            strict_series_entry(
                &format!(r"{parent}\spirit-vs-falcons-m1-anubis.dem"),
                "de_anubis",
                "a",
                1_753_157_168_000,
            ),
            strict_series_entry(
                &format!(r"{parent}\spirit-vs-falcons-m2-mirage.dem"),
                "de_mirage",
                "b",
                1_750_469_653_000,
            ),
            strict_series_entry(
                &format!(r"{parent}\spirit-vs-falcons-m3-dust2.dem"),
                "de_dust2",
                "c",
                1_750_474_435_000,
            ),
        ];

        assign_strict_hltv_series(&mut entries);

        assert!(entries.iter().all(|entry| entry.series.is_some()));
        assert_eq!(
            entries
                .iter()
                .map(|entry| entry.series.as_ref().unwrap().order)
                .collect::<Vec<_>>(),
            vec![1, 2, 3]
        );
    }
}
