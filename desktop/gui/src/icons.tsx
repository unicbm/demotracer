/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useId, type SVGProps } from "react";

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

const base = (size: number): SVGProps<SVGSVGElement> => ({
  width: size,
  height: size,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.7,
  strokeLinecap: "round",
  strokeLinejoin: "round",
  "aria-hidden": true,
});

export function TraceMark({ size = 34, ...props }: IconProps) {
  const gradientId = `demotracer-mark-${useId().replaceAll(":", "")}`;
  return (
    <svg {...base(size)} viewBox="0 0 1024 1024" {...props}>
      <defs>
        <linearGradient id={gradientId} x1="0.24" y1="0.04" x2="0.75" y2="0.97">
          <stop offset="0" stopColor="#FF5FA8" />
          <stop offset="0.5" stopColor="#A744DF" />
          <stop offset="1" stopColor="#3157FF" />
        </linearGradient>
      </defs>
      <circle cx="512" cy="512" r="460" fill={`url(#${gradientId})`} stroke="none" />
      <circle cx="512" cy="512" r="454" fill="none" stroke="#FFFFFF" strokeOpacity="0.22" strokeWidth="8" />
      <g fill="#FFFFFF" stroke="none">
        <path d="M334 656V286c0-12 10-22 22-22h158c98 0 181 62 214 149h-74c-28-49-80-82-140-82H402v258h90v67H334Z" />
        <path d="M680 508h72c-5 96-60 178-145 218v-77c42-31 69-82 73-141Z" />
        <rect x="438" y="423" width="326" height="66" rx="11" />
        <rect x="520" y="460" width="70" height="302" rx="11" />
      </g>
    </svg>
  );
}

export function FolderIcon({ size = 20, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M3.5 6.8h6l1.8 2h9.2v9.4a1.8 1.8 0 0 1-1.8 1.8H5.3a1.8 1.8 0 0 1-1.8-1.8V6.8Z" />
      <path d="M3.5 9h17" />
    </svg>
  );
}

export function ArrowIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M5 12h13.5M14 7.5l4.5 4.5-4.5 4.5" />
    </svg>
  );
}

export function SlidersIcon({ size = 19, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4 7h5m4 0h7M9 4v6M4 17h9m4 0h3m-3-3v6" />
    </svg>
  );
}

export function CheckIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m5 12.3 4.2 4.2L19 6.8" />
    </svg>
  );
}

export function ChevronIcon({ size = 17, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m7.5 9.5 4.5 4.5 4.5-4.5" />
    </svg>
  );
}

export function SidebarIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <rect x="3.5" y="4" width="17" height="16" rx="2.2" />
      <path d="M9 4v16" />
      <path d="M6.2 8h.1M6.2 11.2h.1M6.2 14.4h.1" />
    </svg>
  );
}

export function NoteIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M5 4.5h14v15H5z" />
      <path d="M8.5 8h7M8.5 11.5h7M8.5 15h4.5" />
    </svg>
  );
}

export function CopyIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <rect x="8" y="8" width="11" height="11" rx="2" />
      <path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" />
    </svg>
  );
}

export function CloseIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m6 6 12 12M18 6 6 18" />
    </svg>
  );
}

export function TrashIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4.5 7h15M9 4.5h6l1 2.5M7 7l.8 12h8.4L17 7M10 10.5v5M14 10.5v5" />
    </svg>
  );
}

export function MinimizeIcon({ size = 16, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M6 15.5h12" />
    </svg>
  );
}

export function MaximizeIcon({ size = 16, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <rect x="6" y="6" width="12" height="12" rx="0.8" />
    </svg>
  );
}

export function RestoreIcon({ size = 16, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M9 8V6h9v9h-2" />
      <rect x="6" y="9" width="9" height="9" rx="0.8" />
    </svg>
  );
}

export function AlertIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M12 3.2 21 19H3L12 3.2Z" />
      <path d="M12 9v4.5M12 16.8v.1" />
    </svg>
  );
}

export function ReplayIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4.5 9A8 8 0 1 1 4 14" />
      <path d="M4.5 4.8V9h4.2" />
    </svg>
  );
}

export function LibraryIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4 5.5h5.5v5.5H4zM14.5 5.5H20v5.5h-5.5zM4 14h5.5v5.5H4zM14.5 14H20v5.5h-5.5z" />
    </svg>
  );
}

export function SearchIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <circle cx="10.5" cy="10.5" r="6" />
      <path d="m15 15 4.5 4.5" />
    </svg>
  );
}

export function RefreshIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M19 8.5A7.5 7.5 0 0 0 5.7 6.8L4 8.5" />
      <path d="M4 4.5v4h4" />
      <path d="M5 15.5a7.5 7.5 0 0 0 13.3 1.7l1.7-1.7" />
      <path d="M20 19.5v-4h-4" />
    </svg>
  );
}

export function PlusIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

export function ExternalLinkIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M13 5h6v6M19 5l-8 8" />
      <path d="M17 13v5a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 5 18v-9a1.5 1.5 0 0 1 1.5-1.5h5" />
    </svg>
  );
}

export function HelpIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>
      <circle cx="12" cy="12" r="9" />
      <path d="M9.8 9a2.45 2.45 0 1 1 3.62 2.15c-.88.5-1.42 1.02-1.42 2.1" />
      <path d="M12 17h.01" />
    </svg>
  );
}

export function GithubIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" stroke="none" aria-hidden="true" {...props}>
      <path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.88c-2.78.6-3.37-1.18-3.37-1.18-.45-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.61.07-.61 1 .07 1.53 1.03 1.53 1.03.9 1.53 2.35 1.09 2.92.83.09-.65.35-1.09.64-1.34-2.22-.25-4.56-1.11-4.56-4.94 0-1.09.39-1.98 1.03-2.68-.1-.25-.45-1.27.1-2.64 0 0 .84-.27 2.75 1.02A9.55 9.55 0 0 1 12 6.82a9.5 9.5 0 0 1 2.5.34c1.91-1.3 2.75-1.02 2.75-1.02.55 1.37.2 2.39.1 2.64.64.7 1.03 1.59 1.03 2.68 0 3.84-2.34 4.68-4.57 4.93.36.31.68.92.68 1.85v2.77c0 .27.18.58.69.48A10 10 0 0 0 12 2Z" />
    </svg>
  );
}
