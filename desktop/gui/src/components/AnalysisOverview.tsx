/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { TextDictionary } from "../i18n";
import type { AnalysisPlayerSummary, AnalysisResult } from "../types";
import { displayMap } from "./MapArtwork";
import { playerSelectionKey, type PlayerSelection } from "./PlayerRoster";
import { SteamPlayerIdentity, type SteamProfileMap } from "./SteamProfile";
import "./analysis-overview.css";

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function teamNameFromPlayers(players: AnalysisPlayerSummary[], identity: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of players) {
    if (player.team.toLowerCase() !== identity) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds)) return "—";
  const value = Math.max(0, Math.round(seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const remainder = value % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function platformName(value: string): string {
  return value.toLowerCase() === "faceit" ? "FACEIT" : value;
}

export function analysisRoster(analysis: AnalysisResult, words: TextDictionary) {
  const teamAName = cleanTeamName(analysis.score?.teamA.name)
    || teamNameFromPlayers(analysis.players, "a")
    || words.teamA;
  const teamBName = cleanTeamName(analysis.score?.teamB.name)
    || teamNameFromPlayers(analysis.players, "b")
    || words.teamB;
  const sortedPlayers = [...analysis.players].sort((left, right) => left.name.localeCompare(right.name));
  return {
    teamAName,
    teamBName,
    sortedPlayers,
    teamA: sortedPlayers.filter((player) => player.team.toLowerCase() === "a"),
    teamB: sortedPlayers.filter((player) => player.team.toLowerCase() === "b"),
    unassigned: sortedPlayers.filter((player) => !["a", "b"].includes(player.team.toLowerCase())),
  };
}

function playerMetrics(player: AnalysisPlayerSummary, matchRounds: number | null = null) {
  const kills = player.kills;
  const deaths = player.deaths;
  const assists = player.assists;
  const headshots = player.details?.headshotKills;
  const damage = player.details?.totalDamage;
  const rounds = player.details?.statsRounds ?? matchRounds;
  const validRounds = rounds !== null && rounds > 0 ? rounds : null;
  return {
    kills,
    deaths,
    assists,
    adr: damage !== null && damage !== undefined && validRounds !== null ? (damage / validRounds).toFixed(1) : null,
    kd: kills !== null && kills !== undefined && deaths !== null && deaths !== undefined && deaths > 0 ? (kills / deaths).toFixed(2) : null,
    kr: kills !== null && kills !== undefined && validRounds !== null ? (kills / validRounds).toFixed(2) : null,
    headshots: headshots !== null && headshots !== undefined ? String(headshots) : null,
    hs: kills !== null && kills !== undefined && kills > 0 && headshots !== null && headshots !== undefined && headshots <= kills
      ? `${(headshots / kills * 100).toFixed(1)}%`
      : null,
    mvps: player.mvps,
    twoK: player.details?.twoKRounds,
    threeK: player.details?.threeKRounds,
    fourK: player.details?.fourKRounds,
    fiveK: player.details?.fiveKRounds,
  };
}

type PlayerMetrics = ReturnType<typeof playerMetrics>;

interface AnalysisMetricColumn {
  key: "kills" | "deaths" | "assists" | "adr" | "kd" | "kr" | "headshots" | "hs" | "fiveK" | "fourK" | "threeK" | "twoK" | "mvps";
  label: string;
  width: string;
  value: (metrics: PlayerMetrics) => string | null;
}

function metricValue(value: number | string | null | undefined): string | null {
  return value === null || value === undefined ? null : String(value);
}

function analysisMetricColumns(words: TextDictionary): AnalysisMetricColumn[] {
  return [
    { key: "kills", label: "K", width: "minmax(28px, .32fr)", value: (values) => metricValue(values.kills) },
    { key: "deaths", label: "D", width: "minmax(28px, .32fr)", value: (values) => metricValue(values.deaths) },
    { key: "assists", label: "A", width: "minmax(28px, .32fr)", value: (values) => metricValue(values.assists) },
    { key: "adr", label: words.adr, width: "minmax(42px, .48fr)", value: (values) => metricValue(values.adr) },
    { key: "kd", label: "K/D", width: "minmax(42px, .48fr)", value: (values) => metricValue(values.kd) },
    { key: "kr", label: "K/R", width: "minmax(42px, .48fr)", value: (values) => metricValue(values.kr) },
    { key: "headshots", label: words.headshotKillsShort, width: "minmax(30px, .34fr)", value: (values) => metricValue(values.headshots) },
    { key: "hs", label: "HS%", width: "minmax(50px, .55fr)", value: (values) => metricValue(values.hs) },
    { key: "fiveK", label: "5K", width: "minmax(28px, .3fr)", value: (values) => metricValue(values.fiveK) },
    { key: "fourK", label: "4K", width: "minmax(28px, .3fr)", value: (values) => metricValue(values.fourK) },
    { key: "threeK", label: "3K", width: "minmax(28px, .3fr)", value: (values) => metricValue(values.threeK) },
    { key: "twoK", label: "2K", width: "minmax(28px, .3fr)", value: (values) => metricValue(values.twoK) },
    { key: "mvps", label: "MVP", width: "minmax(34px, .36fr)", value: (values) => metricValue(values.mvps) },
  ];
}

function AnalysisTeamRows({
  teamId,
  name,
  score,
  players,
  columns,
  matchRounds,
  steamProfiles,
  words,
  onSelectPlayer,
}: {
  teamId: string;
  name: string;
  score?: number;
  players: AnalysisPlayerSummary[];
  columns: AnalysisMetricColumn[];
  matchRounds: number | null;
  steamProfiles: SteamProfileMap;
  words: TextDictionary;
  onSelectPlayer: (selection: PlayerSelection) => void;
}) {
  const gridTemplateColumns = `minmax(190px, 1.8fr) ${columns.map((column) => column.width).join(" ")}`;
  return (
    <section className={`analysis-team-block is-team-${teamId}`} aria-label={name}>
      <header className="analysis-team-heading">
        <strong title={name}>{name}</strong>
        <span>{words.rosterPlayerCount.replace("{count}", String(players.length))}</span>
        {score !== undefined ? <b>{score}</b> : null}
      </header>
      <div className="analysis-scoreboard-columns" style={{ gridTemplateColumns }}>
        <span>{words.playerColumn}</span>
        {columns.map((column) => <span key={column.key}>{column.label}</span>)}
      </div>
      <ul>
        {players.map((player, playerIndex) => {
          const metrics = playerMetrics(player, matchRounds);
          const selection = { teamId, playerIndex };
          return (
            <li key={`${player.steamId}:${playerIndex}`}>
              <button
                className="analysis-player-stat-row"
                type="button"
                data-player-key={playerSelectionKey(selection)}
                style={{ gridTemplateColumns }}
                title={words.rosterPlayerHint}
                onClick={() => onSelectPlayer(selection)}
              >
                <SteamPlayerIdentity
                  className="analysis-player-identity"
                  profile={steamProfiles.get(player.steamId)}
                  demoName={player.name}
                  steamId={player.steamId}
                  playerColor={player.playerColor}
                />
                {columns.map((column) => (
                  <span className={`analysis-stat-value is-${column.key}`} key={column.key}>
                    {column.value(metrics) ?? "—"}
                  </span>
                ))}
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

export function AnalysisOverview({
  analysis,
  words,
  steamProfiles,
  onSelectPlayer,
}: {
  analysis: AnalysisResult;
  words: TextDictionary;
  steamProfiles: SteamProfileMap;
  onSelectPlayer: (selection: PlayerSelection) => void;
}) {
  const { teamAName, teamBName, sortedPlayers, teamA, teamB, unassigned } = analysisRoster(analysis, words);
  const matchRounds = analysis.score
    ? analysis.score.teamA.score + analysis.score.teamB.score
    : null;
  const metricColumns = analysisMetricColumns(words);
  return (
    <div className="analysis-overview">
      <section className="analysis-matchbar" aria-label={words.matchAnalysis}>
        <div className="analysis-map-identity"><small>{words.map}</small><strong>{displayMap(analysis.map)}</strong></div>
        <div className="analysis-score-team is-team-a"><small>{words.teamA}</small><strong title={teamAName}>{teamAName}</strong></div>
        <div className="analysis-scoreline" aria-label={analysis.score ? `${teamAName} ${analysis.score.teamA.score} : ${analysis.score.teamB.score} ${teamBName}` : words.scoreUnavailable}>
          {analysis.score
            ? <><b>{analysis.score.teamA.score}</b><i>:</i><b>{analysis.score.teamB.score}</b></>
            : <em>— : —</em>}
        </div>
        <div className="analysis-score-team is-team-b"><small>{words.teamB}</small><strong title={teamBName}>{teamBName}</strong></div>
        <div className="analysis-matchmeta">
          <span>{analysis.demoSource ? platformName(analysis.demoSource.name) : "—"}</span>
          <span>{formatDuration(analysis.durationSeconds)}</span>
          <span>{analysis.rounds.length} {words.roundsUnit}</span>
          <span>{Number.isInteger(analysis.tickRate) ? analysis.tickRate : analysis.tickRate.toFixed(2)} tick</span>
        </div>
      </section>

      {sortedPlayers.length > 0 ? (
        <section className="analysis-scoreboard">
          <header className="analysis-scoreboard-heading">
            <span>
              <h2>{words.matchRoster}</h2>
              <small>{words.rosterPlayerCount.replace("{count}", String(sortedPlayers.length))}</small>
            </span>
          </header>
          <div className="analysis-scoreboard-content">
            <div className="analysis-scoreboard-teams">
              <AnalysisTeamRows teamId="a" name={teamAName} score={analysis.score?.teamA.score} players={teamA} columns={metricColumns} matchRounds={matchRounds} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} />
              <AnalysisTeamRows teamId="b" name={teamBName} score={analysis.score?.teamB.score} players={teamB} columns={metricColumns} matchRounds={matchRounds} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} />
              {unassigned.length > 0 ? <AnalysisTeamRows teamId="unknown" name={words.unassignedPlayers} players={unassigned} columns={metricColumns} matchRounds={matchRounds} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} /> : null}
            </div>
          </div>
        </section>
      ) : null}
    </div>
  );
}
