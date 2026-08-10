/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { CheckIcon, CopyIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary } from "../types";

export interface PlaybackPresetOptions {
  weapons: boolean;
  cosmetics: boolean;
  steamIdentity: boolean;
  avatar: boolean;
  voice: boolean;
  playoff: boolean;
  projectileAlignment: PlaybackToggleOverride;
  crosshairAlignment: PlaybackToggleOverride;
  leftHandAlignment: PlaybackToggleOverride;
  matchPresentation: PlaybackMatchOverride;
  allowPartial: PlaybackToggleOverride;
  handoffMode: PlaybackHandoffMode;
  handoffScope: "slot" | "all";
  threat360: PlaybackToggleOverride;
  threat360Range: number;
  threat360Los: boolean;
}

export type PlaybackToggleOverride = "on" | "off";
export type PlaybackMatchOverride = "off" | "scoreboard";
export type PlaybackHandoffMode = "off" | "death" | "contact" | "death_or_contact" | "death_contact_c4";

type PlaybackAdvancedOptions = Omit<PlaybackPresetOptions, "weapons" | "cosmetics" | "steamIdentity" | "avatar" | "voice" | "playoff">;

export const DEFAULT_PLAYBACK_ADVANCED_OPTIONS: PlaybackAdvancedOptions = {
  projectileAlignment: "on",
  crosshairAlignment: "on",
  leftHandAlignment: "on",
  matchPresentation: "off",
  allowPartial: "on",
  handoffMode: "death_contact_c4",
  handoffScope: "slot",
  threat360: "on",
  threat360Range: 420,
  threat360Los: true,
};

type CommandMode = "sequence" | "round";

interface PlaybackCommandBuilderProps {
  words: TextDictionary;
  result: ConversionSummary;
  options: PlaybackPresetOptions;
  commandMode: CommandMode;
  sequenceDisabled?: boolean;
  retentionCommand?: string | null;
  copied: boolean;
  onOptionsChange: (patch: Partial<PlaybackPresetOptions>) => void;
  onCommandModeChange: (mode: CommandMode) => void;
  onCopy: (command: string) => void;
}

const PRESET_WEAPONS = 0x01;
const PRESET_COSMETICS = 0x02;
const PRESET_STEAM_IDENTITY = 0x04;
const PRESET_AVATAR = 0x08;
const PRESET_VOICE = 0x10;
const PRESET_PLAYOFF = 0x20;

