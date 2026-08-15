/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::State;

const LOG_PREFIX: &str = "activity-";
const LOG_SUFFIX: &str = ".jsonl";
const RETENTION_DAYS: u64 = 14;
const MAX_FILE_BYTES: u64 = 5 * 1024 * 1024;
const MAX_TOTAL_BYTES: u64 = 20 * 1024 * 1024;
const MAX_READ_ENTRIES: usize = 5_000;
const MAX_MESSAGE_BYTES: usize = 4_096;

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ActivityLogLevel {
    Debug,
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ActivityLogEntryDto {
    pub id: String,
    pub timestamp_ms: u64,
    pub level: ActivityLogLevel,
    pub source: String,
    pub message: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppendActivityLogRequestDto {
    pub level: ActivityLogLevel,
    pub source: String,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ActivityLogMaintenanceDto {
    pub checked_files: usize,
    pub removed_files: usize,
    pub repaired_lines: usize,
}

#[derive(Clone)]
pub struct ActivityLogState {
    root: Arc<PathBuf>,
    sequence: Arc<AtomicU64>,
    access: Arc<Mutex<()>>,
}

impl ActivityLogState {
    pub fn new(root: PathBuf) -> CommandResult<Self> {
        fs::create_dir_all(&root).map_err(|error| {
            CommandErrorDto::at_path("activity_log_directory_failed", error.to_string(), &root)
        })?;
        Ok(Self {
            root: Arc::new(root),
            sequence: Arc::new(AtomicU64::new(1)),
            access: Arc::new(Mutex::new(())),
        })
    }

    pub fn root(&self) -> &Path {
        self.root.as_path()
    }

    pub fn append(
        &self,
        level: ActivityLogLevel,
        source: impl AsRef<str>,
        message: impl AsRef<str>,
    ) -> CommandResult<ActivityLogEntryDto> {
        let _guard = self.access.lock().map_err(|_| {
            CommandErrorDto::new("activity_log_lock_failed", "Activity log lock is poisoned.")
        })?;
        fs::create_dir_all(self.root()).map_err(|error| {
            CommandErrorDto::at_path(
                "activity_log_directory_failed",
                error.to_string(),
                self.root(),
            )
        })?;

        let timestamp_ms = now_ms();
        let source = normalize_source(source.as_ref());
        let message = normalize_message(message.as_ref());
        let sequence = self.sequence.fetch_add(1, Ordering::Relaxed);
        let entry = ActivityLogEntryDto {
            id: format!("{timestamp_ms}-{sequence}"),
            timestamp_ms,
            level,
            source,
            message,
        };
        let path = writable_log_path(self.root(), timestamp_ms / 86_400_000)?;
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .map_err(|error| {
                CommandErrorDto::at_path("activity_log_open_failed", error.to_string(), &path)
            })?;
        serde_json::to_writer(&mut file, &entry).map_err(|error| {
            CommandErrorDto::at_path("activity_log_serialize_failed", error.to_string(), &path)
        })?;
        file.write_all(b"\n").map_err(|error| {
            CommandErrorDto::at_path("activity_log_write_failed", error.to_string(), &path)
        })?;
        Ok(entry)
    }

    pub fn list(
        &self,
        limit: usize,
        since_ms: Option<u64>,
    ) -> CommandResult<Vec<ActivityLogEntryDto>> {
        let _guard = self.access.lock().map_err(|_| {
            CommandErrorDto::new("activity_log_lock_failed", "Activity log lock is poisoned.")
        })?;
        let mut entries = Vec::new();
        for path in log_files(self.root())? {
            if since_ms.is_some_and(|since| {
                log_file_coordinates(&path).is_some_and(|(day, _)| {
                    day.saturating_add(1).saturating_mul(86_400_000) <= since
                })
            }) {
                continue;
            }
            let file = File::open(&path).map_err(|error| {
                CommandErrorDto::at_path("activity_log_read_failed", error.to_string(), &path)
            })?;
            for line in BufReader::new(file).lines() {
                let Ok(line) = line else { continue };
                if let Ok(entry) = serde_json::from_str::<ActivityLogEntryDto>(&line) {
                    if since_ms.is_some_and(|since| entry.timestamp_ms < since) {
                        continue;
                    }
                    entries.push(entry);
                }
            }
        }
        entries.sort_by(|left, right| {
            left.timestamp_ms
                .cmp(&right.timestamp_ms)
                .then_with(|| left.id.cmp(&right.id))
        });
        let limit = limit.clamp(1, MAX_READ_ENTRIES);
        if entries.len() > limit {
            entries.drain(..entries.len() - limit);
        }
        Ok(entries)
    }

    pub fn maintain(&self) -> CommandResult<ActivityLogMaintenanceDto> {
        let _guard = self.access.lock().map_err(|_| {
            CommandErrorDto::new("activity_log_lock_failed", "Activity log lock is poisoned.")
        })?;
        maintain_locked(self.root())
    }

    pub fn clear(&self) -> CommandResult<usize> {
        let _guard = self.access.lock().map_err(|_| {
            CommandErrorDto::new("activity_log_lock_failed", "Activity log lock is poisoned.")
        })?;
        let paths = log_files(self.root())?;
        let mut removed = 0;
        for path in paths {
            fs::remove_file(&path).map_err(|error| {
                CommandErrorDto::at_path("activity_log_clear_failed", error.to_string(), &path)
            })?;
            removed += 1;
        }
        Ok(removed)
    }
}

#[tauri::command]
pub fn list_activity_logs(
    limit: Option<usize>,
    since_ms: Option<u64>,
    state: State<'_, ActivityLogState>,
) -> CommandResult<Vec<ActivityLogEntryDto>> {
    state.list(limit.unwrap_or(MAX_READ_ENTRIES), since_ms)
}

#[tauri::command]
pub fn append_activity_log(
    request: AppendActivityLogRequestDto,
    state: State<'_, ActivityLogState>,
) -> CommandResult<ActivityLogEntryDto> {
    state.append(request.level, request.source, request.message)
}

#[tauri::command]
pub fn maintain_activity_logs(
    state: State<'_, ActivityLogState>,
) -> CommandResult<ActivityLogMaintenanceDto> {
    state.maintain()
}

#[tauri::command]
pub fn clear_activity_logs(state: State<'_, ActivityLogState>) -> CommandResult<usize> {
    state.clear()
}

fn normalize_source(source: &str) -> String {
    let normalized: String = source
        .trim()
        .chars()
        .filter(|character| character.is_ascii_alphanumeric() || matches!(character, '-' | '_'))
        .take(32)
        .collect();
    if normalized.is_empty() {
        "app".to_string()
    } else {
        normalized
    }
}

fn normalize_message(message: &str) -> String {
    let normalized = message
        .trim()
        .replace("\r\n", " ↩ ")
        .replace('\r', " ↩ ")
        .replace('\n', " ↩ ");
    if normalized.len() <= MAX_MESSAGE_BYTES {
        return normalized;
    }
    let mut end = MAX_MESSAGE_BYTES;
    while !normalized.is_char_boundary(end) {
        end -= 1;
    }
    normalized[..end].to_string()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64
}

fn writable_log_path(root: &Path, day: u64) -> CommandResult<PathBuf> {
    for segment in 0..10_000_u32 {
        let path = root.join(format!("{LOG_PREFIX}{day}-{segment}{LOG_SUFFIX}"));
        match fs::metadata(&path) {
            Ok(metadata) if metadata.len() >= MAX_FILE_BYTES => continue,
            Ok(_) | Err(_) => return Ok(path),
        }
    }
    Err(CommandErrorDto::new(
        "activity_log_rotation_failed",
        "No writable activity log segment is available.",
    ))
}

fn log_files(root: &Path) -> CommandResult<Vec<PathBuf>> {
    fs::create_dir_all(root).map_err(|error| {
        CommandErrorDto::at_path("activity_log_directory_failed", error.to_string(), root)
    })?;
    let mut paths = Vec::new();
    for entry in fs::read_dir(root).map_err(|error| {
        CommandErrorDto::at_path("activity_log_read_failed", error.to_string(), root)
    })? {
        let entry = entry.map_err(|error| {
            CommandErrorDto::at_path("activity_log_read_failed", error.to_string(), root)
        })?;
        let path = entry.path();
        if entry
            .file_type()
            .map(|kind| kind.is_file())
            .unwrap_or(false)
            && log_file_coordinates(&path).is_some()
        {
            paths.push(path);
        }
    }
    paths.sort_by_key(|path| log_file_coordinates(path).unwrap_or_default());
    Ok(paths)
}

fn log_file_coordinates(path: &Path) -> Option<(u64, u32)> {
    let name = path.file_name()?.to_str()?;
    let middle = name.strip_prefix(LOG_PREFIX)?.strip_suffix(LOG_SUFFIX)?;
    let (day, segment) = middle.split_once('-')?;
    Some((day.parse().ok()?, segment.parse().ok()?))
}

fn maintain_locked(root: &Path) -> CommandResult<ActivityLogMaintenanceDto> {
    let current_day = now_ms() / 86_400_000;
    let mut result = ActivityLogMaintenanceDto::default();
    let mut paths = log_files(root)?;

    for path in paths.clone() {
        let Some((day, _)) = log_file_coordinates(&path) else {
            continue;
        };
        if current_day.saturating_sub(day) >= RETENTION_DAYS {
            fs::remove_file(&path).map_err(|error| {
                CommandErrorDto::at_path("activity_log_cleanup_failed", error.to_string(), &path)
            })?;
            result.removed_files += 1;
            continue;
        }
        result.checked_files += 1;
        result.repaired_lines += repair_file(&path)?;
    }

    paths = log_files(root)?;
    let mut total_bytes: u64 = paths
        .iter()
        .filter_map(|path| fs::metadata(path).ok().map(|metadata| metadata.len()))
        .sum();
    for path in paths {
        if total_bytes <= MAX_TOTAL_BYTES {
            break;
        }
        let length = fs::metadata(&path)
            .map(|metadata| metadata.len())
            .unwrap_or(0);
        fs::remove_file(&path).map_err(|error| {
            CommandErrorDto::at_path("activity_log_cleanup_failed", error.to_string(), &path)
        })?;
        total_bytes = total_bytes.saturating_sub(length);
        result.removed_files += 1;
    }
    Ok(result)
}

fn repair_file(path: &Path) -> CommandResult<usize> {
    let file = File::open(path).map_err(|error| {
        CommandErrorDto::at_path("activity_log_read_failed", error.to_string(), path)
    })?;
    let mut valid_lines = Vec::new();
    let mut invalid = 0;
    for line in BufReader::new(file).lines() {
        match line {
            Ok(line) if serde_json::from_str::<ActivityLogEntryDto>(&line).is_ok() => {
                valid_lines.push(line)
            }
            _ => invalid += 1,
        }
    }
    if invalid == 0 {
        return Ok(0);
    }
    let temporary = path.with_extension("jsonl.repair");
    {
        let mut repaired = File::create(&temporary).map_err(|error| {
            CommandErrorDto::at_path("activity_log_repair_failed", error.to_string(), &temporary)
        })?;
        for line in valid_lines {
            repaired
                .write_all(line.as_bytes())
                .and_then(|_| repaired.write_all(b"\n"))
                .map_err(|error| {
                    CommandErrorDto::at_path(
                        "activity_log_repair_failed",
                        error.to_string(),
                        &temporary,
                    )
                })?;
        }
        repaired.sync_all().map_err(|error| {
            CommandErrorDto::at_path("activity_log_repair_failed", error.to_string(), &temporary)
        })?;
    }
    let backup = path.with_extension("jsonl.backup");
    let _ = fs::remove_file(&backup);
    fs::rename(path, &backup).map_err(|error| {
        let _ = fs::remove_file(&temporary);
        CommandErrorDto::at_path("activity_log_repair_failed", error.to_string(), path)
    })?;
    if let Err(error) = fs::rename(&temporary, path) {
        let _ = fs::rename(&backup, path);
        let _ = fs::remove_file(&temporary);
        return Err(CommandErrorDto::at_path(
            "activity_log_repair_failed",
            error.to_string(),
            path,
        ));
    }
    let _ = fs::remove_file(&backup);
    Ok(invalid)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temporary_log_root(name: &str) -> PathBuf {
        let nonce = now_ms();
        std::env::temp_dir().join(format!("demotracer-activity-log-{name}-{nonce}"))
    }

    #[test]
    fn persisted_entries_round_trip_and_stay_sorted() {
        let root = temporary_log_root("round-trip");
        let state = ActivityLogState::new(root.clone()).unwrap();
        state
            .append(ActivityLogLevel::Info, "analysis", "Parsing started")
            .unwrap();
        state
            .append(ActivityLogLevel::Warn, "conversion", "Skipped one player")
            .unwrap();
        let entries = state.list(20, None).unwrap();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].source, "analysis");
        assert_eq!(entries[1].level, ActivityLogLevel::Warn);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn maintenance_repairs_invalid_json_lines() {
        let root = temporary_log_root("repair");
        let state = ActivityLogState::new(root.clone()).unwrap();
        state
            .append(ActivityLogLevel::Info, "app", "healthy")
            .unwrap();
        let path = log_files(&root).unwrap().pop().unwrap();
        OpenOptions::new()
            .append(true)
            .open(&path)
            .unwrap()
            .write_all(b"broken\n")
            .unwrap();
        let result = state.maintain().unwrap();
        assert_eq!(result.repaired_lines, 1);
        assert_eq!(state.list(20, None).unwrap().len(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn list_filters_entries_older_than_since_timestamp() {
        let root = temporary_log_root("since");
        let state = ActivityLogState::new(root.clone()).unwrap();
        let entry = state
            .append(ActivityLogLevel::Info, "app", "current")
            .unwrap();
        assert_eq!(state.list(20, Some(entry.timestamp_ms)).unwrap().len(), 1);
        assert!(state
            .list(20, Some(entry.timestamp_ms.saturating_add(1)))
            .unwrap()
            .is_empty());
        fs::remove_dir_all(root).unwrap();
    }
}
