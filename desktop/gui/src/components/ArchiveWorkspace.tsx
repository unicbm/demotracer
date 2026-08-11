/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useState } from "react";
import { AlertIcon, CheckIcon, ChevronIcon, FolderIcon, RefreshIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { InventorySimulatorItem } from "../inventorySimulator";
import { useInventorySimulatorSelection } from "../inventorySimulatorSelection";
import { rosterOpeningSide } from "../openingSide";
import {
  buildReplayRetentionCommand,
  canPrioritizeReplayRoster,
  moveReplayRetentionPlayer,
  normalizeReplayRetentionOrder,
  orderReplayRoster,
  replayRetentionStorageKey,
  type ReplayRetentionOrders,
} from "../replayRetention";
import type { ConversionSummary, DemoLibraryEntry, Language, ManifestArchive, ManifestArchiveRound, PlayerSummary } from "../types";
import { displayMap, MapArtwork, mapArtworkStyle } from "./MapArtwork";
import { PlaybackCommandBuilder, type PlaybackPresetOptions } from "./PlaybackCommandBuilder";
import { PlayerAnalysisWorkspace, type PlayerAnalysisTeam } from "./PlayerAnalysisWorkspace";
import { RosterTeam, type PlayerSelection } from "./PlayerRoster";
import { SteamAvatar, teamRepresentative, useSteamProfiles } from "./SteamProfile";
import type { CommandMode, CopyTarget } from "./TaskViews";
import { WorkspaceBackButton } from "./WorkspaceBackButton";
import "./archive-workspace.css";

interface ArchiveWorkspaceProps {
  words: TextDictionary;
  language: Language;
  archive: ManifestArchive;
  seriesEntries: readonly DemoLibraryEntry[];
  busy: boolean;
  selectedRound: number;
  commandMode: CommandMode;
  playbackPreset: PlaybackPresetOptions;
  copiedTarget: CopyTarget | null;
  selectedPlayer: PlayerSelection | null;
  onSelectRound: (round: number) => void;
  onCommandModeChange: (mode: CommandMode) => void;
  onPlaybackPresetChange: (patch: Partial<PlaybackPresetOptions>) => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  onSyncInventorySimulator: (items: InventorySimulatorItem[], language: Language) => Promise<void>;
  onOpenFolder: () => void;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onClosePlayer: () => void;
  onSelectSeriesMap: (manifestPath: string) => void;
  onBackToLibrary: () => void;
  onReconvert: () => void;
  onChooseManifest: () => void;
}

function fileName(path: string): string {
  const parts = path.split(/[\\/]/);
  return parts.at(-1) || path;
}

function sameManifestPath(left: string, right: string): boolean {
  const normalize = (value: string) => value.trim().replace(/\\/g, "/").toLocaleLowerCase();
  return normalize(left) === normalize(right);
}

function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "—";
  const totalSeconds = Math.max(0, Math.round(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainder = totalSeconds % 60;
  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
  }
  return `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function formatDate(value: number | null | undefined): string {
  if (!value || !Number.isFinite(value)) return "—";
  return new Intl.DateTimeFormat(document.documentElement.lang || undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  }).format(new Date(value));
}

function platformName(value: string): string {
  return value.toLowerCase() === "faceit" ? "FACEIT" : value;
}

function readReplayRetentionOrders(key: string): Partial<ReplayRetentionOrders> | null {
  try {
    const parsed = JSON.parse(localStorage.getItem(key) || "null") as Partial<ReplayRetentionOrders> | null;
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

function writeReplayRetentionOrders(key: string, value: ReplayRetentionOrders): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Playback still works when persistent browser storage is unavailable.
  }
}

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function teamNameFromPlayers(archive: ManifestArchive, identity: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of archive.players) {
    if (player.matchTeam?.toLowerCase() !== identity) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function sameIdentityName(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
}

function playerMatchIdentity(
  player: PlayerSummary,
  teamAName: string,
  teamBName: string,
): "a" | "b" | null {
  const explicit = player.matchTeam?.trim().toLowerCase();
  if (explicit === "a" || explicit === "b") return explicit;

  const teamName = cleanTeamName(player.teamName);
  if (!teamName) return null;
  if (sameIdentityName(teamName, teamAName)) return "a";
  if (sameIdentityName(teamName, teamBName)) return "b";
  return null;
}

function roundStableScore(
  round: ManifestArchiveRound,
  teamAName: string,
  teamBName: string,
): [number, number] | null {
  const scoreboard = round.scoreboard;
  const first = scoreboard?.tTeamName?.trim();
  const second = scoreboard?.ctTeamName?.trim();
  if (!scoreboard || !first || !second) return null;
  const same = (left: string, right: string) => left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
  if (same(first, teamAName) && same(second, teamBName)) return [scoreboard.tScore, scoreboard.ctScore];
  if (same(first, teamBName) && same(second, teamAName)) return [scoreboard.ctScore, scoreboard.tScore];
  return null;
}

function adaptArchiveResult(
  archive: ManifestArchive,
  selected: ManifestArchiveRound,
  playableRounds: ManifestArchiveRound[],
  commandRounds: ManifestArchiveRound[],
): ConversionSummary {
  return {
    root: archive.root,
    manifestPath: archive.manifestPath,
    filesWritten: archive.playableFiles,
    validatedFiles: archive.playableFiles,
    outputBytes: archive.outputBytes,
    roundsExported: playableRounds.length,
    firstExportedRound: selected.round,
    rounds: playableRounds.map((round) => ({ round: round.round, files: round.files })),
    players: archive.players,
    voice: {
      requested: archive.voice.sidecars > 0 ? true : archive.voice.requested,
      sidecars: commandRounds.filter((round) => archive.voice.rounds.includes(round.round)).length,
    },
    cosmetics: {
      requested: archive.cosmetics.files > 0 ? true : archive.cosmetics.requested,
      stickerRequested: archive.cosmetics.stickerFiles > 0 ? true : archive.cosmetics.stickerRequested,
      charmRequested: archive.cosmetics.charmFiles > 0 ? true : archive.cosmetics.charmRequested,
      files: commandRounds.reduce((sum, round) => sum + round.cosmeticFiles, 0),
      stickerFiles: commandRounds.reduce((sum, round) => sum + round.stickerFiles, 0),
      charmFiles: commandRounds.reduce((sum, round) => sum + round.charmFiles, 0),
      preset: archive.cosmetics.preset,
    },
    commands: selected.commands,
  };
}

function ArchiveIssues({ archive, words, language }: { archive: ManifestArchive; words: TextDictionary; language: Language }) {
  if (archive.issues.length === 0) return null;
  return (
    <details className="archive-issues">
      <summary>
        <span><AlertIcon size={15} />{words.archiveIssues}</span>
        <strong>{archive.issues.length}</strong>
        <ChevronIcon size={15} />
      </summary>
      <ul>
        {archive.issues.map((issue, index) => (
          <li className={`is-${issue.severity}`} key={`${issue.code}-${issue.round ?? "all"}-${index}`}>
            {issue.round !== undefined && issue.round !== null ? <b>Round {issue.round}</b> : null}
            <span>{issue.code.toLocaleLowerCase().includes("missing") || issue.code.toLocaleLowerCase().includes("unavailable")
              ? (language === "zh" ? "部分回放内容不可用。" : "Some replay content is unavailable.")
              : issue.code.toLocaleLowerCase().includes("version") || issue.code.toLocaleLowerCase().includes("compat")
                ? (language === "zh" ? "归档版本不兼容，相关内容已跳过。" : "Incompatible archive content was skipped.")
                : (language === "zh" ? "归档包含警告。" : "The archive contains a warning.")}</span>
          </li>
        ))}
      </ul>
    </details>
  );
}

export function ArchiveWorkspace({
  words,
  language,
  archive,
  seriesEntries,
  busy,
  selectedRound,
  commandMode,
  playbackPreset,
  copiedTarget,
  selectedPlayer,
  onSelectRound,
  onCommandModeChange,
  onPlaybackPresetChange,
  onCopy,
  onOpenExternal,
  onSyncInventorySimulator,
  onOpenFolder,
  onSelectPlayer,
  onClosePlayer,
  onSelectSeriesMap,
  onBackToLibrary,
  onReconvert,
  onChooseManifest,
}: ArchiveWorkspaceProps) {
  const playableRounds = archive.rounds.filter((round) => round.available);
  const selected = playableRounds.find((round) => round.round === selectedRound) ?? playableRounds[0];
  const selectedIndex = selected
    ? playableRounds.findIndex((round) => round.round === selected.round)
    : -1;
  const sequenceDisabled = Boolean(selected && selected.sequenceLength === 0);
  const effectiveCommandMode: CommandMode = playableRounds.length <= 1 || sequenceDisabled ? "round" : commandMode;
  const sequenceCount = selected?.sequenceLength ?? 0;
  const commandRounds = selected
    ? effectiveCommandMode === "sequence"
      ? playableRounds.slice(selectedIndex, selectedIndex + sequenceCount)
      : [selected]
    : [];
  const result = selected ? adaptArchiveResult(archive, selected, playableRounds, commandRounds) : null;
  const archiveTitle = archive.displayName || fileName(archive.demoPath) || archive.demoId;
  const currentSeriesEntry = seriesEntries.find((entry) => sameManifestPath(
    entry.manifestPath,
    archive.manifestPath,
  ));
  const currentSeriesOrder = currentSeriesEntry?.series?.order ?? null;
  const teamAName = cleanTeamName(archive.score?.teamA.name) || teamNameFromPlayers(archive, "a") || words.teamA;
  const teamBName = cleanTeamName(archive.score?.teamB.name) || teamNameFromPlayers(archive, "b") || words.teamB;
  const expectedPlayers = Math.max(0, ...playableRounds.map((round) => round.files));
  const rosterPlayers = [...archive.players].sort((left, right) => left.name.localeCompare(right.name));
  const baseTeamARoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === "a");
  const baseTeamBRoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === "b");
  const unassignedRoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === null);
  const teamASteamIds = baseTeamARoster.map((player) => player.steamId);
  const teamBSteamIds = baseTeamBRoster.map((player) => player.steamId);
  const teamASignature = teamASteamIds.join("|");
  const teamBSignature = teamBSteamIds.join("|");
  const retentionKey = replayRetentionStorageKey(archive.demoSha256 || archive.demoId);
  const [retentionOrders, setRetentionOrders] = useState<ReplayRetentionOrders>({ a: [], b: [] });
  useEffect(() => {
    const stored = readReplayRetentionOrders(retentionKey);
    setRetentionOrders({
      a: normalizeReplayRetentionOrder(teamASteamIds, stored?.a),
      b: normalizeReplayRetentionOrder(teamBSteamIds, stored?.b),
    });
  }, [retentionKey, teamASignature, teamBSignature]);
  const teamARetentionOrder = normalizeReplayRetentionOrder(teamASteamIds, retentionOrders.a);
  const teamBRetentionOrder = normalizeReplayRetentionOrder(teamBSteamIds, retentionOrders.b);
  const teamARoster = orderReplayRoster(baseTeamARoster, teamARetentionOrder);
  const teamBRoster = orderReplayRoster(baseTeamBRoster, teamBRetentionOrder);
  const teamAOpeningSide = rosterOpeningSide(teamARoster, playableRounds);
  const teamBOpeningSide = rosterOpeningSide(teamBRoster, playableRounds);
  const setRetentionPriority = (team: "a" | "b", playerIndex: number, priority: number) => {
    const priorityIndex = priority - 1;
    const next: ReplayRetentionOrders = {
      a: team === "a" ? moveReplayRetentionPlayer(teamARetentionOrder, playerIndex, priorityIndex) : teamARetentionOrder,
      b: team === "b" ? moveReplayRetentionPlayer(teamBRetentionOrder, playerIndex, priorityIndex) : teamBRetentionOrder,
    };
    setRetentionOrders(next);
    writeReplayRetentionOrders(retentionKey, next);
  };
  const retentionCommand = buildReplayRetentionCommand({
    a: teamARetentionOrder,
    b: teamBRetentionOrder,
  }, selected ? {
    t: selected.tSteamIds ?? [],
    ct: selected.ctSteamIds ?? [],
  } : null);
  const steamProfiles = useSteamProfiles(rosterPlayers.map((player) => player.steamId));
  const inventorySelection = useInventorySimulatorSelection(
    archive.demoSha256 || archive.demoId,
    onSyncInventorySimulator,
  );
  const teamARepresentative = teamRepresentative(teamAName, teamARoster);
  const teamBRepresentative = teamRepresentative(teamBName, teamBRoster);
  const matchRounds = archive.score
    ? archive.score.teamA.score + archive.score.teamB.score
    : null;
  const scoreDeclaresWinner = Boolean(
    archive.score
    && archive.score.status !== "snapshot"
    && archive.score.teamA.score !== archive.score.teamB.score,
  );
  const teamAWon = scoreDeclaresWinner && archive.score!.teamA.score > archive.score!.teamB.score;
  const teamBWon = scoreDeclaresWinner && archive.score!.teamB.score > archive.score!.teamA.score;
  const playerTeams: PlayerAnalysisTeam[] = [
    { id: "a", name: teamAName, players: teamARoster },
    { id: "b", name: teamBName, players: teamBRoster },
    ...(unassignedRoster.length > 0 ? [{ id: "unknown", name: words.unassignedPlayers, players: unassignedRoster }] : []),
  ];

  if (selectedPlayer !== null) {
    return (
      <PlayerAnalysisWorkspace
        words={words}
        language={language}
        teams={playerTeams}
        steamProfiles={steamProfiles}
        selectedPlayer={selectedPlayer}
        copiedTarget={copiedTarget}
        onSelectPlayer={onSelectPlayer}
        onBack={onClosePlayer}
        onCopy={onCopy}
        onOpenExternal={onOpenExternal}
        inventorySelection={inventorySelection}
      />
    );
  }

  return (
    <section className="archive-workspace" aria-label={archiveTitle} style={mapArtworkStyle(archive.map)}>
      <header className="archive-toolbar">
        <div className="archive-toolbar-context">
          <WorkspaceBackButton label={words.backToLibrary} onClick={onBackToLibrary} />
          {seriesEntries.length > 1 ? (
            <nav className="archive-series-switcher" aria-label={words.seriesMapNavigation}>
              {seriesEntries.map((entry) => {
                const current = sameManifestPath(entry.manifestPath, archive.manifestPath);
                const mapName = displayMap(entry.map);
                return (
                  <button
                    className={current ? "is-current" : ""}
                    type="button"
                    aria-current={current ? "page" : undefined}
                    aria-label={`${words.openArchive}: ${mapName}`}
                    title={mapName}
                    disabled={busy || current || entry.compatibility === "unsupported"}
                    onClick={() => onSelectSeriesMap(entry.manifestPath)}
                    key={entry.manifestPath}
                  >
                    <strong>{mapName}</strong>
                  </button>
                );
              })}
            </nav>
          ) : null}
        </div>
        <div className="archive-toolbar-actions">
          <details className="archive-actions-menu">
            <summary>
              <span>{words.archiveContextMenu}</span><ChevronIcon size={14} />
            </summary>
            <div>
              <button type="button" onClick={onOpenFolder} disabled={busy}>
                <FolderIcon size={15} /><span>{words.openFolder}</span>
              </button>
              <button type="button" onClick={onReconvert} disabled={busy} title={words.reconvertArchiveHelp}>
                <RefreshIcon size={15} /><span>{busy ? words.readingSourceDemo : words.reconvertArchive}</span>
              </button>
            </div>
          </details>
        </div>
      </header>

      <section className="archive-match-hero">
        <div className="archive-map-panel">
          <MapArtwork map={archive.map} loading="eager" />
          <div><span>{currentSeriesOrder ? `MAP ${currentSeriesOrder}` : words.map}</span><strong>{displayMap(archive.map)}</strong></div>
        </div>
        <div className="archive-match-summary">
          <div className={`archive-scoreboard is-${archive.score?.status || "unknown"}`}>
            <div className="archive-score-team is-team-a">
              <div className="archive-score-team-identity">
                <strong title={teamAName}>{teamAName}</strong>
                {teamARepresentative ? <SteamAvatar profile={steamProfiles.get(teamARepresentative.steamId)} fallbackName={teamARepresentative.name} playerColor={teamARepresentative.playerColor} size="hero" /> : null}
              </div>
            </div>
            <div className="archive-scoreline" aria-label={archive.score ? `${teamAName} ${archive.score.teamA.score} : ${archive.score.teamB.score} ${teamBName}` : words.scoreUnavailable}>
              <span className="archive-score-numbers">
                {archive.score ? (
                  <>
                    <b className={scoreDeclaresWinner ? teamAWon ? "is-winner" : "is-loser" : ""}>{archive.score.teamA.score}</b>
                    <i>:</i>
                    <b className={scoreDeclaresWinner ? teamBWon ? "is-winner" : "is-loser" : ""}>{archive.score.teamB.score}</b>
                  </>
                ) : <em>— : —</em>}
              </span>
              {archive.score?.status === "completed" ? <small>{words.scoreAtDemoEnd}</small> : null}
            </div>
            <div className="archive-score-team is-team-b">
              <div className="archive-score-team-identity">
                <strong title={teamBName}>{teamBName}</strong>
                {teamBRepresentative ? <SteamAvatar profile={steamProfiles.get(teamBRepresentative.steamId)} fallbackName={teamBRepresentative.name} playerColor={teamBRepresentative.playerColor} size="hero" /> : null}
              </div>
            </div>
          </div>
          <dl className="archive-match-facts">
            <div><dt>{words.demoSource}</dt><dd>{archive.demoSource ? platformName(archive.demoSource.name) : "—"}</dd></div>
            <div><dt>{words.demoFileTime}</dt><dd>{formatDate(archive.sourceModifiedAtMs)}</dd></div>
            <div><dt>{words.demoDuration}</dt><dd>{formatDuration(archive.durationSeconds)}</dd></div>
            <div><dt>{words.playableRounds}</dt><dd>{playableRounds.length}</dd></div>
          </dl>
        </div>
      </section>

      {rosterPlayers.length > 0 ? (
        <section className="archive-roster" aria-label={words.matchRoster}>
          <div className="archive-roster-grid">
            <RosterTeam teamId="a" name={teamAName} players={teamARoster} words={words} metaLabel={teamAOpeningSide === "t" ? words.startsAsT : teamAOpeningSide === "ct" ? words.startsAsCt : undefined} matchRounds={matchRounds} steamProfiles={steamProfiles} retentionPriority={canPrioritizeReplayRoster(teamASteamIds)} onSetPlayerPriority={(playerIndex, priority) => setRetentionPriority("a", playerIndex, priority)} onSelectPlayer={onSelectPlayer} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            <RosterTeam teamId="b" name={teamBName} players={teamBRoster} words={words} metaLabel={teamBOpeningSide === "t" ? words.startsAsT : teamBOpeningSide === "ct" ? words.startsAsCt : undefined} matchRounds={matchRounds} className="is-team-b" steamProfiles={steamProfiles} retentionPriority={canPrioritizeReplayRoster(teamBSteamIds)} onSetPlayerPriority={(playerIndex, priority) => setRetentionPriority("b", playerIndex, priority)} onSelectPlayer={onSelectPlayer} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            {unassignedRoster.length > 0 ? (
              <RosterTeam teamId="unknown" name={words.unassignedPlayers} players={unassignedRoster} words={words} metaLabel={words.rosterPlayerCount.replace("{count}", String(unassignedRoster.length))} matchRounds={matchRounds} className="is-unassigned" steamProfiles={steamProfiles} onSelectPlayer={onSelectPlayer} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            ) : null}
          </div>
        </section>
      ) : null}

      <div className="archive-split-view">
        <section className="archive-round-pane" aria-labelledby="archive-round-list-title">
          <header className="archive-pane-heading">
            <h2 id="archive-round-list-title">{words.choosePlaybackStart}</h2>
            <strong>{words.archiveRoundsShort.replace("{count}", String(playableRounds.length))}</strong>
          </header>

          <div className="archive-round-list" aria-label={words.choosePlaybackStart}>
            {archive.rounds.map((round) => {
              const active = selected?.round === round.round;
              const stableScore = roundStableScore(round, teamAName, teamBName);
              const playableIndex = playableRounds.findIndex((item) => item.round === round.round);
              const continuation = effectiveCommandMode === "sequence"
                && round.available
                && selectedIndex >= 0
                && playableIndex > selectedIndex
                && playableIndex < selectedIndex + sequenceCount;
              const incomplete = round.available && expectedPlayers > 0 && round.files < expectedPlayers;
              const stateLabel = active
                ? words.playbackStart
                : continuation
                  ? words.inPlaybackRange
                  : round.available ? "" : words.unavailable;

              return (
                <button
                  className={[
                    "archive-round-option",
                    active ? "is-start" : "",
                    continuation ? "is-continuation" : "",
                    round.available ? "" : "is-unavailable",
                  ].filter(Boolean).join(" ")}
                  type="button"
                  aria-pressed={active}
                  aria-label={`R${round.round}${stateLabel ? ` · ${stateLabel}` : ""}`}
                  disabled={!round.available}
                  key={round.round}
                  onClick={() => onSelectRound(round.round)}
                >
                  <span className="archive-round-number">R{round.round}</span>
                  <span className="archive-round-score" title={stableScore ? `${teamAName} / ${teamBName}` : undefined}>
                    {stableScore ? <><b>{stableScore[0]}</b><i>:</i><b>{stableScore[1]}</b></> : "— : —"}
                  </span>
                  <span className="archive-round-meta">
                    <b>{formatDuration(round.durationSeconds)}</b>
                    {round.pistolRound ? <small>{words.pistolRound}</small> : null}
                    {incomplete ? <small className="is-warning">{words.partialRoutes.replace("{count}", String(round.files)).replace("{total}", String(expectedPlayers))}</small> : null}
                  </span>
                  <strong className="archive-round-state">
                    {active ? <CheckIcon size={13} /> : null}
                  </strong>
                </button>
              );
            })}
            {archive.rounds.length === 0 ? (
              <div className="archive-empty-state">
                <AlertIcon size={18} />
                <span>{words.noPlayableRounds}</span>
              </div>
            ) : null}
          </div>
        </section>

        <aside className="archive-playback-pane" aria-label={words.playDemoCommand}>
          {result && selected ? (
            <>
              <PlaybackCommandBuilder
                words={words}
                result={result}
                options={playbackPreset}
                commandMode={effectiveCommandMode}
                sequenceDisabled={sequenceDisabled}
                copied={copiedTarget === "playback"}
                retentionCommand={retentionCommand}
                friendlyFire={archive.friendlyFire}
                onOptionsChange={onPlaybackPresetChange}
                onCommandModeChange={onCommandModeChange}
                onCopy={(command) => onCopy(command, "playback")}
              />

              <ArchiveIssues archive={archive} words={words} language={language} />

            </>
          ) : (
            <div className="archive-no-playable">
              <AlertIcon size={20} />
              <strong>{words.noPlayableRounds}</strong>
              <button className="secondary-button" type="button" onClick={onChooseManifest}>
                {words.openAnotherArchive}
              </button>
              <ArchiveIssues archive={archive} words={words} language={language} />
            </div>
          )}
        </aside>
      </div>
    </section>
  );
}
