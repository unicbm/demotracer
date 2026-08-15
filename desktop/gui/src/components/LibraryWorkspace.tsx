/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import {
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
  type ReactNode,
  useEffect,
  useRef,
  useState,
} from "react";
import { AlertIcon, ArrowIcon, CloseIcon, CopyIcon, FolderIcon, NoteIcon, PlusIcon, RefreshIcon, ReplayIcon, SearchIcon, TraceMark, TrashIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import {
  demoLibraryTimestamp,
  normalizeSourceLinkNoteDismissed,
  SOURCE_LINK_NOTE_DISMISSED_STORAGE_KEY,
} from "../library";
import { resolveProfessionalPlayer } from "../professionalPlayers";
import type { DemoLibraryEntry, DemoLibraryScan, Language, LibraryPlayerSummary, ManifestArchive } from "../types";
import { displayMap, MapArtwork, mapArtworkStyle } from "./MapArtwork";
import { useArchiveTeamAvatar } from "./archiveTeamAvatar";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { DialogPrimitive } from "./Dialog";
import { SteamAvatar, teamRepresentative, useSteamProfiles, type SteamProfileMap } from "./SteamProfile";
import "./library-workspace.css";

export type LibrarySort = "recent" | "map" | "platform";
const LIBRARY_PAGE_SIZE = 50;

interface LibraryWorkspaceProps {
  words: TextDictionary;
  language: Language;
  exportRoot: string;
  roots: string[];
  scan: DemoLibraryScan | null;
  loading: boolean;
  taskBusy: boolean;
  archiveOpenDisabled: boolean;
  repairingManifest: string;
  repairingLibrary: boolean;
  importingArchives: boolean;
  notice: string;
  query: string;
  mapFilter: string;
  platformFilter: string;
  sort: LibrarySort;
  onQueryChange: (value: string) => void;
  onMapFilterChange: (value: string) => void;
  onPlatformFilterChange: (value: string) => void;
  onSortChange: (value: LibrarySort) => void;
  onAddRoot: () => void;
  onRemoveRoot: (root: string) => void;
  onChooseExportRoot: () => void;
  onRefresh: () => void;
  onImportArchives: () => void;
  onRepairLibrary: () => void;
  onConvert: () => void;
  onOpenEntry: (entry: DemoLibraryEntry) => void;
  onInspectEntry: (entry: DemoLibraryEntry) => Promise<ManifestArchive>;
  onRepairEntry: (entry: DemoLibraryEntry) => void;
  onRevealManifest: (entry: DemoLibraryEntry) => void;
  onRevealDemo: (entry: DemoLibraryEntry) => void;
  onCopyManifestPath: (entry: DemoLibraryEntry) => void;
  onCopyDemoPath: (entry: DemoLibraryEntry) => void;
  onSaveNote: (entry: DemoLibraryEntry, note: string) => Promise<boolean>;
  onReparseEntry: (entry: DemoLibraryEntry) => void;
  onDeleteEntry: (entry: DemoLibraryEntry) => void;
}

function formatDateParts(value: number, language: Language): { date: string; time: string; iso?: string } {
  if (!Number.isFinite(value) || value <= 0) return { date: "—", time: "" };
  const locale = language === "zh" ? "zh-CN" : "en-US";
  const date = new Intl.DateTimeFormat(locale, {
    year: "numeric",
    month: "short",
    day: "numeric",
  }).format(new Date(value));
  const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));
  return { date, time, iso: new Date(value).toISOString() };
}

