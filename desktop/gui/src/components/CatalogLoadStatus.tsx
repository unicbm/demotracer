/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { TextDictionary } from "../i18n";

export function CatalogLoadStatus({ loading, error, retry, words }: {
  loading: boolean;
  error: boolean;
  retry: () => Promise<void>;
  words: TextDictionary;
}) {
  if (!loading && !error) return null;
  return (
    <div className="catalog-load-status" role={error ? "alert" : "status"}>
      <span>{error ? words.catalogLoadFailed : words.catalogLoading}</span>
      {error ? <button className="quiet-button" type="button" onClick={() => void retry()}>{words.catalogRetry}</button> : null}
    </div>
  );
}
