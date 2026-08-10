/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Theme } from "./types";

export type ResolvedTheme = Exclude<Theme, "system">;
export const THEME_STORAGE_KEY = "demotracer.theme";
export const SIDEBAR_COLLAPSED_STORAGE_KEY = "demotracer.sidebar-collapsed.v2";
export const THEME_CUSTOMIZATION_STORAGE_KEY = "demotracer.theme-customization.v1";
export const THEME_CUSTOMIZATION_STYLE_ID = "demotracer-theme-customization";
export const CUSTOM_CSS_STORAGE_KEY = "demotracer.custom-css.v1";
export const CUSTOM_CSS_STYLE_ID = "demotracer-custom-css";
export const LEGACY_APPEARANCE_STORAGE_KEYS = [
  "demotracer.ui-skin.v1",
  "demotracer.sidebar-width.v1",
  "demotracer.sidebar-collapsed.v1",
] as const;
export const THEME_BACKGROUNDS: Record<ResolvedTheme, string> = {
  light: "#f5f6f8",
  dark: "#20212b",
};
export const UI_SCALE_STEPS = [0.9, 1, 1.1, 1.25] as const;
export type UiScale = (typeof UI_SCALE_STEPS)[number];

export interface ThemePalette {
  primary: string;
  secondary: string;
  textPrimary: string;
  textSecondary: string;
  info: string;
  warning: string;
  danger: string;
  success: string;
}

export interface ThemeCustomization {
  light?: ThemePalette;
  dark?: ThemePalette;
  fontFamily?: string;
}

export const THEME_PALETTE_DEFAULTS: Record<ResolvedTheme, ThemePalette> = {
  light: {
    primary: "#0A84FF",
    secondary: "#B96E0C",
    textPrimary: "#20242A",
    textSecondary: "#5F6670",
    info: "#176B87",
    warning: "#8A5200",
    danger: "#B3263B",
    success: "#19734A",
  },
  dark: {
    primary: "#2495FF",
    secondary: "#F0A23A",
    textPrimary: "#F5F6F8",
    textSecondary: "#C4C8D0",
    info: "#6CC7E5",
    warning: "#F4B65E",
    danger: "#FF7A88",
    success: "#64D394",
  },
};

const THEME_COLOR_PATTERN = /^#[0-9a-f]{6}(?:[0-9a-f]{2})?$/i;
const FONT_FAMILY_PATTERN = /^[\p{L}\p{N}\s"',._-]*$/u;
const THEME_PALETTE_KEYS = [
  "primary",
  "secondary",
  "textPrimary",
  "textSecondary",
  "info",
  "warning",
  "danger",
  "success",
] as const satisfies readonly (keyof ThemePalette)[];

export function resolveTheme(theme: Theme, systemDark: boolean): ResolvedTheme {
  if (theme === "system") return systemDark ? "dark" : "light";
  return theme;
}

export function normalizeTheme(value: unknown): Theme {
  return value === "light" || value === "dark" || value === "system"
    ? value
    : "dark";
}

export function normalizeSidebarCollapsed(value: unknown): boolean {
  return value === true || value === "true";
}

export function themeBackground(theme: ResolvedTheme): string {
  return THEME_BACKGROUNDS[theme];
}

export function toggleResolvedTheme(theme: Theme, systemDark: boolean): ResolvedTheme {
  return resolveTheme(theme, systemDark) === "dark" ? "light" : "dark";
}

export function normalizeUiScale(value: unknown): UiScale {
  if (value === null || value === undefined || value === "") return 1;
  const numeric = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(numeric)) return 1;
  return UI_SCALE_STEPS.reduce((nearest, candidate) => (
    Math.abs(candidate - numeric) < Math.abs(nearest - numeric) ? candidate : nearest
  ), 1 as UiScale);
}

export function recommendedUiScale(
  screenWidth: number,
  screenHeight: number,
  devicePixelRatio: number,
): UiScale {
  const ratio = Number.isFinite(devicePixelRatio) && devicePixelRatio > 0 ? devicePixelRatio : 1;
  const physicalWidth = Math.max(0, screenWidth) * ratio;
  const physicalHeight = Math.max(0, screenHeight) * ratio;
  const longEdge = Math.max(physicalWidth, physicalHeight);
  const shortEdge = Math.min(physicalWidth, physicalHeight);
  return longEdge >= 3000 && shortEdge >= 1600 ? 1.1 : 1;
}

export function normalizeCustomCss(value: unknown): string {
  return typeof value === "string" ? value.slice(0, 65_536) : "";
}

export function isThemeColor(value: unknown): value is string {
  return typeof value === "string" && THEME_COLOR_PATTERN.test(value.trim());
}

export function isThemeFontFamily(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const fontFamily = value.trim();
  return fontFamily.length <= 200 && FONT_FAMILY_PATTERN.test(fontFamily);
}

