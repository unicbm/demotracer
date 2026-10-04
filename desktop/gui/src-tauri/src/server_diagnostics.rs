/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::activity_log::{ActivityLogEntryDto, ActivityLogLevel};
use crate::{playback_installation::resolve_install_paths, CommandErrorDto, CommandResult};
use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::fs::{self, File};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use tauri::{AppHandle, Emitter, State};

const TAIL_BYTES: u64 = 256 * 1024;

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeHealth {
    schema_version: u32,
    written_at_ms: u64,
    running: bool,
    plugin_version: String,
    bot_controller: ControllerHealth,
    bot_hider: ProviderHealth,
    bot_randomizer: ProviderHealth,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct ControllerHealth {
    compatible: bool,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProviderHealth {
    available: bool,
}

#[derive(Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsSnapshot {
    revision: u64,
    session: String,
    health: Option<RuntimeHealth>,
    health_error: Option<String>,
    log_error: Option<String>,
    log_path: Option<String>,
    entries: VecDeque<ActivityLogEntryDto>,
}

#[derive(Default)]
struct LogTail {
    path: PathBuf,
    offset: u64,
    pending: Vec<u8>,
    entries: VecDeque<ActivityLogEntryDto>,
}

impl LogTail {
    fn read(&mut self, path: &Path) -> std::io::Result<()> {
        let mut file = File::open(path)?;
        let length = file.metadata()?.len();
        let reset = self.path != path || length < self.offset;
        if reset {
            self.path = path.to_owned();
            self.offset = 0;
            self.pending.clear();
            self.entries.clear();
        }
        if length.saturating_sub(self.offset) > TAIL_BYTES {
            self.offset = length - TAIL_BYTES;
            self.pending.clear();
            file.seek(SeekFrom::Start(self.offset))?;
            let mut bytes = Vec::new();
            file.take(TAIL_BYTES).read_to_end(&mut bytes)?;
            self.offset += bytes.len() as u64;
            if let Some(start) = bytes.iter().position(|b| *b == b'\n') {
                self.feed(&bytes[start + 1..]);
            }
        } else {
            file.seek(SeekFrom::Start(self.offset))?;
            let mut bytes = Vec::new();
            file.take(TAIL_BYTES).read_to_end(&mut bytes)?;
            self.offset += bytes.len() as u64;
            self.feed(&bytes);
        }
        Ok(())
    }

    fn feed(&mut self, bytes: &[u8]) {
        self.pending.extend_from_slice(bytes);
        while let Some(end) = self.pending.iter().position(|b| *b == b'\n') {
            let line = String::from_utf8_lossy(&self.pending[..end])
                .trim_end_matches('\r')
                .to_owned();
            self.pending.drain(..=end);
            if let Some(entry) = parse_css_line(&line) {
                self.entries.push_back(entry);
                if self.entries.len() > 500 {
                    self.entries.pop_front();
                }
            } else if let Some(last) = self.entries.back_mut() {
                if last.message.len() < TAIL_BYTES as usize {
                    last.message.push('\n');
                    last.message.push_str(&line);
                }
            }
        }
        if self.pending.len() > TAIL_BYTES as usize {
            self.pending.clear();
        }
    }
}

fn parse_css_line(line: &str) -> Option<ActivityLogEntryDto> {
    let (timestamp, body) = line.split_once(" [")?;
    let timestamp = chrono::DateTime::parse_from_str(
        timestamp.trim_start_matches('\u{feff}'),
        "%Y-%m-%d %H:%M:%S%.f %:z",
    )
    .ok()?;
    let (level, body) = body.split_once("] ")?;
    let level = match level.trim() {
        "ERR" | "EROR" | "FTL" | "FATL" => ActivityLogLevel::Error,
        "WRN" | "WARN" => ActivityLogLevel::Warn,
        "DBG" | "DBUG" | "VRB" | "VERB" => ActivityLogLevel::Debug,
        _ => ActivityLogLevel::Info,
    };
    let (source, message) = body.strip_prefix("(cssharp:")?.split_once(") ")?;
    let mut entry = ActivityLogEntryDto::new(level, format!("server:{source}"), message.into());
    entry.timestamp_ms = timestamp.timestamp_millis().max(0) as u64;
    Some(entry)
}

struct Monitor {
    _watcher: RecommendedWatcher,
    root: PathBuf,
    tail: Arc<Mutex<LogTail>>,
    snapshot: Arc<Mutex<DiagnosticsSnapshot>>,
}

#[derive(Default)]
pub struct ServerDiagnosticsState(Mutex<Option<Monitor>>);

fn refresh(root: &Path, snapshot: &mut DiagnosticsSnapshot, tail: &mut LogTail) {
    snapshot.revision += 1;
    let health_path = root.join("plugins/DemoTracer/demotracer-runtime.v1.json");
    match fs::read(&health_path) {
        Ok(bytes) => match serde_json::from_slice::<RuntimeHealth>(&bytes) {
            Ok(health) if health.schema_version == 1 => {
                snapshot.health = Some(health);
                snapshot.health_error = None;
            }
            result => {
                snapshot.health = None;
                snapshot.health_error = Some(match result {
                    Err(e) => e.to_string(),
                    _ => "Unsupported runtime status version".into(),
                });
            }
        },
        Err(e) => {
            snapshot.health = None;
            snapshot.health_error =
                (e.kind() != std::io::ErrorKind::NotFound).then(|| e.to_string());
        }
    }
    let read_logs = || -> std::io::Result<Option<PathBuf>> {
        let mut files = Vec::new();
        for item in fs::read_dir(root.join("logs"))? {
            let item = item?;
            let name = item.file_name().to_string_lossy().into_owned();
            if name.starts_with("log-cssharp")
                && name.ends_with(".txt")
                && item.file_type()?.is_file()
            {
                files.push(item.path());
            }
        }
        files.sort();
        Ok(files.pop())
    };
    let result = read_logs().and_then(|path| {
        snapshot.log_path = path.as_ref().map(|p| p.to_string_lossy().into_owned());
        if let Some(path) = path {
            tail.read(&path)?;
        } else {
            *tail = LogTail::default();
        }
        Ok(())
    });
    snapshot.log_error = match result {
        Ok(()) => None,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            snapshot.log_path = None;
            *tail = LogTail::default();
            None
        }
        Err(e) => Some(e.to_string()),
    };
    snapshot.entries = tail.entries.clone();
}

