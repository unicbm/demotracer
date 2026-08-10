/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useId, useRef, useState } from "react";
import { ChevronIcon } from "../icons";

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
  const [open, setOpen] = useState(false);
  const controlId = useId().replaceAll(":", "");
  const rootRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const optionRefs = useRef(new Map<string, HTMLButtonElement>());
  const selected = options.find((option) => option.value === value) ?? options[0];
  const enabledOptions = options.filter((option) => !option.disabled);

  useEffect(() => {
    if (!open) return undefined;
    const closeOutside = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setOpen(false);
      triggerRef.current?.focus();
    };
    document.addEventListener("pointerdown", closeOutside);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOutside);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [open]);

  const focusOption = (optionValue = value) => {
    requestAnimationFrame(() => {
      const target = optionRefs.current.get(optionValue)
        ?? optionRefs.current.get(enabledOptions[0]?.value ?? "");
      target?.focus();
    });
  };

  const openAndFocus = (optionValue = value) => {
    setOpen(true);
    focusOption(optionValue);
  };

  const moveFocus = (currentValue: string, direction: 1 | -1) => {
    if (enabledOptions.length === 0) return;
    const currentIndex = enabledOptions.findIndex((option) => option.value === currentValue);
    const nextIndex = (Math.max(0, currentIndex) + direction + enabledOptions.length) % enabledOptions.length;
    optionRefs.current.get(enabledOptions[nextIndex].value)?.focus();
  };

  const choose = (nextValue: string) => {
    if (nextValue !== value) onChange(nextValue);
    setOpen(false);
    triggerRef.current?.focus();
  };

  return (
    <div className={`app-select${open ? " is-open" : ""}`} ref={rootRef}>
      <button
        ref={triggerRef}
        className="app-select-trigger"
        type="button"
        disabled={disabled || options.length === 0}
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={`${controlId}-options`}
        onClick={() => {
          if (open) setOpen(false);
          else openAndFocus();
        }}
        onKeyDown={(event) => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const direction = event.key === "ArrowDown" ? 1 : -1;
          const currentIndex = enabledOptions.findIndex((option) => option.value === value);
          const nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + direction + enabledOptions.length) % enabledOptions.length;
          openAndFocus(enabledOptions[nextIndex]?.value);
        }}
      >
        <span>{selected?.label ?? value}</span>
        <ChevronIcon size={14} />
      </button>
      {open ? (
        <div className="app-select-menu" id={`${controlId}-options`} role="listbox" aria-label={label}>
          {options.map((option) => (
            <button
              className={`app-select-option${value === option.value ? " is-selected" : ""}`}
              type="button"
              role="option"
              aria-selected={value === option.value}
              disabled={option.disabled}
              key={option.value}
              ref={(element) => {
                if (element) optionRefs.current.set(option.value, element);
                else optionRefs.current.delete(option.value);
              }}
              onClick={() => choose(option.value)}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                  event.preventDefault();
                  moveFocus(option.value, event.key === "ArrowDown" ? 1 : -1);
                } else if (event.key === "Home" || event.key === "End") {
                  event.preventDefault();
                  const target = event.key === "Home" ? enabledOptions[0] : enabledOptions.at(-1);
                  if (target) optionRefs.current.get(target.value)?.focus();
                }
              }}
            >
              {option.label}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
