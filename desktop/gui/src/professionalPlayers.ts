/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { professionalsCatalogUrl } from "virtual:demotracer-catalogs";
import { createJsonCatalogResource, useCatalogResource } from "./catalogResource";
import type { ProfessionalPlayerSummary } from "./professionalPlayersCatalog";

export type ProfessionalPlayers = Readonly<Record<string, ProfessionalPlayerSummary>>;
const catalog = createJsonCatalogResource<ProfessionalPlayers>(professionalsCatalogUrl);

export function useProfessionalPlayers(enabled: boolean) {
  return useCatalogResource(catalog, enabled);
}