#[tauri::command]
pub fn configure_server_diagnostics(
    cs2_path: String,
    session: String,
    app: AppHandle,
    state: State<'_, ServerDiagnosticsState>,
) -> CommandResult<DiagnosticsSnapshot> {
    let error = |e: String| CommandErrorDto::new("server_diagnostics_failed", e);
    let mut monitor = state.0.lock().map_err(|e| error(e.to_string()))?;
    *monitor = None;
    let snapshot = Arc::new(Mutex::new(DiagnosticsSnapshot {
        session,
        ..Default::default()
    }));
    if cs2_path.trim().is_empty() {
        return Ok(snapshot.lock().unwrap().clone());
    }
    let paths = resolve_install_paths(Path::new(cs2_path.trim()))?;
    let root = paths.game_csgo.join("addons/counterstrikesharp");
    let mut watch_path = paths.game_csgo.join("addons");
    while !watch_path.is_dir() {
        if !watch_path.pop() {
            break;
        }
    }
    let snapshot_for_event = snapshot.clone();
    let root_for_event = root.clone();
    let tail = Arc::new(Mutex::new(LogTail::default()));
    let tail_for_event = tail.clone();
    let mut watcher = notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
        if let Ok(event) = &event {
            if event.kind.is_access()
                || !event.paths.iter().any(|p| {
                    let health =
                        root_for_event.join("plugins/DemoTracer/demotracer-runtime.v1.json");
                    let logs = root_for_event.join("logs");
                    health.starts_with(p)
                        || logs.starts_with(p)
                        || p.parent() == Some(logs.as_path())
                })
            {
                return;
            }
        }
        let Ok(mut snapshot) = snapshot_for_event.lock() else {
            return;
        };
        match event {
            Ok(_) => {
                let Ok(mut tail) = tail_for_event.lock() else {
                    return;
                };
                refresh(&root_for_event, &mut snapshot, &mut tail);
            }
            Err(e) => {
                snapshot.revision += 1;
                snapshot.health = None;
                snapshot.health_error = Some(e.to_string());
            }
        }
        let _ = app.emit("server-diagnostics", &*snapshot);
    })
    .map_err(|e| error(e.to_string()))?;
    watcher
        .watch(&watch_path, RecursiveMode::Recursive)
        .map_err(|e| error(e.to_string()))?;
    let initial = {
        let mut data = snapshot.lock().map_err(|e| error(e.to_string()))?;
        let mut tail = tail.lock().map_err(|e| error(e.to_string()))?;
        refresh(&root, &mut data, &mut tail);
        data.clone()
    };
    *monitor = Some(Monitor {
        _watcher: watcher,
        root,
        tail,
        snapshot,
    });
    Ok(initial)
}

