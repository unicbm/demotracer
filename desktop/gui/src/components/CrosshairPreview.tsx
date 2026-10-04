/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Crosshair, CrosshairLegacyV1 } from "csgo-sharecode";
import { useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import ancientSceneUrl from "../assets/crosshair-scenes/ancient.webp";
import anubisSceneUrl from "../assets/crosshair-scenes/anubis.webp";
import cacheSceneUrl from "../assets/crosshair-scenes/cache.webp";
import dust2SceneUrl from "../assets/crosshair-scenes/dust2.webp";
import infernoSceneUrl from "../assets/crosshair-scenes/inferno.webp";
import mirageSceneUrl from "../assets/crosshair-scenes/mirage.webp";
import nukeSceneUrl from "../assets/crosshair-scenes/nuke.webp";
import {
  buildCrosshairRects,
  decodePreviewCrosshair,
  resolveCrosshairColor,
  resolveCrosshairOpacity,
  resolveCrosshairOutline,
  resolveCrosshairViewBox,
} from "../crosshairPreviewModel";
import { createCrosshairCanvas } from "../crosshairCanvas";
import { advanceCrosshairAnimation, buildCrosshairFrame, createCrosshairAnimation, isAnimatedCrosshair } from "../crosshairRenderer";
import { ArrowIcon } from "../icons";
import type { TextDictionary } from "../i18n";

const VIEWBOX_SIZE = 48;
const SCENE_STORAGE_KEY = "demotracer:crosshair-preview-scene:v1";
const PREVIEW_SCENES = [
  { map: "Dust II", src: dust2SceneUrl },
  { map: "Nuke", src: nukeSceneUrl },
  { map: "Mirage", src: mirageSceneUrl },
  { map: "Ancient", src: ancientSceneUrl },
  { map: "Anubis", src: anubisSceneUrl },
  { map: "Cache", src: cacheSceneUrl },
  { map: "Inferno", src: infernoSceneUrl },
] as const;

function storedSceneIndex(): number {
  try {
    const stored = localStorage.getItem(SCENE_STORAGE_KEY);
    const index = PREVIEW_SCENES.findIndex((scene) => scene.map === stored);
    return index >= 0 ? index : 0;
  } catch {
    return 0;
  }
}

function CrosshairSvg({ crosshair }: { crosshair: CrosshairLegacyV1 }) {
  const shapes = buildCrosshairRects(crosshair, VIEWBOX_SIZE);
  const logicalOutline = resolveCrosshairOutline(crosshair);
  const outline = logicalOutline > 0
    ? Math.max(1, Math.round(logicalOutline * VIEWBOX_SIZE / 64))
    : 0;
  const opacity = resolveCrosshairOpacity(crosshair);
  const viewBox = resolveCrosshairViewBox(shapes, outline, VIEWBOX_SIZE);
  return (
    <svg
      className="crosshair-preview-svg"
      viewBox={`${viewBox.x} ${viewBox.y} ${viewBox.size} ${viewBox.size}`}
      aria-hidden="true"
      shapeRendering="crispEdges"
    >
      {outline > 0 ? shapes.map((shape, index) => (
        <rect
          key={`outline-${index}`}
          x={shape.x - outline}
          y={shape.y - outline}
          width={shape.width + outline * 2}
          height={shape.height + outline * 2}
          fill="#050607"
          fillOpacity={opacity}
        />
      )) : null}
      {shapes.map((shape, index) => (
        <rect
          key={`pip-${index}`}
          x={shape.x}
          y={shape.y}
          width={shape.width}
          height={shape.height}
          fill={resolveCrosshairColor(crosshair)}
          fillOpacity={opacity}
        />
      ))}
    </svg>
  );
}

function PixelCrosshair({ crosshair, animate }: { crosshair: Exclude<Crosshair, CrosshairLegacyV1>; animate: boolean }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [cpu, setCpu] = useState(false);
  const parameters = useRef({ crosshair, animate });
  const repaint = useRef<(() => void) | null>(null);
  useLayoutEffect(() => {
    parameters.current = { crosshair, animate };
    repaint.current?.();
  }, [crosshair, animate]);
  useLayoutEffect(() => {
    const target = canvas.current;
    if (!target) return;
    const renderer = createCrosshairCanvas(target, cpu);
    if (!renderer) { if (!cpu) setCpu(true); return; }
    const state = createCrosshairAnimation();
    let request = 0, last = 0, visible = true;
    const paint = (now: number) => {
      const { crosshair, animate } = parameters.current;
      const elapsed = last ? (now - last) / 1000 : 0;
      if (cpu && elapsed > 0 && elapsed < 1 / 30) { request = requestAnimationFrame(paint); return; }
      if (animate) advanceCrosshairAnimation(state, elapsed);
      last = now;
      const width = Math.max(1, Math.floor(target.clientWidth)), height = Math.max(1, Math.floor(target.clientHeight));
      renderer.draw(buildCrosshairFrame(crosshair, { width, height, animate, time: state.time, recoil: state.recoil }), width, height);
      if (animate && visible && !document.hidden) request = requestAnimationFrame(paint);
    };
    const refresh = () => {
      cancelAnimationFrame(request); last = 0;
      if (visible && !document.hidden) paint(performance.now());
    };
    const resize = new ResizeObserver(refresh);
    repaint.current = refresh;
    resize.observe(target);
    const intersection = new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; refresh(); });
    intersection.observe(target);
    const lost = (event: Event) => { event.preventDefault(); setCpu(true); };
    target.addEventListener("webglcontextlost", lost);
    document.addEventListener("visibilitychange", refresh);
    refresh();
    return () => {
      repaint.current = null;
      cancelAnimationFrame(request);
      resize.disconnect(); intersection.disconnect();
      target.removeEventListener("webglcontextlost", lost);
      document.removeEventListener("visibilitychange", refresh);
      renderer.dispose();
    };
  }, [cpu]);
  return <canvas key={String(cpu)} ref={canvas} className="crosshair-preview-canvas" aria-hidden="true" />;
}

