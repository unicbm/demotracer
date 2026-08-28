/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { detailedAnalysisErrorMessage } from "./errorPresentation.ts";

describe("user-facing command errors", () => {
  it("keeps the parser diagnostic when demo analysis fails", () => {
    const message = detailedAnalysisErrorMessage(
      "无法分析此 Demo。请重试或选择其他文件。",
      "packet entity delta references a missing baseline",
      "zh",
    );

    assert.match(message, /无法分析此 Demo/);
    assert.match(message, /packet entity delta references a missing baseline/);
  });
});