interface SwitchControlProps {
  checked: boolean;
  disabled?: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

function SwitchControl({ checked, disabled = false, label, onChange }: SwitchControlProps) {
  return (
    <button
      className="switch-control"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function PlaybackOption({
  checked,
  disabled,
  label,
  description,
  onChange,
}: SwitchControlProps & { description: string }) {
  return (
    <div className={`playback-option${disabled ? " is-disabled" : ""}`}>
      <span>
        <strong>{label}</strong>
        <small>{description}</small>
      </span>
      <SwitchControl checked={checked} disabled={disabled} label={label} onChange={onChange} />
    </div>
  );
}

function formatPreset(mask: number): string {
  return `0x${mask.toString(16).toUpperCase().padStart(2, "0")}`;
}

export function buildPlaybackCommand(
  goCommand: string,
  mask: number,
  options: PlaybackPresetOptions,
  retentionCommand?: string | null,
): string {
  const defaults = DEFAULT_PLAYBACK_ADVANCED_OPTIONS;
  const commands = [`dtr_preset ${formatPreset(mask)}`];
  if (options.projectileAlignment !== defaults.projectileAlignment) commands.push(`dtr_align projectiles ${options.projectileAlignment}`);
  if (options.crosshairAlignment !== defaults.crosshairAlignment) commands.push(`dtr_align crosshair ${options.crosshairAlignment}`);
  if (options.leftHandAlignment !== defaults.leftHandAlignment) commands.push(`dtr_align left_hand ${options.leftHandAlignment}`);
  if (options.matchPresentation !== defaults.matchPresentation) commands.push(`dtr_match ${options.matchPresentation}`);
  if (options.allowPartial !== defaults.allowPartial) commands.push(`dtr_partial ${options.allowPartial === "on" ? 1 : 0}`);
  if (options.handoffMode !== defaults.handoffMode || options.handoffScope !== defaults.handoffScope) {
    commands.push(`dtr_handoff ${options.handoffMode} ${options.handoffScope}`);
  }
  if (options.threat360 !== defaults.threat360) {
    commands.push(`dtr_handoff_360 ${options.threat360}`);
  } else if (options.threat360 === "on"
    && (options.threat360Range !== defaults.threat360Range || options.threat360Los !== defaults.threat360Los)) {
    commands.push(`dtr_handoff_360 on ${options.threat360Range} ${options.threat360Los ? "los" : "nolos"}`);
  }
  if (retentionCommand) commands.push(retentionCommand);
  commands.push(goCommand);
  return commands.join("; ");
}

export function PlaybackCommandBuilder({
  words,
  result,
  options,
  commandMode,
  sequenceDisabled = false,
  retentionCommand = null,
  copied,
  onOptionsChange,
  onCommandModeChange,
  onCopy,
}: PlaybackCommandBuilderProps) {
  const cosmeticsAvailable = result.cosmetics.files > 0;
  const voiceAvailable = result.voice.sidecars > 0;
  const effectiveCommandMode: CommandMode = sequenceDisabled ? "round" : commandMode;
  const sequenceMode = effectiveCommandMode === "sequence";

  // Normalize dependencies here as well as in the handlers so stale or
  // manually edited localStorage can never produce an invalid preset.
  const cosmetics = cosmeticsAvailable && options.cosmetics;
  const weapons = options.weapons || cosmetics;
  const avatar = options.avatar;
  const steamIdentity = options.steamIdentity || avatar;
  const voice = voiceAvailable && options.voice;
  const playoff = sequenceMode && options.playoff;

  let mask = 0;
  if (weapons) mask |= PRESET_WEAPONS;
  if (cosmetics) mask |= PRESET_COSMETICS;
  if (steamIdentity) mask |= PRESET_STEAM_IDENTITY;
  if (avatar) mask |= PRESET_AVATAR;
  if (voice) mask |= PRESET_VOICE;
  if (playoff) mask |= PRESET_PLAYOFF;

  const goCommand = effectiveCommandMode === "round"
    ? result.commands.goRound
    : result.commands.goSequence;
  const command = buildPlaybackCommand(goCommand, mask, options, retentionCommand);

  return (
    <section className="playback-command-builder" aria-label={words.playDemoCommand}>
      <div className="playback-command-line">
        <code>{command}</code>
        <button className="primary-button" type="button" onClick={() => onCopy(command)}>
          {copied ? <CheckIcon size={16} /> : <CopyIcon size={16} />}
          {copied ? words.copied : words.copyPlaybackCommand}
        </button>
      </div>

      <section className="playback-config" aria-labelledby="playback-config-title">
        <header className="playback-config-heading">
          <div>
            <strong id="playback-config-title">{words.playbackOptions}</strong>
            <code>Preset {formatPreset(mask)}</code>
          </div>
          {result.rounds.length > 1 ? (
            <div className="playback-mode-tabs" role="group" aria-label={words.playDemoMode}>
              <button
                className={sequenceMode ? "is-selected" : ""}
                type="button"
                aria-pressed={sequenceMode}
                disabled={sequenceDisabled}
                title={sequenceDisabled ? words.sequenceUnavailable : undefined}
                onClick={() => onCommandModeChange("sequence")}
              >
                {words.sequenceMode}
              </button>
              <button
                className={!sequenceMode ? "is-selected" : ""}
                type="button"
                aria-pressed={!sequenceMode}
                onClick={() => onCommandModeChange("round")}
              >
                {words.roundMode}
              </button>
            </div>
          ) : null}
        </header>

        <div className="playback-option-grid" role="group" aria-label={words.playbackOptions}>
          <PlaybackOption
            checked={weapons}
            label={words.syncWeapons}
            description={words.syncWeaponsHelp}
            onChange={(checked) => onOptionsChange(checked
              ? { weapons: true }
              : { weapons: false, cosmetics: false })}
          />
          <PlaybackOption
            checked={steamIdentity}
            label={words.syncSteamIdentity}
            description={words.syncSteamIdentityHelp}
            onChange={(checked) => onOptionsChange(checked
              ? { steamIdentity: true }
              : { steamIdentity: false, avatar: false })}
          />
          {voiceAvailable ? <PlaybackOption checked={voice} label={words.syncVoice} description={words.syncVoiceHelp} onChange={(checked) => onOptionsChange({ voice: checked })} /> : null}
          {cosmeticsAvailable ? (
            <PlaybackOption
              checked={cosmetics}
              label={words.syncCosmetics}
              description={words.syncCosmeticsHelp}
              onChange={(checked) => onOptionsChange(checked
                ? { cosmetics: true, weapons: true }
                : { cosmetics: false })}
            />
          ) : null}
          <PlaybackOption
            checked={avatar}
            label={words.syncAvatar}
            description={words.syncAvatarHelp}
            onChange={(checked) => onOptionsChange(checked
              ? { avatar: true, steamIdentity: true }
              : { avatar: false })}
          />
          <PlaybackOption checked={playoff} disabled={!sequenceMode} label={words.playoffBeta} description={words.playoffHelp} onChange={(checked) => onOptionsChange({ playoff: checked })} />
        </div>

      </section>
    </section>
  );
}
