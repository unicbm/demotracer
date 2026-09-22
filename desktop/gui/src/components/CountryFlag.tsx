/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Keep every supported country, but ship only the aspect ratio used by the UI.
const flags = import.meta.glob<string>("../../node_modules/flag-icons/flags/4x3/*.svg", {
  eager: true,
  query: "?url&no-inline",
  import: "default",
});

export function CountryFlag({ code, country, className }: {
  code: string;
  country: string;
  className?: string;
}) {
  const path = (value: string) => `../../node_modules/flag-icons/flags/4x3/${value}.svg`;
  const source = flags[path(code.toLowerCase())] ?? flags[path("xx")];
  return <img src={source} alt={country} title={country} className={className} loading="lazy" decoding="async" />;
}
