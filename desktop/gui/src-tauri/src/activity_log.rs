/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::fs::{self, File};
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use tauri::State;
use tauri_plugin_log::{RotationStrategy, Target, TargetKind};

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

impl ActivityLogEntryDto {
    pub fn new(level: ActivityLogLevel, source: String, message: String) -> Self {
        Self {
            id: uuid::Uuid::new_v4().to_string(),
            timestamp_ms: chrono::Utc::now().timestamp_millis().max(0) as u64,
            level,
            source,
            message,
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppendActivityLogRequestDto {
    level: ActivityLogLevel,
    source: String,
    message: String,
}

#[derive(Clone)]
pub struct ActivityLogState {
    root: PathBuf,
}

impl ActivityLogState {
    pub fn new(root: PathBuf) -> CommandResult<Self> {
        fs::create_dir_all(&root).map_err(|e| {
            CommandErrorDto::at_path("activity_log_directory_failed", e.to_string(), &root)
        })?;
        Ok(Self { root })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn plugin(&self) -> tauri::plugin::TauriPlugin<tauri::Wry> {
        tauri_plugin_log::Builder::new()
            .level(log::LevelFilter::Info)
            .level_for("demotracer_activity", log::LevelFilter::Debug)
            .max_file_size(5 * 1024 * 1024)
            .rotation_strategy(RotationStrategy::KeepSome(3))
            .targets([
                Target::new(TargetKind::Folder {
                    path: self.root.clone(),
                    file_name: Some("activity".into()),
                }),
                Target::new(TargetKind::Webview),
            ])
            .format(|out, message, record| {
                if record.target() == "demotracer_activity" {
                    out.finish(format_args!("{message}"));
                } else {
                    let level = match record.level() {
                        log::Level::Error => ActivityLogLevel::Error,
                        log::Level::Warn => ActivityLogLevel::Warn,
                        log::Level::Info => ActivityLogLevel::Info,
                        _ => ActivityLogLevel::Debug,
                    };
                    let entry = ActivityLogEntryDto::new(
                        level,
                        record.target().into(),
                        message.to_string(),
                    );
                    out.finish(format_args!(
                        "{}",
                        serde_json::to_string(&entry).expect("serializable log entry")
                    ));
                }
            })
            .build()
    }

    pub fn append(
        &self,
        level: ActivityLogLevel,
        source: impl AsRef<str>,
        message: impl AsRef<str>,
    ) -> CommandResult<ActivityLogEntryDto> {
        let entry =
            ActivityLogEntryDto::new(level, source.as_ref().into(), message.as_ref().into());
        let payload = serde_json::to_string(&entry)
            .map_err(|e| CommandErrorDto::new("activity_log_serialize_failed", e.to_string()))?;
        let level = match level {
            ActivityLogLevel::Debug => log::Level::Debug,
            ActivityLogLevel::Info => log::Level::Info,
            ActivityLogLevel::Warn => log::Level::Warn,
            ActivityLogLevel::Error => log::Level::Error,
        };
        log::log!(target: "demotracer_activity", level, "{payload}");
        Ok(entry)
    }

    fn files(&self) -> CommandResult<Vec<PathBuf>> {
        let read_error = |e: std::io::Error| {
            CommandErrorDto::at_path("activity_log_read_failed", e.to_string(), &self.root)
        };
        let mut paths = Vec::new();
        for item in fs::read_dir(&self.root).map_err(read_error)? {
            let item = item.map_err(read_error)?;
            let name = item.file_name().to_string_lossy().into_owned();
            if item.file_type().map_err(read_error)?.is_file()
                && (name == "activity.log"
                    || name.starts_with("activity_") && name.ends_with(".log")
                    || name.starts_with("activity-") && name.ends_with(".jsonl"))
            {
                paths.push(item.path());
            }
        }
        paths.sort_by_key(|p| fs::metadata(p).and_then(|m| m.modified()).ok());
        Ok(paths)
    }
}

#[tauri::command]
pub fn list_activity_logs(
    limit: Option<usize>,
    since_ms: Option<u64>,
    state: State<'_, ActivityLogState>,
) -> CommandResult<Vec<ActivityLogEntryDto>> {
    log::logger().flush();
    let limit = limit.unwrap_or(5000).clamp(1, 5000);
    let mut entries = VecDeque::new();
    for path in state.files()? {
        let file = File::open(&path).map_err(|e| {
            CommandErrorDto::at_path("activity_log_read_failed", e.to_string(), &path)
        })?;
        for line in BufReader::new(file).lines() {
            let line = line.map_err(|e| {
                CommandErrorDto::at_path("activity_log_read_failed", e.to_string(), &path)
            })?;
            if let Ok(entry) = serde_json::from_str::<ActivityLogEntryDto>(&line) {
                if since_ms.is_some_and(|since| entry.timestamp_ms < since) {
                    continue;
                }
                entries.push_back(entry);
                if entries.len() > limit {
                    entries.pop_front();
                }
            }
        }
    }
    Ok(entries.into())
}

#[tauri::command]
pub fn append_activity_log(
    request: AppendActivityLogRequestDto,
    state: State<'_, ActivityLogState>,
) -> CommandResult<ActivityLogEntryDto> {
    state.append(request.level, request.source, request.message)
}
