/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private SoulChallengeState? _soulChallenge;
    private readonly HashSet<int> _soulParticipantUserIds = [];

    [ConsoleCommand("dtr_soul", "dtr_soul <replay.dtr> <source_round> [t|ct]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SoulCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command)) return;
        if (!TryParseRoundArgs(command, "dtr_soul", out var manifest, out var round)) return;
        var team = command.ArgCount > 3
            ? command.GetArg(3).ToLowerInvariant() switch { "t" => CsTeam.Terrorist, "ct" => CsTeam.CounterTerrorist, _ => CsTeam.None }
            : player?.Team ?? CsTeam.None;
        if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            command.ReplyToCommand("[DTR ERR] Join T/CT first, or specify dtr_soul <replay.dtr> <source_round> t|ct.");
            return;
        }
        if (!Utilities.GetPlayers().Any(p => IsSoulHuman(p) && p.Team == team))
        {
            command.ReplyToCommand("[DTR ERR] The challenge team needs at least one human player.");
            return;
        }
        // Hider readiness reissues the command from the server console, where
        // the original player's team is no longer available.
        var deferredCommand = command.ArgCount > 3 ? command.GetCommandString
            : $"{command.GetCommandString} {(team == CsTeam.Terrorist ? "t" : "ct")}";
        if (!EnsureHiderForCommand(command, deferredCommand)) return;
        if (!PlanSingleRound("dtr_soul", manifest, round, loop: false, restart: true, command.ReplyToCommand)) return;

        _soulChallenge = new SoulChallengeState(manifest, round, (int)team);
        _soulParticipantUserIds.Clear();
        LoadSoulDeathSound();
        CancelDtrRoundBanner(resetRound: false);
        command.ReplyToCommand($"[Demo Souls] Source round {round}, team={team}. Team defeat retries this round; victory completes the challenge.");
    }

    [ConsoleCommand("dtr_soul_stop", "Stop Demo Souls and print its statistics")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SoulStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_pendingHiderCommand?.StartsWith("dtr_soul ", StringComparison.OrdinalIgnoreCase) == true)
            CancelHiderSwitch();
        if (_soulChallenge is { Active: true })
            StopAllState("soul_manual_stop");
        else
            CancelSoulChallenge("soul_manual_stop");
        ReplySoulStats(command.ReplyToCommand);
    }

    [ConsoleCommand("dtr_soul_stats", "Show current or last Demo Souls challenge statistics")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SoulStatsCommand(CCSPlayerController? player, CommandInfo command)
        => ReplySoulStats(command.ReplyToCommand);

    private bool IsSoulHuman(CCSPlayerController player)
        // dtr-hider clears the fake-client flag, so IsBot alone is insufficient.
        => player is { IsValid: true, IsBot: false, IsHLTV: false } && player.UserId.HasValue &&
           !_botHiderBridge.IsManagedBot(player.Slot);

    private void BeginSoulAttempt(int startedSlots)
    {
        var soul = _soulChallenge;
        if (soul is not { Active: true } || soul.Fighting) return;
        if (startedSlots == 0)
        {
            StopAllState("soul_replay_start_failed");
            return;
        }
        _soulParticipantUserIds.Clear();
        foreach (var player in Utilities.GetPlayers())
            if (IsSoulHuman(player) && (int)player.Team == soul.Team)
                _soulParticipantUserIds.Add(player.UserId!.Value);
        if (_soulParticipantUserIds.Count == 0)
        {
            StopAllState("soul_team_empty");
            return;
        }
        soul.BeginAttempt(Server.CurrentTime);
        ShowSoulIntro(soul);
        Server.PrintToChatAll($"[Demo Souls] 挑战开始 · 源回合 {soul.SourceRound} · 第 {soul.AttemptNumber} 次尝试");
    }

    [GameEventHandler]
    public HookResult OnSoulRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        var soul = _soulChallenge;
        if (soul == null || !soul.FinishRound(@event.Winner, Server.CurrentTime)) return HookResult.Continue;
        if (soul.Phase == SoulChallengePhase.Cleared)
        {
            StopAllState("soul_cleared");
            Server.PrintToChatAll($"[Demo Souls] 挑战成功！共尝试 {soul.Results.Count} 次，失败 {soul.Results.Count(r => r.Outcome == SoulAttemptOutcome.Defeat)} 次。控制台 dtr_soul_stats 查看每次记录。");
            ReplySoulStats(Server.PrintToConsole);
        }
        else
        {
            StopLoadedReplaySlots("soul_retry");
            _session.Plan.Armed = true;
            _session.Plan.ArmedLoop = false;
            _session.Plan.ArmedPrepared = false;
            _session.Plan.ArmedPreparePollToken++;
            _session.Plan.ArmedManifestPath = soul.ManifestPath;
            _session.Plan.ArmedSourceRound = soul.SourceRound;
            _session.Plan.ArmedLabel = $"soul source_round={soul.SourceRound}";
            PrefetchRoundReplays(soul.ManifestPath, soul.SourceRound);
            Server.PrintToChatAll($"[Demo Souls] 队伍战败 · 第 {soul.Results.Count} 次尝试结束 · 正在重置同一回合");
            Server.ExecuteCommand("mp_restartgame 1");
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSoulPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (_soulChallenge is { Fighting: true } soul &&
            IsSoulParticipant(@event.Attacker) && @event.Userid is { IsValid: true } victim &&
            victim.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist && (int)victim.Team != soul.Team)
            soul.Damage += Math.Max(0, @event.DmgHealth);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSoulPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_soulChallenge is not { Fighting: true } soul) return HookResult.Continue;
        if (IsSoulParticipant(@event.Userid))
        {
            soul.Deaths++;
            PlaySoulDeathSound(@event.Userid!);
        }
        if (IsSoulParticipant(@event.Attacker) && @event.Userid is { IsValid: true } victim &&
            victim.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist && (int)victim.Team != soul.Team)
            soul.Kills++;
        // A human death never resets the attempt; the engine's team result decides it.
        return HookResult.Continue;
    }

    private bool IsSoulParticipant(CCSPlayerController? player)
        => player != null && IsSoulHuman(player) && player.UserId is { } id &&
           _soulParticipantUserIds.Contains(id) && (int)player.Team == _soulChallenge?.Team;

    private void CancelSoulChallenge(string reason)
    {
        if (_soulChallenge is { Active: true } soul)
        {
            soul.Stop(Server.CurrentTime);
            _session.Plan.ClearArmed();
            CancelReplayPrefetch();
            Server.PrintToConsole($"[Demo Souls] Challenge stopped: {reason}");
        }
        _soulParticipantUserIds.Clear();
        StopSoulDeathSound();
        _soulDeathSound = null;
    }

    private void ReplySoulStats(Action<string> reply)
    {
        if (_soulChallenge is not { } soul)
        {
            reply("[Demo Souls] No challenge has been started.");
            return;
        }
        var results = soul.Results;
        reply($"[Demo Souls] source_round={soul.SourceRound} team={(CsTeam)soul.Team} status={soul.Phase} attempts={soul.AttemptNumber} losses={results.Count(r => r.Outcome == SoulAttemptOutcome.Defeat)} interrupted={results.Count(r => r.Outcome == SoulAttemptOutcome.Interrupted)}");
        reply($"[Demo Souls] Human participants: kills={results.Sum(r => r.Kills) + (soul.Fighting ? soul.Kills : 0)} deaths={results.Sum(r => r.Deaths) + (soul.Fighting ? soul.Deaths : 0)} damage={results.Sum(r => r.Damage) + (soul.Fighting ? soul.Damage : 0)} combat_seconds={results.Sum(r => r.Seconds) + soul.Elapsed(Server.CurrentTime):F1}");
        foreach (var result in results)
            reply($"[Demo Souls] #{result.Number} {result.Outcome} {result.Seconds:F1}s K/D={result.Kills}/{result.Deaths} damage={result.Damage}");
    }

    private void CheckSoulTeam(int excludedSlot = -1)
    {
        if (_soulChallenge is { Active: true } soul &&
            !Utilities.GetPlayers().Any(player => player.Slot != excludedSlot &&
                IsSoulHuman(player) && (int)player.Team == soul.Team))
            StopAllState("soul_team_empty");
    }

    private void ShowSoulIntro(SoulChallengeState soul)
    {
        var html = $"<font color='#e8ce8b' class='fontSize-l'>Demo Souls</font><br>源回合 {soul.SourceRound} · {(CsTeam)soul.Team} · 尝试 #{soul.AttemptNumber}<br><font color='#aaaaaa'>队伍获胜即通关 · 战败自动重试</font>";
        foreach (var player in Utilities.GetPlayers())
            if (IsSoulHuman(player) && ((int)player.Team == soul.Team || player.Team == CsTeam.Spectator))
                player.PrintToCenterHtml(html, 5);
    }
}
