/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use serde::{Deserialize, Serialize};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use tauri::{AppHandle, Manager};

const GUI_PREFERENCES_FILE_NAME: &str = "gui-preferences.v1.json";
const GUI_PREFERENCES_SCHEMA_VERSION: u32 = 1;
const MAX_GUI_PREFERENCES_BYTES: u64 = 8 * 1024 * 1024;
const MAX_CUSTOM_CSS_PROFILES: usize = 24;
const MAX_CUSTOM_CSS_CHARS: usize = 65_536;
static NEXT_PREFERENCES_NONCE: AtomicU64 = AtomicU64::new(1);
static PREFERENCES_IO_LOCK: Mutex<()> = Mutex::new(());

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GuiPreferencesDto {
    schema_version: u32,
    language: String,
    appearance: GuiAppearancePreferencesDto,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct GuiAppearancePreferencesDto {
    theme: String,
    ui_font_size: u8,
    sidebar_collapsed: bool,
    #[serde(default)]
    theme_customization: ThemeCustomizationDto,
    #[serde(default)]
    custom_css_profiles: Vec<CustomCssProfileDto>,
    #[serde(default)]
    active_custom_css_profile_id: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct ThemeCustomizationDto {
    #[serde(default)]
    light: Option<ThemePaletteDto>,
    #[serde(default)]
    dark: Option<ThemePaletteDto>,
    #[serde(default)]
    font_family: Option<String>,
    #[serde(default)]
    mono_font_family: Option<String>,
    #[serde(default)]
    sidebar_opacity: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct ThemePaletteDto {
    primary: String,
    secondary: String,
    text_primary: String,
    text_secondary: String,
    info: String,
    warning: String,
    danger: String,
    success: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
struct CustomCssProfileDto {
    id: String,
    name: String,
    css: String,
}

#[tauri::command]
pub(crate) fn load_gui_preferences(app: AppHandle) -> CommandResult<Option<GuiPreferencesDto>> {
    let _guard = lock_preferences_io()?;
    let path = gui_preferences_path(&app)?;
    load_preferences_with_recovery(&path)
}

#[tauri::command]
pub(crate) fn save_gui_preferences(
    app: AppHandle,
    preferences: GuiPreferencesDto,
) -> CommandResult<()> {
    let _guard = lock_preferences_io()?;
    let path = gui_preferences_path(&app)?;
    persist_preferences_atomic(&path, &preferences)
}

fn lock_preferences_io() -> CommandResult<std::sync::MutexGuard<'static, ()>> {
    PREFERENCES_IO_LOCK.lock().map_err(|_| {
        CommandErrorDto::new(
            "gui_preferences_lock_failed",
            "The GUI preferences store is unavailable.",
        )
    })
}

fn gui_preferences_path(app: &AppHandle) -> CommandResult<PathBuf> {
    let root = app.path().app_local_data_dir().map_err(|error| {
        CommandErrorDto::new("gui_preferences_directory_failed", error.to_string())
    })?;
    fs::create_dir_all(&root).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_directory_failed", error.to_string(), &root)
    })?;
    let metadata = fs::symlink_metadata(&root).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_directory_failed", error.to_string(), &root)
    })?;
    if !metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "gui_preferences_directory_invalid",
            "The GUI preferences directory must be a normal local folder.",
            &root,
        ));
    }
    Ok(root.join(GUI_PREFERENCES_FILE_NAME))
}

fn backup_path(path: &Path) -> PathBuf {
    path.with_file_name(format!("{GUI_PREFERENCES_FILE_NAME}.bak"))
}

fn load_preferences_with_recovery(path: &Path) -> CommandResult<Option<GuiPreferencesDto>> {
    let backup = backup_path(path);
    if path.exists() {
        match read_preferences_file(path) {
            Ok(preferences) => {
                if backup.exists() {
                    let _ = fs::remove_file(&backup);
                }
                return Ok(Some(preferences));
            }
            Err(primary_error) if !backup.exists() => return Err(primary_error),
            Err(primary_error) => match read_preferences_file(&backup) {
                Ok(preferences) => {
                    restore_backup(path, &backup)?;
                    return Ok(Some(preferences));
                }
                Err(_) => return Err(primary_error),
            },
        }
    }
    if backup.exists() {
        let preferences = read_preferences_file(&backup)?;
        restore_backup(path, &backup)?;
        return Ok(Some(preferences));
    }
    Ok(None)
}

