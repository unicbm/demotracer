/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { EnvironmentDiagnosticReport } from "./types.ts";

export const ENVIRONMENT_REPORT_STORAGE_KEY = "demotracer.environment-report.v2";

export interface StoredEnvironmentReport {
  cs2Path: string;
  report: EnvironmentDiagnosticReport;
}

export function normalizedDiagnosticPath(path: string): string {
  return path.trim().replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
}

export function isEnvironmentDiagnosticReport(value: unknown): value is EnvironmentDiagnosticReport {
  if (!value || typeof value !== "object") return false;
  const report = value as Partial<EnvironmentDiagnosticReport>;
  return Number.isFinite(report.checkedAtMs)
    && typeof report.requestedPath === "string"
    && typeof report.cs2Root === "string"
    && typeof report.gameCsgoPath === "string"
    && ["pass", "warning", "error", "unverified"].includes(String(report.overall))
    && Array.isArray(report.checks)
    && Boolean(report.receipt && typeof report.receipt === "object");
}

export function cachedEnvironmentReport(report: EnvironmentDiagnosticReport): EnvironmentDiagnosticReport {
  const checks = report.checks
    .filter((check) => check.group !== "runtime")
    .map((check) => check.id === "counterStrikeSharp.runtime" && check.status !== "error"
      ? {
          ...check,
          status: "unverified" as const,
          summary: "CounterStrikeSharp files were present at the last inspection; host compatibility requires a fresh inspection.",
          actual: "cached; runtime version unknown",
        }
      : check);

  return {
    ...report,
    cached: true,
    overall: "unverified",
    runtimeVerification: "unknown",
    checks,
  };
}

export function storedEnvironmentReport(expectedCs2Path: string): EnvironmentDiagnosticReport | null {
  const expectedPath = normalizedDiagnosticPath(expectedCs2Path);
  if (!expectedPath) return null;
  try {
    const saved = JSON.parse(localStorage.getItem(ENVIRONMENT_REPORT_STORAGE_KEY) ?? "null") as Partial<StoredEnvironmentReport> | null;
    if (!saved || typeof saved !== "object" || typeof saved.cs2Path !== "string") return null;
    if (normalizedDiagnosticPath(saved.cs2Path) !== expectedPath || !isEnvironmentDiagnosticReport(saved.report)) return null;
    return cachedEnvironmentReport(saved.report);
  } catch {
    return null;
  }
}
