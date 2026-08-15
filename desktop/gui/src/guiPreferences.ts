/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import {
  normalizeActiveCustomCssProfileId,
  normalizeCustomCssProfiles,
  normalizeSidebarCollapsed,
  normalizeTheme,
  normalizeThemeCustomization,
  normalizeUiFontSize,
  type CustomCssProfile,
  type ThemeCustomization,
} from "./appearance.ts";
import type { Language, Theme } from "./types";

export const GUI_PREFERENCES_SCHEMA_VERSION = 1 as const;

export interface GuiAppearancePreferencesV1 {
  theme: Theme;
  uiFontSize: number;
  sidebarCollapsed: boolean;
  themeCustomization: ThemeCustomization;
  customCssProfiles: CustomCssProfile[];
  activeCustomCssProfileId: string | null;
}

export interface GuiPreferencesV1 {
  schemaVersion: typeof GUI_PREFERENCES_SCHEMA_VERSION;
  language: Language;
  appearance: GuiAppearancePreferencesV1;
}

interface GuiPreferencesInput {
  language: Language;
  theme: Theme;
  uiFontSize: number;
  sidebarCollapsed: boolean;
  themeCustomization: ThemeCustomization;
  customCssProfiles: readonly CustomCssProfile[];
  activeCustomCssProfileId: string | null;
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

export function normalizeGuiPreferences(value: unknown): GuiPreferencesV1 | null {
  const root = recordValue(value);
  if (!root || root.schemaVersion !== GUI_PREFERENCES_SCHEMA_VERSION) return null;
  if (root.language !== "zh" && root.language !== "en") return null;
  const appearance = recordValue(root.appearance);
  if (!appearance) return null;
  const profiles = normalizeCustomCssProfiles(appearance.customCssProfiles);
  return {
    schemaVersion: GUI_PREFERENCES_SCHEMA_VERSION,
    language: root.language,
    appearance: {
      theme: normalizeTheme(appearance.theme),
      uiFontSize: normalizeUiFontSize(appearance.uiFontSize),
      sidebarCollapsed: normalizeSidebarCollapsed(appearance.sidebarCollapsed),
      themeCustomization: normalizeThemeCustomization(appearance.themeCustomization),
      customCssProfiles: profiles,
      activeCustomCssProfileId: normalizeActiveCustomCssProfileId(
        appearance.activeCustomCssProfileId,
        profiles,
      ),
    },
  };
}

export function createGuiPreferences(input: GuiPreferencesInput): GuiPreferencesV1 {
  return normalizeGuiPreferences({
    schemaVersion: GUI_PREFERENCES_SCHEMA_VERSION,
    language: input.language,
    appearance: {
      theme: input.theme,
      uiFontSize: input.uiFontSize,
      sidebarCollapsed: input.sidebarCollapsed,
      themeCustomization: input.themeCustomization,
      customCssProfiles: input.customCssProfiles,
      activeCustomCssProfileId: input.activeCustomCssProfileId,
    },
  }) as GuiPreferencesV1;
}
