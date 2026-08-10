/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type RefObject, useRef } from "react";
import type { TextDictionary } from "../i18n";
import type { ConverterSettings, SideChoice } from "../types";

interface SwitchControlProps {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

function SwitchControl({ checked, label, onChange }: SwitchControlProps) {
  return (
    <button
      className="switch-control"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

interface ExportInspectorProps {
  words: TextDictionary;
  settings: ConverterSettings;
  disabled: boolean;
  onChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onRestoreDefaults: () => void;
}

function InspectorContents({
  words,
  settings,
  firstControlRef,
  disabled,
  onChange,
  onRequestCosmetics,
  onRestoreDefaults,
}: ExportInspectorProps & {
  firstControlRef: RefObject<HTMLButtonElement | null>;
}) {
  const sideOptions: Array<{ value: SideChoice; label: string }> = [
    { value: "both", label: words.both },
    { value: "t", label: words.t },
    { value: "ct", label: words.ct },
  ];

  return (
    <>
      <header className="inspector-header">
        <h2 id="export-inspector-title">{words.inspectorTitle}</h2>
      </header>

      <fieldset className="inspector-controls" disabled={disabled}>
      <div className="inspector-body">
        <section className="inspector-section">
          <h3>{words.playback}</h3>
          <div className="field-group">
            <span className="field-label">{words.side}</span>
            <div className="segmented-control" role="group" aria-label={words.side}>
              {sideOptions.map(({ value, label }, index) => (
                <button
                  ref={index === 0 ? firstControlRef : undefined}
                  className={settings.side === value ? "is-selected" : ""}
                  type="button"
                  aria-pressed={settings.side === value}
                  key={value}
                  onClick={() => onChange({ side: value })}
                >
                  {label}
                </button>
              ))}
            </div>
          </div>

          <fieldset className="choice-fieldset">
            <legend>{words.playbackRange}</legend>
            <label className="radio-choice">
              <input type="radio" name="playback-range" checked={!settings.fullRound} onChange={() => onChange({ fullRound: false })} />
              <span><strong>{words.cutBeforePlant}</strong><small>{words.cutBeforePlantHelp}</small></span>
            </label>
            <label className="radio-choice compact">
              <input type="radio" name="playback-range" checked={settings.fullRound} onChange={() => onChange({ fullRound: true })} />
              <span><strong>{words.fullRoundLabel}</strong></span>
            </label>
          </fieldset>
        </section>

        <section className="inspector-section">
          <h3>{words.media}</h3>
          <div className="setting-line">
            <div><strong>{words.exportVoice}</strong><small>{words.voiceHelp}</small></div>
            <SwitchControl checked={settings.exportVoice} label={words.exportVoice} onChange={(exportVoice) => onChange({ exportVoice })} />
          </div>
        </section>

        <details className="inspector-disclosure">
          <summary>{words.advanced}</summary>
          <label className="number-setting" htmlFor="freeze-preroll">
            <span>{words.freezePreroll}</span>
            <span className="number-input-wrap">
              <input
                id="freeze-preroll"
                type="number"
                min="0"
                max="120"
                step="1"
                value={settings.freezePrerollSeconds}
                onChange={(event) => {
                  const value = Math.min(120, Math.max(0, Number(event.target.value) || 0));
                  onChange({ freezePrerollSeconds: value });
                }}
              />
              <i>{words.seconds}</i>
            </span>
          </label>
        </details>

        <section className="inspector-section risk-section">
          <h3>{words.highRisk}</h3>
          <div className="setting-line">
            <div>
              <strong>{words.exportCosmetics}</strong>
              <small>{words.cosmeticsHelp}</small>
            </div>
            <SwitchControl
              checked={settings.exportCosmetics}
              label={words.exportCosmetics}
              onChange={(checked) => {
                if (checked) onRequestCosmetics();
                else onChange({ exportCosmetics: false });
              }}
            />
          </div>
          {settings.exportCosmetics ? (
            <div className="sub-settings">
              <div><span>{words.exportStickers}</span><SwitchControl checked={settings.exportStickers} label={words.exportStickers} onChange={(exportStickers) => onChange({ exportStickers })} /></div>
              <div><span>{words.exportCharms}</span><SwitchControl checked={settings.exportCharms} label={words.exportCharms} onChange={(exportCharms) => onChange({ exportCharms })} /></div>
            </div>
          ) : null}
        </section>
      </div>

      <footer className="inspector-footer">
        <button className="text-button" type="button" onClick={onRestoreDefaults}>{words.restoreDefaults}</button>
      </footer>
      </fieldset>
    </>
  );
}

export function ExportInspector(props: ExportInspectorProps) {
  const firstControlRef = useRef<HTMLButtonElement | null>(null);

  return (
    <aside
      className="export-inspector is-docked"
      aria-labelledby="export-inspector-title"
    >
      <InspectorContents {...props} firstControlRef={firstControlRef} />
    </aside>
  );
}
