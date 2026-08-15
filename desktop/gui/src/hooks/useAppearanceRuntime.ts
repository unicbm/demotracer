/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, type Dispatch, type RefObject, type SetStateAction } from "react";
import {
  ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY,
  applyCustomCss,
  applyThemeCustomization,
  CUSTOM_CSS_PROFILES_STORAGE_KEY,
  CUSTOM_CSS_STORAGE_KEY,
  LEGACY_APPEARANCE_STORAGE_KEYS,
  normalizeActiveCustomCssProfileId,
  normalizeCustomCssProfiles,
  normalizeThemeCustomization,
  normalizeUiFontSize,
  SIDEBAR_COLLAPSED_STORAGE_KEY,
  stepUiFontSize,
  themeBackground,
  THEME_CUSTOMIZATION_STORAGE_KEY,
  THEME_STORAGE_KEY,
  UI_FONT_SIZE_DEFAULT,
  UI_FONT_SIZE_STORAGE_KEY,
  type CustomCssProfile,
  type ThemeCustomization,
} from "../appearance";
import {
  INVENTORY_SIMULATOR_PANEL_WIDTH_KEY,
  LEGACY_UI_SCALE_STORAGE_KEY,
  measureInventorySimulatorPanel,
  normalizeInventorySimulatorPanelWidth,
  parseCommandError,
} from "../appSupport";
import {
  CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY,
  STARTER_CUSTOM_CSS_PROFILES,
} from "../customCssPresets";
import {
  IGNORED_UPDATE_VERSIONS_STORAGE_KEY,
  type IgnoredUpdateVersions,
} from "../updatePrompt";
import type {
  CommandErrorDto,
  Language,
  Theme,
  WorkspaceBackground,
} from "../types";

interface AppearanceRuntimeOptions {
  language: Language;
  theme: Theme;
  resolvedTheme: Exclude<Theme, "system">;
  sidebarCollapsed: boolean;
  ignoredUpdateVersions: IgnoredUpdateVersions;
  uiFontSize: number;
  setUiFontSize: Dispatch<SetStateAction<number>>;
  themeCustomization: ThemeCustomization;
  customCssProfiles: CustomCssProfile[];
  activeCustomCssProfileId: string | null;
  setWorkspaceBackground: Dispatch<SetStateAction<WorkspaceBackground | null>>;
  inventoryPanelAvailable: boolean;
  inventoryPanelOpen: boolean;
  inventoryPanelResizing: boolean;
  inventoryPanelWidth: number;
  setInventoryPanelWidth: Dispatch<SetStateAction<number>>;
  inventoryPanelHostRef: RefObject<HTMLDivElement | null>;
  onError: (error: CommandErrorDto) => void;
}

