import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { runtimeCatalogs } from "./scripts/runtime-catalog-plugin";

export default defineConfig({
  plugins: [react(), runtimeCatalogs()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    host: "127.0.0.1",
  },
  envPrefix: ["VITE_", "TAURI_ENV_*"],
  build: {
    target: "es2022",
    sourcemap: false,
  },
});
/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
