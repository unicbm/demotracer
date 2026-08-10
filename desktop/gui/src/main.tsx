/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import {
  applyThemeCustomization,
  LEGACY_APPEARANCE_STORAGE_KEYS,
  normalizeTheme,
  normalizeThemeCustomization,
  resolveTheme,
  themeBackground,
  THEME_CUSTOMIZATION_STORAGE_KEY,
  THEME_STORAGE_KEY,
} from "./appearance";
import "./styles.css";
import "./verge-theme.css";

const initialTheme = normalizeTheme(localStorage.getItem(THEME_STORAGE_KEY));
const initialResolvedTheme = resolveTheme(
  initialTheme,
  window.matchMedia("(prefers-color-scheme: dark)").matches,
);
const initialBackground = themeBackground(initialResolvedTheme);

document.documentElement.dataset.theme = initialTheme;
document.documentElement.dataset.colorMode = initialResolvedTheme;
document.documentElement.style.backgroundColor = initialBackground;
document.body.style.backgroundColor = initialBackground;
document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute("content", initialBackground);
applyThemeCustomization(normalizeThemeCustomization(localStorage.getItem(THEME_CUSTOMIZATION_STORAGE_KEY)));
for (const key of LEGACY_APPEARANCE_STORAGE_KEYS) localStorage.removeItem(key);

document.addEventListener("contextmenu", (event) => {
  const target = event.target instanceof Element ? event.target : null;
  const editable = target?.closest('input, textarea, [contenteditable]:not([contenteditable="false"])');
  if (!editable) event.preventDefault();
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
