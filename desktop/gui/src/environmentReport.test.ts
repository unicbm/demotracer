/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { it } from "node:test";
import {
  cachedEnvironmentReport,
  ENVIRONMENT_REPORT_STORAGE_KEY,
  storedEnvironmentReport,
} from "./environmentReport.ts";
import type { EnvironmentDiagnosticReport } from "./types.ts";

function report(): EnvironmentDiagnosticReport {
  return {
    checkedAtMs: 123,
    requestedPath: "fixture/game/csgo",
    cs2Root: "fixture",
    gameCsgoPath: "fixture/game/csgo",
    overall: "unverified",
    runtimeVerification: "verified",
    checks: [
      { id: "runtime.botController", group: "runtime", status: "pass", title: "ABI", summary: "Live ABI matches" },
      { id: "counterStrikeSharp.runtime", group: "dependencies", status: "unverified", title: "CSS", summary: "Fresh API version", actual: "1.0.375" },
      { id: "metamod.files", group: "dependencies", status: "pass", title: "Files", summary: "Files present" },
    ],
    receipt: { found: false, filesChecked: 0, filesMismatched: 0 },
  };
}

it("cached reports do not retain live ABI or CSS version evidence", () => {
  const live = report();
  const cached = cachedEnvironmentReport(live);
  assert.equal(cached.cached, true);
  assert.equal(cached.runtimeVerification, "unknown");
  assert.equal(cached.overall, "unverified");
  assert.equal(cached.checks.some((check) => check.group === "runtime"), false);
  assert.equal(cached.checks.find((check) => check.id === "counterStrikeSharp.runtime")?.actual, "cached; runtime version unknown");
  assert.equal(live.runtimeVerification, "verified");
});

it("cached missing dependency evidence is not rewritten as installed", () => {
  const live = report();
  live.checks[1] = { ...live.checks[1], status: "error", summary: "Missing CSS files", actual: "missing" };
  const cached = cachedEnvironmentReport(live);
  assert.equal(cached.checks[0].status, "error");
  assert.equal(cached.checks[0].actual, "missing");
});

it("old heuristic reports and reports for another CS2 tree are not restored", (context) => {
  const entries = new Map<string, string>();
  const previous = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  Object.defineProperty(globalThis, "localStorage", {
    configurable: true,
    value: { getItem: (key: string) => entries.get(key) ?? null },
  });
  context.after(() => {
    if (previous) Object.defineProperty(globalThis, "localStorage", previous);
    else Reflect.deleteProperty(globalThis, "localStorage");
  });
  const saved = JSON.stringify({ cs2Path: "fixture/game/csgo", report: report() });
  entries.set("demotracer.environment-report.v1", saved);
  assert.equal(storedEnvironmentReport("fixture/game/csgo"), null);
  entries.set(ENVIRONMENT_REPORT_STORAGE_KEY, saved);
  assert.equal(storedEnvironmentReport("another/game/csgo"), null);
  assert.equal(storedEnvironmentReport("fixture/game/csgo")?.runtimeVerification, "unknown");
  entries.set(ENVIRONMENT_REPORT_STORAGE_KEY, "{invalid");
  assert.equal(storedEnvironmentReport("fixture/game/csgo"), null);
});
