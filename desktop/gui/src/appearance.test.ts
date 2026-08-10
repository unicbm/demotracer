/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  cycleUiScale,
  CUSTOM_CSS_STORAGE_KEY,
  isThemeColor,
  LEGACY_APPEARANCE_STORAGE_KEYS,
  normalizeSidebarCollapsed,
  normalizeCustomCss,
  normalizeThemeCustomization,
  normalizeUiScale,
  normalizeTheme,
  recommendedUiScale,
  resolveTheme,
  stepUiScale,
  SIDEBAR_COLLAPSED_STORAGE_KEY,
  THEME_CUSTOMIZATION_STORAGE_KEY,
  THEME_PALETTE_DEFAULTS,
  themeBackground,
  themeCustomizationCss,
  themePalette,
  toggleResolvedTheme,
} from "./appearance.ts";

describe("appearance preferences", () => {
  it("toggles the visible system theme on the first click", () => {
    assert.equal(toggleResolvedTheme("system", false), "dark");
    assert.equal(toggleResolvedTheme("system", true), "light");
    assert.equal(toggleResolvedTheme("light", true), "dark");
    assert.equal(toggleResolvedTheme("dark", false), "light");
  });

  it("normalizes stored theme values", () => {
    assert.equal(normalizeTheme("light"), "light");
    assert.equal(normalizeTheme("dark"), "dark");
    assert.equal(normalizeTheme("system"), "system");
    assert.equal(normalizeTheme("invalid"), "dark");
    assert.equal(normalizeTheme(null), "dark");
  });

  it("resolves system theme using the current OS preference", () => {
    assert.equal(resolveTheme("system", false), "light");
    assert.equal(resolveTheme("system", true), "dark");
  });

  it("lists obsolete appearance preferences for cleanup", () => {
    assert.deepEqual(LEGACY_APPEARANCE_STORAGE_KEYS, [
      "demotracer.ui-skin.v1",
      "demotracer.sidebar-width.v1",
      "demotracer.sidebar-collapsed.v1",
    ]);
  });

  it("normalizes the persisted sidebar state without reviving the legacy key", () => {
    assert.equal(SIDEBAR_COLLAPSED_STORAGE_KEY, "demotracer.sidebar-collapsed.v2");
    assert.equal(normalizeSidebarCollapsed("true"), true);
    assert.equal(normalizeSidebarCollapsed(true), true);
    assert.equal(normalizeSidebarCollapsed("false"), false);
    assert.equal(normalizeSidebarCollapsed(null), false);
  });

  it("uses one native background per color mode", () => {
    assert.equal(themeBackground("light"), "#f5f6f8");
    assert.equal(themeBackground("dark"), "#20212b");
  });

  it("normalizes and steps persistent UI scale values", () => {
    assert.equal(normalizeUiScale("1.1"), 1.1);
    assert.equal(normalizeUiScale(1.22), 1.25);
    assert.equal(normalizeUiScale(null), 1);
    assert.equal(normalizeUiScale("invalid"), 1);
    assert.equal(stepUiScale(1, 1), 1.1);
    assert.equal(stepUiScale(1, -1), 0.9);
    assert.equal(stepUiScale(1.25, 1), 1.25);
    assert.equal(cycleUiScale(1.25), 0.9);
  });

  it("recommends the larger first-run scale only for high-resolution displays", () => {
    assert.equal(recommendedUiScale(1920, 1080, 1), 1);
    assert.equal(recommendedUiScale(2560, 1440, 1), 1);
    assert.equal(recommendedUiScale(2560, 1440, 1.5), 1.1);
    assert.equal(recommendedUiScale(1920, 1080, 2), 1.1);
  });

  it("keeps custom CSS local storage bounded and ignores non-text values", () => {
    assert.equal(CUSTOM_CSS_STORAGE_KEY, "demotracer.custom-css.v1");
    assert.equal(normalizeCustomCss(".card { border-radius: 18px; }"), ".card { border-radius: 18px; }");
    assert.equal(normalizeCustomCss(null), "");
    assert.equal(normalizeCustomCss("x".repeat(70_000)).length, 65_536);
  });

  it("normalizes visual theme settings without accepting CSS fragments", () => {
    const customization = normalizeThemeCustomization(JSON.stringify({
      dark: { ...THEME_PALETTE_DEFAULTS.dark, primary: "#0a84ff" },
      fontFamily: '"Segoe UI Variable", sans-serif',
    }));
    assert.equal(THEME_CUSTOMIZATION_STORAGE_KEY, "demotracer.theme-customization.v1");
    assert.equal(customization.dark?.primary, "#0A84FF");
    assert.equal(customization.fontFamily, '"Segoe UI Variable", sans-serif');
    assert.equal(isThemeColor("#EBEBE599"), true);
    assert.equal(isThemeColor("red; color: white"), false);
    assert.equal(normalizeThemeCustomization({
      dark: THEME_PALETTE_DEFAULTS.dark,
      fontFamily: "sans-serif; color: red",
    }).fontFamily, undefined);
  });

  it("keeps light and dark palette overrides independent", () => {
    const customization = normalizeThemeCustomization({ dark: THEME_PALETTE_DEFAULTS.dark });
    assert.deepEqual(themePalette(customization, "dark"), THEME_PALETTE_DEFAULTS.dark);
    assert.deepEqual(themePalette(customization, "light"), THEME_PALETTE_DEFAULTS.light);
    const css = themeCustomizationCss(customization);
    assert.match(css, /data-color-mode="dark"/);
    assert.doesNotMatch(css, /data-color-mode="light"/);
    assert.match(css, /--trace: #2495FF/);
  });
});
