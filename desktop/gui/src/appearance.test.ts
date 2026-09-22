/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  isThemeColor,
  normalizeSidebarCollapsed,
  normalizeSidebarOpacity,
  normalizeCustomCss,
  normalizeCustomCssProfiles,
  normalizeActiveCustomCssProfileId,
  normalizeThemeCustomization,
  normalizeUiFontSize,
  normalizeUiScale,
  normalizeTheme,
  recommendedUiScale,
  resolveTheme,
  stepUiFontSize,
  THEME_PALETTE_DEFAULTS,
  themeCustomizationCss,
  themePalette,
} from "./appearance.ts";

describe("appearance preferences", () => {
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

  it("normalizes the persisted sidebar state", () => {
    assert.equal(normalizeSidebarCollapsed("true"), true);
    assert.equal(normalizeSidebarCollapsed(true), true);
    assert.equal(normalizeSidebarCollapsed("false"), false);
    assert.equal(normalizeSidebarCollapsed(null), false);
  });

  it("keeps the background sidebar opacity within the readable range", () => {
    assert.equal(normalizeSidebarOpacity(0.72), 0.72);
    assert.equal(normalizeSidebarOpacity(0), 0.2);
    assert.equal(normalizeSidebarOpacity(4), 1);
    assert.equal(normalizeSidebarOpacity("invalid"), 0.86);
    const customization = normalizeThemeCustomization({ sidebarOpacity: 0.73 });
    assert.equal(customization.sidebarOpacity, 0.73);
    assert.match(themeCustomizationCss(customization), /--sidebar-background-opacity: 73%/);
  });

  it("normalizes legacy UI scale values for preference migration", () => {
    assert.equal(normalizeUiScale("1.1"), 1.1);
    assert.equal(normalizeUiScale(1.22), 1.25);
    assert.equal(normalizeUiScale(null), 1);
    assert.equal(normalizeUiScale("invalid"), 1);
  });

  it("normalizes editable UI font sizes without blocking intermediate input", () => {
    assert.equal(normalizeUiFontSize("15"), 15);
    assert.equal(normalizeUiFontSize(12), 13);
    assert.equal(normalizeUiFontSize(24), 20);
    assert.equal(normalizeUiFontSize("invalid"), 15);
    assert.equal(stepUiFontSize(15, 1), 16);
    assert.equal(stepUiFontSize(13, -1), 13);
  });

  it("recommends the larger first-run scale only for high-resolution displays", () => {
    assert.equal(recommendedUiScale(1920, 1080, 1), 1);
    assert.equal(recommendedUiScale(2560, 1440, 1), 1);
    assert.equal(recommendedUiScale(2560, 1440, 1.5), 1.1);
    assert.equal(recommendedUiScale(1920, 1080, 2), 1.1);
  });

  it("keeps custom CSS local storage bounded and ignores non-text values", () => {
    assert.equal(normalizeCustomCss(".card { border-radius: 18px; }"), ".card { border-radius: 18px; }");
    assert.equal(normalizeCustomCss(null), "");
    assert.equal(normalizeCustomCss("x".repeat(70_000)).length, 65_536);
  });

  it("normalizes named custom CSS profiles and their active selection", () => {
    const profiles = normalizeCustomCssProfiles(JSON.stringify([
      { id: "hanbaiyu", name: " 汉白玉 ", css: ":root { --accent: #24765f; }" },
      { id: "hanbaiyu", name: "duplicate", css: "body {}" },
      { id: "bad id", name: "invalid", css: "body {}" },
      { id: "empty", name: "", css: "body {}" },
    ]));
    assert.deepEqual(profiles, [{ id: "hanbaiyu", name: "汉白玉", css: ":root { --accent: #24765f; }" }]);
    assert.equal(normalizeActiveCustomCssProfileId("hanbaiyu", profiles), "hanbaiyu");
    assert.equal(normalizeActiveCustomCssProfileId("missing", profiles), null);
  });

  it("normalizes visual theme settings without accepting CSS fragments", () => {
    const customization = normalizeThemeCustomization(JSON.stringify({
      dark: { ...THEME_PALETTE_DEFAULTS.dark, primary: "#0a84ff" },
      fontFamily: '"Segoe UI Variable", sans-serif',
      monoFontFamily: '"Cascadia Mono", Consolas',
    }));
    assert.equal(customization.dark?.primary, "#0A84FF");
    assert.equal(customization.fontFamily, '"Segoe UI Variable", sans-serif');
    assert.equal(customization.monoFontFamily, '"Cascadia Mono", Consolas');
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
  });
});
