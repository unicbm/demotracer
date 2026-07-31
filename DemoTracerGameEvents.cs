using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetDtrRoundBannerForRound();
        BeginBotHiderPresentationTransition();
        try
        {
            if (StopReplayStateForRoundBoundary("round_start"))
                Server.PrintToConsole("[DTR WARN] round_start stopped stale DTR replay state");

            if ((_session.SequenceActive || _session.Armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
            {
                Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
                StopAllState("warmup_block");
                return HookResult.Continue;
            }

            if (_session.SequenceActive)
            {
                if (PrepareNextSequenceRound("round_start"))
                    ScheduleFreezePrerollStart($"sequence round {_session.SequencePreparedRound}");
            }
            else if (IsPlayoffPlanReady())
            {
                if (PrepareNextPlayoffRound("round_start"))
                    ScheduleFreezePrerollStart($"playoff extra round {_session.PlayoffRoundIndex + 1}");
            }
            else if (_session.Armed)
            {
                if (PrepareArmedRound("round_start"))
                    ScheduleFreezePrerollStart(_session.ArmedLabel);
            }
            Server.NextFrame(ScheduleLoadedReplayMusicKitRepairs);

            return HookResult.Continue;
        }
        finally
        {
            EndBotHiderPresentationTransition();
        }
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        InvalidateFreezePreroll();

        if ((_session.SequenceActive || _session.Armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

        var resumeLoop = !_session.SequenceActive &&
                         !HasPlayoffSchedulingState() &&
                         _session.Armed &&
                         _session.ArmedLoop;
        ResumeFreezePrerollReplays(resumeLoop);

        var missingFreezePrerollSlots = MissingFreezePrerollResumeSlots();
        if (missingFreezePrerollSlots.Length > 0)
        {
            Server.PrintToConsole(
                "[DTR WARN] freeze pre-roll ownership was incomplete; " +
                $"retrying affected slots from their live replay index slots={string.Join(",", missingFreezePrerollSlots)}");
        }

        if (_session.SequenceActive)
        {
            Server.NextFrame(StartPreparedSequenceRound);
            return HookResult.Continue;
        }

        if (HasPlayoffSchedulingState())
        {
            Server.NextFrame(StartPreparedPlayoffRound);
            return HookResult.Continue;
        }

        if (!_session.Armed)
            return HookResult.Continue;
        if (!_session.ArmedPrepared)
        {
            Server.PrintToConsole($"[DTR WARN] armed round is waiting for the next full round_start: {_session.ArmedLabel}");
            return HookResult.Continue;
        }

        var loop = _session.ArmedLoop;
        var label = _session.ArmedLabel;
        _session.Armed = false;
        _session.ArmedPrepared = false;
        Server.NextFrame(() =>
        {
            var message = StartLoaded(loop, ReplayStartAnchor.Live, null);
            Server.PrintToConsole($"dtr: auto-start {label}: {message}");
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            var spawnedSlot = player.Slot;
            var spawnedUserId = player.UserId;
            if (_retainedReplayViewmodelSlots.Contains(player.Slot) &&
                !_session.LastPlayingSlots.Contains(player.Slot))
            {
                RestoreReplayBotViewmodel(player.Slot);
            }
            ScheduleLoadedReplayCosmeticRepairForSlot(player.Slot);
            if (_session.LoadedSlots.Count > 0)
            {
                if (_session.LoadedReplays.ContainsKey(player.Slot))
                {
                    // Establish the replay identity and its Agent/Knife/Gloves
                    // writer lease in the spawn event itself. BotRandomizer's
                    // pawn writes run on its later spawn callback.
                    _ = SyncBotHiderPresentationLease(announce: false);
                    _ = SyncBotRandomizerCosmeticLease(announce: false);
                    // Buy plans are slot-scoped, but the engine creates a new
                    // pawn each round. Reassert the skip edge at spawn and redo
                    // loadout preparation once the new pawn is fully usable.
                    _ = BotControllerNative.SetBuySkip(spawnedSlot);
                    var spawnToken = _session.InitialSpawnAssignmentToken;
                    Server.NextFrame(() =>
                    {
                        if (spawnToken != _session.InitialSpawnAssignmentToken ||
                            spawnedUserId is not int expectedUserId ||
                            Utilities.GetPlayerFromSlot(spawnedSlot) is not { IsValid: true } currentPlayer ||
                            currentPlayer.UserId != expectedUserId ||
                            !_session.LoadedReplays.TryGetValue(spawnedSlot, out var replay))
                        {
                            return;
                        }

                        ApplyReplayLoadoutForSlot(spawnedSlot, replay);
                        PreloadReplayWeaponsForSlot(spawnedSlot, replay);
                    });
                }
                ScheduleReplayMusicKitRepairForSlot(spawnedSlot);
                ScheduleInitialRoundSpawnAssignment();
                Server.NextFrame(() => SyncBotHiderPresentationLease(announce: false));
                AddTimer(
                    0.05f,
                    () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true),
                    TimerFlags.STOP_ON_MAPCHANGE);
                AddTimer(
                    0.20f,
                    () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true),
                    TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
        {
            if (IsReplaySlotPlaying(victim.Slot))
            {
                BotControllerNative.StopReplay(victim.Slot);
                ReleaseReplaySlot(victim.Slot, "replay_target_death");
            }
            else if (_retainedReplayViewmodelSlots.Contains(victim.Slot))
            {
                RestoreReplayBotViewmodel(victim.Slot);
            }
        }

        if (HandoffIncludesDeath(_handoffMode) && HasActiveReplaySlots())
        {
            var triggerSlot = GetDeathHandoffSlot(@event);
            if (triggerSlot >= 0)
                HandoffActiveReplays($"player_death_slot{triggerSlot}", triggerSlot);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (!HandoffIncludesC4(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        var triggerSlot = @event.Userid is { IsValid: true } planter && IsReplaySlotPlaying(planter.Slot)
            ? planter.Slot
            : -1;
        HandoffActiveReplays(
            triggerSlot >= 0 ? $"bomb_planted_slot{triggerSlot}" : "bomb_planted",
            triggerSlot,
            forceAll: true);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBulletDamage(EventBulletDamage @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Victim, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_session.PendingBulletDamages.TryGetValue(victimSlot, out var damage) &&
            damage.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(damage.Time))
        {
            _session.PendingBulletDamages.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage.Damage);
        }
        else
        {
            _session.PendingBulletHits[victimSlot] = new PendingBulletHit(attackerSlot, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Userid, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        var damage = Math.Max(0, @event.DmgHealth) + Math.Max(0, @event.DmgArmor);
        if (damage < BulletHandoffMinDamage)
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_session.PendingBulletHits.TryGetValue(victimSlot, out var hit) &&
            hit.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(hit.Time))
        {
            _session.PendingBulletHits.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage);
        }
        else
        {
            _session.PendingBulletDamages[victimSlot] = new PendingBulletDamage(attackerSlot, damage, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        TickRuntimeHealthHeartbeat();

        if (!_mapActive || _lifecycleResetInProgress)
            return;

        _botHiderBridge.BeginTickQueryScope();
        try
        {
            EnsureBotHiderPresentationLease();
            EnsureBotRandomizerCosmeticLease();
            ProcessReplayTick();
        }
        finally
        {
            _botHiderBridge.EndTickQueryScope();
        }
    }
}
