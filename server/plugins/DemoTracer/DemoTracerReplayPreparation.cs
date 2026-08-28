/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
    private readonly HashSet<uint> _pendingSafeC4DropHandles = [];
    private long _pendingSafeC4GrantEpoch = -1;
    private int _pendingSafeC4GrantSlot = -1;
    private long _safeC4ReplacementAuthorizedEpoch = -1;

    private string PlayLoaded(bool loop)
    {
        PreloadLoadedReplays();
        return StartLoaded(loop);
    }

    private void PrepareLoadedReplayOwnership()
    {
        foreach (var slot in _session.LoadedSlots)
        {
            if (IsReplaySlotStillSafe(slot))
                _session.ReplaySlots.Claim(slot);
        }

        // Establish round-start positions before any later freeze-time replay
        // scheduling can leave partial-roster bots at native spawn points.
        ScheduleInitialRoundSpawnAssignment();
        _ = SyncBotHiderPresentationLease(announce: false);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void PreloadLoadedReplays()
    {
        PrepareLoadedReplayOwnership();
        CancelPendingReplaySlotReconciliations();

        if (_weaponAlignEnabled)
        {
            foreach (var slot in _session.LoadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_session.LoadedReplays.TryGetValue(slot, out var replay))
                {
                    ApplyReplayLoadoutForSlot(slot, replay);
                    PreloadReplayWeaponsForSlot(slot, replay);
                }
            }
        }

        // Replay identity cosmetics are mandatory even when every optional
        // positive-evidence component is disabled: missing agent/knife/glove
        // evidence means native/default, not Randomizer ownership.
        if (_session.LoadedReplays.Count > 0)
        {
            foreach (var slot in _session.LoadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_session.LoadedReplays.TryGetValue(slot, out var replay))
                {
                    QueueLoadedReplayCosmeticAlignmentForSlot(slot);
                }
            }
        }

        ApplyLoadedReplayScoreboards();
        AlignSafeC4OwnerForLoadedReplays();
    }

    private bool ReplayMusicKitAlignmentAllowed(int musicKitId)
        => _cosmeticAlignEnabled && IsKnownMusicKitId(musicKitId);

    private static string DetectCs2PatchVersion()
    {
        try
        {
            var steamInfPath = Path.Combine(Server.GameDirectory, "steam.inf");
            var patchLine = File.ReadLines(steamInfPath)
                .FirstOrDefault(line => line.StartsWith("PatchVersion=", StringComparison.OrdinalIgnoreCase));
            return patchLine?["PatchVersion=".Length..].Trim() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private int NormalizeMusicKitId(uint? musicKitId)
        => musicKitId is > 0 and <= int.MaxValue && IsKnownMusicKitId((int)musicKitId.Value)
            ? (int)musicKitId.Value
            : 0;

    private int NormalizeMusicKitId(int musicKitId)
        => IsKnownMusicKitId(musicKitId) ? musicKitId : 0;

    private void AlignSafeC4OwnerForLoadedReplays(bool forceReconcile = false)
    {
        if (_session.SafeC4Aligned && !forceReconcile)
            return;

        var plantedOwner = FindLoadedC4Owner(IsBombPlantedEvent);
        var initialOwner = FindLoadedC4Owner(IsBombInitialOwnerEvent);
        var targetOwner = plantedOwner ?? initialOwner;

        if (!targetOwner.HasValue)
        {
            ResetSafeC4RoundMutationState();
            return;
        }

        var targetSlot = targetOwner.Value.Slot;
        var targetSteamId = targetOwner.Value.SteamId;
        if (targetSlot < 0 || !CanWriteReplaySlot(targetSlot))
            return;

        var player = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;
        if (!_session.LoadedReplays.TryGetValue(targetSlot, out var targetReplay))
        {
            Server.PrintToConsole(
                $"[DTR ERR] refusing C4 alignment slot={targetSlot}: replay is not loaded");
            return;
        }
        if (!ReplayTeamAssignmentPolicy.CanAlignC4(targetReplay.ManifestTeam, player.Team))
        {
            Server.PrintToConsole(
                $"[DTR ERR] refusing C4 alignment slot={targetSlot} " +
                $"actual={player.Team} manifest={targetReplay.ManifestTeam}");
            return;
        }

        var foreignOwners = new List<(CCSPlayerController Player, bool CanMutate)>();

        // CS2 may assign its native C4 to another live T, including a human who
        // joins during freeze time. C4 is a unique networked objective entity:
        // move it through the engine's drop path and never detach/kill it in the
        // same pass that grants its replacement.
        foreach (var candidate in FindTeamPlayers())
        {
            if (candidate.Slot == targetSlot ||
                CountCurrentReplayItems(candidate, "weapon_c4") <= 0)
                continue;

            var canMutate = ReplayWeaponReplacementPolicy.CanMutateForeignC4Owner(
                IsReplayTargetBot(candidate),
                _session.LoadedReplays.ContainsKey(candidate.Slot),
                _session.ReplaySlots.IsOwned(candidate.Slot));
            foreignOwners.Add((candidate, canMutate));
        }

        var targetHasC4 = CountCurrentReplayItems(player, "weapon_c4") > 0;
        var grantPending = _pendingSafeC4GrantEpoch == _replayRoundWorkEpoch &&
                           _pendingSafeC4GrantSlot == targetSlot;
        var replacementAuthorized = _safeC4ReplacementAuthorizedEpoch == _replayRoundWorkEpoch;
        switch (ReplayWeaponReplacementPolicy.DecideSafeC4Alignment(
                    targetHasC4,
                    foreignOwners.Count,
                    _pendingSafeC4DropHandles.Count,
                    grantPending,
                    replacementAuthorized))
        {
            case SafeC4AlignmentAction.DropForeignOwners:
                foreach (var foreignOwner in foreignOwners)
                {
                    if (!foreignOwner.CanMutate)
                    {
                        Server.PrintToConsole(
                            $"[DTR WARN] C4 safe transfer blocked by unowned replay slot={foreignOwner.Player.Slot}");
                        continue;
                    }

                    _ = DropC4FromPlayerForSafeTransfer(
                        foreignOwner.Player,
                        "safe_c4_owner_align");
                }
                return;

            case SafeC4AlignmentAction.WaitForCleanup:
            case SafeC4AlignmentAction.WaitForNativeAssignment:
                return;

            case SafeC4AlignmentAction.GrantTarget:
                BeginSafeC4Grant(player, targetSteamId);
                return;

            case SafeC4AlignmentAction.TargetReady:
                _pendingSafeC4GrantEpoch = -1;
                _pendingSafeC4GrantSlot = -1;
                _safeC4ReplacementAuthorizedEpoch = -1;
                MarkSafeC4OwnerAligned(
                    targetSlot,
                    targetSteamId,
                    plantedOwner,
                    initialOwner);
                return;
        }
    }

    private void MarkSafeC4OwnerAligned(
        int targetSlot,
        ulong targetSteamId,
        (int Slot, ulong SteamId)? plantedOwner,
        (int Slot, ulong SteamId)? initialOwner)
    {
        var firstAlignment = !_session.SafeC4Aligned;
        _session.SafeC4Aligned = true;
        if (!firstAlignment)
            return;

        if (plantedOwner.HasValue &&
            initialOwner.HasValue &&
            plantedOwner.Value.SteamId != initialOwner.Value.SteamId)
        {
            Server.PrintToConsole(
                "dtr: C4 safe owner collapsed to planter " +
                $"slot={targetSlot} steam_id={targetSteamId} initial_steam_id={initialOwner.Value.SteamId}");
            return;
        }

        var source = plantedOwner.HasValue ? "bomb_planted" : "bomb_initial_owner";
        Server.PrintToConsole(
            $"dtr: C4 safe owner aligned slot={targetSlot} steam_id={targetSteamId} source={source}");
    }

    private void BeginSafeC4Grant(CCSPlayerController player, ulong targetSteamId)
    {
        var roundEpoch = _replayRoundWorkEpoch;
        if (_safeC4ReplacementAuthorizedEpoch != roundEpoch)
            return;

        // Consume the authorization before calling the engine. A successful
        // GiveNamedItem whose attachment is delayed or unobservable must never
        // be retried as a second C4 grant.
        _safeC4ReplacementAuthorizedEpoch = -1;
        _pendingSafeC4GrantEpoch = roundEpoch;
        _pendingSafeC4GrantSlot = player.Slot;
        if (!TryGiveNamedItem(player, "weapon_c4"))
        {
            _pendingSafeC4GrantEpoch = -1;
            _pendingSafeC4GrantSlot = -1;
            Server.PrintToConsole(
                $"dtr: C4 safe owner grant failed slot={player.Slot} steam_id={targetSteamId}");
            return;
        }

        Server.NextFrame(() => VerifySafeC4Grant(
            roundEpoch,
            player.Slot,
            targetSteamId,
            checksRemaining: WeaponSlotReplacementGrantWaitFrames));
    }

    private void VerifySafeC4Grant(
        long roundEpoch,
        int targetSlot,
        ulong targetSteamId,
        int checksRemaining)
    {
        if (!IsReplayRoundWorkEpochCurrent(roundEpoch) ||
            _pendingSafeC4GrantEpoch != roundEpoch ||
            _pendingSafeC4GrantSlot != targetSlot)
        {
            return;
        }

        if (!CanWriteReplaySlot(targetSlot))
        {
            _pendingSafeC4GrantEpoch = -1;
            _pendingSafeC4GrantSlot = -1;
            return;
        }

        var player = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is { IsValid: true, PawnIsAlive: true } &&
            CountCurrentReplayItems(player, "weapon_c4") > 0)
        {
            _pendingSafeC4GrantEpoch = -1;
            _pendingSafeC4GrantSlot = -1;
            AlignSafeC4OwnerForLoadedReplays(forceReconcile: true);
            return;
        }

        if (checksRemaining > 0)
        {
            Server.NextFrame(() => VerifySafeC4Grant(
                roundEpoch,
                targetSlot,
                targetSteamId,
                checksRemaining - 1));
            return;
        }

        _pendingSafeC4GrantEpoch = -1;
        _pendingSafeC4GrantSlot = -1;
        Server.PrintToConsole(
            $"[DTR WARN] C4 safe owner grant was not observed slot={targetSlot} steam_id={targetSteamId}");
    }

    private static bool IsBombInitialOwnerEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_initial_owner", StringComparison.OrdinalIgnoreCase);

    private static bool IsBombPlantedEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_planted", StringComparison.OrdinalIgnoreCase);

    private (int Slot, ulong SteamId)? FindLoadedC4Owner(Func<ReplayHifiEvent, bool> predicate)
    {
        foreach (var slot in _session.LoadedSlots)
        {
            if (!CanWriteReplaySlot(slot) ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay))
                continue;

            var replayEvent = replay.HifiEvents.FirstOrDefault(predicate);
            if (replayEvent is null)
                continue;

            var steamId = replayEvent.ActorSteamId.GetValueOrDefault(replay.SteamId);
            return (slot, steamId);
        }

        return null;
    }

    private bool DropC4FromPlayerForSafeTransfer(CCSPlayerController player, string reason)
    {
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
            !IsReplayTargetBot(player))
            return false;

        var pawn = player.PlayerPawn.Value;
        var success = true;
        foreach (var weapon in GetReplayWeaponsByClass(pawn, "weapon_c4").ToArray())
            success &= DropC4ForSafeTransfer(player, pawn, weapon, reason);
        return success;
    }

    private bool DropC4ForSafeTransfer(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponEntityHandle = weapon.EntityHandle.Raw;
        var pawnOwnsC4 = PawnOwnsWeapon(pawn, weapon);
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex ||
            !WeaponClassMatches(weapon.DesignerName, "weapon_c4") ||
            !pawnOwnsC4)
        {
            return false;
        }
        if (_pendingSafeC4DropHandles.Contains(weaponEntityHandle))
            return true;
        if (pawn.ItemServices == null || pawn.ItemServices.Handle == IntPtr.Zero)
            return false;

        var activeWeaponHandle = pawn.WeaponServices?.ActiveWeapon.Raw ??
                                 Utilities.InvalidEHandleIndex;
        if (!ReplayWeaponReplacementPolicy.CanUseActiveWeaponDropForC4(
                pawnOwnsC4,
                activeWeaponHandle == weaponEntityHandle))
        {
            // CounterStrikeSharp's DropActivePlayerWeapon drops the pawn's active
            // weapon even though it accepts a weapon argument. Never call it for
            // an inactive C4: doing so drops whichever gun or knife replay just
            // selected while leaving the C4 owned, which starts a retry loop.
            Server.PrintToConsole(
                $"[DTR WARN] preserving native C4 owner slot={player.Slot} " +
                $"reason={reason} active_handle={activeWeaponHandle} c4_handle={weaponEntityHandle}");
            return false;
        }

        try
        {
            // The C4 identity and active-weapon identity were observed to match
            // immediately above, so this active-weapon API cannot drop a gun.
            var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            itemServices.DropActivePlayerWeapon(weapon);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to engine-drop C4 slot={player.Slot} reason={reason}: {ex.Message}");
            return false;
        }

        var roundEpoch = _replayRoundWorkEpoch;
        _pendingSafeC4DropHandles.Add(weaponEntityHandle);
        Server.NextFrame(() =>
        {
            if (!IsReplayRoundWorkEpochCurrent(roundEpoch) ||
                !_pendingSafeC4DropHandles.Contains(weaponEntityHandle))
            {
                return;
            }

            Server.NextFrame(() => CleanupDroppedSafeC4(
                roundEpoch,
                player.Slot,
                weaponEntityHandle,
                reason,
                killIssued: false,
                retriesRemaining: DetachedWeaponCleanupRetryFrames));
        });
        return true;
    }

    private void CleanupDroppedSafeC4(
        long roundEpoch,
        int sourceSlot,
        uint weaponEntityHandle,
        string reason,
        bool killIssued,
        int retriesRemaining)
    {
        if (!IsReplayRoundWorkEpochCurrent(roundEpoch) ||
            !_pendingSafeC4DropHandles.Contains(weaponEntityHandle))
        {
            return;
        }
        if (!HasSafeC4AlignmentTarget())
        {
            _pendingSafeC4DropHandles.Remove(weaponEntityHandle);
            return;
        }

        try
        {
            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            if (weapon is not { IsValid: true } ||
                weapon.EntityHandle.Raw != weaponEntityHandle ||
                !WeaponClassMatches(weapon.DesignerName, "weapon_c4"))
            {
                FinishSafeC4Drop(
                    roundEpoch,
                    weaponEntityHandle,
                    authorizeReplacement: killIssued);
                return;
            }

            var ownedByPawn = false;
            var activeWeaponReference = false;
            foreach (var candidate in Utilities.GetPlayers())
            {
                var candidatePawn = candidate?.PlayerPawn.Value;
                var weaponServices = candidatePawn?.WeaponServices;
                if (candidate is not { IsValid: true } ||
                    candidatePawn is not { IsValid: true } ||
                    weaponServices == null)
                {
                    continue;
                }

                ownedByPawn |= PawnOwnsWeapon(candidatePawn, weapon);
                activeWeaponReference |= weaponServices.ActiveWeapon.Raw == weaponEntityHandle;
                if (ownedByPawn && activeWeaponReference)
                    break;
            }

            // If somebody picked the valid dropped C4 up, stop touching that
            // entity and let reconciliation either accept or engine-drop it.
            if (ownedByPawn)
            {
                FinishSafeC4Drop(
                    roundEpoch,
                    weaponEntityHandle,
                    authorizeReplacement: false);
                return;
            }

            if (activeWeaponReference)
            {
                if (retriesRemaining > 0)
                {
                    Server.NextFrame(() => CleanupDroppedSafeC4(
                        roundEpoch,
                        sourceSlot,
                        weaponEntityHandle,
                        reason,
                        killIssued,
                        retriesRemaining - 1));
                    return;
                }

                Server.PrintToConsole(
                    $"[DTR WARN] dropped C4 remains engine-referenced slot={sourceSlot} reason={reason}");
                return;
            }

            if (!killIssued)
            {
                weapon.AcceptInput("Kill");
                Server.NextFrame(() => CleanupDroppedSafeC4(
                    roundEpoch,
                    sourceSlot,
                    weaponEntityHandle,
                    reason,
                    killIssued: true,
                    retriesRemaining));
                return;
            }

            if (retriesRemaining > 0)
            {
                Server.NextFrame(() => CleanupDroppedSafeC4(
                    roundEpoch,
                    sourceSlot,
                    weaponEntityHandle,
                    reason,
                    killIssued: true,
                    retriesRemaining - 1));
                return;
            }

            Server.PrintToConsole(
                $"[DTR WARN] dropped C4 destruction was not observed slot={sourceSlot} reason={reason}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to clean dropped C4 slot={sourceSlot} reason={reason}: {ex.Message}");
        }
    }

    private void FinishSafeC4Drop(
        long roundEpoch,
        uint weaponEntityHandle,
        bool authorizeReplacement)
    {
        if (!IsReplayRoundWorkEpochCurrent(roundEpoch) ||
            !_pendingSafeC4DropHandles.Remove(weaponEntityHandle))
        {
            return;
        }

        // A new C4 may be created only after the exact old entity was killed
        // and that destruction was observed. Merely dropping it, losing sight
        // of it, or seeing another pawn pick it up never authorizes a clone.
        _safeC4ReplacementAuthorizedEpoch = authorizeReplacement
            ? roundEpoch
            : -1;

        ScheduleReplayRoundNextFrame(
            ReplayRoundWorkKind.C4PostMutationReconcile,
            () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true));
    }

    private void ResetSafeC4RoundMutationState()
    {
        _pendingSafeC4DropHandles.Clear();
        _pendingSafeC4GrantEpoch = -1;
        _pendingSafeC4GrantSlot = -1;
        _safeC4ReplacementAuthorizedEpoch = -1;
    }

    private bool HasSafeC4AlignmentTarget()
        => FindLoadedC4Owner(IsBombPlantedEvent).HasValue ||
           FindLoadedC4Owner(IsBombInitialOwnerEvent).HasValue;

    private void CancelSafeC4MutationWithoutTarget()
    {
        if (!HasSafeC4AlignmentTarget())
            ResetSafeC4RoundMutationState();
    }
}
