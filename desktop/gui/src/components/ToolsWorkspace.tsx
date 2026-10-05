/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { ColorPicker, NumberInput, Popover, Slider } from "@mantine/core";
import { crosshairToConVars, encodeCrosshair, type Crosshair } from "csgo-sharecode";
import { useRef, useState } from "react";
import { importCrosshair, type CrosshairEditorSession } from "../crosshairEditor";
import { CloseIcon, CopyIcon, RefreshIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import { CrosshairParameterPreview } from "./CrosshairPreview";
import { DialogPrimitive } from "./Dialog";
import { SelectControl } from "./SelectControl";
import type { CopyTarget } from "./TaskViews";
import "./player-analysis.css";
import "./tools-workspace.css";

function NumericSetting({ label, value, min = 0, max, step = 1, sliderMin = min, sliderMax = max, onChange }: {
  label: string; value: number; min?: number; max: number; step?: number;
  sliderMin?: number; sliderMax?: number; onChange: (value: number) => void;
}) {
  return <div className="crosshair-setting-row">
    <span>{label}</span>
    <div className="crosshair-numeric-control">
      <Slider label={null} thumbLabel={label} value={Math.max(sliderMin, Math.min(sliderMax, value))}
        min={sliderMin} max={sliderMax} step={step} onChange={onChange} size="xs" />
      <NumberInput aria-label={label} value={value} min={min} max={max} step={step} hideControls
        decimalScale={step < 1 ? 2 : 0} clampBehavior="strict" size="xs"
        onChange={(next) => { if (typeof next === "number") onChange(next); }} />
    </div>
  </div>;
}

function ColorSetting({ label, red, green, blue, alpha, onChange, onAlphaChange, words }: {
  label: string; red: number; green: number; blue: number; alpha?: number;
  onChange: (red: number, green: number, blue: number) => void;
  onAlphaChange: (alpha: number) => void; words: TextDictionary;
}) {
  const hex = `#${[red, green, blue].map((v) => v.toString(16).padStart(2, "0")).join("")}`;
  return <div className="crosshair-setting-row"><span>{label}</span>
    <Popover position="bottom-end" width={300} shadow="md" withinPortal trapFocus returnFocus>
      <Popover.Target><button className="crosshair-color-button" type="button" aria-label={label}>
        <i style={{ backgroundColor: hex, opacity: (alpha ?? 255) / 255 }} />
        <code>{hex.toUpperCase()}</code>
      </button></Popover.Target>
      <Popover.Dropdown className="crosshair-color-popover" role="dialog" aria-label={label}>
        <ColorPicker format="hex" value={hex} fullWidth saturationLabel={words.chColorSaturation} hueLabel={words.chColorHue}
          onChange={(value) => onChange(parseInt(value.slice(1, 3), 16), parseInt(value.slice(3, 5), 16), parseInt(value.slice(5, 7), 16))} />
        <div className="crosshair-rgb-fields">
          {[red, green, blue].map((value, index) => <NumberInput key={index} label={[words.chRed, words.chGreen, words.chBlue][index]}
            min={0} max={255} value={value} size="xs" hideControls clampBehavior="strict" allowDecimal={false}
            onChange={(next) => {
              if (typeof next !== "number") return;
              onChange(index === 0 ? next : red, index === 1 ? next : green, index === 2 ? next : blue);
            }} />)}
        </div>
        {alpha !== undefined ? <NumericSetting label={words.chOpacity} value={alpha} max={255} onChange={onAlphaChange} /> : null}
      </Popover.Dropdown>
    </Popover>
  </div>;
}

export function ToolsWorkspace({ words, session, onChange, copiedTarget, onCopy }: {
  words: TextDictionary; session: CrosshairEditorSession; onChange: (session: CrosshairEditorSession) => void;
  copiedTarget: CopyTarget | null; onCopy: (value: string, target: CopyTarget) => void;
}) {
  const [invalid, setInvalid] = useState(false);
  const [sharing, setSharing] = useState(false);
  const shareButton = useRef<HTMLButtonElement>(null);
  const importInput = useRef<HTMLInputElement>(null);
  const c = session.draft;
  const legacy = c.format === "legacy-v1";
  const current = c.format === "cs2-v1";
  const patch = (values: Partial<Crosshair>) => onChange({ ...session, draft: { ...c, ...values } as Crosshair });
  const numeric = (key: keyof Crosshair, label: string, min: number, max: number, step = 1, sliderMin = min, sliderMax = max) => (
    <NumericSetting key={key} label={label} value={c[key] as number} min={min} max={max} step={step}
      sliderMin={sliderMin} sliderMax={sliderMax} onChange={(value) => patch({ [key]: value })} />
  );
  const toggle = (label: string, checked: boolean, onToggle: (checked: boolean) => void) => (
    <div className="crosshair-setting-row"><span>{label}</span><SelectControl label={label} value={String(checked)}
      options={[{ value: "true", label: words.chYes }, { value: "false", label: words.chNo }]}
      onChange={(value) => onToggle(value === "true")} /></div>
  );
  const styles = legacy
    ? [words.chLegacyDefault, words.chLegacyStatic, words.chLegacyClassic, words.chLegacyDynamic, words.chStaticCross, words.chShotCross]
    : [words.chDynamicCross, words.chDynamicCircle, words.chClassicDynamic, words.chStaticCircle, words.chStaticCross,
      words.chShotCross, words.chDotOnly, words.chDynamicQuad, words.chStaticSquare, words.chStaticQuad];
  const maxStyle = legacy ? 5 : c.format === "legacy-v3" ? 7 : c.format === "legacy-v4" ? 8 : 9;
  const styleOrder = legacy ? [0, 1, 2, 3, 4, 5] : [4, 3, 8, 6, 9, 0, 1, 2, 5, 7];
  const cross = legacy || [0, 2, 4, 5, 7].includes(c.style);
  const split = legacy ? c.style <= 3 : c.style === 2;
  const outlined = "outlineMode" in c ? c.outlineMode !== 0 : c.outlineEnabled;
  const code = encodeCrosshair(c);

  return <section className="tools-workspace" aria-labelledby="crosshair-tool-title">
    <div className="crosshair-tool-content">
      <h1 className="sr-only" id="crosshair-tool-title">{words.crosshairEditor}</h1>
      <div className="crosshair-editor-layout">
        <aside className="crosshair-editor-preview">
          <CrosshairParameterPreview crosshair={c} words={words} label={words.crosshairPreview} unavailableLabel={words.crosshairPreviewUnavailable}
            actions={<>
              <button ref={shareButton} type="button" onClick={() => { setInvalid(false); setSharing(true); }}><CopyIcon size={15} />{words.chShareImport}</button>
              <button type="button" onClick={() => onChange({ ...session, draft: session.original })}><RefreshIcon size={15} />{words.chRestore}</button>
            </>} />
          {legacy ? <p className="crosshair-editor-note">{words.chLegacyNote}</p> : null}
        </aside>
        <section className="crosshair-editor-controls" aria-label={words.chShape}>
          <h2>{words.chShape}</h2>
          <div className="crosshair-setting-row"><span>{words.chStyle}</span><SelectControl label={words.chStyle} value={String(c.style)}
            options={styleOrder.filter((value) => value <= maxStyle).map((value) => ({ label: styles[value], value: String(value) }))}
            onChange={(value) => patch({ style: Number(value) })} /></div>
          {legacy ? <div className="crosshair-setting-row"><span>{words.chColor}</span><SelectControl label={words.chColor} value={String(c.color)}
            options={[words.chRed, words.chGreen, words.chYellow, words.chBlue, words.chCyan, words.chCustom].map((label, value) => ({ label, value: String(value) }))}
            onChange={(value) => patch({ color: Number(value) })} /></div> : null}
          {!legacy || c.color === 5 ? <ColorSetting label={words.chColor} red={c.red} green={c.green} blue={c.blue} words={words}
            alpha={!legacy || c.alphaEnabled ? c.alpha : undefined} onAlphaChange={(alpha) => patch({ alpha })}
            onChange={(red, green, blue) => patch({ red, green, blue })} /> : null}
          {legacy ? toggle(words.chUseAlpha, c.alphaEnabled, (alphaEnabled) => patch({ alphaEnabled })) : null}
          {legacy && c.color !== 5 && c.alphaEnabled ? numeric("alpha", words.chOpacity, 0, 255) : null}
          <div className="crosshair-setting-row"><span>{words.chOutline}</span><SelectControl label={words.chOutline}
            value={String("outlineMode" in c ? c.outlineMode : Number(c.outlineEnabled))}
            options={[{ label: words.chOutlineFull, value: "1" }, ...("outlineMode" in c ? [{ label: words.chOutlineHalf, value: "2" }] : []), { label: words.chOutlineNone, value: "0" }]}
            onChange={(value) => patch("outlineMode" in c ? { outlineMode: Number(value) } : { outlineEnabled: value === "1" })} /></div>
          {outlined && current ? <ColorSetting label={words.chOutlineColor} red={c.outlineRed} green={c.outlineGreen} blue={c.outlineBlue} words={words}
            alpha={c.outlineAlpha} onAlphaChange={(outlineAlpha) => patch({ outlineAlpha })}
            onChange={(outlineRed, outlineGreen, outlineBlue) => patch({ outlineRed, outlineGreen, outlineBlue })} /> : null}
          {outlined && legacy ? <NumericSetting label={words.chOutlineThickness} value={c.outline} max={3} step={0.5} onChange={(outline) => patch({ outline })} /> : null}
          {numeric("thickness", words.chThickness, 0, legacy ? 25.5 : current ? 32 : 31, legacy ? 0.1 : 1, 0, legacy ? 6 : current ? 32 : 31)}
          {legacy || c.style !== 6 ? toggle(words.chCenterDot, c.centerDotEnabled, (centerDotEnabled) => patch({ centerDotEnabled })) : null}
          {cross ? numeric("length", words.chLength, 0, legacy ? 25.5 : 255, legacy ? 0.1 : 1) : null}
          {legacy || ![1, 6].includes(c.style) ? numeric("gap", words.chGap, legacy ? -12.8 : current ? -3840 : 0, legacy ? 12.7 : current ? 3840 : 255,
            legacy ? 0.1 : 1, legacy ? -12.8 : current && c.style === 2 ? -10 : 0, legacy ? 12.7 : 127) : null}
          {!legacy && [0, 1, 7].includes(c.style) ? <NumericSetting label={words.chSpreadLimit} value={c.dynamicSpreadLimit} max={255} onChange={(dynamicSpreadLimit) => patch({ dynamicSpreadLimit })} /> : null}
          {split ? <>
            {numeric("splitDistance", words.chSplitDistance, 0, legacy ? 7 : 127)}
            {numeric("innerSplitAlpha", words.chInnerAlpha, 0, 1, legacy ? 0.1 : current ? 0.01 : 0.05)}
            {numeric("outerSplitAlpha", words.chOuterAlpha, 0.3, 1, legacy ? 0.1 : current ? 0.01 : 0.05)}
          </> : null}
          {split || c.style === 9 ? numeric("splitSizeRatio", c.style === 9 && !legacy ? words.chQuadSeparation : words.chSplitRatio, 0, 1, legacy ? 0.1 : 0.01) : null}
          {cross ? toggle(words.chTStyle, c.tStyleEnabled, (tStyleEnabled) => patch({ tStyleEnabled })) : null}
          {current ? <>
            {toggle(words.chScopeColor, c.scopeDotUseCrosshairColor, (scopeDotUseCrosshairColor) => patch({ scopeDotUseCrosshairColor }))}
            <NumericSetting label={words.chScopeScale} value={c.scopeDotScale} min={0.1} max={2} step={0.01} onChange={(scopeDotScale) => patch({ scopeDotScale })} />
          </> : null}
          {toggle(words.chRecoil, c.followRecoil, (followRecoil) => patch({ followRecoil }))}
          <details className="crosshair-extra"><summary>{words.chAdditional}</summary>
            {!legacy ? <div className="crosshair-setting-row"><span>{words.chScreenHeight}</span>
              <NumberInput aria-label={words.chScreenHeight} value={c.screenHeight} min={current ? 240 : 0} max={65535} allowDecimal={false} clampBehavior="strict" size="xs"
                onChange={(next) => { if (typeof next === "number") patch({ screenHeight: next }); }} /></div> : null}
            {legacy ? <>
              <NumericSetting label={words.chFixedGap} value={c.fixedCrosshairGap} min={-12.8} max={12.7} step={0.1} onChange={(fixedCrosshairGap) => patch({ fixedCrosshairGap })} />
              {toggle(words.chWeaponGap, c.deployedWeaponGapEnabled, (deployedWeaponGapEnabled) => patch({ deployedWeaponGapEnabled }))}
            </> : null}
            <button className="secondary-button" type="button" onClick={() => onCopy(crosshairToConVars(c).trim(), "crosshair-convars")}><CopyIcon size={14} />{copiedTarget === "crosshair-convars" ? words.copied : words.chCopySettings}</button>
            {current ? <p className="crosshair-editor-note">{words.chScopeNote}</p> : null}
          </details>
        </section>
      </div>
    </div>
    {sharing ? <DialogPrimitive labelledBy="crosshair-share-title" onDismiss={() => setSharing(false)} initialFocusRef={importInput} returnFocusRef={shareButton} className="dialog-surface crosshair-share-dialog">
      <header className="dialog-header"><h2 id="crosshair-share-title">{words.chShareImport}</h2>
        <button className="icon-button" type="button" aria-label={words.close} onClick={() => setSharing(false)}><CloseIcon size={16} /></button>
      </header>
      <div className="crosshair-share-field"><label htmlFor="crosshair-output-code">{words.chOutputCode}</label>
        <div><input className="crosshair-code-input" id="crosshair-output-code" value={code} readOnly onFocus={(event) => event.currentTarget.select()} />
          <button className="secondary-button" type="button" onClick={() => onCopy(code, "crosshair-editor")}>{copiedTarget === "crosshair-editor" ? words.copied : words.chCopyCode}</button></div>
      </div>
      <form className="crosshair-share-field" onSubmit={(event) => {
        event.preventDefault();
        try { onChange(importCrosshair(session.input)); setInvalid(false); setSharing(false); }
        catch { setInvalid(true); }
      }}>
        <label htmlFor="crosshair-import-code">{words.chPasteCode}</label>
        <div><input ref={importInput} data-autofocus className="crosshair-code-input" id="crosshair-import-code" value={session.input} spellCheck={false}
          aria-invalid={invalid} aria-describedby={invalid ? "crosshair-import-error" : undefined}
          onChange={(event) => { onChange({ ...session, input: event.currentTarget.value }); setInvalid(false); }} />
          <button className="primary-button" type="submit" disabled={!session.input.trim()}>{words.chLoad}</button></div>
        {invalid ? <p className="crosshair-import-error" id="crosshair-import-error" role="alert">{words.chInvalidCode}</p> : null}
      </form>
    </DialogPrimitive> : null}
  </section>;
}
