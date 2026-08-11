/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { MantineProvider, createTheme } from "@mantine/core";
import "@mantine/core/styles/baseline.css";
import "@mantine/core/styles/default-css-variables.css";
import "@mantine/core/styles/global.css";
import "@mantine/core/styles/UnstyledButton.css";
import "@mantine/core/styles/Button.css";
import "@mantine/core/styles/Paper.css";
import "@mantine/core/styles/Popover.css";
import "@mantine/core/styles/Menu.css";
import "@mantine/core/styles/Overlay.css";
import "@mantine/core/styles/ModalBase.css";
import "@mantine/core/styles/Modal.css";
import "@mantine/core/styles/ScrollArea.css";
import "@mantine/core/styles/Input.css";
import "@mantine/core/styles/Combobox.css";
import "@mantine/core/styles/InlineInput.css";
import "@mantine/core/styles/Switch.css";
import "@mantine/core/styles/Group.css";
import "@mantine/core/styles/SimpleGrid.css";
import "@mantine/core/styles/Stack.css";
import "@mantine/core/styles/Tabs.css";
import "@mantine/core/styles/Text.css";
import "@mantine/core/styles/Tooltip.css";
import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource-variable/noto-sans/standard.css";
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

const mantineTheme = createTheme({
  fontFamily: "var(--font-ui)",
  primaryColor: "blue",
  defaultRadius: "md",
  respectReducedMotion: true,
});

function MantineShell() {
  const currentColorScheme = (): "light" | "dark" =>
    document.documentElement.dataset.colorMode === "dark" ? "dark" : "light";
  const [colorScheme, setColorScheme] = useState<"light" | "dark">(currentColorScheme);

  useEffect(() => {
    const observer = new MutationObserver(() => setColorScheme(currentColorScheme()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-color-mode"] });
    return () => observer.disconnect();
  }, []);

  return (
    <MantineProvider forceColorScheme={colorScheme} theme={mantineTheme}>
      <App />
    </MantineProvider>
  );
}

document.addEventListener("contextmenu", (event) => {
  const target = event.target instanceof Element ? event.target : null;
  const editable = target?.closest('input, textarea, [contenteditable]:not([contenteditable="false"])');
  if (!editable) event.preventDefault();
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <MantineShell />
  </StrictMode>,
);