fn read_preferences_file(path: &Path) -> CommandResult<GuiPreferencesDto> {
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_read_failed", error.to_string(), path)
    })?;
    if !metadata.is_file()
        || crate::catalog::is_symlink_or_reparse(&metadata)
        || metadata.len() > MAX_GUI_PREFERENCES_BYTES
    {
        return Err(CommandErrorDto::at_path(
            "gui_preferences_read_failed",
            "GUI preferences must be a normal JSON file of a supported size.",
            path,
        ));
    }
    let bytes = fs::read(path).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_read_failed", error.to_string(), path)
    })?;
    let preferences: GuiPreferencesDto = serde_json::from_slice(&bytes).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_invalid_json", error.to_string(), path)
    })?;
    validate_preferences(&preferences)
        .map_err(|message| CommandErrorDto::at_path("gui_preferences_invalid", message, path))?;
    Ok(preferences)
}

fn persist_preferences_atomic(path: &Path, preferences: &GuiPreferencesDto) -> CommandResult<()> {
    validate_preferences(preferences)
        .map_err(|message| CommandErrorDto::at_path("gui_preferences_invalid", message, path))?;
    let mut bytes = serde_json::to_vec_pretty(preferences).map_err(|error| {
        CommandErrorDto::new("gui_preferences_serialize_failed", error.to_string())
    })?;
    bytes.push(b'\n');
    if bytes.len() as u64 > MAX_GUI_PREFERENCES_BYTES {
        return Err(CommandErrorDto::at_path(
            "gui_preferences_too_large",
            "The GUI preferences document is too large.",
            path,
        ));
    }
    let parent = path.parent().ok_or_else(|| {
        CommandErrorDto::at_path(
            "gui_preferences_write_failed",
            "The GUI preferences file has no parent folder.",
            path,
        )
    })?;
    fs::create_dir_all(parent).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_write_failed", error.to_string(), parent)
    })?;
    let sequence = NEXT_PREFERENCES_NONCE.fetch_add(1, Ordering::Relaxed);
    let temporary = path.with_file_name(format!(
        ".{GUI_PREFERENCES_FILE_NAME}.tmp.{}.{}",
        std::process::id(),
        sequence
    ));
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| {
            CommandErrorDto::at_path(
                "gui_preferences_write_failed",
                error.to_string(),
                &temporary,
            )
        })?;
    if let Err(error) = file.write_all(&bytes).and_then(|_| file.sync_all()) {
        let _ = fs::remove_file(&temporary);
        return Err(CommandErrorDto::at_path(
            "gui_preferences_write_failed",
            error.to_string(),
            &temporary,
        ));
    }
    drop(file);

    let backup = backup_path(path);
    if backup.exists() {
        fs::remove_file(&backup).map_err(|error| {
            CommandErrorDto::at_path("gui_preferences_write_failed", error.to_string(), &backup)
        })?;
    }
    let had_previous = path.exists();
    if had_previous {
        if let Err(error) = fs::rename(path, &backup) {
            let _ = fs::remove_file(&temporary);
            return Err(CommandErrorDto::at_path(
                "gui_preferences_write_failed",
                error.to_string(),
                path,
            ));
        }
    }
    if let Err(error) = fs::rename(&temporary, path) {
        if had_previous {
            let _ = fs::rename(&backup, path);
        }
        let _ = fs::remove_file(&temporary);
        return Err(CommandErrorDto::at_path(
            "gui_preferences_write_failed",
            format!("Could not promote GUI preferences: {error}"),
            path,
        ));
    }
    if had_previous {
        let _ = fs::remove_file(&backup);
    }
    Ok(())
}

fn restore_backup(path: &Path, backup: &Path) -> CommandResult<()> {
    if path.exists() {
        fs::remove_file(path).map_err(|error| {
            CommandErrorDto::at_path("gui_preferences_recovery_failed", error.to_string(), path)
        })?;
    }
    fs::rename(backup, path).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_recovery_failed", error.to_string(), backup)
    })
}

fn validate_preferences(preferences: &GuiPreferencesDto) -> Result<(), String> {
    if preferences.schema_version != GUI_PREFERENCES_SCHEMA_VERSION {
        return Err(format!(
            "GUI preferences schema {} is not supported.",
            preferences.schema_version
        ));
    }
    if !matches!(preferences.language.as_str(), "zh" | "en") {
        return Err("GUI preferences contain an unsupported language.".to_string());
    }
    let appearance = &preferences.appearance;
    if !matches!(appearance.theme.as_str(), "system" | "light" | "dark") {
        return Err("GUI preferences contain an unsupported theme.".to_string());
    }
    if !(13..=20).contains(&appearance.ui_font_size) {
        return Err("GUI font size must be between 13 and 20 pixels.".to_string());
    }
    validate_customization(&appearance.theme_customization)?;
    if appearance.custom_css_profiles.len() > MAX_CUSTOM_CSS_PROFILES {
        return Err(format!(
            "GUI preferences support at most {MAX_CUSTOM_CSS_PROFILES} custom CSS profiles."
        ));
    }
    let mut ids = std::collections::BTreeSet::new();
    for profile in &appearance.custom_css_profiles {
        if profile.id.is_empty()
            || profile.id.len() > 96
            || !profile
                .id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
            || !ids.insert(profile.id.as_str())
        {
            return Err(
                "GUI preferences contain an invalid or duplicate CSS profile ID.".to_string(),
            );
        }
        if profile.name.trim().is_empty() || profile.name.chars().count() > 64 {
            return Err("GUI preferences contain an invalid CSS profile name.".to_string());
        }
        if profile.css.trim().is_empty() || profile.css.chars().count() > MAX_CUSTOM_CSS_CHARS {
            return Err("GUI preferences contain an invalid custom CSS document.".to_string());
        }
    }
    if let Some(active) = &appearance.active_custom_css_profile_id {
        if !ids.contains(active.as_str()) {
            return Err("The active CSS profile is not present in GUI preferences.".to_string());
        }
    }
    Ok(())
}

