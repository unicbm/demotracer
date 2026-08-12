/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::http_client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Manager, State};
use uuid::Uuid;

const TELEMETRY_ENDPOINT: &str = "https://telemetry.detr.site/v1/events";
const TELEMETRY_SEED_FILE: &str = "telemetry-seed-v1";
const MAX_RESPONSE_BYTES: usize = 4 * 1024;
const TIMEOUT_MS: i32 = 5_000;

pub(crate) struct TelemetryState {
    enabled: AtomicBool,
}

impl Default for TelemetryState {
    fn default() -> Self {
        Self {
            enabled: AtomicBool::new(false),
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct TelemetryEventRequest {
    playback_version: Option<String>,
    kind: String,
    outcome: String,
    demo_source: Option<String>,
    error_code: Option<String>,
    rounds_bucket: Option<String>,
    duration_bucket: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct TelemetryEventPayload {
    schema_version: u8,
    event_id: String,
    daily_id: String,
    app_version: &'static str,
    playback_version: String,
    kind: String,
    outcome: String,
    demo_source: String,
    error_code: String,
    rounds_bucket: String,
    duration_bucket: String,
}

#[tauri::command]
pub(crate) fn configure_telemetry(enabled: bool, state: State<'_, TelemetryState>) {
    state.enabled.store(enabled, Ordering::Release);
}

#[tauri::command]
pub(crate) fn submit_telemetry(
    app: AppHandle,
    state: State<'_, TelemetryState>,
    event: TelemetryEventRequest,
) -> Result<(), String> {
    if !state.enabled.load(Ordering::Acquire) {
        return Ok(());
    }

    let event = normalize_event(event)?;
    let local_data = app
        .path()
        .app_local_data_dir()
        .map_err(|_| "telemetry local data directory is unavailable".to_string())?;
    tauri::async_runtime::spawn_blocking(move || {
        let Some(payload) = build_payload(&local_data, event) else {
            return;
        };
        let Ok(body) = serde_json::to_vec(&payload) else {
            return;
        };
        let _ =
            http_client::post_json_https(TELEMETRY_ENDPOINT, &body, MAX_RESPONSE_BYTES, TIMEOUT_MS);
    });
    Ok(())
}

fn normalize_event(mut event: TelemetryEventRequest) -> Result<TelemetryEventRequest, String> {
    if !matches!(event.kind.as_str(), "session" | "analysis" | "conversion") {
        return Err("unsupported telemetry event kind".to_string());
    }
    if !matches!(event.outcome.as_str(), "ping" | "success" | "failure") {
        return Err("unsupported telemetry outcome".to_string());
    }

    if event.kind == "session" {
        if event.outcome != "ping" {
            return Err("session telemetry must be a ping".to_string());
        }
        event.error_code = Some("-".to_string());
        event.demo_source = Some("unknown".to_string());
        event.rounds_bucket = Some("unknown".to_string());
        event.duration_bucket = Some("unknown".to_string());
    } else {
        if event.outcome == "ping" {
            return Err("task telemetry cannot be a ping".to_string());
        }
        event.error_code = Some(if event.outcome == "success" {
            "-".to_string()
        } else {
            categorized_error_code(event.error_code.as_deref()).to_string()
        });
        event.demo_source = Some(bounded_demo_source(event.demo_source.as_deref()).to_string());
        event.rounds_bucket =
            Some(bounded_rounds_bucket(event.rounds_bucket.as_deref()).to_string());
        event.duration_bucket =
            Some(bounded_duration_bucket(event.duration_bucket.as_deref()).to_string());
    }
    event.playback_version = Some(bounded_version(event.playback_version.as_deref()));
    Ok(event)
}

fn build_payload(local_data: &Path, event: TelemetryEventRequest) -> Option<TelemetryEventPayload> {
    let day_number = SystemTime::now().duration_since(UNIX_EPOCH).ok()?.as_secs() / (24 * 60 * 60);
    let seed = load_or_create_seed(local_data)?;
    let mut hash = Sha256::new();
    hash.update(b"demotracer-telemetry-v1\0");
    hash.update(seed.as_bytes());
    hash.update(day_number.to_le_bytes());
    let daily_id = format!("{:x}", hash.finalize());

    Some(TelemetryEventPayload {
        schema_version: 1,
        event_id: Uuid::new_v4().to_string(),
        daily_id,
        app_version: env!("CARGO_PKG_VERSION"),
        playback_version: event.playback_version.unwrap_or_else(|| "-".to_string()),
        kind: event.kind,
        outcome: event.outcome,
        demo_source: event.demo_source.unwrap_or_else(|| "unknown".to_string()),
        error_code: event.error_code.unwrap_or_else(|| "-".to_string()),
        rounds_bucket: event.rounds_bucket.unwrap_or_else(|| "unknown".to_string()),
        duration_bucket: event
            .duration_bucket
            .unwrap_or_else(|| "unknown".to_string()),
    })
}

fn load_or_create_seed(local_data: &Path) -> Option<Uuid> {
    fs::create_dir_all(local_data).ok()?;
    let path = local_data.join(TELEMETRY_SEED_FILE);
    if let Ok(saved) = fs::read_to_string(&path) {
        if let Ok(seed) = Uuid::parse_str(saved.trim()) {
            return Some(seed);
        }
    }

    let seed = Uuid::new_v4();
    match OpenOptions::new().write(true).create_new(true).open(&path) {
        Ok(mut file) => {
            if file.write_all(seed.to_string().as_bytes()).is_ok() {
                Some(seed)
            } else {
                None
            }
        }
        Err(_) => fs::read_to_string(&path)
            .ok()
            .and_then(|saved| Uuid::parse_str(saved.trim()).ok()),
    }
}

fn bounded_version(value: Option<&str>) -> String {
    let Some(value) = value else {
        return "-".to_string();
    };
    if !value.is_empty()
        && value.len() <= 32
        && value.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_alphanumeric() || (index > 0 && b".+-".contains(&byte))
        })
    {
        value.to_string()
    } else {
        "-".to_string()
    }
}

fn categorized_error_code(value: Option<&str>) -> &'static str {
    let code = value.unwrap_or_default().to_ascii_lowercase();
    if code.contains("cancel") || code.contains("stopped") {
        "cancelled"
    } else if code.contains("output_exists")
        || code.contains("already_exists")
        || code.contains("conflict")
        || code.contains("overwrite")
    {
        "output_conflict"
    } else if code.contains("batch") {
        "batch_failed"
    } else if code.contains("parse") || code.contains("decode") {
        "parse_failed"
    } else if code.contains("validation") || code.contains("invalid") {
        "validation_failed"
    } else if code.contains("write")
        || code.contains("output")
        || code.contains("archive")
        || code.contains("manifest")
    {
        "output_failed"
    } else if code.contains("playback") || code.contains("plugin") || code.contains("server_config")
    {
        "playback_failed"
    } else if code.contains("network")
        || code.contains("http")
        || code.contains("download")
        || code.contains("update")
    {
        "network_failed"
    } else if code.contains("environment") || code.contains("steam") || code.contains("cs2") {
        "environment_failed"
    } else if code.contains("internal") || code.contains("panic") {
        "internal_error"
    } else if code.contains("demo")
        || code.contains("source")
        || code.contains("input")
        || code.contains("path")
        || code.contains("file")
    {
        "input_unavailable"
    } else {
        "unknown"
    }
}

fn bounded_demo_source(value: Option<&str>) -> &str {
    match value {
        Some(value)
            if matches!(
                value,
                "5e" | "perfect-world"
                    | "faceit"
                    | "valve-premier"
                    | "matchmaking"
                    | "pracc"
                    | "popflash"
                    | "esportal"
                    | "gamers-club"
                    | "fastcup"
                    | "renown"
                    | "cevo"
                    | "challengermode"
                    | "esea"
                    | "starladder"
                    | "flashpoint"
                    | "blast"
                    | "pgl"
                    | "esl"
                    | "matchzy"
                    | "ebot"
                    | "get5"
                    | "other"
                    | "unknown"
            ) =>
        {
            value
        }
        _ => "unknown",
    }
}

fn bounded_rounds_bucket(value: Option<&str>) -> &str {
    match value {
        Some(value) if matches!(value, "0" | "1-4" | "5-12" | "13-24" | "25+" | "unknown") => value,
        _ => "unknown",
    }
}

fn bounded_duration_bucket(value: Option<&str>) -> &str {
    match value {
        Some(value)
            if matches!(
                value,
                "<10s" | "10-29s" | "30-59s" | "1-2m" | "3-9m" | "10m+" | "unknown"
            ) =>
        {
            value
        }
        _ => "unknown",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn contract_endpoint_is_https_and_fixed() {
        assert_eq!(TELEMETRY_ENDPOINT, "https://telemetry.detr.site/v1/events");
    }

    #[test]
    fn error_codes_are_reduced_to_finite_categories() {
        assert_eq!(
            categorized_error_code(Some("failed at C:\\private\\demo.dem")),
            "input_unavailable"
        );
        assert_eq!(
            categorized_error_code(Some("demo_parse_failed")),
            "parse_failed"
        );
        assert_eq!(
            categorized_error_code(Some("output_exists")),
            "output_conflict"
        );
        assert_eq!(categorized_error_code(Some("some_future_error")), "unknown");
    }

    #[test]
    fn only_contract_buckets_are_preserved() {
        assert_eq!(bounded_rounds_bucket(Some("13-24")), "13-24");
        assert_eq!(bounded_rounds_bucket(Some("23")), "unknown");
        assert_eq!(bounded_duration_bucket(Some("1-2m")), "1-2m");
        assert_eq!(bounded_duration_bucket(Some("125 seconds")), "unknown");
    }

    #[test]
    fn only_fixed_demo_source_categories_are_preserved() {
        assert_eq!(bounded_demo_source(Some("5e")), "5e");
        assert_eq!(bounded_demo_source(Some("private.example.com")), "unknown");
    }
}