#[tauri::command]
pub fn server_diagnostics(
    state: State<'_, ServerDiagnosticsState>,
) -> CommandResult<DiagnosticsSnapshot> {
    let error = |e: String| CommandErrorDto::new("server_diagnostics_failed", e);
    let monitor = state.0.lock().map_err(|e| error(e.to_string()))?;
    match monitor.as_ref() {
        Some(monitor) => {
            let mut snapshot = monitor.snapshot.lock().map_err(|e| error(e.to_string()))?;
            let mut tail = monitor.tail.lock().map_err(|e| error(e.to_string()))?;
            refresh(&monitor.root, &mut snapshot, &mut tail);
            Ok(snapshot.clone())
        }
        None => Ok(DiagnosticsSnapshot::default()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tail_preserves_split_utf8_and_exception_stack() {
        let text = "2026-10-04 12:00:00.123 +08:00 [EROR] (cssharp:DemoTracer) 加载失败\nSystem.Exception: native module\n   at Plugin.Load()\n";
        let split = text.find('加').unwrap() + 1;
        let mut tail = LogTail::default();
        tail.feed(&text.as_bytes()[..split]);
        assert!(tail.entries.is_empty());
        tail.feed(&text.as_bytes()[split..]);
        let entry = &tail.entries[0];
        assert_eq!(entry.level, ActivityLogLevel::Error);
        assert_eq!(entry.source, "server:DemoTracer");
        assert_eq!(
            entry.message,
            "加载失败\nSystem.Exception: native module\n   at Plugin.Load()"
        );
        assert_eq!(entry.timestamp_ms, 1791086400123);
    }

    #[test]
    fn tail_reads_only_appends_and_resets_on_truncation_and_rotation() {
        use std::io::Write;
        let root = std::env::temp_dir().join(format!("dtr-log-tail-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let first = root.join("log-cssharp20261004.txt");
        let second = root.join("log-cssharp20261005.txt");
        let line = "2026-10-04 12:00:00.000 +08:00 [INFO] (cssharp:DemoTracer) ready\n";
        fs::write(&first, line.repeat(2)).unwrap();
        let mut tail = LogTail::default();
        tail.read(&first).unwrap();
        let original_id = tail.entries[0].id.clone();
        fs::OpenOptions::new()
            .append(true)
            .open(&first)
            .unwrap()
            .write_all(line.as_bytes())
            .unwrap();
        tail.read(&first).unwrap();
        assert_eq!(tail.entries.len(), 3);
        assert_eq!(tail.entries[0].id, original_id);
        fs::write(&first, line).unwrap();
        tail.read(&first).unwrap();
        assert_eq!(tail.entries.len(), 1);
        fs::write(&second, line.replace("ready", "next day")).unwrap();
        tail.read(&second).unwrap();
        assert_eq!(tail.entries.len(), 1);
        assert_eq!(tail.entries[0].message, "next day");
        fs::remove_dir_all(root).unwrap();
    }
}
