/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Switch } from "@mantine/core";

export interface SwitchControlProps {
  checked: boolean;
  disabled?: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

export function SwitchControl({
  checked,
  disabled = false,
  label,
  onChange,
}: SwitchControlProps) {
  return (
    <Switch
      className="app-switch"
      checked={checked}
      disabled={disabled}
      aria-label={label}
      size="md"
      onChange={(event) => onChange(event.currentTarget.checked)}
    />
  );
}
