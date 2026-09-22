/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::diagnostics::resolve_install_paths;
use crate::{CommandErrorDto, CommandResult};
use cs2_demotracer::demo_id::sha256_hex;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeSet;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

const CONFIG_FILE_NAME: &str = "demotracer.config.json";
const EXAMPLE_CONFIG_FILE_NAME: &str = "demotracer.config.example.json";
const CONFIG_RELATIVE_DIRECTORY: &str = "addons/counterstrikesharp/plugins/DemoTracer";
const MAX_CONFIG_BYTES: usize = 512 * 1024;
const HANDOFF_THREAT_360_MIN_RANGE: f64 = 150.0;
const HANDOFF_THREAT_360_MAX_RANGE: f64 = 800.0;

const BUILTIN_DEFAULT_CONFIG: &str = r#"{
  "identity": "steam",
  "allow_partial": true,
  "playoff": false,
  "chat_auto": true,
  "round_banner": true,
  "handoff": {
    "mode": "death_contact_c4",
    "scope": "slot",
    "threat_360": true,
    "threat_360_range": 420,
    "threat_360_los": true,
    "viewmodel_continuity": "round"
  },
  "fidelity": {
    "preset": "default",
    "crosshair": true,
    "balance": false
  },
  "match": {
    "preset": "off"
  },
  "cosmetics": {
    "preset": "off",
    "agents": false,
    "preserve_native": false
  }
}
"#;

