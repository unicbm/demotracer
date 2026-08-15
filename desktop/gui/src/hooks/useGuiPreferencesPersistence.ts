/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import type { CustomCssProfile, ThemeCustomization } from "../appearance";
import { parseCommandError, reconcileCustomCssProfiles } from "../appSupport";
import {
  createGuiPreferences,
  normalizeGuiPreferences,
  type GuiPreferencesV1,
} from "../guiPreferences";
import type { CommandErrorDto, Language, Theme } from "../types";

interface GuiPreferencesPersistenceOptions {
  language: Language;
  setLanguage: Dispatch<SetStateAction<Language>>;
  theme: Theme;
  setTheme: Dispatch<SetStateAction<Theme>>;
  uiFontSize: number;
  setUiFontSize: Dispatch<SetStateAction<number>>;
  sidebarCollapsed: boolean;
  setSidebarCollapsed: Dispatch<SetStateAction<boolean>>;
  themeCustomization: ThemeCustomization;
  setThemeCustomization: Dispatch<SetStateAction<ThemeCustomization>>;
  customCssProfiles: CustomCssProfile[];
  setCustomCssProfiles: Dispatch<SetStateAction<CustomCssProfile[]>>;
  activeCustomCssProfileId: string | null;
  setActiveCustomCssProfileId: Dispatch<SetStateAction<string | null>>;
  onError: (error: CommandErrorDto) => void;
}

export function useGuiPreferencesPersistence({
  language,
  setLanguage,
  theme,
  setTheme,
  uiFontSize,
  setUiFontSize,
  sidebarCollapsed,
  setSidebarCollapsed,
  themeCustomization,
  setThemeCustomization,
  customCssProfiles,
  setCustomCssProfiles,
  activeCustomCssProfileId,
  setActiveCustomCssProfileId,
  onError,
}: GuiPreferencesPersistenceOptions): void {
  const [hydrated, setHydrated] = useState(false);
  const saveQueueRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) {
      setHydrated(true);
      return;
    }
    let disposed = false;
    void invoke<GuiPreferencesV1 | null>("load_gui_preferences").then((stored) => {
      if (disposed) return;
      if (stored) {
        const preferences = normalizeGuiPreferences(stored);
        if (!preferences) {
          onError({
            code: "gui_preferences_invalid",
            message: "The saved GUI preferences use an unsupported format.",
          });
          return;
        }
        const profiles = reconcileCustomCssProfiles(
          preferences.appearance.customCssProfiles,
          true,
        );
        const activeProfileId = profiles.some((profile) => (
          profile.id === preferences.appearance.activeCustomCssProfileId
        )) ? preferences.appearance.activeCustomCssProfileId : null;
        setLanguage(preferences.language);
        setTheme(preferences.appearance.theme);
        setUiFontSize(preferences.appearance.uiFontSize);
        setSidebarCollapsed(preferences.appearance.sidebarCollapsed);
        setThemeCustomization(preferences.appearance.themeCustomization);
        setCustomCssProfiles(profiles);
        setActiveCustomCssProfileId(activeProfileId);
      }
      setHydrated(true);
    }).catch((reason) => {
      if (!disposed) onError(parseCommandError(reason));
    });
    return () => {
      disposed = true;
    };
  }, [
    onError,
    setActiveCustomCssProfileId,
    setCustomCssProfiles,
    setLanguage,
    setSidebarCollapsed,
    setTheme,
    setThemeCustomization,
    setUiFontSize,
  ]);

  useEffect(() => {
    if (!hydrated || !("__TAURI_INTERNALS__" in window)) return;
    const preferences = createGuiPreferences({
      language,
      theme,
      uiFontSize,
      sidebarCollapsed,
      themeCustomization,
      customCssProfiles,
      activeCustomCssProfileId,
    });
    const timer = window.setTimeout(() => {
      saveQueueRef.current = saveQueueRef.current
        .catch(() => undefined)
        .then(() => invoke<void>("save_gui_preferences", { preferences }))
        .catch((reason) => {
          onError(parseCommandError(reason));
        });
    }, 120);
    return () => window.clearTimeout(timer);
  }, [
    activeCustomCssProfileId,
    customCssProfiles,
    hydrated,
    language,
    onError,
    sidebarCollapsed,
    theme,
    themeCustomization,
    uiFontSize,
  ]);
}
