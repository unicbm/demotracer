/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::activity_log::{ActivityLogLevel, ActivityLogState};
use crate::{CommandErrorDto, CommandResult};
use serde::Serialize;
use serde_json::Value;
use std::fs;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::State;

const GSI_ADDRESS: &str = "127.0.0.1:32123";
const GSI_URI: &str = "http://127.0.0.1:32123";
const GSI_CONFIG_NAME: &str = "gamestate_integration_demotracer.cfg";
const MAX_GSI_REQUEST_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct GsiStatusDto {
    pub listening: bool,
    pub configured: bool,
    pub connected: bool,
    pub port: u16,
    pub config_path: Option<String>,
    pub last_update_ms: Option<u64>,
    pub provider: Option<String>,
    pub map: Option<String>,
    pub map_phase: Option<String>,
    pub round: Option<i64>,
    pub round_phase: Option<String>,
    pub player_activity: Option<String>,
    pub player_health: Option<i64>,
    pub error: Option<String>,
}

#[derive(Debug, Default)]
struct GsiRuntimeStatus {
    listening: bool,
    config_path: Option<String>,
    last_update_ms: Option<u64>,
    provider: Option<String>,
    map: Option<String>,
    map_phase: Option<String>,
    round: Option<i64>,
    round_phase: Option<String>,
    player_activity: Option<String>,
    player_health: Option<i64>,
    last_signature: String,
    error: Option<String>,
}

#[derive(Clone)]
pub struct GsiState {
    token: Arc<String>,
    status: Arc<Mutex<GsiRuntimeStatus>>,
    activity_log: ActivityLogState,
}

impl GsiState {
    pub fn new(activity_log: ActivityLogState) -> Self {
        let token = format!("{:x}{:x}", now_ms(), std::process::id());
        Self {
            token: Arc::new(token),
            status: Arc::new(Mutex::new(GsiRuntimeStatus::default())),
            activity_log,
        }
    }

    pub fn start(&self) {
        let listener = match TcpListener::bind(GSI_ADDRESS) {
            Ok(listener) => listener,
            Err(error) => {
                if let Ok(mut status) = self.status.lock() {
                    status.error = Some(error.to_string());
                }
                let _ = self.activity_log.append(
                    ActivityLogLevel::Error,
                    "gsi",
                    format!("GSI listener failed to bind {GSI_ADDRESS}: {error}"),
                );
                return;
            }
        };
        if let Ok(mut status) = self.status.lock() {
            status.listening = true;
            status.error = None;
        }
        let runtime = self.clone();
        if let Err(error) = thread::Builder::new()
            .name("demotracer-gsi".to_string())
            .spawn(move || {
                for incoming in listener.incoming() {
                    match incoming {
                        Ok(stream) => runtime.handle_connection(stream),
                        Err(error) => {
                            let _ = runtime.activity_log.append(
                                ActivityLogLevel::Warn,
                                "gsi",
                                format!("GSI connection failed: {error}"),
                            );
                        }
                    }
                }
            })
        {
            if let Ok(mut status) = self.status.lock() {
                status.listening = false;
                status.error = Some(error.to_string());
            }
            let _ = self.activity_log.append(
                ActivityLogLevel::Error,
                "gsi",
                format!("GSI listener thread failed to start: {error}"),
            );
        }
    }