export function CrosshairPreview({ code, ...props }: {
  code: string;
  label: string;
  unavailableLabel: string;
  words: TextDictionary;
}) {
  const crosshair = useMemo(() => {
    try { return decodePreviewCrosshair(code); }
    catch { return null; }
  }, [code]);
  return <CrosshairParameterPreview crosshair={crosshair} {...props} />;
}

export function CrosshairParameterPreview({ crosshair, label, unavailableLabel, words, actions }: {
  crosshair: Crosshair | null;
  label: string;
  unavailableLabel: string;
  words: TextDictionary;
  actions?: ReactNode;
}) {
  const [sceneIndex, setSceneIndex] = useState(storedSceneIndex);
  const [animate, setAnimate] = useState(() => !window.matchMedia("(prefers-reduced-motion: reduce)").matches);
  const dynamic = crosshair?.format !== "legacy-v1" && crosshair !== null && isAnimatedCrosshair(crosshair.style);
  const selectScene = (index: number) => {
    setSceneIndex(index);
    try {
      localStorage.setItem(SCENE_STORAGE_KEY, PREVIEW_SCENES[index].map);
    } catch {
      // Scene selection remains usable when persistent browser storage is unavailable.
    }
  };
  const moveScene = (offset: number) => {
    selectScene((sceneIndex + offset + PREVIEW_SCENES.length) % PREVIEW_SCENES.length);
  };

  return (
    <figure className={`crosshair-preview${crosshair ? "" : " is-unavailable"}`} aria-label={crosshair ? label : unavailableLabel}>
      <div className="crosshair-preview-stage">
        <div className="crosshair-preview-scenes" aria-hidden="true">
          {PREVIEW_SCENES.map((scene, index) => (
            <img className={index === sceneIndex ? "is-active" : ""} src={scene.src} alt="" draggable={false} key={scene.map} />
          ))}
        </div>
        <span className="crosshair-preview-map">{PREVIEW_SCENES[sceneIndex].map}</span>
        {crosshair ? (crosshair.format === "legacy-v1"
          ? <CrosshairSvg crosshair={crosshair} />
          : <PixelCrosshair crosshair={crosshair} animate={dynamic && animate} />) : <span aria-hidden="true">×</span>}
        {dynamic ? <button className="crosshair-preview-animation" type="button" aria-pressed={animate}
          onClick={() => setAnimate((value) => !value)}>{animate ? words.crosshairPreviewStop : words.crosshairPreviewPlay}</button> : null}
        <button className="crosshair-scene-arrow is-previous" type="button" onClick={() => moveScene(-1)} aria-label={words.previousCrosshairScene}><ArrowIcon size={16} /></button>
        <button className="crosshair-scene-arrow is-next" type="button" onClick={() => moveScene(1)} aria-label={words.nextCrosshairScene}><ArrowIcon size={16} /></button>
        <div className="crosshair-scene-dots" role="group" aria-label={words.crosshairSceneSelector}>
          {PREVIEW_SCENES.map((scene, index) => (
            <button className={index === sceneIndex ? "is-active" : ""} type="button" onClick={() => selectScene(index)} aria-label={scene.map} aria-current={index === sceneIndex ? "true" : undefined} key={scene.map} />
          ))}
        </div>
        {actions ? <div className="crosshair-preview-actions">{actions}</div> : null}
      </div>
      {crosshair ? <figcaption className="crosshair-preview-note">
        {crosshair.format === "legacy-v1" ? words.crosshairPreviewLegacyReference : words.crosshairPreviewPixelReference}
      </figcaption> : null}
    </figure>
  );
}
