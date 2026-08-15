/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  createGuiPreferences,
  normalizeGuiPreferences,
} from "./guiPreferences.ts";

describe("GUI preferences document", () => {
  it("builds one versioned appearance document", () => {
    const preferences = createGuiPreferences({
      language: "zh",
      theme: "system",
      uiFontSize: 16,
      sidebarCollapsed: true,
      themeCustomization: { sidebarOpacity: 0.73 },
      customCssProfiles: [{ id: "local-style", name: "Local Style", css: ":root { color: red; }" }],
      activeCustomCssProfileId: "local-style",
    });

    assert.equal(preferences.schemaVersion, 1);
    assert.equal(preferences.language, "zh");
    assert.equal(preferences.appearance.theme, "system");
    assert.equal(preferences.appearance.uiFontSize, 16);
    assert.equal(preferences.appearance.sidebarCollapsed, true);
    assert.equal(preferences.appearance.themeCustomization.sidebarOpacity, 0.73);
    assert.equal(preferences.appearance.activeCustomCssProfileId, "local-style");
  });

  it("rejects an unsupported document version", () => {
    assert.equal(normalizeGuiPreferences({ schemaVersion: 2 }), null);
  });

  it("normalizes bounded values and drops a missing active CSS profile", () => {
    const preferences = normalizeGuiPreferences({
      schemaVersion: 1,
      language: "en",
      appearance: {
        theme: "light",
        uiFontSize: 200,
        sidebarCollapsed: false,
        themeCustomization: { sidebarOpacity: 0.01 },
        customCssProfiles: [],
        activeCustomCssProfileId: "missing",
      },
    });

    assert.equal(preferences?.appearance.uiFontSize, 20);
    assert.equal(preferences?.appearance.themeCustomization.sidebarOpacity, 0.2);
    assert.equal(preferences?.appearance.activeCustomCssProfileId, null);
  });
});
