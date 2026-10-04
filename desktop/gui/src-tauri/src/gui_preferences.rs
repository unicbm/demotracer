/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::{CommandErrorDto, CommandResult};
use serde_json::{Map, Value};
use std::fs;
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Manager};

const GUI_PREFERENCES_FILE_NAME: &str = "gui-preferences.v1.json";

#[tauri::command]
pub(crate) fn load_gui_preferences(app: AppHandle) -> CommandResult<Option<Map<String, Value>>> {
    read_preferences_file(&gui_preferences_path(&app)?)
}

#[tauri::command]
pub(crate) fn save_gui_preferences(
    app: AppHandle,
    preferences: Map<String, Value>,
) -> CommandResult<()> {
    persist_preferences_atomic(&gui_preferences_path(&app)?, &preferences)
}

fn gui_preferences_path(app: &AppHandle) -> CommandResult<PathBuf> {
    app.path()
        .app_local_data_dir()
        .map(|root| root.join(GUI_PREFERENCES_FILE_NAME))
        .map_err(|error| {
            CommandErrorDto::new("gui_preferences_directory_failed", error.to_string())
        })
}

// Appearance fields are normalized by the frontend; this module only stores the document.
fn read_preferences_file(path: &Path) -> CommandResult<Option<Map<String, Value>>> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => {
            return Err(CommandErrorDto::at_path(
                "gui_preferences_read_failed",
                error.to_string(),
                path,
            ));
        }
    };
    serde_json::from_slice(&bytes).map(Some).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_invalid_json", error.to_string(), path)
    })
}

fn persist_preferences_atomic(path: &Path, preferences: &Map<String, Value>) -> CommandResult<()> {
    let bytes = serde_json::to_vec_pretty(preferences).map_err(|error| {
        CommandErrorDto::new("gui_preferences_serialize_failed", error.to_string())
    })?;
    fs::create_dir_all(path.parent().unwrap()).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_directory_failed", error.to_string(), path)
    })?;
    crate::atomic_file::write(path, &bytes).map_err(|error| {
        CommandErrorDto::at_path("gui_preferences_write_failed", error.to_string(), path)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preferences_round_trip_through_atomic_json() {
        let directory =
            std::env::temp_dir().join(format!("dtr-preferences-{}", uuid::Uuid::new_v4()));
        let path = directory.join(GUI_PREFERENCES_FILE_NAME);
        assert_eq!(read_preferences_file(&path).unwrap(), None);
        let preferences = serde_json::json!({
            "schemaVersion": 1,
            "language": "zh",
            "appearance": {
                "theme": "system", "uiFontSize": 16, "sidebarCollapsed": false,
                "themeCustomization": { "sidebarOpacity": 0.73 },
                "customCssProfiles": [{ "id": "local-style", "name": "Local Style", "css": ":root { color: red; }" }],
                "activeCustomCssProfileId": "local-style"
            }
        }).as_object().unwrap().clone();
        persist_preferences_atomic(&path, &preferences).unwrap();
        assert_eq!(read_preferences_file(&path).unwrap(), Some(preferences));
        fs::write(&path, b"not json").unwrap();
        assert!(read_preferences_file(&path).is_err());
        fs::remove_dir_all(directory).unwrap();
    }
}
