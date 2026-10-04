/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Modal } from "@mantine/core";
import {
  type ReactNode,
  type RefObject,
  useEffect,
} from "react";

export interface DialogPrimitiveProps {
  children: ReactNode;
  labelledBy: string;
  describedBy?: string;
  onDismiss: () => void;
  initialFocusRef?: RefObject<HTMLElement | null>;
  returnFocusRef?: RefObject<HTMLElement | null>;
  dismissOnScrimClick?: boolean;
  scrimClassName?: string;
  className?: string;
}

export function DialogPrimitive({
  children,
  labelledBy,
  describedBy,
  onDismiss,
  initialFocusRef,
  returnFocusRef,
  dismissOnScrimClick = true,
  scrimClassName = "dialog-scrim",
  className = "dialog-surface",
}: DialogPrimitiveProps) {
  useEffect(() => {
    const frame = requestAnimationFrame(() => initialFocusRef?.current?.focus({ preventScroll: true }));
    return () => {
      cancelAnimationFrame(frame);
      if (returnFocusRef?.current?.isConnected) {
        queueMicrotask(() => returnFocusRef.current?.focus({ preventScroll: true }));
      }
    };
  }, [initialFocusRef, returnFocusRef]);

  return (
    <Modal.Root
      opened
      centered
      onClose={onDismiss}
      closeOnClickOutside={dismissOnScrimClick}
      closeOnEscape
      returnFocus={returnFocusRef === undefined}
      trapFocus
      size="auto"
      padding={0}
      radius="lg"
      shadow="xl"
    >
      <Modal.Overlay className={scrimClassName} />
      <Modal.Content
        renderRoot={(props) => <section {...props} aria-labelledby={labelledBy} aria-describedby={describedBy} />}
        classNames={{ content: className }}
      >
        {children}
      </Modal.Content>
    </Modal.Root>
  );
}
