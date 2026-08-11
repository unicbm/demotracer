/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Select } from "@mantine/core";

export interface SelectControlOption {
  value: string;
  label: string;
  disabled?: boolean;
}

export function SelectControl({
  value,
  options,
  label,
  disabled = false,
  onChange,
}: {
  value: string;
  options: readonly SelectControlOption[];
  label: string;
  disabled?: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <Select
      className="app-select"
      value={value}
      data={options.map((option) => ({ ...option }))}
      aria-label={label}
      disabled={disabled || options.length === 0}
      allowDeselect={false}
      checkIconPosition="right"
      comboboxProps={{ withinPortal: true, shadow: "lg" }}
      onChange={(nextValue) => {
        if (nextValue !== null && nextValue !== value) onChange(nextValue);
      }}
    />
  );
}
