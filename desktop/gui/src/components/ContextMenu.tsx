/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Menu } from "@mantine/core";
import { type ReactNode, useEffect } from "react";

export interface ContextMenuItem {
  label: string;
  icon?: ReactNode;
  disabled?: boolean;
  dividerBefore?: boolean;
  danger?: boolean;
  onSelect: () => void;
}

export interface ContextMenuState {
  x: number;
  y: number;
  items: ContextMenuItem[];
  label: string;
}

export function ContextMenu({ menu, onClose }: {
  menu: ContextMenuState;
  onClose: () => void;
}) {
  useEffect(() => {
    window.addEventListener("blur", onClose);
    window.addEventListener("resize", onClose);
    window.addEventListener("scroll", onClose, true);
    return () => {
      window.removeEventListener("blur", onClose);
      window.removeEventListener("resize", onClose);
      window.removeEventListener("scroll", onClose, true);
    };
  }, [onClose]);

  return (
    <Menu
      opened
      withinPortal
      position="bottom-start"
      offset={0}
      width={220}
      shadow="lg"
      radius="md"
      trapFocus
      menuItemTabIndex={0}
      onChange={(opened) => {
        if (!opened) onClose();
      }}
    >
      <Menu.Target>
        <span
          aria-hidden="true"
          style={{ position: "fixed", left: menu.x, top: menu.y, width: 1, height: 1, pointerEvents: "none" }}
        />
      </Menu.Target>
      <Menu.Dropdown aria-label={menu.label}>
        {menu.items.map((item, index) => (
          <div key={`${item.label}-${index}`}>
            {item.dividerBefore ? <Menu.Divider /> : null}
            <Menu.Item
              leftSection={item.icon}
              color={item.danger ? "red" : undefined}
              disabled={item.disabled}
              onClick={() => {
                onClose();
                item.onSelect();
              }}
            >
              {item.label}
            </Menu.Item>
          </div>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
