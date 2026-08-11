/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useLayoutEffect, useRef, type CSSProperties } from "react";
import steamMarkUrl from "../assets/steam-mark.svg";
import { ArrowIcon, CheckIcon, CopyIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { InventorySimulatorSelectionController } from "../inventorySimulatorSelection";
import { resolveProfessionalPlayer } from "../professionalPlayers";
import { formattedBirthDateWithAge, formattedProfessionalRoles } from "../professionalPlayersCatalog";
import type { Language } from "../types";
import {
  PlayerDossier,
  hasSteamProfile,
  playerSelectionKey,
  steamProfileUrl,
  type PlayerSelection,
  type RosterPlayer,
} from "./PlayerRoster";
import type { CopyTarget } from "./TaskViews";
import { currentSteamAlias, demoPlayerColorValue, SteamAvatar, type SteamProfileMap } from "./SteamProfile";
import "flag-icons/css/flag-icons.min.css";
import "./archive-workspace.css";
import "./player-analysis.css";

export interface PlayerAnalysisTeam {
  id: string;
  name: string;
  players: RosterPlayer[];
}

interface PlayerAnalysisWorkspaceProps {
  words: TextDictionary;
  language: Language;
  teams: PlayerAnalysisTeam[];
  steamProfiles: SteamProfileMap;
  selectedPlayer: PlayerSelection;
  copiedTarget: CopyTarget | null;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onBack: () => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  inventorySelection: InventorySimulatorSelectionController;
}

function playerMetricCards(player: RosterPlayer, words: TextDictionary) {
  const hasValue = (value: number | null | undefined): value is number => value !== null && value !== undefined;
  const rounds = player.details?.statsRounds;
  const validRounds = hasValue(rounds) && rounds > 0 ? rounds : null;
  const headshots = player.details?.headshotKills;
  const totalDamage = player.details?.totalDamage;
  return [
    { key: "kills", label: "K", value: hasValue(player.kills) ? String(player.kills) : null },
    { key: "deaths", label: "D", value: hasValue(player.deaths) ? String(player.deaths) : null },
    { key: "assists", label: "A", value: hasValue(player.assists) ? String(player.assists) : null },
    { key: "adr", label: "ADR", value: hasValue(totalDamage) && validRounds !== null ? (totalDamage / validRounds).toFixed(1) : null },
    { key: "kd", label: "KD", value: hasValue(player.kills) && hasValue(player.deaths) && player.deaths > 0 ? (player.kills / player.deaths).toFixed(2) : null },
    { key: "kpr", label: "KPR", value: hasValue(player.kills) && validRounds !== null ? (player.kills / validRounds).toFixed(2) : null },
    { key: "headshots", label: words.headshotKillsShort, value: hasValue(headshots) ? String(headshots) : null },
    { key: "hs", label: "HS%", value: hasValue(player.kills) && player.kills > 0 && hasValue(headshots) && headshots <= player.kills ? `${(headshots / player.kills * 100).toFixed(1)}%` : null },
    { key: "first-kills", label: words.firstKills, value: hasValue(player.details?.firstKills) ? String(player.details.firstKills) : null },
    { key: "first-deaths", label: words.firstDeaths, value: hasValue(player.details?.firstDeaths) ? String(player.details.firstDeaths) : null },
    { key: "five-k", label: "5K", value: hasValue(player.details?.fiveKRounds) ? String(player.details.fiveKRounds) : null },
    { key: "four-k", label: "4K", value: hasValue(player.details?.fourKRounds) ? String(player.details.fourKRounds) : null },
    { key: "three-k", label: "3K", value: hasValue(player.details?.threeKRounds) ? String(player.details.threeKRounds) : null },
    { key: "two-k", label: "2K", value: hasValue(player.details?.twoKRounds) ? String(player.details.twoKRounds) : null },
    { key: "mvps", label: "MVP", value: hasValue(player.mvps) ? String(player.mvps) : null },
  ].filter((metric): metric is { key: string; label: string; value: string } => metric.value !== null);
}

export function PlayerAnalysisWorkspace({
  words,
  language,
  teams,
  steamProfiles,
  selectedPlayer,
  copiedTarget,
  onSelectPlayer,
  onBack,
  onCopy,
  onOpenExternal,
  inventorySelection,
}: PlayerAnalysisWorkspaceProps) {
  const entries = teams.flatMap((team) => team.players.map((player, playerIndex) => {
    const selection = { teamId: team.id, playerIndex };
    return { team, player, selection, key: playerSelectionKey(selection) };
  }));
  const selectedKey = playerSelectionKey(selectedPlayer);
  const selectedEntry = entries.find((entry) => entry.key === selectedKey);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const scrollPositionsRef = useRef(new Map<string, number>());
  const lastScrollTopRef = useRef(0);
  const focusedHeadingRef = useRef(false);

  useEffect(() => {
    if (focusedHeadingRef.current || !selectedEntry) return;
    focusedHeadingRef.current = true;
    headingRef.current?.focus({ preventScroll: true });
  }, [selectedEntry?.key, selectedKey]);

  useLayoutEffect(() => {
    const scroll = scrollRef.current;
    if (!scroll || !selectedEntry) return;
    scroll.scrollTop = scrollPositionsRef.current.get(selectedKey) ?? lastScrollTopRef.current;
  }, [selectedEntry?.key, selectedKey]);

  const selectPlayer = (selection: PlayerSelection) => {
    const scroll = scrollRef.current;
    if (scroll && selectedEntry) {
      scrollPositionsRef.current.set(selectedKey, scroll.scrollTop);
      lastScrollTopRef.current = scroll.scrollTop;
    }
    onSelectPlayer(selection);
  };

  if (!selectedEntry) {
    return (
      <section className="player-analysis-workspace" aria-labelledby="player-analysis-title">
        <header className="player-analysis-toolbar">
          <button className="player-analysis-back" type="button" onClick={onBack}>
            <ArrowIcon size={16} />
            <span>{words.backToMatch}</span>
          </button>
          <div>
            <span>{words.playerAnalysis}</span>
            <strong>{words.playerAnalysisUnavailable}</strong>
          </div>
        </header>
        <div className="player-analysis-scroll">
          <article className="player-analysis-main player-analysis-empty">
            <header className="player-analysis-heading">
              <h1 id="player-analysis-title" ref={headingRef} tabIndex={-1}>{words.playerAnalysis}</h1>
              <p>{words.playerAnalysisUnavailable}</p>
            </header>
          </article>
        </div>
      </section>
    );
  }

  const { player, team } = selectedEntry;
  const steamProfile = steamProfiles.get(player.steamId);
  const steamAlias = currentSteamAlias(steamProfile, player.name);
  const professionalPlayer = resolveProfessionalPlayer(player.steamId);
  const professionalRealName = professionalPlayer?.registry?.nameLatin
    ?? professionalPlayer?.registry?.nameNative
    ?? professionalPlayer?.hltv?.realName;
  const professionalCountry = professionalPlayer?.registry?.country
    ?? professionalPlayer?.hltv?.country?.name;
  const professionalCountryCode = professionalPlayer?.registry?.countryCode
    ?? professionalPlayer?.hltv?.country?.code;
  const professionalFlagCode = professionalCountryCode && /^[A-Z]{2}$/i.test(professionalCountryCode)
    ? professionalCountryCode.toLowerCase()
    : null;
  const professionalBirthday = formattedBirthDateWithAge(
    professionalPlayer?.registry?.birthDate,
    language,
  );
  const professionalRoles = formattedProfessionalRoles(professionalPlayer?.registry?.roles);
  const steamProfileAvailable = hasSteamProfile(player.steamId);
  const metrics = playerMetricCards(player, words);
  const kdaMetrics = metrics.filter((metric) => metric.key === "kills" || metric.key === "deaths" || metric.key === "assists");
  const secondaryMetrics = metrics.filter((metric) => metric.key !== "kills" && metric.key !== "deaths" && metric.key !== "assists");
  const steamCopyTarget = `player:${selectedEntry.key}:steam:0` as CopyTarget;
  const selectedPlayerColor = demoPlayerColorValue(player.playerColor);

  return (
    <section
      className={`player-analysis-workspace is-team-${team.id}`}
      aria-labelledby="player-analysis-title"
      style={selectedPlayerColor
        ? ({ "--selected-team-color": selectedPlayerColor } as CSSProperties)
        : undefined}
    >
      <header className="player-analysis-toolbar">
        <button className="player-analysis-back" type="button" onClick={onBack}>
          <ArrowIcon size={16} />
          <span>{words.backToMatch}</span>
        </button>
        <div>
          <span>{words.matchRoster}</span>
          <strong>{words.playerAnalysis}</strong>
        </div>
      </header>

      <div
        className="player-analysis-scroll"
        ref={scrollRef}
        onScroll={(event) => {
          const top = event.currentTarget.scrollTop;
          lastScrollTopRef.current = top;
          scrollPositionsRef.current.set(selectedKey, top);
        }}
      >
        <div className="player-analysis-layout">
          <aside className="player-analysis-index" aria-label={words.choosePlayer}>
            <nav aria-label={words.choosePlayer}>
              {teams.map((team) => (
                <section className={`player-analysis-team is-team-${team.id}`} aria-labelledby={`player-analysis-team-${team.id}`} key={team.id}>
                  <h3 id={`player-analysis-team-${team.id}`}>{team.name}</h3>
                  <div className="player-analysis-team-list">
                    {team.players.map((teamPlayer, playerIndex) => {
                      const selection = { teamId: team.id, playerIndex };
                      const entryKey = playerSelectionKey(selection);
                      const selected = entryKey === selectedEntry.key;
                      const playerColor = demoPlayerColorValue(teamPlayer.playerColor);
                      return (
                        <button
                          className={selected ? "is-selected" : ""}
                          type="button"
                          aria-current={selected ? "page" : undefined}
                          aria-label={teamPlayer.name}
                          onClick={() => selectPlayer(selection)}
                          key={entryKey}
                          style={playerColor
                            ? ({ "--player-color": playerColor } as CSSProperties)
                            : undefined}
                        >
                          <span className="player-analysis-index-identity">
                            <SteamAvatar profile={steamProfiles.get(teamPlayer.steamId)} fallbackName={teamPlayer.name} playerColor={teamPlayer.playerColor} size="compact" />
                            <strong title={teamPlayer.name}>{teamPlayer.name}</strong>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </section>
              ))}
            </nav>
          </aside>

          <article className="player-analysis-main" aria-labelledby="player-analysis-title">
            <header className="player-analysis-heading">
              <div className="player-analysis-heading-identity">
                <SteamAvatar profile={steamProfile} fallbackName={player.name} playerColor={player.playerColor} size="profile" />
                <div>
                  <span>{team.name}</span>
                  <h1 id="player-analysis-title" ref={headingRef} tabIndex={-1}>
                    {professionalPlayer?.handle ?? player.name}
                  </h1>
                  {!professionalPlayer && steamAlias ? <p>Steam · {steamAlias}</p> : null}
                  {professionalPlayer && (professionalRealName || professionalBirthday) ? (
                    <div className="professional-player-identity">
                      {professionalRealName ? (
                        <div className="professional-player-person">
                          {professionalFlagCode ? (
                            <span
                              className={`fi fi-${professionalFlagCode} professional-player-flag`}
                              role="img"
                              aria-label={professionalCountry ?? professionalCountryCode ?? undefined}
                              title={professionalCountry ?? professionalCountryCode ?? undefined}
                            />
                          ) : null}
                          <strong>{professionalRealName}</strong>
                        </div>
                      ) : null}
                      {professionalBirthday ? (
                        <div className="professional-player-birthday">
                          {professionalRoles ? <strong>{professionalRoles}</strong> : null}
                          <span>{professionalBirthday}</span>
                        </div>
                      ) : null}
                    </div>
                  ) : null}
                </div>
              </div>
              {steamProfileAvailable ? (
                <div className="player-analysis-heading-actions">
                  <button className="player-steam-id-tag" type="button" onClick={() => onCopy(player.steamId, steamCopyTarget)} title={words.copySteamId} aria-label={words.copySteamId}>
                    <code>{player.steamId}</code>
                    {copiedTarget === steamCopyTarget ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
                  </button>
                  <button className="player-steam-profile-link" type="button" onClick={() => onOpenExternal(steamProfileUrl(player.steamId))} title={words.openSteamProfile} aria-label={words.openSteamProfile}>
                    <img src={steamMarkUrl} alt="" aria-hidden="true" />
                  </button>
                </div>
              ) : null}
            </header>

            <section className="player-analysis-overview-panel" aria-labelledby="player-match-data-title">
              <header>
                <h2 id="player-match-data-title">{words.playerMatchData}</h2>
                <span>{player.details?.statsRounds ? `${player.details.statsRounds} ${words.roundsUnit}` : team.name}</span>
              </header>
              <div className={`player-analysis-metrics${kdaMetrics.length === 0 ? " has-no-kda" : ""}`}>
                {kdaMetrics.length > 0 ? (
                  <div className="player-analysis-kda" aria-label={words.kda}>
                    {kdaMetrics.map((metric) => (
                      <div className="player-analysis-kda-metric" key={metric.key}>
                        <strong>{metric.value}</strong>
                        <span>{metric.label}</span>
                      </div>
                    ))}
                  </div>
                ) : null}
                <div className="player-analysis-secondary-metrics">
                  {secondaryMetrics.map((metric) => (
                    <div className={`is-${metric.key}`} key={metric.key}>
                      <span>{metric.label}</span>
                      <strong>{metric.value}</strong>
                    </div>
                  ))}
                </div>
              </div>
            </section>

            <div className="player-analysis-dossier">
              <PlayerDossier
                key={selectedEntry.key}
                playerKey={selectedEntry.key}
                player={player}
                language={language}
                words={words}
                copiedTarget={copiedTarget}
                onCopy={onCopy}
                onOpenExternal={onOpenExternal}
                inventorySelection={inventorySelection}
                view="evidence"
              />
            </div>
          </article>
        </div>
      </div>
    </section>
  );
}