function playedAtTimestamp(value: string | null | undefined): number {
  if (!value?.trim()) return 0;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function formatDuration(value: number | null | undefined): string | null {
  if (!value || !Number.isFinite(value)) return null;
  const totalSeconds = Math.max(0, Math.round(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function formatBytes(value: number | string): string {
  const bytes = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return String(value);
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let amount = bytes / 1024;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }
  return `${amount >= 10 ? amount.toFixed(1) : amount.toFixed(2)} ${units[unit]}`;
}

function formatTickRate(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(2);
}

function platformName(value: string): string {
  return value.toLowerCase() === "faceit" ? "FACEIT" : value;
}

function compatibilityLabel(entry: DemoLibraryEntry, words: TextDictionary): string {
  if (entry.compatibility === "current") return words.versionCurrent;
  if (entry.compatibility === "supported") return words.versionSupported;
  if (entry.compatibility === "legacy") return words.versionLegacy;
  return words.versionUnsupported;
}

function playerSearchText(player: LibraryPlayerSummary): string {
  const professional = resolveProfessionalPlayer(player.steamId);
  return [
    player.name,
    player.steamId,
    player.teamName,
    professional?.handle,
    ...(professional?.aliases ?? []),
    professional?.registry?.nameNative,
    professional?.registry?.nameLatin,
    professional?.registry?.country,
    professional?.hltv?.registeredHandle,
    professional?.hltv?.realName,
  ].filter(Boolean).join(" ").toLowerCase();
}

function entrySearchText(entry: DemoLibraryEntry): string {
  return [
    entry.demoPath,
    entry.sourcePath,
    entry.demoId,
    entry.displayName,
    entry.note,
    entry.map,
    entry.demoSource?.name,
    entry.serverName,
    entry.score?.teamA.name,
    entry.score?.teamB.name,
    ...entry.players.map(playerSearchText),
  ].filter(Boolean).join(" ").toLowerCase();
}

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function sameTeamName(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
}

function teamNameForIdentity(players: LibraryPlayerSummary[], team: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of players) {
    if (player.team?.toLowerCase() !== team) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function playersForIdentity(
  players: LibraryPlayerSummary[],
  team: "a" | "b",
  identity: string | null,
): LibraryPlayerSummary[] {
  const direct = players.filter((player) => player.team?.toLowerCase() === team);
  const matching = direct.length > 0
    ? direct
    : identity
      ? players.filter((player) => player.teamName && sameTeamName(player.teamName, identity))
      : [];
  const unique = new Map<string, LibraryPlayerSummary>();
  for (const player of matching) {
    const name = player.name.trim();
    if (name) unique.set(player.steamId || name.toLocaleLowerCase(), player);
  }
  return [...unique.values()];
}

function libraryTeamContext(entry: DemoLibraryEntry, firstFallback: string, secondFallback: string) {
  const scoreFirstIdentity = cleanTeamName(entry.score?.teamA.name);
  const scoreSecondIdentity = cleanTeamName(entry.score?.teamB.name);
  const firstIdentity = scoreFirstIdentity
    || teamNameForIdentity(entry.players, "a")
    || null;
  const secondIdentity = [
    scoreSecondIdentity,
    teamNameForIdentity(entry.players, "b"),
  ].find((name): name is string => name !== null
    && (!firstIdentity || !sameTeamName(name, firstIdentity))) ?? null;
  const firstName = firstIdentity || firstFallback;
  const secondName = secondIdentity || secondFallback;
  const firstPlayers = playersForIdentity(entry.players, "a", firstIdentity);
  const secondPlayers = playersForIdentity(entry.players, "b", secondIdentity);
  return {
    firstName,
    secondName,
    firstPlayers,
    secondPlayers,
    firstRepresentative: teamRepresentative(firstName, firstPlayers),
    secondRepresentative: teamRepresentative(secondName, secondPlayers),
  };
}

function representativeWithLoadedAvatar(
  teamName: string,
  players: LibraryPlayerSummary[],
  profiles: SteamProfileMap,
): LibraryPlayerSummary | undefined {
  const preferred = teamRepresentative(teamName, players);
  if (preferred && profiles.has(preferred.steamId)) return preferred;
  return players.find((player) => profiles.has(player.steamId)) ?? preferred;
}

function LibraryRow({
  entry,
  seriesOrder,
  seriesScore,
  words,
  language,
  onOpen,
  onRepair,
  onOpenContextMenu,
  repairing,
  disabled,
  taskBusy,
}: {
  entry: DemoLibraryEntry;
  seriesOrder?: number;
  seriesScore?: SeriesMapScore;
  words: TextDictionary;
  language: Language;
  onOpen: () => void;
  onRepair: () => void;
  onOpenContextMenu: (event: ReactMouseEvent) => void;
  repairing: boolean;
  disabled: boolean;
  taskBusy: boolean;
}) {
  const rowRef = useRef<HTMLElement | null>(null);
  const [loadAvatars, setLoadAvatars] = useState(false);
  const {
    firstName,
    secondName,
    firstPlayers,
    secondPlayers,
  } = libraryTeamContext(entry, words.teamA, words.teamB);
  const profileSteamIds = loadAvatars
    ? [...firstPlayers, ...secondPlayers].map((player) => player.steamId)
    : [];
  const profiles = useSteamProfiles(profileSteamIds);
  const firstArchiveAvatar = useArchiveTeamAvatar(entry, firstPlayers, loadAvatars);
  const secondArchiveAvatar = useArchiveTeamAvatar(entry, secondPlayers, loadAvatars);
  const firstRepresentative = representativeWithLoadedAvatar(firstName, firstPlayers, profiles);
  const secondRepresentative = representativeWithLoadedAvatar(secondName, secondPlayers, profiles);
  const firstPlayerNames = firstPlayers.map((player) => player.name);
  const secondPlayerNames = secondPlayers.map((player) => player.name);
  const duration = formatDuration(entry.durationSeconds);
  const demoDate = formatDateParts(demoLibraryTimestamp(entry), language);
  const sourceName = entry.demoSource ? platformName(entry.demoSource.name) : words.unknownPlatform;
  const scoreStatus = entry.score?.status || (entry.scoreIsSnapshot ? "snapshot" : "final");
  const needsMetadata = entry.metadataStatus !== "current";
  const needsSourceLink = !entry.sourcePath || entry.sourceAvailable === false;
  const needsRepair = needsMetadata || needsSourceLink;
  const repairLabel = needsMetadata ? words.repairMetadata : words.linkSourceDemo;
  const repairHelp = needsMetadata ? words.repairMetadataHelp : words.linkSourceDemoHelp;
  const repairActionLabel = repairing
    ? (needsMetadata ? words.repairingMetadata : words.linkingSourceDemo)
    : repairLabel;
  const scoreTitle = scoreStatus === "snapshot"
    ? words.archiveScoreSnapshot
    : scoreStatus === "completed"
      ? words.completedScore
      : undefined;
  useEffect(() => {
    const row = rowRef.current;
    if (!row || loadAvatars) return undefined;
    if (!("IntersectionObserver" in window)) {
      setLoadAvatars(true);
      return undefined;
    }
    const observer = new IntersectionObserver((entries) => {
      if (!entries.some((candidate) => candidate.isIntersecting)) return;
      setLoadAvatars(true);
      observer.disconnect();
    }, { rootMargin: "240px 0px" });
    observer.observe(row);
    return () => observer.disconnect();
  }, [loadAvatars]);

  const activate = () => {
    if (!disabled) onOpen();
  };

  if (seriesOrder) {
    const seriesFirstScore = seriesScore?.first ?? null;
    const seriesSecondScore = seriesScore?.second ?? null;
    const hasSeriesScore = seriesFirstScore !== null && seriesSecondScore !== null;
    const firstWon = hasSeriesScore && seriesFirstScore > seriesSecondScore;
    const secondWon = hasSeriesScore && seriesSecondScore > seriesFirstScore;
    const firstLost = hasSeriesScore && seriesFirstScore < seriesSecondScore;
    const secondLost = hasSeriesScore && seriesSecondScore < seriesFirstScore;
    const seriesScoreLabel = hasSeriesScore
      ? `${seriesFirstScore} : ${seriesSecondScore}`
      : words.scoreUnavailable;
    return (
      <article
        ref={rowRef}
        className="library-series-map-card"
        style={mapArtworkStyle(entry.map)}
        onContextMenu={onOpenContextMenu}
      >
          <button
            className="library-series-map-open"
            type="button"
            disabled={disabled}
            aria-label={`${displayMap(entry.map)}: ${seriesScoreLabel}`}
            onClick={activate}
          >
            <div className="library-series-map-art">
              <MapArtwork map={entry.map} className="library-series-map-artwork" />
              <strong>{displayMap(entry.map)}</strong>
              <div className="library-series-map-score" title={scoreTitle} aria-label={seriesScoreLabel}>
                <b className={firstWon ? "is-winner" : firstLost ? "is-loser" : ""}>
                  {hasSeriesScore ? seriesFirstScore : "—"}
                </b>
                <i>:</i>
                <b className={secondWon ? "is-winner" : secondLost ? "is-loser" : ""}>
                  {hasSeriesScore ? seriesSecondScore : "—"}
                </b>
              </div>
              {entry.note ? <small className="library-series-map-note" title={entry.note}>{entry.note}</small> : null}
            </div>
          </button>
          <div className="library-series-map-badges">
            {entry.compatibility !== "current" ? (
              <span
                className={`library-series-map-indicator is-${entry.compatibility}${entry.compatibility === "unsupported" ? " has-label" : ""}`}
                title={compatibilityLabel(entry, words)}
                aria-label={compatibilityLabel(entry, words)}
                role="img"
              >
                <AlertIcon size={12} />
                {entry.compatibility === "unsupported" ? <span>{compatibilityLabel(entry, words)}</span> : null}
              </span>
            ) : null}
            {needsRepair ? (
              <button
                className="library-series-map-indicator is-action"
                type="button"
                disabled={repairing || disabled || taskBusy}
                title={repairHelp}
                aria-label={repairActionLabel}
                onClick={onRepair}
              >
                {needsMetadata ? <RefreshIcon size={12} /> : <FolderIcon size={12} />}
              </button>
            ) : null}
          </div>
      </article>
    );
  }

  return (
    <article
      ref={rowRef}
      className="library-row"
      style={mapArtworkStyle(entry.map)}
      tabIndex={disabled ? -1 : 0}
      role="button"
      aria-disabled={disabled}
      onClick={activate}
      onKeyDown={(event) => {
        if (event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault();
        activate();
      }}
      onContextMenu={onOpenContextMenu}
    >
      <div className="library-row-date" title={entry.serverName ? `${words.demoServerName}: ${entry.serverName}` : undefined}>
        <time dateTime={demoDate.iso} title={demoDate.iso ? undefined : words.matchTimeUnknown}>
          <strong>{demoDate.date}</strong>
          <small>
            <span>{demoDate.time || words.timeUnknown}</span>
            {entry.demoSource ? <b>{sourceName}</b> : null}
          </small>
        </time>
      </div>
      <div className="library-row-match" aria-label={`${firstName} vs ${secondName}`}>
        <div className="library-row-team">
          {firstRepresentative ? <SteamAvatar overrideUrl={firstArchiveAvatar} profile={profiles.get(firstRepresentative.steamId)} fallbackName={firstRepresentative.name} playerColor={firstRepresentative.playerColor} size="compact" /> : null}
          <span className="library-row-team-copy">
            <strong title={firstName}>{firstName}</strong>
            {firstPlayerNames.length > 0 ? <small title={firstPlayerNames.join(" · ")}>{firstPlayerNames.join(" · ")}</small> : null}
          </span>
        </div>
        <span>vs</span>
        <div className="library-row-team is-opponent">
          <span className="library-row-team-copy">
            <strong title={secondName}>{secondName}</strong>
            {secondPlayerNames.length > 0 ? <small title={secondPlayerNames.join(" · ")}>{secondPlayerNames.join(" · ")}</small> : null}
          </span>
          {secondRepresentative ? <SteamAvatar overrideUrl={secondArchiveAvatar} profile={profiles.get(secondRepresentative.steamId)} fallbackName={secondRepresentative.name} playerColor={secondRepresentative.playerColor} size="compact" /> : null}
        </div>
      </div>
      <div
        className="library-row-score"
        aria-label={entry.score && scoreStatus !== "snapshot" ? `${entry.score.teamA.score} : ${entry.score.teamB.score}` : words.scoreUnavailable}
        title={scoreTitle}
      >
        <span className="library-row-score-numbers">
          {entry.score && scoreStatus !== "snapshot"
            ? <><strong>{entry.score.teamA.score}</strong><i>:</i><strong>{entry.score.teamB.score}</strong></>
            : <span>— : —</span>}
        </span>
        <small>
          <span>{words.archiveRoundsShort.replace("{count}", String(entry.rounds))}</span>
          {duration ? <span>{duration}</span> : null}
          {entry.compatibility !== "current" || needsRepair ? (
            <span className="library-row-indicators">
              {entry.compatibility !== "current" ? (
                <span
                  className={`library-row-indicator is-${entry.compatibility}${entry.compatibility === "unsupported" ? " has-label" : ""}`}
                  title={compatibilityLabel(entry, words)}
                  aria-label={compatibilityLabel(entry, words)}
                  role="img"
                >
                  <AlertIcon size={11} />
                  {entry.compatibility === "unsupported" ? <span>{compatibilityLabel(entry, words)}</span> : null}
                </span>
              ) : null}
              {needsRepair ? (
                <span className="library-row-indicator is-repair" title={repairHelp} aria-label={repairActionLabel} role="img">
                  {needsMetadata ? <RefreshIcon size={11} /> : <FolderIcon size={11} />}
                </span>
              ) : null}
            </span>
          ) : null}
        </small>
      </div>
      <div className="library-row-map">
        <MapArtwork map={entry.map} className="library-row-map-artwork" />
        <span>
          <strong>{displayMap(entry.map)}</strong>
          {entry.note ? <small title={entry.note}>{entry.note}</small> : null}
        </span>
      </div>
      <ArrowIcon className="library-row-arrow" size={14} />

    </article>
  );
}

interface LibrarySeriesGroupProps {
  entries: DemoLibraryEntry[];
  words: TextDictionary;
  language: Language;
  primaryDisabled: boolean;
  onOpenPrimary: (entry: DemoLibraryEntry) => void;
  onOpenSeriesContextMenu: (event: ReactMouseEvent, entries: DemoLibraryEntry[]) => void;
  renderEntry: (
    entry: DemoLibraryEntry,
    seriesOrder?: number,
    seriesScore?: SeriesMapScore,
  ) => ReactNode;
}

interface SeriesMapScore {
  first: number | null;
  second: number | null;
}

function playerSteamIds(players: LibraryPlayerSummary[]): Set<string> {
  return new Set(players
    .map((player) => player.steamId.trim())
    .filter((steamId) => steamId && steamId !== "0"));
}

function sharedSteamIds(reference: Set<string>, players: LibraryPlayerSummary[]): number {
  return players.reduce((count, player) => count + (reference.has(player.steamId.trim()) ? 1 : 0), 0);
}

function scoreForSeriesEntry(
  entry: DemoLibraryEntry,
  reference: ReturnType<typeof libraryTeamContext>,
  words: TextDictionary,
): SeriesMapScore {
  if (!entry.score || entry.score.status === "snapshot" || entry.scoreIsSnapshot) {
    return { first: null, second: null };
  }
  const current = libraryTeamContext(entry, words.teamA, words.teamB);
  const namesAreEvidence = !sameTeamName(reference.firstName, words.teamA)
    && !sameTeamName(reference.secondName, words.teamB)
    && !sameTeamName(current.firstName, words.teamA)
    && !sameTeamName(current.secondName, words.teamB);
  if (namesAreEvidence) {
    const directNames = sameTeamName(current.firstName, reference.firstName)
      || sameTeamName(current.secondName, reference.secondName);
    const reversedNames = sameTeamName(current.firstName, reference.secondName)
      || sameTeamName(current.secondName, reference.firstName);
    if (reversedNames && !directNames) {
      return { first: entry.score.teamB.score, second: entry.score.teamA.score };
    }
    if (directNames && !reversedNames) {
      return { first: entry.score.teamA.score, second: entry.score.teamB.score };
    }
  }

  const firstIds = playerSteamIds(reference.firstPlayers);
  const secondIds = playerSteamIds(reference.secondPlayers);
  const directOverlap = sharedSteamIds(firstIds, current.firstPlayers)
    + sharedSteamIds(secondIds, current.secondPlayers);
  const reversedOverlap = sharedSteamIds(firstIds, current.secondPlayers)
    + sharedSteamIds(secondIds, current.firstPlayers);
  return reversedOverlap > directOverlap
    ? { first: entry.score.teamB.score, second: entry.score.teamA.score }
    : { first: entry.score.teamA.score, second: entry.score.teamB.score };
}

function LibrarySeriesGroup({
  entries,
  words,
  language,
  primaryDisabled,
  onOpenPrimary,
  onOpenSeriesContextMenu,
  renderEntry,
}: LibrarySeriesGroupProps) {
  const ordered = [...entries].sort((left, right) => (left.series?.order ?? 0) - (right.series?.order ?? 0));
  const primaryEntry = ordered[0];
  const context = libraryTeamContext(ordered[0], words.teamA, words.teamB);
  const groupRef = useRef<HTMLElement | null>(null);
  const [loadAvatars, setLoadAvatars] = useState(false);
  const profiles = useSteamProfiles(loadAvatars
    ? [...context.firstPlayers, ...context.secondPlayers].map((player) => player.steamId)
    : []);
  const firstArchiveAvatar = useArchiveTeamAvatar(primaryEntry, context.firstPlayers, loadAvatars);
  const secondArchiveAvatar = useArchiveTeamAvatar(primaryEntry, context.secondPlayers, loadAvatars);
  const firstRepresentative = representativeWithLoadedAvatar(context.firstName, context.firstPlayers, profiles);
  const secondRepresentative = representativeWithLoadedAvatar(context.secondName, context.secondPlayers, profiles);
  const firstPlayerNames = context.firstPlayers.map((player) => player.name);
  const secondPlayerNames = context.secondPlayers.map((player) => player.name);
  useEffect(() => {
    const group = groupRef.current;
    if (!group || loadAvatars) return undefined;
    if (!("IntersectionObserver" in window)) {
      setLoadAvatars(true);
      return undefined;
    }
    const observer = new IntersectionObserver((candidates) => {
      if (!candidates.some((candidate) => candidate.isIntersecting)) return;
      setLoadAvatars(true);
      observer.disconnect();
    }, { rootMargin: "240px 0px" });
    observer.observe(group);
    return () => observer.disconnect();
  }, [loadAvatars]);
  const scores = new Map(ordered.map((entry) => [
    entry.manifestPath,
    scoreForSeriesEntry(entry, context, words),
  ]));
  const wins = ordered.reduce((result, entry) => {
    const { first, second } = scores.get(entry.manifestPath) ?? { first: null, second: null };
    if (first === null || second === null || first === second) return result;
    if (first > second) result.first += 1;
    else result.second += 1;
    return result;
  }, { first: 0, second: 0 });
  const decidedMaps = wins.first + wins.second;
  const format = ordered.length >= 4 || Math.max(wins.first, wins.second) >= 3
    ? "BO5"
    : Math.max(wins.first, wins.second) >= 2
      ? "BO3"
      : ordered.length === 1
        ? "BO1"
        : `${ordered.length} MAPS`;
  const playedAt = ordered
    .map((entry) => playedAtTimestamp(entry.playedAt))
    .filter((timestamp) => timestamp > 0)
    .sort((left, right) => left - right)[0] ?? 0;
  const date = formatDateParts(playedAt || demoLibraryTimestamp(ordered[0]), language);
  const source = ordered.find((entry) => entry.demoSource)?.demoSource?.name;
  const firstWonSeries = decidedMaps > 0 && wins.first > wins.second;
  const secondWonSeries = decidedMaps > 0 && wins.second > wins.first;
  const firstLostSeries = decidedMaps > 0 && wins.first < wins.second;
  const secondLostSeries = decidedMaps > 0 && wins.second < wins.first;
  const activatePrimary = () => {
    if (!primaryDisabled) onOpenPrimary(primaryEntry);
  };
  const activatePrimaryFromKeyboard = (event: ReactKeyboardEvent) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    event.preventDefault();
    activatePrimary();
  };
  return (
    <section
      ref={groupRef}
      className="library-series-group"
      style={mapArtworkStyle(ordered[0].map)}
      aria-label={`${context.firstName} vs ${context.secondName}`}
      onContextMenu={(event) => onOpenSeriesContextMenu(event, ordered)}
    >
      <div
        className="library-row-date library-series-date"
        role="button"
        tabIndex={primaryDisabled ? -1 : 0}
        aria-disabled={primaryDisabled}
        aria-label={`${words.openArchive}: ${displayMap(primaryEntry.map)}`}
        onClick={activatePrimary}
        onKeyDown={activatePrimaryFromKeyboard}
      >
        <time dateTime={date.iso} title={date.iso ? undefined : words.matchTimeUnknown}>
          <strong>{date.date}</strong>
          <small>
            <span>{format}</span>
            {source ? <b>{platformName(source)}</b> : null}
          </small>
        </time>
      </div>
      <div
        className="library-series-match"
        role="button"
        tabIndex={primaryDisabled ? -1 : 0}
        aria-disabled={primaryDisabled}
        aria-label={`${context.firstName} vs ${context.secondName}: ${words.openArchive} ${displayMap(primaryEntry.map)}`}
        onClick={activatePrimary}
        onKeyDown={activatePrimaryFromKeyboard}
      >
        <div className="library-row-team">
          {firstRepresentative ? <SteamAvatar overrideUrl={firstArchiveAvatar} profile={profiles.get(firstRepresentative.steamId)} fallbackName={firstRepresentative.name} playerColor={firstRepresentative.playerColor} size="compact" /> : null}
          <span className="library-row-team-copy">
            <strong title={context.firstName}>{context.firstName}</strong>
            {firstPlayerNames.length > 0 ? <small title={firstPlayerNames.join(" · ")}>{firstPlayerNames.join(" · ")}</small> : null}
          </span>
        </div>
        <span className="library-series-scoreboard">
          <b className={firstWonSeries ? "is-winner" : firstLostSeries ? "is-loser" : ""}>
            {decidedMaps > 0 ? wins.first : "—"}
          </b>
          <i>:</i>
          <b className={secondWonSeries ? "is-winner" : secondLostSeries ? "is-loser" : ""}>
            {decidedMaps > 0 ? wins.second : "—"}
          </b>
        </span>
        <div className="library-row-team is-opponent">
          <span className="library-row-team-copy">
            <strong title={context.secondName}>{context.secondName}</strong>
            {secondPlayerNames.length > 0 ? <small title={secondPlayerNames.join(" · ")}>{secondPlayerNames.join(" · ")}</small> : null}
          </span>
          {secondRepresentative ? <SteamAvatar overrideUrl={secondArchiveAvatar} profile={profiles.get(secondRepresentative.steamId)} fallbackName={secondRepresentative.name} playerColor={secondRepresentative.playerColor} size="compact" /> : null}
        </div>
      </div>
      <div
        className="library-series-maps"
        style={{ gridTemplateColumns: `repeat(${ordered.length}, minmax(0, 1fr))` }}
      >
        {ordered.map((entry) => renderEntry(
          entry,
          entry.series?.order,
          scores.get(entry.manifestPath),
        ))}
      </div>
    </section>
  );
}

function LibrarySkeleton() {
  return (
    <div className="library-list" aria-hidden="true">
      {[0, 1, 2, 3].map((index) => (
        <div className="library-row library-card-skeleton" key={index}>
          <div /><span /><span /><span />
        </div>
      ))}
    </div>
  );
}

export function LibraryWorkspace({
  words,
  language,
  exportRoot,
  roots,
  scan,
  loading,
  taskBusy,
  archiveOpenDisabled,
  repairingManifest,
  repairingLibrary,
  importingArchives,
  notice,
  query,
  mapFilter,
  platformFilter,
  sort,
  onQueryChange,
  onMapFilterChange,
  onPlatformFilterChange,
  onSortChange,
  onAddRoot,
  onRemoveRoot,
  onChooseExportRoot,
  onRefresh,
  onImportArchives,
  onRepairLibrary,
  onConvert,
  onOpenEntry,
  onInspectEntry,
  onRepairEntry,
  onRevealManifest,
  onRevealDemo,
  onCopyManifestPath,
  onCopyDemoPath,
  onSaveNote,
  onReparseEntry,
  onDeleteEntry,
}: LibraryWorkspaceProps) {
  const [propertiesEntry, setPropertiesEntry] = useState<DemoLibraryEntry | null>(null);
  const [propertiesArchive, setPropertiesArchive] = useState<ManifestArchive | null>(null);
  const [propertiesLoading, setPropertiesLoading] = useState(false);
  const [propertiesError, setPropertiesError] = useState(false);
  const [noteEntry, setNoteEntry] = useState<DemoLibraryEntry | null>(null);
  const [noteDraft, setNoteDraft] = useState("");
  const [noteSaving, setNoteSaving] = useState(false);
  const [entryMenu, setEntryMenu] = useState<ContextMenuState | null>(null);
  const [page, setPage] = useState(0);
  const propertiesRequestRef = useRef(0);
  const [sourceLinkNoteDismissed, setSourceLinkNoteDismissed] = useState(() => {
    try {
      return normalizeSourceLinkNoteDismissed(localStorage.getItem(SOURCE_LINK_NOTE_DISMISSED_STORAGE_KEY));
    } catch {
      return false;
    }
  });
  const dismissSourceLinkNote = () => {
    setSourceLinkNoteDismissed(true);
    try {
      localStorage.setItem(SOURCE_LINK_NOTE_DISMISSED_STORAGE_KEY, "true");
    } catch {
      // The current session still honors the dismissal when storage is unavailable.
    }
  };
  const rootsMenuRef = useRef<HTMLDetailsElement | null>(null);
  useEffect(() => {
    const closeOnPointer = (event: PointerEvent) => {
      const menu = rootsMenuRef.current;
      if (menu?.open && event.target instanceof Node && !menu.contains(event.target)) menu.open = false;
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      const menu = rootsMenuRef.current;
      if (event.key === "Escape" && menu?.open) {
        menu.open = false;
        menu.querySelector<HTMLElement>("summary")?.focus();
      }
    };
    document.addEventListener("pointerdown", closeOnPointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnPointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, []);
  const closeRootsMenu = () => {
    if (rootsMenuRef.current) rootsMenuRef.current.open = false;
  };
  const inspectEntry = (entry: DemoLibraryEntry) => {
    const request = ++propertiesRequestRef.current;
    setPropertiesEntry(entry);
    setPropertiesArchive(null);
    setPropertiesError(false);
    setPropertiesLoading(true);
    void onInspectEntry(entry).then((archive) => {
      if (request !== propertiesRequestRef.current) return;
      setPropertiesArchive(archive);
    }).catch(() => {
      if (request !== propertiesRequestRef.current) return;
      setPropertiesError(true);
    }).finally(() => {
      if (request !== propertiesRequestRef.current) return;
      setPropertiesLoading(false);
    });
  };
  const closeProperties = () => {
    propertiesRequestRef.current += 1;
    setPropertiesEntry(null);
    setPropertiesArchive(null);
    setPropertiesLoading(false);
    setPropertiesError(false);
  };
  const editNote = (entry: DemoLibraryEntry) => {
    setNoteEntry(entry);
    setNoteDraft(entry.note ?? "");
  };
  const closeNote = () => {
    if (noteSaving) return;
    setNoteEntry(null);
    setNoteDraft("");
  };
  const maps = [...new Set((scan?.entries ?? []).map((entry) => entry.map).filter(Boolean))]
    .sort((left, right) => left.localeCompare(right));
  const platforms = [...new Set((scan?.entries ?? []).map((entry) => entry.demoSource?.name).filter((value): value is string => Boolean(value)))]
    .sort((left, right) => left.localeCompare(right));
  const normalizedQuery = query.trim().toLowerCase();
  const isScanning = loading || scan === null;
  const maintenanceBusy = repairingLibrary || importingArchives || Boolean(repairingManifest);
  const openEntryMenu = (event: ReactMouseEvent, entry: DemoLibraryEntry) => {
    event.preventDefault();
    event.stopPropagation();
    const disabled = maintenanceBusy || archiveOpenDisabled;
    const repairing = repairingManifest === entry.manifestPath;
    const needsMetadata = entry.metadataStatus !== "current";
    const needsSourceLink = !entry.sourcePath || entry.sourceAvailable === false;
    const needsRepair = needsMetadata || needsSourceLink;
    const repairLabel = needsMetadata ? words.repairMetadata : words.linkSourceDemo;
    const repairActionLabel = repairing
      ? (needsMetadata ? words.repairingMetadata : words.linkingSourceDemo)
      : repairLabel;
    setEntryMenu({
      x: event.clientX,
      y: event.clientY,
      label: words.archiveContextMenu,
      items: [
        { label: words.openArchive, icon: <ReplayIcon size={15} />, disabled, onSelect: () => onOpenEntry(entry) },
        { label: words.viewDtrProperties, icon: <TraceMark size={15} />, onSelect: () => inspectEntry(entry) },
        { label: words.archiveCustomNote, icon: <NoteIcon size={15} />, onSelect: () => editNote(entry) },
        { label: words.openManifestLocation, icon: <FolderIcon size={15} />, onSelect: () => onRevealManifest(entry) },
        { label: `${words.copyPath} · ${words.manifest}`, icon: <CopyIcon size={15} />, onSelect: () => onCopyManifestPath(entry) },
        { label: words.openDemoLocation, icon: <FolderIcon size={15} />, disabled: needsSourceLink, onSelect: () => onRevealDemo(entry) },
        { label: words.copyDemoPath, icon: <CopyIcon size={15} />, disabled: needsSourceLink, onSelect: () => onCopyDemoPath(entry) },
        { label: words.reparseDemo, icon: <RefreshIcon size={15} />, dividerBefore: true, disabled: disabled || taskBusy, onSelect: () => onReparseEntry(entry) },
        ...(needsRepair ? [{ label: repairActionLabel, icon: <RefreshIcon size={15} />, disabled: repairing || disabled || taskBusy, onSelect: () => onRepairEntry(entry) }] : []),
        { label: words.deleteArchive, icon: <TrashIcon size={15} />, dividerBefore: true, danger: true, disabled: disabled || taskBusy, onSelect: () => onDeleteEntry(entry) },
      ],
    });
  };
  const openSeriesMenu = (event: ReactMouseEvent, series: DemoLibraryEntry[]) => {
    event.preventDefault();
    event.stopPropagation();
    const disabled = maintenanceBusy || archiveOpenDisabled;
    setEntryMenu({
      x: event.clientX,
      y: event.clientY,
      label: words.archiveContextMenu,
      items: series.map((entry, index) => ({
        label: `${words.map} ${entry.series?.order ?? index + 1} · ${displayMap(entry.map)}`,
        icon: <ReplayIcon size={15} />,
        disabled,
        onSelect: () => onOpenEntry(entry),
      })),
    });
  };
  const hasRepairableArchives = (scan?.entries ?? []).some((entry) => (
    entry.metadataStatus !== "current" || !entry.sourcePath || entry.sourceAvailable === false
  ));
  const hasMissingSourceArchives = (scan?.entries ?? []).some((entry) => (
    !entry.sourcePath || entry.sourceAvailable === false
  ));
  const entries = (scan?.entries ?? [])
    .filter((entry) => !normalizedQuery || entrySearchText(entry).includes(normalizedQuery))
    .filter((entry) => !mapFilter || entry.map === mapFilter)
    .filter((entry) => !platformFilter || entry.demoSource?.name === platformFilter)
    .sort((left, right) => {
      const leftDate = demoLibraryTimestamp(left);
      const rightDate = demoLibraryTimestamp(right);
      if (sort === "map") return left.map.localeCompare(right.map) || rightDate - leftDate;
      if (sort === "platform") return (left.demoSource?.name ?? "").localeCompare(right.demoSource?.name ?? "") || rightDate - leftDate;
      return rightDate - leftDate;
    });
  const seriesEntries = new Map<string, DemoLibraryEntry[]>();
  for (const entry of entries) {
    if (!entry.series?.id) continue;
    const group = seriesEntries.get(entry.series.id) ?? [];
    group.push(entry);
    seriesEntries.set(entry.series.id, group);
  }
  const emittedSeries = new Set<string>();
  const libraryItems: (
    | { kind: "entry"; entry: DemoLibraryEntry }
    | { kind: "series"; id: string; entries: DemoLibraryEntry[] }
  )[] = [];
  for (const entry of entries) {
    const seriesId = entry.series?.id;
    const group = seriesId ? seriesEntries.get(seriesId) ?? [] : [];
    if (!seriesId || group.length < 2) {
      libraryItems.push({ kind: "entry", entry });
      continue;
    }
    if (emittedSeries.has(seriesId)) continue;
    emittedSeries.add(seriesId);
    libraryItems.push({ kind: "series", id: seriesId, entries: group });
  }
  const pageCount = Math.max(1, Math.ceil(libraryItems.length / LIBRARY_PAGE_SIZE));
  const visiblePage = Math.min(page, pageCount - 1);
  const visibleItems = libraryItems.slice(
    visiblePage * LIBRARY_PAGE_SIZE,
    (visiblePage + 1) * LIBRARY_PAGE_SIZE,
  );
  const pageStart = libraryItems.length === 0 ? 0 : visiblePage * LIBRARY_PAGE_SIZE + 1;
  const pageEnd = Math.min(libraryItems.length, (visiblePage + 1) * LIBRARY_PAGE_SIZE);
  useEffect(() => setPage(0), [mapFilter, platformFilter, query, sort]);
  useEffect(() => {
    setPage((current) => Math.min(current, pageCount - 1));
  }, [pageCount]);
  const renderLibraryRow = (
    entry: DemoLibraryEntry,
    seriesOrder?: number,
    seriesScore?: SeriesMapScore,
  ) => (
    <LibraryRow
      key={entry.manifestPath}
      entry={entry}
      seriesOrder={seriesOrder}
      seriesScore={seriesScore}
      words={words}
      language={language}
      onOpen={() => onOpenEntry(entry)}
      onRepair={() => onRepairEntry(entry)}
      onOpenContextMenu={(event) => openEntryMenu(event, entry)}
      repairing={repairingManifest === entry.manifestPath}
      disabled={maintenanceBusy || archiveOpenDisabled}
      taskBusy={taskBusy}
    />
  );
  return (
    <section className="library-workspace" aria-labelledby="library-title">
      <header className="library-heading">
        <h1 id="library-title">{words.libraryTitle}</h1>
      </header>

      {!exportRoot ? (
        <div className="library-first-run">
          <div className="library-empty-mark"><TraceMark size={58} /></div>
          <div>
            <span>{words.libraryFolder}</span>
            <h2>{words.libraryEmptyTitle}</h2>
            <p>{words.libraryEmptyBody}</p>
          </div>
          <button className="primary-button" type="button" onClick={onChooseExportRoot}><FolderIcon size={17} />{words.chooseLibrary}</button>
          <small>{words.libraryDefaultLocation}</small>
        </div>
      ) : (
        <>
          {(isScanning || (scan?.entries.length ?? 0) > 0) ? <div className="library-command-bar">
            <details className="library-roots-menu" ref={rootsMenuRef}>
              <summary className="library-root-button" title={exportRoot}>
                <FolderIcon size={16} />
                <span><small>{words.exportFolder}</small><code>{exportRoot}</code></span>
                <b>{(roots.length === 1 ? words.libraryFolderCountOne : words.libraryFolderCountMany).replace("{count}", String(roots.length))}</b>
              </summary>
              <div className="library-roots-popover">
                <header>
                  <strong>{words.indexedFolders}</strong>
                  <button className="quiet-button" type="button" onClick={() => { closeRootsMenu(); onAddRoot(); }} disabled={maintenanceBusy}><PlusIcon size={14} />{words.addFolder}</button>
                </header>
                <ul>
                  {roots.map((root) => {
                    const isExport = root.toLocaleLowerCase() === exportRoot.toLocaleLowerCase();
                    return (
                      <li key={root}>
                        <span><code title={root}>{root}</code>{isExport ? <small>{words.defaultExport}</small> : null}</span>
                        {!isExport ? (
                          <button className="icon-button" type="button" onClick={() => onRemoveRoot(root)} disabled={maintenanceBusy} aria-label={`${words.removeFolder}: ${root}`} title={words.removeFolder}>
                            <CloseIcon size={14} />
                          </button>
                        ) : null}
                      </li>
                    );
                  })}
                </ul>
                <button className="secondary-button" type="button" onClick={() => { closeRootsMenu(); onChooseExportRoot(); }} disabled={maintenanceBusy}>
                  <FolderIcon size={14} />{words.changeExportFolder}
                </button>
                <section
                  className="library-maintenance"
                  aria-label={words.libraryMaintenance}
                >
                  <small>{words.libraryMaintenance}</small>
                  <button
                    type="button"
                    onClick={() => { closeRootsMenu(); onImportArchives(); }}
                    disabled={maintenanceBusy}
                    title={words.importArchivesHelp}
                  >
                    <FolderIcon size={15} />
                    <span>
                      <strong>{importingArchives ? words.importingArchives : words.importLegacyArchives}</strong>
                      <em>{words.importArchivesHelp}</em>
                    </span>
                  </button>
                  {hasRepairableArchives ? (
                    <button
                      type="button"
                      onClick={() => { closeRootsMenu(); onRepairLibrary(); }}
                      disabled={maintenanceBusy}
                      title={words.repairMetadataHelp}
                    >
                      <RefreshIcon size={15} />
                      <span>
                        <strong>{repairingLibrary ? words.repairingLibrary : words.repairLegacyLibrary}</strong>
                        <em>{words.repairLibraryHelp}</em>
                      </span>
                    </button>
                  ) : null}
                </section>
              </div>
            </details>

            <label className="library-search">
              <SearchIcon size={17} />
              <span className="sr-only">{words.librarySearch}</span>
              <input value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder={words.librarySearch} />
            </label>

            <select className="library-map-filter" value={mapFilter} onChange={(event) => onMapFilterChange(event.target.value)} aria-label={words.map}>
              <option value="">{words.allMaps}</option>
              {maps.map((map) => <option value={map} key={map}>{displayMap(map)}</option>)}
            </select>

            <select className="library-platform-filter" value={platformFilter} onChange={(event) => onPlatformFilterChange(event.target.value)} aria-label={words.demoSource}>
              <option value="">{words.allPlatforms}</option>
              {platforms.map((platform) => <option value={platform} key={platform}>{platformName(platform)}</option>)}
            </select>

            <select className="library-sort-filter" value={sort} onChange={(event) => onSortChange(event.target.value as LibrarySort)} aria-label={words.recentFirst}>
              <option value="recent">{words.recentFirst}</option>
              <option value="map">{words.mapOrder}</option>
              <option value="platform">{words.platformOrder}</option>
            </select>

            <button className="icon-button library-refresh" type="button" disabled={loading || maintenanceBusy} onClick={onRefresh} aria-label={words.scanLibrary} title={words.scanLibrary}>
              <RefreshIcon size={17} />
            </button>
          </div> : null}

          <div className={`library-result-meta${isScanning ? " is-scanning" : ""}`} role="status" aria-live="polite">
            {isScanning ? <span className="library-scan-indicator" aria-hidden="true"><RefreshIcon size={13} /></span> : null}
            <strong>{importingArchives
              ? words.importingArchives
              : repairingLibrary
              ? words.repairingLibrary
              : isScanning ? words.scanningLibrary : words.libraryCount.replace("{count}", String(entries.length))}</strong>
            <span>{(roots.length === 1 ? words.indexedFolderSummaryOne : words.indexedFolderSummaryMany).replace("{count}", String(roots.length))}</span>
            {notice ? <em className="library-notice">{notice}</em>
              : scan && scan.skipped.length > 0 ? <em>{words.libraryScanNotes.replace("{count}", String(scan.skipped.length))}</em> : null}
          </div>

          {hasMissingSourceArchives && !sourceLinkNoteDismissed ? (
            <aside className="library-source-link-note">
              <FolderIcon size={16} />
              <span><strong>{words.linkSourceDemo}</strong><small>{words.linkSourceDemoHelp}</small></span>
              <button type="button" onClick={dismissSourceLinkNote}>{words.acknowledge}</button>
            </aside>
          ) : null}

          {isScanning ? <LibrarySkeleton /> : entries.length > 0 ? (
            <>
              <div className="library-list">
                {visibleItems.map((item) => item.kind === "series" ? (
                  <LibrarySeriesGroup
                    key={item.id}
                    entries={item.entries}
                    words={words}
                    language={language}
                    primaryDisabled={maintenanceBusy || archiveOpenDisabled}
                    onOpenPrimary={onOpenEntry}
                    onOpenSeriesContextMenu={openSeriesMenu}
                    renderEntry={renderLibraryRow}
                  />
                ) : renderLibraryRow(item.entry))}
              </div>
              {pageCount > 1 ? (
                <nav className="library-pagination" aria-label={words.libraryPagination}>
                  <span>{words.libraryPageRange
                    .replace("{start}", String(pageStart))
                    .replace("{end}", String(pageEnd))
                    .replace("{total}", String(libraryItems.length))}</span>
                  <div>
                    <button className="secondary-button" type="button" disabled={visiblePage === 0} onClick={() => setPage((current) => Math.max(0, current - 1))}>{words.previousPage}</button>
                    <strong>{words.libraryPageNumber.replace("{current}", String(visiblePage + 1)).replace("{total}", String(pageCount))}</strong>
                    <button className="secondary-button" type="button" disabled={visiblePage >= pageCount - 1} onClick={() => setPage((current) => Math.min(pageCount - 1, current + 1))}>{words.nextPage}</button>
                  </div>
                </nav>
              ) : null}
            </>
          ) : (
            <div className={`library-no-results${scan?.entries.length === 0 ? " is-blank-slate" : ""}`}>
              <span className="library-blank-mark" aria-hidden="true">
                {scan?.entries.length === 0 ? <TraceMark size={36} /> : <SearchIcon size={22} />}
              </span>
              <strong>{scan?.entries.length === 0 ? words.libraryDirectoryEmptyTitle : words.libraryNoResultsTitle}</strong>
              <p>{scan?.entries.length === 0 ? words.libraryDirectoryEmptyBody : words.libraryNoResultsBody}</p>
              {scan?.entries.length === 0 ? <button className="primary-button" type="button" onClick={onConvert}><PlusIcon size={15} />{words.convertDemo}</button> : null}
              {scan?.entries.length === 0 ? <em>{words.dropDemo} · {words.dropTypes}</em> : null}
            </div>
          )}
        </>
      )}
      {entryMenu ? <ContextMenu menu={entryMenu} onClose={() => setEntryMenu(null)} /> : null}
      {propertiesEntry ? (
        <DialogPrimitive
          labelledBy="dtr-properties-title"
          onDismiss={closeProperties}
          className="dialog-surface dtr-properties-dialog"
        >
          <header>
            <div>
              <span>{words.dtrPropertiesTitle}</span>
              <h2 id="dtr-properties-title">{propertiesEntry.displayName || propertiesEntry.demoId}</h2>
            </div>
            <button className="icon-button" type="button" onClick={closeProperties} aria-label={words.close} title={words.close}>
              <CloseIcon size={17} />
            </button>
          </header>
          {propertiesLoading ? (
            <div className="dtr-properties-state">{words.loadingDtrProperties}</div>
          ) : propertiesArchive ? (
            <div className="dtr-properties-content">
              <dl>
                <div><dt>{words.manifestFormat}</dt><dd>DTR v{propertiesArchive.formatVersion}</dd></div>
                <div><dt>{words.manifestAbi}</dt><dd>{propertiesArchive.abi}</dd></div>
                <div><dt>{words.tickRate}</dt><dd>{formatTickRate(propertiesArchive.tickRate)}</dd></div>
                <div><dt>{words.traceSize}</dt><dd>{formatBytes(propertiesArchive.outputBytes)}</dd></div>
                <div><dt>{words.rounds}</dt><dd>{propertiesArchive.rounds.length}</dd></div>
                <div><dt>{words.availableFiles}</dt><dd>{propertiesArchive.playableFiles} / {propertiesArchive.totalFiles}</dd></div>
                <div><dt>{words.converterVersion}</dt><dd>{propertiesArchive.converterVersion || "—"}</dd></div>
              </dl>
              <div className="dtr-properties-path">
                <span>{words.manifest}</span>
                <code title={propertiesArchive.manifestPath}>{propertiesArchive.manifestPath}</code>
              </div>
              <div className="dtr-properties-path">
                <span>{words.source}</span>
                <code title={propertiesArchive.sourcePath || propertiesArchive.demoPath}>{propertiesArchive.sourcePath || propertiesArchive.demoPath || "—"}</code>
              </div>
            </div>
          ) : propertiesError ? (
            <div className="dtr-properties-state is-error">{words.dtrPropertiesUnavailable}</div>
          ) : null}
          <footer>
            <button className="secondary-button" type="button" onClick={() => onRevealManifest(propertiesEntry)}>
              <FolderIcon size={15} />{words.openManifestLocation}
            </button>
            <button className="primary-button" type="button" onClick={closeProperties}>{words.close}</button>
          </footer>
        </DialogPrimitive>
      ) : null}
      {noteEntry ? (
        <DialogPrimitive
          labelledBy="archive-note-title"
          onDismiss={closeNote}
          className="dialog-surface archive-note-dialog"
        >
          <form onSubmit={(event) => {
            event.preventDefault();
            if (noteSaving) return;
            setNoteSaving(true);
            void onSaveNote(noteEntry, noteDraft).then((saved) => {
              if (saved) {
                setNoteEntry(null);
                setNoteDraft("");
              }
            }).finally(() => setNoteSaving(false));
          }}>
            <header>
              <div>
                <span>{words.archiveCustomNote}</span>
                <h2 id="archive-note-title">{noteEntry.displayName || noteEntry.demoId}</h2>
              </div>
              <button className="icon-button" type="button" disabled={noteSaving} onClick={closeNote} aria-label={words.close} title={words.close}>
                <CloseIcon size={17} />
              </button>
            </header>
            <div className="archive-note-dialog-body">
              <textarea
                autoFocus
                value={noteDraft}
                maxLength={240}
                disabled={noteSaving}
                aria-label={words.archiveCustomNote}
                placeholder={words.archiveNotePlaceholder}
                onChange={(event) => setNoteDraft(event.target.value)}
              />
              <small>{noteDraft.length}/240</small>
            </div>
            <footer>
              <button className="secondary-button" type="button" disabled={noteSaving} onClick={closeNote}>{words.cancel}</button>
              <button className="primary-button" type="submit" disabled={noteSaving || noteDraft.trim() === (noteEntry.note ?? "")}>
                {noteSaving ? words.savingArchiveNote : words.saveArchiveNote}
              </button>
            </footer>
          </form>
        </DialogPrimitive>
      ) : null}
    </section>
  );
}
