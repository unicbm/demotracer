/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY,
  STARTER_CUSTOM_CSS_PROFILES,
} from "./customCssPresets.ts";

describe("starter custom CSS profiles", () => {
  it("ships the five named local styles", () => {
    assert.equal(CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY, "demotracer.custom-css-starter-profiles.v4");
    assert.deepEqual(
      STARTER_CUSTOM_CSS_PROFILES.map((profile) => profile.name),
      ["汉白玉", "中国新年", "黑金", "紫外", "莫奈"],
    );
  });

  it("provides independent light and dark palettes for every style", () => {
    for (const profile of STARTER_CUSTOM_CSS_PROFILES) {
      assert.match(profile.css, /:root\[data-color-mode="light"\]/, profile.name);
      assert.match(profile.css, /:root\[data-color-mode="dark"\]/, profile.name);
      assert.match(profile.css, /color-scheme: light/, profile.name);
      assert.match(profile.css, /color-scheme: dark/, profile.name);
    }
  });
});