function normalizeThemePalette(value: unknown): ThemePalette | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const candidate = value as Record<string, unknown>;
  const palette = {} as ThemePalette;
  for (const key of THEME_PALETTE_KEYS) {
    const color = candidate[key];
    if (!isThemeColor(color)) return undefined;
    palette[key] = color.trim().toUpperCase();
  }
  return palette;
}

export function normalizeThemeCustomization(value: unknown): ThemeCustomization {
  let candidate = value;
  if (typeof value === "string") {
    if (!value.trim()) return {};
    try {
      candidate = JSON.parse(value) as unknown;
    } catch {
      return {};
    }
  }
  if (!candidate || typeof candidate !== "object" || Array.isArray(candidate)) return {};
  const record = candidate as Record<string, unknown>;
  const customization: ThemeCustomization = {};
  const light = normalizeThemePalette(record.light);
  const dark = normalizeThemePalette(record.dark);
  if (light) customization.light = light;
  if (dark) customization.dark = dark;
  if (typeof record.fontFamily === "string") {
    const fontFamily = record.fontFamily.trim().slice(0, 200);
    if (fontFamily && isThemeFontFamily(fontFamily)) customization.fontFamily = fontFamily;
  }
  return customization;
}

export function themePalette(customization: ThemeCustomization, theme: ResolvedTheme): ThemePalette {
  return customization[theme] ?? THEME_PALETTE_DEFAULTS[theme];
}

function paletteCss(palette: ThemePalette): string {
  return [
    `--trace: ${palette.primary}`,
    `--trace-hover: color-mix(in srgb, ${palette.primary} 84%, white)`,
    `--trace-pressed: color-mix(in srgb, ${palette.primary} 82%, black)`,
    `--trace-soft: color-mix(in srgb, ${palette.primary} 16%, transparent)`,
    `--accent: ${palette.primary}`,
    `--accent-hover: color-mix(in srgb, ${palette.primary} 84%, white)`,
    `--accent-pressed: color-mix(in srgb, ${palette.primary} 82%, black)`,
    `--accent-soft: color-mix(in srgb, ${palette.primary} 16%, transparent)`,
    `--focus-ring: ${palette.primary}`,
    `--selected-row: color-mix(in srgb, ${palette.primary} 16%, transparent)`,
    `--hover-row: color-mix(in srgb, ${palette.primary} 10%, transparent)`,
    `--team-a: ${palette.secondary}`,
    `--side-t: ${palette.secondary}`,
    `--text-primary: ${palette.textPrimary}`,
    `--text-secondary: ${palette.textSecondary}`,
    `--info: ${palette.info}`,
    `--warning: ${palette.warning}`,
    `--danger: ${palette.danger}`,
    `--danger-hover: color-mix(in srgb, ${palette.danger} 82%, black)`,
    `--success: ${palette.success}`,
  ].join(";\n  ");
}

export function themeCustomizationCss(customization: ThemeCustomization): string {
  const rules: string[] = [];
  if (customization.fontFamily) rules.push(`:root { --font-ui: ${customization.fontFamily}; }`);
  if (customization.light) {
    rules.push(`:root[data-color-mode="light"] {\n  ${paletteCss(customization.light)};\n}`);
  }
  if (customization.dark) {
    rules.push(`:root[data-color-mode="dark"] {\n  ${paletteCss(customization.dark)};\n}`);
  }
  return rules.join("\n");
}

export function applyThemeCustomization(customization: ThemeCustomization, target: Document = document): void {
  const css = themeCustomizationCss(normalizeThemeCustomization(customization));
  let style = target.getElementById(THEME_CUSTOMIZATION_STYLE_ID) as HTMLStyleElement | null;
  if (!css) {
    style?.remove();
    return;
  }
  if (!style) {
    style = target.createElement("style");
    style.id = THEME_CUSTOMIZATION_STYLE_ID;
    const customCssStyle = target.getElementById(CUSTOM_CSS_STYLE_ID);
    target.head.insertBefore(style, customCssStyle);
  }
  style.textContent = css;
}

export function applyCustomCss(css: string, target: Document = document): void {
  const normalized = normalizeCustomCss(css);
  let style = target.getElementById(CUSTOM_CSS_STYLE_ID) as HTMLStyleElement | null;
  if (!normalized) {
    style?.remove();
    return;
  }
  if (!style) {
    style = target.createElement("style");
    style.id = CUSTOM_CSS_STYLE_ID;
    target.head.append(style);
  }
  style.textContent = normalized;
}

export function stepUiScale(current: number, direction: 1 | -1): UiScale {
  const normalized = normalizeUiScale(current);
  const index = UI_SCALE_STEPS.indexOf(normalized);
  const next = Math.min(UI_SCALE_STEPS.length - 1, Math.max(0, index + direction));
  return UI_SCALE_STEPS[next];
}

export function cycleUiScale(current: number): UiScale {
  const normalized = normalizeUiScale(current);
  const index = UI_SCALE_STEPS.indexOf(normalized);
  return UI_SCALE_STEPS[(index + 1) % UI_SCALE_STEPS.length];
}
