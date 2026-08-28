/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Language } from "./types";

export function detailedAnalysisErrorMessage(
  summary: string,
  diagnostic: string,
  language: Language,
): string {
  const detail = diagnostic.trim();
  return detail
    ? `${summary}${language === "zh" ? " 原因：" : " Reason: "}${detail}`
    : summary;
}
