/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Button } from "@mantine/core";
import { ArrowIcon } from "../icons";

interface WorkspaceBackButtonProps {
  label: string;
  onClick: () => void;
}

export function WorkspaceBackButton({ label, onClick }: WorkspaceBackButtonProps) {
  return (
    <Button
      type="button"
      variant="subtle"
      color="gray"
      size="compact-md"
      leftSection={(
        <span style={{ display: "inline-flex", transform: "rotate(180deg)" }}>
          <ArrowIcon size={15} />
        </span>
      )}
      styles={{ root: { color: "var(--text-secondary)", fontWeight: "var(--weight-semibold)" } }}
      onClick={onClick}
    >
      {label}
    </Button>
  );
}