fn validate_customization(customization: &ThemeCustomizationDto) -> Result<(), String> {
    if let Some(palette) = &customization.light {
        validate_palette(palette)?;
    }
    if let Some(palette) = &customization.dark {
        validate_palette(palette)?;
    }
    for font in [
        customization.font_family.as_deref(),
        customization.mono_font_family.as_deref(),
    ]
    .into_iter()
    .flatten()
    {
        if font.trim().is_empty()
            || font.chars().count() > 200
            || !font.chars().all(|character| {
                character.is_alphanumeric()
                    || character.is_whitespace()
                    || matches!(character, '"' | '\'' | ',' | '.' | '_' | '-')
            })
        {
            return Err("GUI preferences contain an invalid font family.".to_string());
        }
    }
    if let Some(opacity) = customization.sidebar_opacity {
        if !opacity.is_finite() || !(0.2..=1.0).contains(&opacity) {
            return Err("Sidebar opacity must be between 0.2 and 1.0.".to_string());
        }
    }
    Ok(())
}

fn validate_palette(palette: &ThemePaletteDto) -> Result<(), String> {
    for color in [
        &palette.primary,
        &palette.secondary,
        &palette.text_primary,
        &palette.text_secondary,
        &palette.info,
        &palette.warning,
        &palette.danger,
        &palette.success,
    ] {
        if !is_theme_color(color) {
            return Err("GUI preferences contain an invalid theme color.".to_string());
        }
    }
    Ok(())
}

fn is_theme_color(color: &str) -> bool {
    matches!(color.len(), 7 | 9)
        && color.starts_with('#')
        && color[1..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "demotracer-gui-preferences-{}-{}",
                std::process::id(),
                NEXT_PREFERENCES_NONCE.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn preferences() -> GuiPreferencesDto {
        GuiPreferencesDto {
            schema_version: 1,
            language: "zh".to_string(),
            appearance: GuiAppearancePreferencesDto {
                theme: "system".to_string(),
                ui_font_size: 16,
                sidebar_collapsed: false,
                theme_customization: ThemeCustomizationDto {
                    sidebar_opacity: Some(0.73),
                    ..ThemeCustomizationDto::default()
                },
                custom_css_profiles: vec![CustomCssProfileDto {
                    id: "local-style".to_string(),
                    name: "Local Style".to_string(),
                    css: ":root { --trace: #0A84FF; }".to_string(),
                }],
                active_custom_css_profile_id: Some("local-style".to_string()),
            },
        }
    }

    #[test]
    fn preferences_round_trip_through_atomic_json() {
        let directory = TestDirectory::new();
        let path = directory.0.join(GUI_PREFERENCES_FILE_NAME);
        let expected = preferences();
        persist_preferences_atomic(&path, &expected).unwrap();
        assert_eq!(
            load_preferences_with_recovery(&path).unwrap(),
            Some(expected)
        );
    }

    #[test]
    fn preferences_recover_the_last_valid_backup() {
        let directory = TestDirectory::new();
        let path = directory.0.join(GUI_PREFERENCES_FILE_NAME);
        let backup = backup_path(&path);
        let expected = preferences();
        persist_preferences_atomic(&path, &expected).unwrap();
        fs::rename(&path, &backup).unwrap();
        fs::write(&path, b"not json").unwrap();

        assert_eq!(
            load_preferences_with_recovery(&path).unwrap(),
            Some(expected)
        );
        assert!(path.exists());
        assert!(!backup.exists());
    }

    #[test]
    fn preferences_reject_an_unresolved_active_css_profile() {
        let mut invalid = preferences();
        invalid.appearance.active_custom_css_profile_id = Some("missing".to_string());
        assert!(validate_preferences(&invalid)
            .unwrap_err()
            .contains("active CSS profile"));
    }
}
