/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useSyncExternalStore } from "react";

export interface CatalogState<T> {
  data: T | null;
  loading: boolean;
  error: boolean;
}

export function createCatalogResource<T>(loader: () => Promise<T>) {
  let state: CatalogState<T> = { data: null, loading: false, error: false };
  let pending: Promise<void> | null = null;
  const listeners = new Set<() => void>();
  const publish = (next: CatalogState<T>) => {
    state = next;
    listeners.forEach((listener) => listener());
  };
  return {
    getSnapshot: () => state,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    load() {
      if (pending) return pending;
      if (state.data !== null) return Promise.resolve();
      publish({ data: null, loading: true, error: false });
      pending = Promise.resolve().then(loader).then(
        (data) => publish({ data, loading: false, error: false }),
        () => publish({ data: null, loading: false, error: true }),
      ).finally(() => { pending = null; });
      return pending;
    },
  };
}

export function createJsonCatalogResource<T>(url: string) {
  return createCatalogResource<T>(async () => {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Catalog request failed: ${response.status}`);
    return await response.json() as T;
  });
}

export function useCatalogResource<T>(resource: ReturnType<typeof createCatalogResource<T>>, enabled: boolean) {
  const state = useSyncExternalStore(resource.subscribe, resource.getSnapshot);
  useEffect(() => {
    if (enabled) void resource.load();
  }, [resource, enabled]);
  return { ...state, loading: enabled && state.data === null && !state.error, retry: resource.load };
}
