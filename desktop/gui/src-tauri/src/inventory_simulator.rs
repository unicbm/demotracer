/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use base64::Engine;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::sync::Mutex;
use std::time::Duration;
use tauri::{
    webview::WebviewBuilder, AppHandle, Manager, PhysicalPosition, PhysicalSize, Rect, Webview,
    WebviewUrl,
};

use crate::{CommandErrorDto, CommandResult};

const INVENTORY_SIMULATOR_ORIGIN: &str = "https://inventory.cstrike.app";
const MAIN_WINDOW_LABEL: &str = "main";
const INVENTORY_SIMULATOR_WEBVIEW_LABEL: &str = "inventory-simulator-panel";
static INVENTORY_SIMULATOR_WEBVIEW_LOCK: Mutex<()> = Mutex::new(());
const MAX_BATCH_ITEMS: usize = 64;
const MAX_BATCH_JSON_BYTES: usize = 64 * 1024;
const MAX_ITEM_SEED: u32 = 1_000;
const MAX_KEYCHAIN_SEED: u32 = 100_000;
const MAX_KEYCHAIN_OFFSET: f64 = 100.0;
const MAX_STICKERS: usize = 5;
const MAX_STICKER_SCHEMA_COUNT: u32 = 8;
const MAX_KEYCHAINS: usize = 1;
const MAX_PANEL_PIXEL_DIMENSION: u32 = 32_768;

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum InventorySimulatorLanguage {
    En,
    Zh,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorSticker {
    pub id: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rotation: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wear: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub y: Option<f64>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorKeychain {
    pub id: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seed: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub y: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub z: Option<f64>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorItem {
    pub id: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub keychains: Option<BTreeMap<String, InventorySimulatorKeychain>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name_tag: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seed: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stat_trak: Option<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stickers: Option<BTreeMap<String, InventorySimulatorSticker>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wear: Option<f64>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorBatchRequest {
    pub items: Vec<InventorySimulatorItem>,
    pub language: InventorySimulatorLanguage,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InventorySimulatorBatchStartedDto {
    item_count: usize,
}

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorPanelBounds {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InventorySimulatorPanelRequest {
    visible: bool,
    #[serde(default)]
    bounds: Option<InventorySimulatorPanelBounds>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BatchScriptCopy {
    title: &'static str,
    checking: &'static str,
    adding: &'static str,
    refreshing: &'static str,
    success: &'static str,
    duplicates: &'static str,
    sign_in: &'static str,
    auth_required: &'static str,
    retry: &'static str,
    failed: &'static str,
    refresh_required: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BatchScriptConfig<'a> {
    items: &'a [InventorySimulatorItem],
    run: String,
    copy: BatchScriptCopy,
}

fn command_error(code: &'static str, message: impl Into<String>) -> CommandErrorDto {
    CommandErrorDto::new(code, message)
}

fn validate_map_key(value: &str, maximum: usize) -> bool {
    value
        .parse::<usize>()
        .is_ok_and(|number| number <= maximum && number.to_string() == value)
}

fn valid_optional_number(value: Option<f64>, minimum: f64, maximum: f64) -> bool {
    value.is_none_or(|number| number.is_finite() && number >= minimum && number <= maximum)
}

fn validate_item(item: &InventorySimulatorItem, index: usize) -> CommandResult<()> {
    let invalid = |field: &str| {
        command_error(
            "inventory_simulator_item_invalid",
            format!(
                "Inventory Simulator item {} has an invalid {field}.",
                index + 1
            ),
        )
    };

    if item.id == 0 {
        return Err(invalid("catalog ID"));
    }
    if item
        .seed
        .is_some_and(|seed| seed == 0 || seed > MAX_ITEM_SEED)
    {
        return Err(invalid("pattern seed"));
    }
    if !valid_optional_number(item.wear, 0.0, 1.0) {
        return Err(invalid("wear value"));
    }
    if item.stat_trak.is_some_and(|value| value != 0) {
        return Err(invalid("StatTrak value"));
    }
    if item
        .name_tag
        .as_ref()
        .is_some_and(|name| name.chars().count() > 20 || name.chars().any(char::is_control))
    {
        return Err(invalid("name tag"));
    }

    if let Some(stickers) = &item.stickers {
        if stickers.is_empty() || stickers.len() > MAX_STICKERS {
            return Err(invalid("sticker collection"));
        }
        for (slot, sticker) in stickers {
            if !validate_map_key(slot, MAX_STICKERS - 1)
                || sticker.id == 0
                || sticker
                    .schema
                    .is_some_and(|schema| schema >= MAX_STICKER_SCHEMA_COUNT)
                || !valid_optional_number(sticker.wear, 0.0, 1.0)
                || !valid_optional_number(sticker.rotation, -180.0, 180.0)
                || !valid_optional_number(sticker.x, -1.0, 1.0)
                || !valid_optional_number(sticker.y, -1.0, 1.0)
            {
                return Err(invalid("sticker evidence"));
            }
        }
    }

    if let Some(keychains) = &item.keychains {
        if keychains.is_empty() || keychains.len() > MAX_KEYCHAINS {
            return Err(invalid("keychain collection"));
        }
        for (slot, keychain) in keychains {
            if !validate_map_key(slot, MAX_KEYCHAINS - 1)
                || keychain.id == 0
                || keychain
                    .seed
                    .is_some_and(|seed| seed == 0 || seed > MAX_KEYCHAIN_SEED)
                || !valid_optional_number(keychain.x, -MAX_KEYCHAIN_OFFSET, MAX_KEYCHAIN_OFFSET)
                || !valid_optional_number(keychain.y, -MAX_KEYCHAIN_OFFSET, MAX_KEYCHAIN_OFFSET)
                || !valid_optional_number(keychain.z, -MAX_KEYCHAIN_OFFSET, MAX_KEYCHAIN_OFFSET)
            {
                return Err(invalid("keychain evidence"));
            }
        }
    }

    Ok(())
}

fn copy_for(language: InventorySimulatorLanguage) -> BatchScriptCopy {
    match language {
        InventorySimulatorLanguage::Zh => BatchScriptCopy {
            title: "DemoTracer 批量同步",
            checking: "正在检查当前库存与重复项…",
            adding: "正在添加 {count} 件，已跳过 {skipped} 件重复项…",
            refreshing: "正在刷新页面库存…",
            success: "已添加 {count} 件，跳过 {skipped} 件重复项。",
            duplicates: "未添加：{skipped} 件在库存中已存在。",
            sign_in: "使用 Steam 登录",
            auth_required: "请先在此窗口登录 Inventory Simulator。登录后将自动继续。",
            retry: "重试",
            failed: "批量添加失败（HTTP {status}）。未确认成功前请勿重复提交。",
            refresh_required: "页面库存未能自动刷新，请手动刷新一次查看。",
        },
        InventorySimulatorLanguage::En => BatchScriptCopy {
            title: "DemoTracer batch sync",
            checking: "Checking the current inventory for duplicates…",
            adding: "Adding {count}; skipped {skipped} duplicates…",
            refreshing: "Refreshing the visible inventory…",
            success: "Added {count}; skipped {skipped} duplicates.",
            duplicates: "No items added: {skipped} matching items already exist.",
            sign_in: "Sign in with Steam",
            auth_required: "Sign in to Inventory Simulator here. The batch will continue automatically afterward.",
            retry: "Retry",
            failed: "Batch add failed (HTTP {status}). Do not resubmit until success is confirmed.",
            refresh_required: "The visible inventory could not refresh automatically; refresh the page once to view it.",
        },
    }
}

fn validate_request(request: &InventorySimulatorBatchRequest) -> CommandResult<()> {
    if request.items.is_empty() || request.items.len() > MAX_BATCH_ITEMS {
        return Err(command_error(
            "inventory_simulator_batch_size_invalid",
            format!("Select between 1 and {MAX_BATCH_ITEMS} Inventory Simulator items."),
        ));
    }
    for (index, item) in request.items.iter().enumerate() {
        validate_item(item, index)?;
    }
    let bytes = serde_json::to_vec(&request.items).map_err(|error| {
        command_error("inventory_simulator_batch_encode_failed", error.to_string())
    })?;
    if bytes.len() > MAX_BATCH_JSON_BYTES {
        return Err(command_error(
            "inventory_simulator_batch_too_large",
            "The selected Inventory Simulator batch is too large.",
        ));
    }
    Ok(())
}

fn is_inventory_simulator_url(url: &tauri::Url) -> bool {
    url.scheme() == "https" && url.host_str() == Some("inventory.cstrike.app")
}

fn navigation_allowed(url: &tauri::Url) -> bool {
    is_inventory_simulator_url(url)
        || (url.scheme() == "https" && url.host_str() == Some("steamcommunity.com"))
}

fn validate_panel_bounds(bounds: InventorySimulatorPanelBounds) -> CommandResult<()> {
    if bounds.x < 0
        || bounds.y < 0
        || bounds.width == 0
        || bounds.height == 0
        || bounds.width > MAX_PANEL_PIXEL_DIMENSION
        || bounds.height > MAX_PANEL_PIXEL_DIMENSION
    {
        return Err(command_error(
            "inventory_simulator_panel_bounds_invalid",
            "Inventory Simulator panel bounds are invalid.",
        ));
    }
    Ok(())
}

fn apply_panel_bounds(
    webview: &Webview,
    bounds: InventorySimulatorPanelBounds,
) -> CommandResult<()> {
    validate_panel_bounds(bounds)?;
    webview
        .set_bounds(Rect {
            position: PhysicalPosition::new(bounds.x, bounds.y).into(),
            size: PhysicalSize::new(bounds.width, bounds.height).into(),
        })
        .map_err(|error| {
            command_error("inventory_simulator_panel_resize_failed", error.to_string())
        })
}

fn initialization_script(request: &InventorySimulatorBatchRequest) -> CommandResult<String> {
    let run = format!(
        "{}-{}",
        std::process::id(),
        crate::NEXT_STAGING_NONCE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    );
    let config = BatchScriptConfig {
        items: &request.items,
        run,
        copy: copy_for(request.language),
    };
    let json = serde_json::to_vec(&config).map_err(|error| {
        command_error("inventory_simulator_batch_encode_failed", error.to_string())
    })?;
    let encoded = base64::engine::general_purpose::STANDARD.encode(json);
    Ok(include_str!("inventory_simulator_batch.js")
        .replace("__DTR_BATCH_CONFIG_BASE64__", &encoded)
        .replace(
            "__DTR_DEDUPE_SOURCE__",
            include_str!("inventory_simulator_dedupe.js"),
        ))
}

#[tauri::command]
pub async fn start_inventory_simulator_batch(
    app: AppHandle,
    request: InventorySimulatorBatchRequest,
    bounds: InventorySimulatorPanelBounds,
) -> CommandResult<InventorySimulatorBatchStartedDto> {
    let _webview_guard = INVENTORY_SIMULATOR_WEBVIEW_LOCK.lock().map_err(|_| {
        command_error(
            "inventory_simulator_webview_lock_failed",
            "The Inventory Simulator panel lock is unavailable.",
        )
    })?;
    validate_request(&request)?;
    validate_panel_bounds(bounds)?;
    let item_count = request.items.len();
    let script = initialization_script(&request)?;
    let data_directory = app
        .path()
        .app_local_data_dir()
        .map_err(|error| command_error("inventory_simulator_data_dir_failed", error.to_string()))?
        .join("inventory-simulator-webview");
    fs::create_dir_all(&data_directory).map_err(|error| {
        CommandErrorDto::at_path(
            "inventory_simulator_data_dir_failed",
            error.to_string(),
            &data_directory,
        )
    })?;

    if let Some(existing) = app.get_webview(INVENTORY_SIMULATOR_WEBVIEW_LABEL) {
        if existing
            .url()
            .is_ok_and(|url| is_inventory_simulator_url(&url))
        {
            existing.eval(script).map_err(|error| {
                command_error("inventory_simulator_panel_reuse_failed", error.to_string())
            })?;
            apply_panel_bounds(&existing, bounds)?;
            existing.show().map_err(|error| {
                command_error("inventory_simulator_panel_show_failed", error.to_string())
            })?;
            existing.set_focus().map_err(|error| {
                command_error("inventory_simulator_panel_focus_failed", error.to_string())
            })?;
            return Ok(InventorySimulatorBatchStartedDto { item_count });
        }

        existing.close().map_err(|error| {
            command_error(
                "inventory_simulator_panel_replace_failed",
                error.to_string(),
            )
        })?;
        for _ in 0..50 {
            if app.get_webview(INVENTORY_SIMULATOR_WEBVIEW_LABEL).is_none() {
                break;
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        if app.get_webview(INVENTORY_SIMULATOR_WEBVIEW_LABEL).is_some() {
            return Err(command_error(
                "inventory_simulator_panel_replace_failed",
                "The previous Inventory Simulator panel did not close in time.",
            ));
        }
    }

    let url = INVENTORY_SIMULATOR_ORIGIN.parse().map_err(|error| {
        command_error(
            "inventory_simulator_url_invalid",
            format!("Invalid Inventory Simulator URL: {error}"),
        )
    })?;
    let main_window = app.get_window(MAIN_WINDOW_LABEL).ok_or_else(|| {
        command_error(
            "inventory_simulator_parent_window_missing",
            "The DemoTracer main window is unavailable.",
        )
    })?;
    let webview_builder =
        WebviewBuilder::new(INVENTORY_SIMULATOR_WEBVIEW_LABEL, WebviewUrl::External(url))
            .data_directory(data_directory)
            .initialization_script(script)
            .on_navigation(navigation_allowed);
    let webview = main_window
        .add_child(
            webview_builder,
            PhysicalPosition::new(bounds.x, bounds.y),
            PhysicalSize::new(bounds.width, bounds.height),
        )
        .map_err(|error| command_error("inventory_simulator_panel_failed", error.to_string()))?;
    webview.set_focus().map_err(|error| {
        command_error("inventory_simulator_panel_focus_failed", error.to_string())
    })?;

    Ok(InventorySimulatorBatchStartedDto { item_count })
}

#[tauri::command]
pub fn set_inventory_simulator_panel(
    app: AppHandle,
    request: InventorySimulatorPanelRequest,
) -> CommandResult<()> {
    let Some(webview) = app.get_webview(INVENTORY_SIMULATOR_WEBVIEW_LABEL) else {
        return Ok(());
    };
    if !request.visible {
        return webview.hide().map_err(|error| {
            command_error("inventory_simulator_panel_hide_failed", error.to_string())
        });
    }
    let bounds = request.bounds.ok_or_else(|| {
        command_error(
            "inventory_simulator_panel_bounds_missing",
            "Visible Inventory Simulator panel bounds are required.",
        )
    })?;
    apply_panel_bounds(&webview, bounds)?;
    webview
        .show()
        .map_err(|error| command_error("inventory_simulator_panel_show_failed", error.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request() -> InventorySimulatorBatchRequest {
        InventorySimulatorBatchRequest {
            language: InventorySimulatorLanguage::En,
            items: vec![InventorySimulatorItem {
                id: 307,
                keychains: None,
                name_tag: Some("demo evidence".to_string()),
                seed: Some(42),
                stat_trak: Some(0),
                stickers: Some(BTreeMap::from([(
                    "0".to_string(),
                    InventorySimulatorSticker {
                        id: 10_225,
                        rotation: Some(12.5),
                        schema: Some(2),
                        wear: Some(0.01),
                        x: Some(0.1234),
                        y: Some(-0.6543),
                    },
                )])),
                wear: Some(0.123456),
            }],
        }
    }

    #[test]
    fn validates_demo_backed_batch_shape() {
        validate_request(&request()).unwrap();
    }

    #[test]
    fn rejects_out_of_range_or_ambiguous_values() {
        let mut invalid = request();
        invalid.items[0].seed = Some(0);
        assert_eq!(
            validate_request(&invalid).unwrap_err().code,
            "inventory_simulator_item_invalid"
        );

        let mut invalid = request();
        invalid.items[0].stickers.as_mut().unwrap().insert(
            "01".to_string(),
            InventorySimulatorSticker {
                id: 1,
                rotation: None,
                schema: None,
                wear: None,
                x: None,
                y: None,
            },
        );
        assert_eq!(
            validate_request(&invalid).unwrap_err().code,
            "inventory_simulator_item_invalid"
        );
    }

    #[test]
    fn embedded_script_keeps_item_text_out_of_source() {
        let mut malicious = request();
        malicious.items[0].name_tag = Some("</script>safe".to_string());
        let script = initialization_script(&malicious).unwrap();
        assert!(!script.contains("</script>safe"));
        assert!(!script.contains("__DTR_DEDUPE_SOURCE__"));
    }

    #[test]
    fn accepts_catalog_schema_seven_but_rejects_schema_eight() {
        let mut valid = request();
        valid.items[0]
            .stickers
            .as_mut()
            .unwrap()
            .get_mut("0")
            .unwrap()
            .schema = Some(7);
        validate_request(&valid).unwrap();

        let mut invalid = valid;
        invalid.items[0]
            .stickers
            .as_mut()
            .unwrap()
            .get_mut("0")
            .unwrap()
            .schema = Some(8);
        assert_eq!(
            validate_request(&invalid).unwrap_err().code,
            "inventory_simulator_item_invalid"
        );
    }

    #[test]
    fn webview_navigation_is_limited_to_service_and_steam_login() {
        let inventory_url = "https://inventory.cstrike.app/sign-in".parse().unwrap();
        assert!(is_inventory_simulator_url(&inventory_url));
        assert!(navigation_allowed(&inventory_url));
        assert!(navigation_allowed(
            &"https://steamcommunity.com/openid/login".parse().unwrap()
        ));
        assert!(!is_inventory_simulator_url(
            &"https://steamcommunity.com/openid/login".parse().unwrap()
        ));
        assert!(!navigation_allowed(
            &"http://inventory.cstrike.app".parse().unwrap()
        ));
        assert!(!navigation_allowed(
            &"https://inventory.cstrike.app.example.com".parse().unwrap()
        ));
    }

    #[test]
    fn panel_bounds_reject_empty_negative_or_excessive_regions() {
        validate_panel_bounds(InventorySimulatorPanelBounds {
            x: 100,
            y: 50,
            width: 800,
            height: 600,
        })
        .unwrap();
        for bounds in [
            InventorySimulatorPanelBounds {
                x: -1,
                y: 0,
                width: 800,
                height: 600,
            },
            InventorySimulatorPanelBounds {
                x: 0,
                y: 0,
                width: 0,
                height: 600,
            },
            InventorySimulatorPanelBounds {
                x: 0,
                y: 0,
                width: MAX_PANEL_PIXEL_DIMENSION + 1,
                height: 600,
            },
        ] {
            assert_eq!(
                validate_panel_bounds(bounds).unwrap_err().code,
                "inventory_simulator_panel_bounds_invalid"
            );
        }
    }
}