export function useAppearanceRuntime({
  language,
  theme,
  resolvedTheme,
  sidebarCollapsed,
  ignoredUpdateVersions,
  uiFontSize,
  setUiFontSize,
  themeCustomization,
  customCssProfiles,
  activeCustomCssProfileId,
  setWorkspaceBackground,
  inventoryPanelAvailable,
  inventoryPanelOpen,
  inventoryPanelResizing,
  inventoryPanelWidth,
  setInventoryPanelWidth,
  inventoryPanelHostRef,
  onError,
}: AppearanceRuntimeOptions) {
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.colorMode = resolvedTheme;
    document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
    const nativeBackground = themeBackground(resolvedTheme);
    document.documentElement.style.backgroundColor = nativeBackground;
    document.body.style.backgroundColor = nativeBackground;
    document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute("content", nativeBackground);
    localStorage.setItem(THEME_STORAGE_KEY, theme);
    localStorage.setItem("demotracer.language", language);
    if ("__TAURI_INTERNALS__" in window) {
      void Promise.all([
        getCurrentWindow().setTheme(theme === "system" ? null : theme),
        getCurrentWindow().setBackgroundColor(nativeBackground),
        getCurrentWebview().setBackgroundColor(nativeBackground),
      ]).catch(() => undefined);
    }
  }, [language, resolvedTheme, theme]);

  useEffect(() => {
    for (const key of LEGACY_APPEARANCE_STORAGE_KEYS) localStorage.removeItem(key);
  }, []);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    void invoke<WorkspaceBackground | null>("read_workspace_background")
      .then(setWorkspaceBackground)
      .catch((reason) => onError(parseCommandError(reason)));
  }, [onError, setWorkspaceBackground]);

  useEffect(() => {
    localStorage.setItem(SIDEBAR_COLLAPSED_STORAGE_KEY, String(sidebarCollapsed));
  }, [sidebarCollapsed]);

  useEffect(() => {
    if (ignoredUpdateVersions.gui || ignoredUpdateVersions.playback) {
      localStorage.setItem(IGNORED_UPDATE_VERSIONS_STORAGE_KEY, JSON.stringify(ignoredUpdateVersions));
    } else {
      localStorage.removeItem(IGNORED_UPDATE_VERSIONS_STORAGE_KEY);
    }
  }, [ignoredUpdateVersions]);

  useEffect(() => {
    const normalized = normalizeUiFontSize(uiFontSize);
    localStorage.setItem(UI_FONT_SIZE_STORAGE_KEY, String(normalized));
    localStorage.removeItem(LEGACY_UI_SCALE_STORAGE_KEY);
    document.documentElement.style.zoom = "";
    document.documentElement.style.setProperty("--ui-font-size", `${normalized}px`);
    if ("__TAURI_INTERNALS__" in window) void getCurrentWebview().setZoom(1).catch(() => undefined);
  }, [uiFontSize]);

  useEffect(() => {
    const normalized = normalizeThemeCustomization(themeCustomization);
    applyThemeCustomization(normalized);
    if (Object.keys(normalized).length > 0) {
      localStorage.setItem(THEME_CUSTOMIZATION_STORAGE_KEY, JSON.stringify(normalized));
    } else {
      localStorage.removeItem(THEME_CUSTOMIZATION_STORAGE_KEY);
    }
  }, [themeCustomization]);

  useEffect(() => {
    const normalizedProfiles = normalizeCustomCssProfiles(customCssProfiles);
    if (STARTER_CUSTOM_CSS_PROFILES.every((starter) => (
      normalizedProfiles.some((profile) => profile.id === starter.id && profile.css === starter.css)
    ))) {
      localStorage.setItem(CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY, "1");
    }
    if (normalizedProfiles.length > 0) {
      localStorage.setItem(CUSTOM_CSS_PROFILES_STORAGE_KEY, JSON.stringify(normalizedProfiles));
    } else {
      localStorage.removeItem(CUSTOM_CSS_PROFILES_STORAGE_KEY);
    }
    const normalizedActiveId = normalizeActiveCustomCssProfileId(activeCustomCssProfileId, normalizedProfiles);
    if (normalizedActiveId) localStorage.setItem(ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY, normalizedActiveId);
    else localStorage.removeItem(ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY);
    const activeCss = normalizedProfiles.find((profile) => profile.id === normalizedActiveId)?.css ?? "";
    applyCustomCss(activeCss);
    if (activeCss) localStorage.setItem(CUSTOM_CSS_STORAGE_KEY, activeCss);
    else localStorage.removeItem(CUSTOM_CSS_STORAGE_KEY);
  }, [activeCustomCssProfileId, customCssProfiles]);

  useEffect(() => {
    if (!inventoryPanelAvailable) return;
    if (!inventoryPanelOpen || inventoryPanelResizing) {
      void invoke("set_inventory_simulator_panel", { request: { visible: false } }).catch(() => undefined);
      return;
    }
    const host = inventoryPanelHostRef.current;
    if (!host) return;
    let frame = 0;
    let disposed = false;
    const updateBounds = () => {
      frame = 0;
      if (disposed) return;
      const bounds = measureInventorySimulatorPanel(host);
      if (!bounds) return;
      void invoke("set_inventory_simulator_panel", { request: { visible: true, bounds } }).catch(() => undefined);
    };
    const scheduleBoundsUpdate = () => {
      if (frame === 0) frame = window.requestAnimationFrame(updateBounds);
    };
    const observer = new ResizeObserver(scheduleBoundsUpdate);
    observer.observe(host);
    window.addEventListener("resize", scheduleBoundsUpdate);
    scheduleBoundsUpdate();
    return () => {
      disposed = true;
      observer.disconnect();
      window.removeEventListener("resize", scheduleBoundsUpdate);
      if (frame !== 0) window.cancelAnimationFrame(frame);
    };
  }, [
    inventoryPanelAvailable,
    inventoryPanelHostRef,
    inventoryPanelOpen,
    inventoryPanelResizing,
    uiFontSize,
  ]);

  useEffect(() => {
    localStorage.setItem(INVENTORY_SIMULATOR_PANEL_WIDTH_KEY, String(Math.round(inventoryPanelWidth)));
  }, [inventoryPanelWidth]);

  useEffect(() => {
    const clampPanelWidth = () => {
      setInventoryPanelWidth((current) => normalizeInventorySimulatorPanelWidth(current));
    };
    window.addEventListener("resize", clampPanelWidth);
    return () => window.removeEventListener("resize", clampPanelWidth);
  }, [setInventoryPanelWidth]);

  useEffect(() => {
    const handleZoomShortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      if (event.key === "0") {
        event.preventDefault();
        setUiFontSize(UI_FONT_SIZE_DEFAULT);
        return;
      }
      if (event.key === "+" || event.key === "=" || event.code === "NumpadAdd") {
        event.preventDefault();
        setUiFontSize((current) => stepUiFontSize(current, 1));
        return;
      }
      if (event.key === "-" || event.code === "NumpadSubtract") {
        event.preventDefault();
        setUiFontSize((current) => stepUiFontSize(current, -1));
      }
    };
    window.addEventListener("keydown", handleZoomShortcut);
    return () => window.removeEventListener("keydown", handleZoomShortcut);
  }, [setUiFontSize]);
}