static NEXT_CONFIG_WRITE_NONCE: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) enum ServerConfigSourceDto {
    Installed,
    Example,
    BuiltInDefault,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ServerConfigIssueDto {
    pub path: String,
    pub code: String,
    pub message: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ServerConfigValidationDto {
    pub valid: bool,
    pub errors: Vec<ServerConfigIssueDto>,
    pub warnings: Vec<ServerConfigIssueDto>,
    pub unknown_paths: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ServerConfigDocumentDto {
    pub config_path: String,
    pub source: ServerConfigSourceDto,
    pub json: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<String>,
    pub validation: ServerConfigValidationDto,
    pub reload_command: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ValidateServerConfigRequestDto {
    pub json: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SaveServerConfigRequestDto {
    pub cs2_path: String,
    /// The complete JSON/JSONC editor document, including unknown fields.
    pub json: String,
    /// The fingerprint returned by `load_server_config`. `None` means that the
    /// caller observed no installed config file.
    pub expected_fingerprint: Option<String>,
}

#[derive(Debug)]
struct ConfigSource {
    source: ServerConfigSourceDto,
    raw: Vec<u8>,
    fingerprint: Option<String>,
}

#[tauri::command]
pub(crate) fn load_server_config(cs2_path: String) -> CommandResult<ServerConfigDocumentDto> {
    load_server_config_for(&cs2_path)
}

#[tauri::command]
pub(crate) fn validate_server_config(
    request: ValidateServerConfigRequestDto,
) -> ServerConfigValidationDto {
    validate_config_text(&request.json)
}

#[tauri::command]
pub(crate) fn save_server_config(
    request: SaveServerConfigRequestDto,
) -> CommandResult<ServerConfigDocumentDto> {
    save_server_config_for(&request)
}

fn load_server_config_for(cs2_path: &str) -> CommandResult<ServerConfigDocumentDto> {
    let paths = resolve_install_paths(Path::new(cs2_path.trim()))?;
    let config_path = config_path(&paths.game_csgo);
    let source = read_config_source(&paths.game_csgo, &config_path)?;
    let json = String::from_utf8(source.raw).map_err(|error| {
        CommandErrorDto::at_path(
            "server_config_not_utf8",
            format!("The DemoTracer config is not UTF-8: {error}"),
            &config_path,
        )
    })?;
    let (json, validation) = match parse_config_text(&json) {
        Ok(value) => {
            let validation = validate_config_value(&value);
            let normalized =
                serde_json::to_string_pretty(&value).expect("parsed JSON values can be serialized");
            (format!("{normalized}\n"), validation)
        }
        // Preserve invalid source text so the editor can show and repair it.
        Err(message) => (json, invalid_json_validation(message)),
    };

    Ok(ServerConfigDocumentDto {
        config_path: config_path.display().to_string(),
        source: source.source,
        json,
        fingerprint: source.fingerprint,
        validation,
        reload_command: "dtr_config_reload".to_string(),
    })
}

fn save_server_config_for(
    request: &SaveServerConfigRequestDto,
) -> CommandResult<ServerConfigDocumentDto> {
    let paths = resolve_install_paths(Path::new(request.cs2_path.trim()))?;
    let config_path = config_path(&paths.game_csgo);
    let config_directory = config_path
        .parent()
        .expect("DemoTracer config always has a parent directory");
    require_normal_directory_below(&paths.game_csgo, config_directory).map_err(|message| {
        CommandErrorDto::at_path(
            "demotracer_plugin_directory_unavailable",
            format!(
                "The DemoTracer plugin directory must already exist as a normal local directory: {message}"
            ),
            config_directory,
        )
    })?;

    let current_fingerprint = installed_config_fingerprint(&paths.game_csgo, &config_path)?;
    if current_fingerprint != normalized_expected_fingerprint(&request.expected_fingerprint) {
        return Err(CommandErrorDto::at_path(
            "server_config_changed",
            "The DemoTracer config changed after it was loaded. Reload it before saving so external edits are not overwritten.",
            &config_path,
        ));
    }

    let mut value = parse_config_text(&request.json).map_err(|message| {
        CommandErrorDto::at_path("server_config_invalid_json", message, &config_path)
    })?;
    let validation = validate_config_value(&value);
    if !validation.valid {
        return Err(CommandErrorDto::at_path(
            "server_config_invalid",
            summarize_validation_errors(&validation),
            &config_path,
        ));
    }
    canonicalize_known_field_names(&mut value);

    let mut bytes = serde_json::to_vec_pretty(&value).map_err(|error| {
        CommandErrorDto::at_path(
            "server_config_serialize_failed",
            error.to_string(),
            &config_path,
        )
    })?;
    bytes.push(b'\n');
    if bytes.len() > MAX_CONFIG_BYTES {
        return Err(CommandErrorDto::at_path(
            "server_config_too_large",
            format!("The serialized config exceeds the {MAX_CONFIG_BYTES}-byte safety limit."),
            &config_path,
        ));
    }
    atomic_write_config(&paths.game_csgo, &config_path, &bytes)?;

    load_server_config_for(request.cs2_path.trim())
}

fn config_path(game_csgo: &Path) -> PathBuf {
    CONFIG_RELATIVE_DIRECTORY
        .split('/')
        .fold(game_csgo.to_path_buf(), |path, segment| path.join(segment))
        .join(CONFIG_FILE_NAME)
}

fn example_config_path(game_csgo: &Path) -> PathBuf {
    CONFIG_RELATIVE_DIRECTORY
        .split('/')
        .fold(game_csgo.to_path_buf(), |path, segment| path.join(segment))
        .join(EXAMPLE_CONFIG_FILE_NAME)
}

fn read_config_source(game_csgo: &Path, installed_path: &Path) -> CommandResult<ConfigSource> {
    if path_entry_exists(installed_path)? {
        let raw = read_bounded_normal_file(game_csgo, installed_path)?;
        return Ok(ConfigSource {
            fingerprint: Some(sha256_hex(&raw)),
            source: ServerConfigSourceDto::Installed,
            raw,
        });
    }

    let example_path = example_config_path(game_csgo);
    if path_entry_exists(&example_path)? {
        return Ok(ConfigSource {
            raw: read_bounded_normal_file(game_csgo, &example_path)?,
            source: ServerConfigSourceDto::Example,
            fingerprint: None,
        });
    }

    Ok(ConfigSource {
        source: ServerConfigSourceDto::BuiltInDefault,
        raw: BUILTIN_DEFAULT_CONFIG.as_bytes().to_vec(),
        fingerprint: None,
    })
}

fn installed_config_fingerprint(
    game_csgo: &Path,
    installed_path: &Path,
) -> CommandResult<Option<String>> {
    if !path_entry_exists(installed_path)? {
        return Ok(None);
    }
    read_bounded_normal_file(game_csgo, installed_path).map(|bytes| Some(sha256_hex(&bytes)))
}

fn normalized_expected_fingerprint(value: &Option<String>) -> Option<String> {
    value
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_ascii_lowercase)
}

fn path_entry_exists(path: &Path) -> CommandResult<bool> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(true),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(CommandErrorDto::at_path(
            "server_config_unavailable",
            error.to_string(),
            path,
        )),
    }
}

fn read_bounded_normal_file(root: &Path, path: &Path) -> CommandResult<Vec<u8>> {
    let metadata = metadata_below_without_reparse(root, path)
        .map_err(|message| CommandErrorDto::at_path("server_config_unavailable", message, path))?;
    if !metadata.is_file() {
        return Err(CommandErrorDto::at_path(
            "server_config_not_normal_file",
            "The DemoTracer config path is not a normal file.",
            path,
        ));
    }
    if metadata.len() > MAX_CONFIG_BYTES as u64 {
        return Err(CommandErrorDto::at_path(
            "server_config_too_large",
            format!("The config exceeds the {MAX_CONFIG_BYTES}-byte safety limit."),
            path,
        ));
    }
    let bytes = fs::read(path).map_err(|error| {
        CommandErrorDto::at_path("server_config_read_failed", error.to_string(), path)
    })?;
    if bytes.len() > MAX_CONFIG_BYTES {
        return Err(CommandErrorDto::at_path(
            "server_config_too_large",
            format!("The config exceeds the {MAX_CONFIG_BYTES}-byte safety limit."),
            path,
        ));
    }
    Ok(bytes)
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

fn require_normal_directory_below(root: &Path, path: &Path) -> Result<(), String> {
    let metadata = metadata_below_without_reparse(root, path)?;
    if !metadata.is_dir() {
        return Err("path is not a directory".to_string());
    }
    Ok(())
}

fn atomic_write_config(root: &Path, target: &Path, bytes: &[u8]) -> CommandResult<()> {
    let directory = target
        .parent()
        .expect("DemoTracer config always has a parent directory");
    require_normal_directory_below(root, directory).map_err(|message| {
        CommandErrorDto::at_path("server_config_write_unsafe", message, directory)
    })?;
    if path_entry_exists(target)? {
        let metadata = metadata_below_without_reparse(root, target).map_err(|message| {
            CommandErrorDto::at_path("server_config_write_unsafe", message, target)
        })?;
        if !metadata.is_file() {
            return Err(CommandErrorDto::at_path(
                "server_config_write_unsafe",
                "The destination is not a normal file.",
                target,
            ));
        }
    }

    let nonce = NEXT_CONFIG_WRITE_NONCE.fetch_add(1, Ordering::Relaxed);
    let temp_path = directory.join(format!(
        ".{CONFIG_FILE_NAME}.{}.{}.tmp",
        std::process::id(),
        nonce
    ));
    let write_result = (|| -> io::Result<()> {
        let mut file = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&temp_path)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        atomic_replace(&temp_path, target)
    })();
    if let Err(error) = write_result {
        let _ = fs::remove_file(&temp_path);
        return Err(CommandErrorDto::at_path(
            "server_config_write_failed",
            error.to_string(),
            target,
        ));
    }
    Ok(())
}

#[cfg(not(windows))]
pub(crate) fn atomic_replace(source: &Path, target: &Path) -> io::Result<()> {
    fs::rename(source, target)
}

#[cfg(windows)]
pub(crate) fn atomic_replace(source: &Path, target: &Path) -> io::Result<()> {
    if !target.exists() {
        return fs::rename(source, target);
    }

    use std::os::windows::ffi::OsStrExt;

    const MOVEFILE_REPLACE_EXISTING: u32 = 0x1;
    const MOVEFILE_WRITE_THROUGH: u32 = 0x8;

    #[link(name = "Kernel32")]
    extern "system" {
        fn MoveFileExW(
            existing_file_name: *const u16,
            new_file_name: *const u16,
            flags: u32,
        ) -> i32;
    }

    let target_wide = target
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let source_wide = source
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // MoveFileExW replaces an existing destination on the same volume in one
    // rename operation. The temporary file intentionally lives beside it.
    let replaced = unsafe {
        MoveFileExW(
            source_wide.as_ptr(),
            target_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if replaced == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn parse_config_text(text: &str) -> Result<Value, String> {
    if text.len() > MAX_CONFIG_BYTES {
        return Err(format!(
            "The config exceeds the {MAX_CONFIG_BYTES}-byte safety limit."
        ));
    }
    let without_comments = strip_json_comments(text)?;
    let normalized_syntax = strip_trailing_commas(&without_comments);
    let value = serde_json::from_str::<Value>(&normalized_syntax).map_err(|error| {
        format!(
            "Invalid JSON at line {}, column {}: {error}",
            error.line(),
            error.column()
        )
    })?;
    if !value.is_object() {
        return Err("The DemoTracer config root must be a JSON object.".to_string());
    }
    Ok(value)
}

fn strip_json_comments(text: &str) -> Result<String, String> {
    let characters = text.chars().collect::<Vec<_>>();
    let mut output = String::with_capacity(text.len());
    let mut index = 0;
    let mut in_string = false;
    let mut escaped = false;

    while index < characters.len() {
        let character = characters[index];
        if in_string {
            output.push(character);
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                in_string = false;
            }
            index += 1;
            continue;
        }

        if character == '"' {
            in_string = true;
            output.push(character);
            index += 1;
            continue;
        }
        if character == '/' && characters.get(index + 1) == Some(&'/') {
            output.push(' ');
            output.push(' ');
            index += 2;
            while index < characters.len() && characters[index] != '\n' {
                output.push(' ');
                index += 1;
            }
            continue;
        }
        if character == '/' && characters.get(index + 1) == Some(&'*') {
            output.push(' ');
            output.push(' ');
            index += 2;
            let mut closed = false;
            while index < characters.len() {
                if characters[index] == '*' && characters.get(index + 1) == Some(&'/') {
                    output.push(' ');
                    output.push(' ');
                    index += 2;
                    closed = true;
                    break;
                }
                output.push(if characters[index] == '\n' { '\n' } else { ' ' });
                index += 1;
            }
            if !closed {
                return Err("The config contains an unterminated block comment.".to_string());
            }
            continue;
        }

        output.push(character);
        index += 1;
    }
    Ok(output)
}

fn strip_trailing_commas(text: &str) -> String {
    let characters = text.chars().collect::<Vec<_>>();
    let mut output = String::with_capacity(text.len());
    let mut index = 0;
    let mut in_string = false;
    let mut escaped = false;
    while index < characters.len() {
        let character = characters[index];
        if in_string {
            output.push(character);
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                in_string = false;
            }
            index += 1;
            continue;
        }
        if character == '"' {
            in_string = true;
            output.push(character);
            index += 1;
            continue;
        }
        if character == ',' {
            let mut next = index + 1;
            while next < characters.len() && characters[next].is_whitespace() {
                next += 1;
            }
            if matches!(characters.get(next), Some('}') | Some(']')) {
                index += 1;
                continue;
            }
        }
        output.push(character);
        index += 1;
    }
    output
}

fn validate_config_text(text: &str) -> ServerConfigValidationDto {
    match parse_config_text(text) {
        Ok(value) => validate_config_value(&value),
        Err(message) => invalid_json_validation(message),
    }
}

fn invalid_json_validation(message: String) -> ServerConfigValidationDto {
    ServerConfigValidationDto {
        valid: false,
        errors: vec![issue("$", "invalid_json", message)],
        warnings: Vec::new(),
        unknown_paths: Vec::new(),
    }
}

fn validate_config_value(value: &Value) -> ServerConfigValidationDto {
    let Some(root) = value.as_object() else {
        return ServerConfigValidationDto {
            valid: false,
            errors: vec![issue(
                "$",
                "root_not_object",
                "The DemoTracer config root must be a JSON object.",
            )],
            warnings: Vec::new(),
            unknown_paths: Vec::new(),
        };
    };

    let mut errors = Vec::new();
    let mut warnings = Vec::new();
    let mut unknown_paths = BTreeSet::new();
    check_duplicate_case_insensitive_keys("$", root, &mut warnings);
    collect_unknown_keys(
        "$",
        root,
        &[
            "identity",
            "allow_partial",
            "playoff",
            "chat_auto",
            "round_banner",
            "handoff",
            "align",
            "fidelity",
            "match",
            "cosmetics",
        ],
        &mut unknown_paths,
    );

    validate_string_enum(
        root,
        "identity",
        "$.identity",
        &[
            "off",
            "0",
            "false",
            "name",
            "steam",
            "sid",
            "steamid",
            "1",
            "on",
            "true",
            "avatar",
            "avatars",
            "event_avatar",
            "event-avatar",
            "full",
        ],
        &mut errors,
        &mut warnings,
    );
    for name in ["allow_partial", "playoff", "chat_auto", "round_banner"] {
        validate_bool(root, name, &format!("$.{name}"), &mut errors);
    }

    validate_handoff(
        object_section(root, "handoff", "$.handoff", &mut errors),
        &mut errors,
        &mut warnings,
        &mut unknown_paths,
    );
    validate_align(
        object_section(root, "align", "$.align", &mut errors),
        &mut errors,
        &mut warnings,
        &mut unknown_paths,
    );
    validate_fidelity(
        object_section(root, "fidelity", "$.fidelity", &mut errors),
        &mut errors,
        &mut warnings,
        &mut unknown_paths,
    );
    validate_match(
        object_section(root, "match", "$.match", &mut errors),
        &mut errors,
        &mut warnings,
        &mut unknown_paths,
    );
    validate_cosmetics(
        object_section(root, "cosmetics", "$.cosmetics", &mut errors),
        &mut errors,
        &mut warnings,
        &mut unknown_paths,
    );

    let has_legacy_align =
        get_case_insensitive(root, "align").is_some_and(|value| !value.is_null());
    let has_new_sections = ["fidelity", "match", "cosmetics"]
        .iter()
        .any(|name| get_case_insensitive(root, name).is_some_and(|value| !value.is_null()));
    if has_legacy_align && has_new_sections {
        warnings.push(issue(
            "$",
            "legacy_align_overridden",
            "The config contains legacy align and new fidelity/match/cosmetics sections. The CSS plugin lets the new sections override matching legacy fields.",
        ));
    }

    ServerConfigValidationDto {
        valid: errors.is_empty(),
        errors,
        warnings,
        unknown_paths: unknown_paths.into_iter().collect(),
    }
}

fn validate_handoff(
    section: Option<&Map<String, Value>>,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
    unknown: &mut BTreeSet<String>,
) {
    let Some(section) = section else { return };
    check_duplicate_case_insensitive_keys("$.handoff", section, warnings);
    collect_unknown_keys(
        "$.handoff",
        section,
        &[
            "mode",
            "scope",
            "threat_360",
            "threat_360_range",
            "threat_360_los",
            "viewmodel_continuity",
        ],
        unknown,
    );
    validate_string_enum_untrimmed(
        section,
        "mode",
        "$.handoff.mode",
        &[
            "0",
            "off",
            "none",
            "death",
            "kill",
            "contact",
            "see",
            "sight",
            "death_or_contact",
            "contact_or_death",
            "1",
            "auto",
            "default",
            "death_contact_c4",
            "death_contact_c4planted",
            "death_contact_c4_planted",
            "death_or_contact_or_c4",
            "death_or_contact_or_bomb",
            "death_contact_bomb",
        ],
        errors,
        warnings,
    );
    validate_string_enum_untrimmed(
        section,
        "scope",
        "$.handoff.scope",
        &["slot", "all"],
        errors,
        warnings,
    );
    validate_bool(section, "threat_360", "$.handoff.threat_360", errors);
    validate_bool(
        section,
        "threat_360_los",
        "$.handoff.threat_360_los",
        errors,
    );
    validate_string_enum(
        section,
        "viewmodel_continuity",
        "$.handoff.viewmodel_continuity",
        &[
            "release",
            "off",
            "none",
            "immediate",
            "round",
            "retain",
            "retain_round",
            "retain-round",
        ],
        errors,
        warnings,
    );
    if let Some(value) = get_case_insensitive(section, "threat_360_range") {
        if value.is_null() {
            return;
        }
        match value.as_f64() {
            Some(value)
                if !(HANDOFF_THREAT_360_MIN_RANGE..=HANDOFF_THREAT_360_MAX_RANGE)
                    .contains(&value) =>
            {
                warnings.push(issue(
                    "$.handoff.threat_360_range",
                    "value_clamped",
                    format!(
                        "The CSS plugin clamps this value to {HANDOFF_THREAT_360_MIN_RANGE:.0}-{HANDOFF_THREAT_360_MAX_RANGE:.0}."
                    ),
                ));
            }
            Some(_) => {}
            None => errors.push(type_issue("$.handoff.threat_360_range", "number or null")),
        }
    }
}

fn validate_align(
    section: Option<&Map<String, Value>>,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
    unknown: &mut BTreeSet<String>,
) {
    let Some(section) = section else { return };
    check_duplicate_case_insensitive_keys("$.align", section, warnings);
    let fields = [
        "weapons",
        "projectiles",
        "crosshair",
        "left_hand_desired",
        "cosmetics",
        "stickers",
        "charms",
        "scoreboard",
    ];
    collect_unknown_keys("$.align", section, &fields, unknown);
    for field in fields {
        validate_bool(section, field, &format!("$.align.{field}"), errors);
    }
}

fn validate_fidelity(
    section: Option<&Map<String, Value>>,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
    unknown: &mut BTreeSet<String>,
) {
    let Some(section) = section else { return };
    check_duplicate_case_insensitive_keys("$.fidelity", section, warnings);
    let fields = [
        "preset",
        "weapons",
        "projectiles",
        "crosshair",
        "left_hand_desired",
        "balance",
    ];
    collect_unknown_keys("$.fidelity", section, &fields, unknown);
    validate_string_enum(
        section,
        "preset",
        "$.fidelity.preset",
        &[
            "default",
            "full",
            "handoff_safe",
            "handoff-safe",
            "handoff",
            "off",
            "none",
        ],
        errors,
        warnings,
    );
    for field in [
        "weapons",
        "projectiles",
        "crosshair",
        "left_hand_desired",
        "balance",
    ] {
        validate_bool(section, field, &format!("$.fidelity.{field}"), errors);
    }
}

fn validate_match(
    section: Option<&Map<String, Value>>,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
    unknown: &mut BTreeSet<String>,
) {
    let Some(section) = section else { return };
    check_duplicate_case_insensitive_keys("$.match", section, warnings);
    collect_unknown_keys("$.match", section, &["preset", "scoreboard"], unknown);
    validate_string_enum(
        section,
        "preset",
        "$.match.preset",
        &["off", "none", "scoreboard", "full", "all"],
        errors,
        warnings,
    );
    validate_bool(section, "scoreboard", "$.match.scoreboard", errors);
}

fn validate_cosmetics(
    section: Option<&Map<String, Value>>,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
    unknown: &mut BTreeSet<String>,
) {
    let Some(section) = section else { return };
    check_duplicate_case_insensitive_keys("$.cosmetics", section, warnings);
    let fields = [
        "preset",
        "weapons",
        "knives",
        "gloves",
        "names",
        "agents",
        "stickers",
        "charms",
        "preserve_native",
    ];
    collect_unknown_keys("$.cosmetics", section, &fields, unknown);
    validate_string_enum(
        section,
        "preset",
        "$.cosmetics.preset",
        &["off", "none", "weapons", "weapon", "basic", "full", "all"],
        errors,
        warnings,
    );
    for field in fields.into_iter().filter(|field| *field != "preset") {
        validate_bool(section, field, &format!("$.cosmetics.{field}"), errors);
    }
}

fn object_section<'a>(
    root: &'a Map<String, Value>,
    name: &str,
    path: &str,
    errors: &mut Vec<ServerConfigIssueDto>,
) -> Option<&'a Map<String, Value>> {
    let value = get_case_insensitive(root, name)?;
    if value.is_null() {
        return None;
    }
    match value.as_object() {
        Some(section) => Some(section),
        None => {
            errors.push(type_issue(path, "object or null"));
            None
        }
    }
}

fn validate_bool(
    object: &Map<String, Value>,
    name: &str,
    path: &str,
    errors: &mut Vec<ServerConfigIssueDto>,
) {
    if let Some(value) = get_case_insensitive(object, name) {
        if !value.is_null() && !value.is_boolean() {
            errors.push(type_issue(path, "boolean or null"));
        }
    }
}

fn validate_string_enum(
    object: &Map<String, Value>,
    name: &str,
    path: &str,
    allowed: &[&str],
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
) {
    validate_string_enum_inner(object, name, path, allowed, true, errors, warnings);
}

fn validate_string_enum_untrimmed(
    object: &Map<String, Value>,
    name: &str,
    path: &str,
    allowed: &[&str],
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
) {
    validate_string_enum_inner(object, name, path, allowed, false, errors, warnings);
}

fn validate_string_enum_inner(
    object: &Map<String, Value>,
    name: &str,
    path: &str,
    allowed: &[&str],
    trim: bool,
    errors: &mut Vec<ServerConfigIssueDto>,
    warnings: &mut Vec<ServerConfigIssueDto>,
) {
    let Some(value) = get_case_insensitive(object, name) else {
        return;
    };
    if value.is_null() {
        return;
    }
    let Some(value) = value.as_str() else {
        errors.push(type_issue(path, "string or null"));
        return;
    };
    let normalized = if trim { value.trim() } else { value }.to_ascii_lowercase();
    if !normalized.is_empty() && !allowed.iter().any(|allowed| *allowed == normalized) {
        warnings.push(issue(
            path,
            "value_ignored",
            format!("The CSS plugin does not recognize \"{value}\" here and will ignore it."),
        ));
    }
}

fn collect_unknown_keys(
    path: &str,
    object: &Map<String, Value>,
    known: &[&str],
    unknown: &mut BTreeSet<String>,
) {
    for key in object.keys() {
        if !known.iter().any(|known| key.eq_ignore_ascii_case(known)) {
            unknown.insert(format!("{path}.{key}"));
        }
    }
}

fn check_duplicate_case_insensitive_keys(
    path: &str,
    object: &Map<String, Value>,
    warnings: &mut Vec<ServerConfigIssueDto>,
) {
    let mut names = BTreeSet::new();
    for key in object.keys() {
        let normalized = key.to_ascii_lowercase();
        if !names.insert(normalized) {
            warnings.push(issue(
                format!("{path}.{key}"),
                "case_insensitive_duplicate",
                "The CSS plugin treats property names case-insensitively; keep only one casing of this property.",
            ));
        }
    }
}

fn get_case_insensitive<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    object
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, value)| value)
}

