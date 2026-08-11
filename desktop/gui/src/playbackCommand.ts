/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { FriendlyFireSummary } from "./types";

export type PlaybackToggleOverride = "on" | "off";
export type PlaybackMatchOverride = "off" | "scoreboard";
export type PlaybackFriendlyFireOverride = "auto" | "on" | "off";
export type PlaybackHandoffMode = "off" | "death" | "contact" | "death_or_contact" | "death_contact_c4";

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
  friendlyFire: PlaybackFriendlyFireOverride;
}

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
  friendlyFire: "off",
};

export function formatPlaybackPreset(mask: number): string {
  return `0x${mask.toString(16).toUpperCase().padStart(2, "0")}`;
}

export function buildPlaybackCommand(
  goCommand: string,
  mask: number,
  options: PlaybackPresetOptions,
  retentionCommand?: string | null,
  friendlyFire?: FriendlyFireSummary | null,
): string {
  const defaults = DEFAULT_PLAYBACK_ADVANCED_OPTIONS;
  const commands: string[] = [];
  const friendlyFireEnabled = options.friendlyFire === "on"
    ? true
    : options.friendlyFire === "off"
      ? false
      : friendlyFire?.enabled;
  if (friendlyFireEnabled !== null && friendlyFireEnabled !== undefined) {
    commands.push(`mp_friendlyfire ${friendlyFireEnabled ? 1 : 0}`);
  }
  commands.push(`dtr_preset ${formatPlaybackPreset(mask)}`);
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
