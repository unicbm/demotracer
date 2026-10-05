/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useMemo, useRef, useState } from "react";
import type { TextDictionary } from "../i18n";
import type { WorkspaceBackground } from "../types";
import { DEFAULT_BACKGROUND_CROP, panBackgroundCrop, type BackgroundCrop } from "../workspaceBackground";
import { DialogPrimitive } from "./Dialog";

export function WorkspaceBackgroundImage({ image, crop }: { image: WorkspaceBackground; crop: BackgroundCrop }) {
  const position = `${crop.x * 100}% ${crop.y * 100}%`;
  const backgroundImage = useMemo(() => `url(${image.dataUrl})`, [image.dataUrl]);
  // Large data URLs exceed Chromium's CSS custom-property substitution limit.
  return <div className="workspace-background-image" aria-hidden="true" style={{
    backgroundImage, backgroundPosition: position,
    transform: `scale(${crop.zoom})`, transformOrigin: position,
  }} />;
}

export function WorkspaceBackgroundEditor({ image, initialCrop, words, onSave, onCancel }: {
  image: WorkspaceBackground; initialCrop: BackgroundCrop; words: TextDictionary;
  onSave: (crop: BackgroundCrop) => Promise<void>; onCancel: () => void;
}) {
  const [crop, setCrop] = useState(initialCrop);
  const [aspect, setAspect] = useState(16 / 9);
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const viewport = useRef<HTMLDivElement>(null);
  const pointer = useRef<{ x: number; y: number } | null>(null);
  useEffect(() => {
    const body = document.querySelector(".app-shell");
    if (!body) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry.contentRect.height > 0) setAspect(entry.contentRect.width / entry.contentRect.height);
    });
    observer.observe(body);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    let active = true;
    const bitmap = new Image();
    bitmap.onload = () => { if (active) setReady(true); };
    bitmap.onerror = () => { if (active) setFailed(true); };
    bitmap.src = image.dataUrl;
    return () => { active = false; };
  }, [image]);
  const pan = (dx: number, dy: number) => {
    if (!viewport.current || saving) return;
    const rect = viewport.current.getBoundingClientRect();
    setCrop((value) => panBackgroundCrop(value, image, rect, dx, dy));
  };
  const save = async () => {
    setSaving(true);
    setFailed(false);
    try { await onSave(crop); } catch { setFailed(true); setSaving(false); }
  };
  return <DialogPrimitive labelledBy="background-editor-title" onDismiss={() => { if (!saving) onCancel(); }} className="dialog-surface background-editor">
    <h2 id="background-editor-title">{words.workspaceBackgroundEdit}</h2>
    <p>{words.workspaceBackgroundCropHelp}</p>
    <div ref={viewport} className="background-editor-preview" style={{ aspectRatio: aspect, width: `min(100%, ${55 * aspect}vh)` }}
      tabIndex={0} role="group" aria-label={words.workspaceBackgroundCropHelp}
      onPointerDown={(event) => {
        if (event.button !== 0 || saving) return;
        event.currentTarget.setPointerCapture(event.pointerId);
        pointer.current = { x: event.clientX, y: event.clientY };
      }}
      onPointerMove={(event) => {
        if (!pointer.current) return;
        pan(event.clientX - pointer.current.x, event.clientY - pointer.current.y);
        pointer.current = { x: event.clientX, y: event.clientY };
      }}
      onPointerUp={() => { pointer.current = null; }}
      onPointerCancel={() => { pointer.current = null; }}
      onLostPointerCapture={() => { pointer.current = null; }}
      onKeyDown={(event) => {
        const offset = { ArrowLeft: [-10, 0], ArrowRight: [10, 0], ArrowUp: [0, -10], ArrowDown: [0, 10] }[event.key];
        if (offset) { event.preventDefault(); pan(offset[0], offset[1]); }
      }}>
      <WorkspaceBackgroundImage image={image} crop={crop} />
    </div>
    <small>{words.workspaceBackgroundCropSize.replace("{width}", String(image.width)).replace("{height}", String(image.height)).replace("{ratio}", aspect.toFixed(2))}</small>
    <label className="background-editor-zoom">
      <span>{words.workspaceBackgroundZoom}</span>
      <input type="range" min={1} max={3} step={0.01} value={crop.zoom} disabled={saving}
        onChange={(event) => setCrop((value) => ({ ...value, zoom: Number(event.target.value) }))} />
      <output>{Math.round(crop.zoom * 100)}%</output>
    </label>
    {failed ? <p role="alert">{words.workspaceBackgroundFailed}</p> : null}
    <div className="dialog-actions">
      <button type="button" className="secondary-button" disabled={saving} onClick={() => setCrop(DEFAULT_BACKGROUND_CROP)}>{words.workspaceBackgroundReset}</button>
      <button type="button" className="secondary-button" disabled={saving} onClick={onCancel}>{words.cancel}</button>
      <button type="button" className="primary-button" disabled={saving || !ready} onClick={() => void save()}>{words.save}</button>
    </div>
  </DialogPrimitive>;
}
