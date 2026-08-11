/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Modal } from "@mantine/core";
import {
  type ReactNode,
  type RefObject,
  useCallback,
  useEffect,
  useLayoutEffect,
  useState,
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
  const [contentElement, setContentElement] = useState<HTMLDivElement | null>(null);
  const contentRef = useCallback((element: HTMLDivElement | null) => setContentElement(element), []);

  useLayoutEffect(() => {
    const content = contentElement;
    if (!content) return;

    const syncAccessibleName = () => {
      if (content.getAttribute("aria-labelledby") !== labelledBy) {
        content.setAttribute("aria-labelledby", labelledBy);
      }
      if (describedBy) {
        if (content.getAttribute("aria-describedby") !== describedBy) {
          content.setAttribute("aria-describedby", describedBy);
        }
      } else if (content.hasAttribute("aria-describedby")) {
        content.removeAttribute("aria-describedby");
      }
    };

    syncAccessibleName();
    const observer = new MutationObserver(syncAccessibleName);
    observer.observe(content, {
      attributes: true,
      attributeFilter: ["aria-labelledby", "aria-describedby"],
    });
    return () => observer.disconnect();
  }, [contentElement, describedBy, labelledBy]);

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
        ref={contentRef}
        classNames={{ content: className }}
      >
        {children}
      </Modal.Content>
    </Modal.Root>
  );
}