fn issue(
    path: impl Into<String>,
    code: impl Into<String>,
    message: impl Into<String>,
) -> ServerConfigIssueDto {
    ServerConfigIssueDto {
        path: path.into(),
        code: code.into(),
        message: message.into(),
    }
}

fn type_issue(path: &str, expected: &str) -> ServerConfigIssueDto {
    issue(
        path,
        "invalid_type",
        format!("Expected {expected}; this type would make the CSS plugin reject the config file."),
    )
}

fn summarize_validation_errors(validation: &ServerConfigValidationDto) -> String {
    validation
        .errors
        .iter()
        .take(4)
        .map(|error| format!("{}: {}", error.path, error.message))
        .collect::<Vec<_>>()
        .join("; ")
}

fn canonicalize_known_field_names(value: &mut Value) {
    let Some(root) = value.as_object_mut() else {
        return;
    };
    canonicalize_object_keys(
        root,
        &[
            "identity",
            "allow_partial",
            "playoff",
            "chat_auto",
            "round_banner",
            "handoff",
            "align",
            "fidelity",
            "match",
            "cosmetics",
        ],
    );
    canonicalize_section(
        root,
        "handoff",
        &[
            "mode",
            "scope",
            "threat_360",
            "threat_360_range",
            "threat_360_los",
            "viewmodel_continuity",
        ],
    );
    canonicalize_section(
        root,
        "align",
        &[
            "weapons",
            "projectiles",
            "crosshair",
            "left_hand_desired",
            "cosmetics",
            "stickers",
            "charms",
            "scoreboard",
        ],
    );
    canonicalize_section(
        root,
        "fidelity",
        &[
            "preset",
            "weapons",
            "projectiles",
            "crosshair",
            "left_hand_desired",
            "balance",
        ],
    );
    canonicalize_section(root, "match", &["preset", "scoreboard"]);
    canonicalize_section(
        root,
        "cosmetics",
        &[
            "preset",
            "weapons",
            "knives",
            "gloves",
            "names",
            "agents",
            "stickers",
            "charms",
            "preserve_native",
        ],
    );
}

