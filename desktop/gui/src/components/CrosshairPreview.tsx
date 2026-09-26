/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Crosshair, CrosshairV1 } from "csgo-sharecode";
import { useLayoutEffect, useMemo, useRef, useState } from "react";
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
  rasterizeCrosshair,
  resolveCrosshairColor,
  resolveCrosshairOpacity,
  resolveCrosshairOutline,
  resolveCrosshairViewBox,
} from "../crosshairPreviewModel";
import { ArrowIcon } from "../icons";
import type { TextDictionary } from "../i18n";

const VIEWBOX_SIZE = 48;
const SCENE_STORAGE_KEY = "demotracer:crosshair-preview-scene:v1";
const PREVIEW_SCENES = [
  { map: "Dust II", src: dust2SceneUrl },
  { map: "Mirage", src: mirageSceneUrl },
  { map: "Inferno", src: infernoSceneUrl },
  { map: "Ancient", src: ancientSceneUrl },
  { map: "Nuke", src: nukeSceneUrl },
  { map: "Cache", src: cacheSceneUrl },
  { map: "Anubis", src: anubisSceneUrl },
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

function CrosshairSvg({ crosshair }: { crosshair: CrosshairV1 }) {
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

function PixelCrosshair({ crosshair }: { crosshair: Exclude<Crosshair, CrosshairV1> }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  useLayoutEffect(() => {
    const target = canvas.current;
    const context = target?.getContext("2d");
    if (!target || !context) return;
    const pixels = rasterizeCrosshair(crosshair, VIEWBOX_SIZE);
    target.width = pixels.width;
    target.height = pixels.height;
    const image = context.createImageData(pixels.width, pixels.height);
    image.data.set(pixels.data);
    context.putImageData(image, 0, 0);
  }, [crosshair]);
  return <canvas ref={canvas} className="crosshair-preview-svg" aria-hidden="true" />;
}

export function CrosshairPreview({ code, label, unavailableLabel, words }: {
  code: string;
  label: string;
  unavailableLabel: string;
  words: TextDictionary;
}) {
  const [sceneIndex, setSceneIndex] = useState(storedSceneIndex);
  const crosshair = useMemo(() => {
    try { return decodePreviewCrosshair(code); }
    catch { return null; }
  }, [code]);
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
        {crosshair ? (crosshair.version === 1
          ? <CrosshairSvg crosshair={crosshair} />
          : <PixelCrosshair crosshair={crosshair} />) : <span aria-hidden="true">×</span>}
        <button className="crosshair-scene-arrow is-previous" type="button" onClick={() => moveScene(-1)} aria-label={words.previousCrosshairScene}><ArrowIcon size={16} /></button>
        <button className="crosshair-scene-arrow is-next" type="button" onClick={() => moveScene(1)} aria-label={words.nextCrosshairScene}><ArrowIcon size={16} /></button>
        <div className="crosshair-scene-dots" role="group" aria-label={words.crosshairSceneSelector}>
          {PREVIEW_SCENES.map((scene, index) => (
            <button className={index === sceneIndex ? "is-active" : ""} type="button" onClick={() => selectScene(index)} aria-label={scene.map} aria-current={index === sceneIndex ? "true" : undefined} key={scene.map} />
          ))}
        </div>
      </div>
      {crosshair ? <figcaption className="crosshair-preview-note">
        {crosshair.version === 1 ? words.crosshairPreviewLegacyReference : words.crosshairPreviewPixelReference}
      </figcaption> : null}
    </figure>
  );
}