    pub fn configure(&self, cs2_path: &str) -> CommandResult<GsiStatusDto> {
        let cfg_dir = resolve_cs2_cfg_dir(cs2_path)?;
        let metadata = fs::symlink_metadata(&cfg_dir).map_err(|error| {
            CommandErrorDto::at_path("gsi_config_directory_failed", error.to_string(), &cfg_dir)
        })?;
        if !metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&metadata) {
            return Err(CommandErrorDto::at_path(
                "gsi_config_directory_unsafe",
                "The CS2 cfg directory must be a normal local directory.",
                &cfg_dir,
            ));
        }
        let target = cfg_dir.join(GSI_CONFIG_NAME);
        if let Ok(metadata) = fs::symlink_metadata(&target) {
            if !metadata.is_file() || crate::catalog::is_symlink_or_reparse(&metadata) {
                return Err(CommandErrorDto::at_path(
                    "gsi_config_target_unsafe",
                    "The DemoTracer GSI config target is not a normal file.",
                    &target,
                ));
            }
        }
        let contents = format!(
            "\"CS2 DemoTracer\"\n{{\n  \"uri\" \"{GSI_URI}\"\n  \"timeout\" \"5.0\"\n  \"buffer\" \"0.1\"\n  \"throttle\" \"0.5\"\n  \"heartbeat\" \"10.0\"\n  \"auth\"\n  {{\n    \"token\" \"{}\"\n  }}\n  \"data\"\n  {{\n    \"provider\" \"1\"\n    \"map\" \"1\"\n    \"round\" \"1\"\n    \"player_id\" \"1\"\n    \"player_state\" \"1\"\n    \"player_match_stats\" \"1\"\n  }}\n}}\n",
            self.token.as_str()
        );
        let temporary = cfg_dir.join(format!("{GSI_CONFIG_NAME}.tmp"));
        let backup = cfg_dir.join(format!("{GSI_CONFIG_NAME}.backup"));
        fs::write(&temporary, contents.as_bytes()).map_err(|error| {
            CommandErrorDto::at_path("gsi_config_write_failed", error.to_string(), &temporary)
        })?;
        if target.exists() {
            let _ = fs::remove_file(&backup);
            fs::rename(&target, &backup).map_err(|error| {
                CommandErrorDto::at_path("gsi_config_write_failed", error.to_string(), &target)
            })?;
        }
        if let Err(error) = fs::rename(&temporary, &target) {
            let _ = fs::rename(&backup, &target);
            let _ = fs::remove_file(&temporary);
            return Err(CommandErrorDto::at_path(
                "gsi_config_write_failed",
                error.to_string(),
                &target,
            ));
        }
        let _ = fs::remove_file(&backup);
        if let Ok(mut status) = self.status.lock() {
            status.config_path = Some(target.display().to_string());
        }
        let _ = self.activity_log.append(
            ActivityLogLevel::Info,
            "gsi",
            format!(
                "GSI configured at {}. Restart CS2 if it was already running.",
                target.display()
            ),
        );
        Ok(self.status())
    }

    pub fn status(&self) -> GsiStatusDto {
        let Ok(status) = self.status.lock() else {
            return GsiStatusDto {
                port: 32123,
                error: Some("GSI status lock is unavailable.".to_string()),
                ..GsiStatusDto::default()
            };
        };
        let now = now_ms();
        GsiStatusDto {
            listening: status.listening,
            configured: status.config_path.is_some(),
            connected: status
                .last_update_ms
                .is_some_and(|timestamp| now.saturating_sub(timestamp) <= 15_000),
            port: 32123,
            config_path: status.config_path.clone(),
            last_update_ms: status.last_update_ms,
            provider: status.provider.clone(),
            map: status.map.clone(),
            map_phase: status.map_phase.clone(),
            round: status.round,
            round_phase: status.round_phase.clone(),
            player_activity: status.player_activity.clone(),
            player_health: status.player_health,
            error: status.error.clone(),
        }
    }

    fn handle_connection(&self, mut stream: TcpStream) {
        let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
        let response = match read_http_json(&mut stream) {
            Ok(payload) => match self.accept_payload(&payload) {
                Ok(()) => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK",
                Err(_) => "HTTP/1.1 403 Forbidden\r\nContent-Length: 9\r\nConnection: close\r\n\r\nForbidden",
            },
            Err(_) => "HTTP/1.1 400 Bad Request\r\nContent-Length: 11\r\nConnection: close\r\n\r\nBad Request",
        };
        let _ = stream.write_all(response.as_bytes());
        let _ = stream.flush();
    }

    fn accept_payload(&self, payload: &Value) -> Result<(), ()> {
        if payload.pointer("/auth/token").and_then(Value::as_str) != Some(self.token.as_str()) {
            return Err(());
        }
        let provider = text_at(payload, "/provider/name");
        let map = text_at(payload, "/map/name");
        let map_phase = text_at(payload, "/map/phase");
        let round = payload.pointer("/map/round").and_then(Value::as_i64);
        let round_phase = text_at(payload, "/round/phase");
        let player_activity = text_at(payload, "/player/activity");
        let player_health = payload
            .pointer("/player/state/health")
            .and_then(Value::as_i64);
        let signature = format!(
            "{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}",
            provider, map, map_phase, round, round_phase, player_activity, player_health
        );
        let changed = if let Ok(mut status) = self.status.lock() {
            let changed = status.last_signature != signature;
            status.last_update_ms = Some(now_ms());
            status.provider = provider.clone();
            status.map = map.clone();
            status.map_phase = map_phase.clone();
            status.round = round;
            status.round_phase = round_phase.clone();
            status.player_activity = player_activity.clone();
            status.player_health = player_health;
            status.last_signature = signature;
            status.error = None;
            changed
        } else {
            false
        };
        if changed {
            let message = [
                map.map(|value| format!("map={value}")),
                round.map(|value| format!("round={value}")),
                round_phase.map(|value| format!("roundPhase={value}")),
                player_activity.map(|value| format!("activity={value}")),
                player_health.map(|value| format!("health={value}")),
            ]
            .into_iter()
            .flatten()
            .collect::<Vec<_>>()
            .join(" · ");
            let _ = self.activity_log.append(
                ActivityLogLevel::Info,
                "gsi",
                if message.is_empty() {
                    "GSI heartbeat".to_string()
                } else {
                    message
                },
            );
        }
        Ok(())
    }
}