fn canonicalize_section(root: &mut Map<String, Value>, name: &str, known: &[&str]) {
    if let Some(Value::Object(section)) = root.get_mut(name) {
        canonicalize_object_keys(section, known);
    }
}

fn canonicalize_object_keys(object: &mut Map<String, Value>, known: &[&str]) {
    for canonical in known {
        let variants = object
            .keys()
            .filter(|key| key.eq_ignore_ascii_case(canonical) && key.as_str() != *canonical)
            .cloned()
            .collect::<Vec<_>>();
        if object.contains_key(*canonical) {
            for variant in variants {
                object.remove(&variant);
            }
        } else if let Some(first_variant) = variants.first() {
            let selected_value = object.remove(first_variant);
            for variant in variants.iter().skip(1) {
                object.remove(variant);
            }
            if let Some(value) = selected_value {
                object.insert((*canonical).to_string(), value);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    struct TempTree(PathBuf);

    impl TempTree {
        fn new(label: &str) -> Self {
            let unique = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock")
                .as_nanos();
            let root = std::env::temp_dir().join(format!(
                "cs2-demotracer-server-config-{label}-{}-{unique}",
                std::process::id()
            ));
            let plugin = root
                .join("game/csgo")
                .join(CONFIG_RELATIVE_DIRECTORY.replace('/', std::path::MAIN_SEPARATOR_STR));
            fs::create_dir_all(&plugin).expect("create plugin directory");
            fs::write(root.join("game/csgo/gameinfo.gi"), b"GameInfo\n").expect("write gameinfo");
            Self(root)
        }

        fn root(&self) -> &Path {
            &self.0
        }

        fn game_csgo(&self) -> PathBuf {
            self.0.join("game/csgo")
        }

        fn config(&self) -> PathBuf {
            config_path(&self.game_csgo())
        }

        fn example(&self) -> PathBuf {
            example_config_path(&self.game_csgo())
        }
    }

    impl Drop for TempTree {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn missing_installed_config_uses_jsonc_example() {
        let tree = TempTree::new("example");
        fs::write(
            tree.example(),
            br#"{
              // CSS accepts comments and trailing commas.
              "identity": "steam",
              "future_option": { "enabled": true, },
            }"#,
        )
        .expect("write example");

        let document = load_server_config_for(tree.root().to_str().expect("UTF-8 path"))
            .expect("load example");
        assert_eq!(document.source, ServerConfigSourceDto::Example);
        assert!(document.fingerprint.is_none());
        assert!(document.validation.valid);
        assert_eq!(document.validation.unknown_paths, ["$.future_option"]);
        let editor_json: Value = serde_json::from_str(&document.json).expect("normalized JSON");
        assert_eq!(editor_json["future_option"]["enabled"], true);
    }

    #[test]
    fn save_editor_document_preserves_unknown_fields_and_applies_deletions() {
        let tree = TempTree::new("editor");
        fs::write(
            tree.config(),
            br#"{
              "identity": "steam",
              "handoff": { "scope": "slot", "future_nested": 42 },
              "future_top": { "kept": true }
            }"#,
        )
        .expect("write config");
        let loaded =
            load_server_config_for(tree.root().to_str().expect("UTF-8 path")).expect("load config");
        let mut draft: Value = serde_json::from_str(&loaded.json).expect("editor JSON");
        draft["identity"] = serde_json::json!("name");
        draft["handoff"].as_object_mut().unwrap().remove("scope");

        let saved = save_server_config_for(&SaveServerConfigRequestDto {
            cs2_path: tree.root().display().to_string(),
            json: serde_json::to_string(&draft).unwrap(),
            expected_fingerprint: loaded.fingerprint,
        })
        .expect("save editor document");
        assert_eq!(saved.reload_command, "dtr_config_reload");
        assert_eq!(saved.source, ServerConfigSourceDto::Installed);

        let value: Value = serde_json::from_slice(&fs::read(tree.config()).expect("read config"))
            .expect("parse saved config");
        assert_eq!(value["identity"], "name");
        assert!(value["handoff"].get("scope").is_none());
        assert_eq!(value["handoff"]["future_nested"], 42);
        assert_eq!(value["future_top"]["kept"], true);
    }

    #[test]
    fn save_rejects_invalid_editor_content_without_changing_the_file() {
        let tree = TempTree::new("invalid-editor");
        let original = r#"{"identity":"steam","future_option":42}"#;
        fs::write(tree.config(), original).expect("write config");
        let loaded = load_server_config_for(tree.root().to_str().unwrap()).unwrap();
        for (draft, code) in [
            ("{ broken", "server_config_invalid_json"),
            (r#"{"allow_partial":"yes"}"#, "server_config_invalid"),
        ] {
            let error = save_server_config_for(&SaveServerConfigRequestDto {
                cs2_path: tree.root().display().to_string(),
                json: draft.to_string(),
                expected_fingerprint: loaded.fingerprint.clone(),
            })
            .expect_err("invalid editor contents must not be saved");
            assert_eq!(error.code, code);
            assert_eq!(fs::read_to_string(tree.config()).unwrap(), original);
        }
    }

    #[test]
    fn save_rejects_a_stale_fingerprint() {
        let tree = TempTree::new("stale");
        fs::write(tree.config(), br#"{"identity":"steam"}"#).expect("write config");
        let loaded =
            load_server_config_for(tree.root().to_str().expect("UTF-8 path")).expect("load config");
        fs::write(tree.config(), br#"{"identity":"avatar"}"#).expect("external edit");

        let error = save_server_config_for(&SaveServerConfigRequestDto {
            cs2_path: tree.root().display().to_string(),
            json: r#"{"identity":"name"}"#.to_string(),
            expected_fingerprint: loaded.fingerprint,
        })
        .expect_err("stale save must fail");
        assert_eq!(error.code, "server_config_changed");
        assert_eq!(
            fs::read_to_string(tree.config()).expect("read external edit"),
            r#"{"identity":"avatar"}"#
        );
    }

    #[test]
    fn validation_matches_css_types_aliases_and_clamping() {
        let validation = validate_config_text(
            r#"{
              "allow_partial": "yes",
              "identity": "future",
              "handoff": { "mode": "kill", "threat_360_range": 900 },
              "align": { "weapons": true },
              "fidelity": { "preset": "handoff_safe" }
            }"#,
        );
        assert!(!validation.valid);
        assert!(validation
            .errors
            .iter()
            .any(|issue| issue.path == "$.allow_partial"));
        assert!(validation
            .warnings
            .iter()
            .any(|issue| issue.path == "$.identity" && issue.code == "value_ignored"));
        assert!(validation.warnings.iter().any(|issue| {
            issue.path == "$.handoff.threat_360_range" && issue.code == "value_clamped"
        }));
        assert!(validation
            .warnings
            .iter()
            .any(|issue| issue.code == "legacy_align_overridden"));
    }

    #[test]
    fn validation_accepts_fidelity_balance_as_a_known_boolean() {
        let valid = validate_config_text(r#"{"fidelity":{"balance":true}}"#);
        assert!(valid.valid);
        assert!(!valid
            .unknown_paths
            .contains(&"$.fidelity.balance".to_string()));

        let invalid = validate_config_text(r#"{"fidelity":{"balance":"yes"}}"#);
        assert!(!invalid.valid);
        assert!(invalid
            .errors
            .iter()
            .any(|issue| issue.path == "$.fidelity.balance"));
    }

    #[test]
    fn jsonc_parser_does_not_treat_comment_markers_inside_strings_as_comments() {
        let parsed = parse_config_text(
            r#"{
              "future_url": "https://example.invalid/a/*b*/",
              "future_text": "// still text",
            }"#,
        )
        .expect("parse JSONC strings");
        assert_eq!(parsed["future_url"], "https://example.invalid/a/*b*/");
        assert_eq!(parsed["future_text"], "// still text");
    }

    #[test]
    fn save_can_repair_an_invalid_existing_document() {
        let tree = TempTree::new("repair");
        fs::write(tree.config(), b"{ invalid").expect("write invalid config");
        let loaded = load_server_config_for(tree.root().to_str().expect("UTF-8 path"))
            .expect("load invalid config");
        assert!(!loaded.validation.valid);
        assert_eq!(loaded.json, "{ invalid");
        assert_eq!(loaded.validation.errors[0].code, "invalid_json");

        let saved = save_server_config_for(&SaveServerConfigRequestDto {
            cs2_path: tree.root().display().to_string(),
            json: r#"{"identity":"steam"}"#.to_string(),
            expected_fingerprint: loaded.fingerprint,
        })
        .expect("replace invalid config");
        assert!(saved.validation.valid);
    }

    #[test]
    fn relative_cs2_path_is_rejected() {
        let error = load_server_config_for("game/csgo").expect_err("relative path must fail");
        assert_eq!(error.code, "cs2_path_not_absolute");
    }
}
