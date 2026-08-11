/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type RefObject, useRef } from "react";
import { Button, Group, Stack, Text } from "@mantine/core";
import { ArrowIcon, FolderIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConverterSettings, SideChoice } from "../types";
import { SwitchControl } from "./SwitchControl";

interface ExportInspectorProps {
  words: TextDictionary;
  settings: ConverterSettings;
  selectedRoundCount: number;
  outputDir: string;
  outputRoot: string;
  disabled: boolean;
  onChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onRestoreDefaults: () => void;
  onChooseOutput: () => void;
  onConvert: () => void;
}

function compactPath(path: string, limit = 42): string {
  if (path.length <= limit) return path;
  const keep = Math.floor((limit - 1) / 2);
  return `${path.slice(0, keep)}…${path.slice(-keep)}`;
}

function InspectorContents({
  words,
  settings,
  selectedRoundCount,
  outputDir,
  outputRoot,
  firstControlRef,
  disabled,
  onChange,
  onRequestCosmetics,
  onRestoreDefaults,
  onChooseOutput,
  onConvert,
}: ExportInspectorProps & {
  firstControlRef: RefObject<HTMLButtonElement | null>;
}) {
  const sideOptions: Array<{ value: SideChoice; label: string }> = [
    { value: "both", label: words.both },
    { value: "t", label: words.t },
    { value: "ct", label: words.ct },
  ];
  const canConvert = selectedRoundCount > 0 && Boolean(outputDir) && !disabled;

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

          <div className="field-group playback-range-control">
            <span className="field-label">{words.playbackRange}</span>
            <div className="segmented-control" role="group" aria-label={words.playbackRange}>
              <button
                className={!settings.fullRound ? "is-selected" : ""}
                type="button"
                aria-pressed={!settings.fullRound}
                onClick={() => onChange({ fullRound: false })}
              >
                {words.cutBeforePlant}
              </button>
              <button
                className={settings.fullRound ? "is-selected" : ""}
                type="button"
                aria-pressed={settings.fullRound}
                onClick={() => onChange({ fullRound: true })}
              >
                {words.fullRoundLabel}
              </button>
            </div>
          </div>
          {!settings.fullRound ? (
            <Text className="inspector-field-help" size="xs" c="var(--text-tertiary)">
              {words.cutBeforePlantHelp}
            </Text>
          ) : null}
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
          <div className="setting-line">
            <div><strong>{words.freezePreroll}</strong><small>{words.freezePrerollDefaultHelp}</small></div>
            <span className="setting-value-badge">{words.freezePrerollAutoValue}</span>
          </div>
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

      <footer className="inspector-footer inspector-export-footer">
        <Stack gap="sm">
          <Group justify="space-between" gap="sm" wrap="nowrap">
            <Text size="sm" fw={700} aria-live="polite">
              {selectedRoundCount > 0
                ? words.selectedCount.replace("{count}", String(selectedRoundCount))
                : words.selectAtLeastOne}
            </Text>
            <Button
              type="button"
              variant="subtle"
              color="gray"
              size="compact-sm"
              leftSection={<FolderIcon size={14} />}
              onClick={onChooseOutput}
            >
              {outputDir ? words.changeOutput : words.chooseOutput}
            </Button>
          </Group>
          <div className="inspector-output-path">
            <Text size="xs" c="var(--text-tertiary)">{words.outputParent}</Text>
            <Text component="code" size="xs" title={outputDir}>
              {outputDir ? compactPath(outputDir) : words.notSelected}
            </Text>
            {outputRoot ? (
              <Text size="xs" c="var(--text-tertiary)" title={outputRoot}>
                {words.outputTarget}: {compactPath(outputRoot)}
              </Text>
            ) : null}
          </div>
          <Button
            type="button"
            fullWidth
            size="md"
            rightSection={<ArrowIcon size={16} />}
            disabled={!canConvert}
            loading={disabled}
            onClick={onConvert}
          >
            {disabled ? words.preparing : words.convertCount.replace("{count}", String(selectedRoundCount))}
          </Button>
          <Button type="button" variant="subtle" color="gray" size="compact-sm" onClick={onRestoreDefaults}>
            {words.restoreDefaults}
          </Button>
        </Stack>
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