#[tauri::command]
pub fn configure_gsi(cs2_path: String, state: State<'_, GsiState>) -> CommandResult<GsiStatusDto> {
    state.configure(cs2_path.trim())
}

#[tauri::command]
pub fn gsi_status(state: State<'_, GsiState>) -> GsiStatusDto {
    state.status()
}

fn text_at(payload: &Value, pointer: &str) -> Option<String> {
    payload
        .pointer(pointer)
        .and_then(Value::as_str)
        .map(str::to_string)
}

fn resolve_cs2_cfg_dir(cs2_path: &str) -> CommandResult<PathBuf> {
    let root = PathBuf::from(cs2_path.trim());
    if !root.is_absolute() {
        return Err(CommandErrorDto::at_path(
            "gsi_cs2_path_invalid",
            "The CS2 path must be absolute.",
            root,
        ));
    }
    let candidates = [
        root.join("game").join("csgo").join("cfg"),
        root.join("csgo").join("cfg"),
        root.join("cfg"),
    ];
    candidates
        .into_iter()
        .find(|candidate| candidate.is_dir())
        .ok_or_else(|| {
            CommandErrorDto::at_path(
                "gsi_cfg_not_found",
                "No CS2 cfg directory was found under this installation.",
                root,
            )
        })
}

fn read_http_json(stream: &mut TcpStream) -> Result<Value, ()> {
    let mut bytes = Vec::new();
    let mut buffer = [0_u8; 8192];
    let mut header_end = None;
    let mut content_length = None;
    loop {
        let read = stream.read(&mut buffer).map_err(|_| ())?;
        if read == 0 {
            break;
        }
        bytes.extend_from_slice(&buffer[..read]);
        if bytes.len() > MAX_GSI_REQUEST_BYTES {
            return Err(());
        }
        if header_end.is_none() {
            header_end = bytes
                .windows(4)
                .position(|window| window == b"\r\n\r\n")
                .map(|index| index + 4);
            if let Some(end) = header_end {
                let header = String::from_utf8_lossy(&bytes[..end]);
                content_length = header.lines().find_map(|line| {
                    let (name, value) = line.split_once(':')?;
                    name.eq_ignore_ascii_case("content-length")
                        .then(|| value.trim().parse::<usize>().ok())
                        .flatten()
                });
            }
        }
        if let (Some(end), Some(length)) = (header_end, content_length) {
            if bytes.len() >= end.saturating_add(length) {
                return serde_json::from_slice(&bytes[end..end + length]).map_err(|_| ());
            }
        }
    }
    Err(())
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_supported_cs2_cfg_layouts() {
        let root = std::env::temp_dir().join(format!("demotracer-gsi-layout-{}", now_ms()));
        let cfg = root.join("game").join("csgo").join("cfg");
        fs::create_dir_all(&cfg).unwrap();
        assert_eq!(resolve_cs2_cfg_dir(root.to_str().unwrap()).unwrap(), cfg);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rejects_relative_cs2_paths() {
        assert!(resolve_cs2_cfg_dir("game/csgo").is_err());
    }
}
