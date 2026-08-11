/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { CheckIcon, CopyIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary, FriendlyFireSummary } from "../types";
import { SwitchControl, type SwitchControlProps } from "./SwitchControl";
import {
  buildPlaybackCommand,
  formatPlaybackPreset,
  type PlaybackPresetOptions,
} from "../playbackCommand";
export {
  buildPlaybackCommand,
  DEFAULT_PLAYBACK_ADVANCED_OPTIONS,
  type PlaybackFriendlyFireOverride,
  type PlaybackHandoffMode,
  type PlaybackMatchOverride,
  type PlaybackPresetOptions,
  type PlaybackToggleOverride,
} from "../playbackCommand";

type CommandMode = "sequence" | "round";

interface PlaybackCommandBuilderProps {
  words: TextDictionary;
  result: ConversionSummary;
  options: PlaybackPresetOptions;
  commandMode: CommandMode;
  sequenceDisabled?: boolean;
  retentionCommand?: string | null;
  friendlyFire?: FriendlyFireSummary | null;
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

function PlaybackOption({
  checked,
  disabled,
  label,
  description,
  onChange,
}: SwitchControlProps & { description: string }) {
  return (
    <div className={`playback-option${disabled ? " is-disabled" : ""}`} title={description}>
      <span>
        <strong>{label}</strong>
        <small>{description}</small>
      </span>
      <SwitchControl checked={checked} disabled={disabled} label={label} onChange={onChange} />
    </div>
  );
}

export function PlaybackCommandBuilder({
  words,
  result,
  options,
  commandMode,
  sequenceDisabled = false,
  retentionCommand = null,
  friendlyFire = null,
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
  const command = buildPlaybackCommand(goCommand, mask, options, retentionCommand, friendlyFire);
  const effectiveFriendlyFire = options.friendlyFire === "on"
    ? true
    : options.friendlyFire === "off"
      ? false
      : friendlyFire?.enabled ?? null;
  const friendlyFireDescription = options.friendlyFire === "auto"
    ? effectiveFriendlyFire === true
      ? words.friendlyFireAutoOn
      : effectiveFriendlyFire === false
        ? words.friendlyFireAutoOff
        : words.friendlyFireAutoUnknown
    : effectiveFriendlyFire
      ? words.friendlyFireManualOn
      : words.friendlyFireManualOff;

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
            <code>Preset {formatPlaybackPreset(mask)}</code>
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
          <div className="playback-option">
            <span>
              <strong>{words.friendlyFirePlayback}</strong>
              <small>{friendlyFireDescription}</small>
            </span>
            <div className="playback-option-control">
              {options.friendlyFire !== "auto" ? (
                <button className="text-button" type="button" onClick={() => onOptionsChange({ friendlyFire: "auto" })}>
                  {words.friendlyFireUseDemo}
                </button>
              ) : null}
              <SwitchControl
                checked={effectiveFriendlyFire ?? false}
                label={words.friendlyFirePlayback}
                onChange={(checked) => onOptionsChange({ friendlyFire: checked ? "on" : "off" })}
              />
            </div>
          </div>
        </div>

      </section>
    </section>
  );
}
