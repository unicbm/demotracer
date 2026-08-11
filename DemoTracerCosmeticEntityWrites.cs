/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool TryApplyAgentCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayAgentCosmetic cosmetic,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        if (!IsReplaySlotStillSafe(slot) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Agent) ||
            NormalizeAgentModelPath(cosmetic.ModelPath) is not { } modelPath)
        {
            return false;
        }

        try
        {
            ApplyAgentModel(pawn, modelPath);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: agent model apply failed slot={player.Slot} model={modelPath}: {ex.Message}");
            return false;
        }
    }

    private static void ApplyAgentModel(CCSPlayerPawn pawn, string modelPath)
    {
        Server.PrecacheModel(modelPath);
        pawn.SetModel(modelPath);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        var color = pawn.Render;
        pawn.Render = System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    private bool TryRestoreNativeAgentModel(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        if (!IsReplaySlotStillSafe(slot) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Agent) ||
            player.UserId is not int userId ||
            !_nativeAgentModels.TryGetValue(slot, out var native) ||
            native.UserId != userId ||
            native.PawnEntityHandle != pawn.EntityHandle.Raw ||
            native.Team != player.Team)
        {
            return false;
        }

        try
        {
            ApplyAgentModel(pawn, native.ModelPath);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: native agent model restore failed slot={slot} model={native.ModelPath}: {ex.Message}");
            return false;
        }
    }

    private bool TryApplyWeaponCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        ulong replaySteamId,
        bool countStickerStats = true)
    {
        if (!TryGetWeaponClassByDefIndex(cosmetic.WeaponDefIndex, out _))
            return false;

        var paintClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponPaint,
            cosmetic.WeaponDefIndex);
        var stickersClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponStickers,
            cosmetic.WeaponDefIndex);
        var keychainClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponKeychain,
            cosmetic.WeaponDefIndex);
        if (!paintClaimed && !stickersClaimed && !keychainClaimed)
            return false;

        var writeKey = (player.Slot, weapon.Handle);
        var writeStamp = new AppliedCosmeticEntityWrite(
            CurrentReplayIdentityGeneration(player.Slot),
            replaySteamId,
            cosmetic);
        if (_appliedWeaponCosmeticWrites.TryGetValue(writeKey, out var currentWrite) &&
            currentWrite == writeStamp &&
            (!paintClaimed || HasExpectedWeaponPaintState(weapon, cosmetic)))
        {
            return true;
        }

        var paintApplied = !paintClaimed ||
            (TryApplyItemCosmetic(
                 player,
                 weapon,
                 new ReplayItemCosmetic
                 {
                     ItemDefIndex = cosmetic.WeaponDefIndex,
                     PaintKit = cosmetic.PaintKit,
                     Seed = cosmetic.Seed,
                     SeedKnown = null,
                     Wear = cosmetic.Wear,
                     OriginalOwnerSteamId = cosmetic.OriginalOwnerSteamId,
                     ItemAccountId = cosmetic.ItemAccountId,
                     ItemId = cosmetic.ItemId,
                     CustomName = cosmetic.CustomName
                 },
                 replaySteamId,
                 DemoTracerCosmeticWriteField.WeaponPaint,
                 allowSubclassChange: false,
                 applyPaint: true,
                 applyCustomName: _cosmeticNamesEnabled) &&
             HasExpectedWeaponPaintState(weapon, cosmetic));
        var stattrakApplied = !paintClaimed ||
            ApplyWeaponStattrakEvidence(player.Slot, replaySteamId, weapon, cosmetic);
        var stickersApplied = !stickersClaimed ||
            ApplyWeaponStickers(player.Slot, replaySteamId, weapon, cosmetic, countStickerStats);
        var keychainApplied = !keychainClaimed ||
            ApplyWeaponCharms(player.Slot, replaySteamId, weapon, cosmetic, countStickerStats);
        var applied = paintApplied && stattrakApplied && stickersApplied && keychainApplied;
        if (applied)
            _appliedWeaponCosmeticWrites[writeKey] = writeStamp;
        return applied;
    }

    private bool HasExpectedWeaponPaintState(
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic)
    {
        try
        {
            return WeaponPaintStateMatches(
                NormalizeWeaponDefIndex(weapon.AttributeManager.Item.ItemDefinitionIndex),
                weapon.FallbackPaintKit,
                weapon.FallbackSeed,
                weapon.FallbackWear,
                cosmetic.WeaponDefIndex,
                (int)Math.Min(cosmetic.PaintKit, int.MaxValue),
                (int)Math.Min(cosmetic.Seed, int.MaxValue),
                cosmetic.Wear);
        }
        catch
        {
            return false;
        }
    }

    internal static bool WeaponPaintStateMatches(
        int actualWeaponDefIndex,
        int actualPaintKit,
        int actualSeed,
        float actualWear,
        int expectedWeaponDefIndex,
        int expectedPaintKit,
        int expectedSeed,
        float expectedWear)
        => actualWeaponDefIndex == expectedWeaponDefIndex &&
           actualPaintKit == expectedPaintKit &&
           actualSeed == expectedSeed &&
           actualWear.Equals(expectedWear);

    private bool TryApplyKnifeCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        if (!TryValidateBotRandomizerClaim(
                player.Slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Knife))
        {
            return false;
        }

        var writeKey = (player.Slot, weapon.Handle);
        var writeStamp = new AppliedCosmeticEntityWrite(
            CurrentReplayIdentityGeneration(player.Slot),
            replaySteamId,
            cosmetic);
        if (_appliedKnifeCosmeticWrites.TryGetValue(writeKey, out var currentWrite) &&
            currentWrite == writeStamp)
        {
            return true;
        }

        var applied = TryApplyItemCosmetic(
            player,
            weapon,
            cosmetic,
            replaySteamId,
            DemoTracerCosmeticWriteField.Knife,
            allowSubclassChange: true,
            applyPaint: true,
            applyCustomName: _cosmeticNamesEnabled);
        if (applied)
        {
            _appliedKnifeCosmeticWrites[writeKey] = writeStamp;
            ScheduleReplayKnifeSubclassRepair(
                player,
                weapon,
                cosmetic,
                replaySteamId);
        }
        return applied;
    }

    private void ScheduleReplayKnifeSubclassRepair(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        if (player.UserId is not int userId ||
            cosmetic.ItemDefIndex is not int itemDefinitionIndex ||
            !IsKnifeCosmeticDefIndex(itemDefinitionIndex))
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } ||
            weapon is not { IsValid: true } ||
            !PawnOwnsWeapon(pawn, weapon))
        {
            return;
        }

        var pending = new PendingReplayKnifeSubclassRepair(
            player.Slot,
            userId,
            pawn.EntityHandle.Raw,
            weapon.EntityHandle.Raw,
            CurrentReplayWriteEpoch(player.Slot),
            replaySteamId,
            itemDefinitionIndex);
        ScheduleCosmeticNextFrame(() => ReassertReplayKnifeSubclass(pending));
        AddTimer(
            0.10f,
            () => ReassertReplayKnifeSubclass(pending),
            TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(
            0.25f,
            () => ReassertReplayKnifeSubclass(pending),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ReassertReplayKnifeSubclass(PendingReplayKnifeSubclassRepair pending)
    {
        var writeEpochCurrent = IsReplayWriteEpochCurrent(
            pending.PlayerSlot,
            pending.ReplayWriteEpoch);
        var player = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        var samePlayer = player is { IsValid: true, PawnIsAlive: true } &&
                         player.UserId == pending.PlayerUserId;
        var pawn = player?.PlayerPawn.Value;
        var samePawn = samePlayer &&
                       pawn is { IsValid: true } &&
                       pawn.EntityHandle.Raw == pending.PawnEntityHandle;
        var weapon = new CHandle<CBasePlayerWeapon>(pending.WeaponEntityHandle).Value;
        var sameWeapon = weapon is { IsValid: true } &&
                         weapon.EntityHandle.Raw == pending.WeaponEntityHandle;
        var ownedKnife = samePawn &&
                         sameWeapon &&
                         PawnOwnsWeapon(pawn!, weapon!) &&
                         GetReplayWeaponSlot(weapon!.DesignerName) == ReplayWeaponSlot.Knife;
        var activeClaim = HasActiveBotRandomizerClaim(
            pending.PlayerSlot,
            pending.ReplaySteamId,
            DemoTracerCosmeticWriteField.Knife);
        if (!CanReassertReplayKnifeSubclass(
                writeEpochCurrent,
                samePlayer,
                samePawn,
                sameWeapon,
                ownedKnife,
                activeClaim) ||
            !TryValidateBotRandomizerClaim(
                pending.PlayerSlot,
                pending.ReplaySteamId,
                DemoTracerCosmeticWriteField.Knife))
        {
            return;
        }

        try
        {
            var item = weapon!.AttributeManager.Item;
            // ChangeSubclass updates the knife's model, animation graph and HUD
            // asynchronously. ItemDefinitionIndex equality and a changed
            // SubclassID are not semantic completion signals for those three
            // client-facing states. Reissue the same desired subclass on every
            // bounded repair callback so a partially settled transition cannot
            // leave a correct model paired with another knife's animations/HUD.
            weapon.AcceptInput(
                "ChangeSubclass",
                value: pending.ItemDefinitionIndex.ToString(CultureInfo.InvariantCulture));
            item.ItemDefinitionIndex = (ushort)pending.ItemDefinitionIndex;
            item.EntityQuality = 3;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: knife subclass repair failed slot={pending.PlayerSlot} " +
                $"item={pending.ItemDefinitionIndex}: {ex.Message}");
        }
    }

    internal static bool CanReassertReplayKnifeSubclass(
        bool writeEpochCurrent,
        bool samePlayer,
        bool samePawn,
        bool sameWeapon,
        bool ownedKnife,
        bool activeClaim)
        => writeEpochCurrent &&
           samePlayer &&
           samePawn &&
           sameWeapon &&
           ownedKnife &&
           activeClaim;

    private bool TryApplyItemCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        DemoTracerCosmeticWriteField writeField,
        bool allowSubclassChange,
        bool applyPaint,
        bool applyCustomName)
    {
        try
        {
            var weaponDefinitionIndex = writeField == DemoTracerCosmeticWriteField.WeaponPaint
                ? NormalizeWeaponDefIndex(cosmetic.ItemDefIndex ?? WeaponDefIndex(weapon))
                : 0;
            if (!TryValidateBotRandomizerClaim(
                    player.Slot,
                    replaySteamId,
                    writeField,
                    weaponDefinitionIndex))
            {
                return false;
            }

            var item = weapon.AttributeManager.Item;
            if (cosmetic.ItemDefIndex is { } itemDef)
            {
                if (allowSubclassChange &&
                    IsKnifeCosmeticDefIndex(itemDef) &&
                    item.ItemDefinitionIndex != (ushort)itemDef)
                {
                    weapon.AcceptInput("ChangeSubclass", value: itemDef.ToString(CultureInfo.InvariantCulture));
                }
                item.ItemDefinitionIndex = (ushort)itemDef;
            }

            item.EntityQuality = allowSubclassChange ? 3 : 0;
            ApplyReplayEconIdentity(player, weapon, item, cosmetic, replaySteamId);
            if (applyPaint)
            {
                var networkedDynamicAttributes = item.NetworkedDynamicAttributes;
                var attributeList = item.AttributeList;
                if (networkedDynamicAttributes.Handle == IntPtr.Zero ||
                    attributeList.Handle == IntPtr.Zero)
                {
                    return false;
                }

                if (ShouldClearCompleteAttributeLists(writeField))
                {
                    attributeList.Attributes.RemoveAll();
                    networkedDynamicAttributes.Attributes.RemoveAll();
                }
                weapon.FallbackPaintKit = (int)Math.Min(cosmetic.PaintKit, int.MaxValue);
                weapon.FallbackSeed = (int)Math.Min(cosmetic.Seed, int.MaxValue);
                weapon.FallbackWear = cosmetic.Wear;
                MarkWeaponPaintStateChanged(weapon);
                _ = TrySetTextureAttributes(networkedDynamicAttributes.Handle, cosmetic);
                _ = TrySetTextureAttributes(attributeList.Handle, cosmetic);
                var bodygroup = IsLegacyCosmeticPaint(
                    item.ItemDefinitionIndex,
                    (int)Math.Min(cosmetic.PaintKit, int.MaxValue)) ? 1 : 0;
                weapon.AcceptInput("SetBodygroup", value: $"body,{bodygroup}");
            }
            if (applyCustomName && !string.IsNullOrWhiteSpace(cosmetic.CustomName))
                item.CustomName = cosmetic.CustomName;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: cosmetic apply failed slot={player.Slot} item={weapon.DesignerName}: {ex.Message}");
            return false;
        }
    }

    private bool IsLegacyCosmeticPaint(int weaponDefIndex, int paintKit)
        => paintKit > 0 &&
           _legacyCosmeticPaints.Contains((NormalizeWeaponDefIndex(weaponDefIndex), (uint)paintKit));

    private bool IsExactKnifeCosmeticDefIndex(int weaponDefIndex)
        => IsKnifeCosmeticDefIndex(weaponDefIndex) &&
           _replayEquipment.ByDefIndex.TryGetValue(weaponDefIndex, out var definition) &&
           !definition.ClassName.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase) &&
           !definition.ClassName.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase);

    private bool ApplyWeaponStattrakEvidence(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic)
    {
        if (cosmetic.Quality != 9 && cosmetic.StattrakCounter == null)
            return true;
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponPaint,
                cosmetic.WeaponDefIndex))
        {
            return false;
        }

        var item = weapon.AttributeManager.Item;
        item.EntityQuality = 9;
        weapon.FallbackStatTrak = cosmetic.StattrakCounter ?? 0;
        _ = TrySetStattrakAttributes(item.NetworkedDynamicAttributes.Handle, weapon.FallbackStatTrak);
        _ = TrySetStattrakAttributes(item.AttributeList.Handle, weapon.FallbackStatTrak);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
        return item.EntityQuality == 9 &&
            weapon.FallbackStatTrak == (cosmetic.StattrakCounter ?? 0);
    }

    private bool TryApplyGloveCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        out bool changed)
    {
        changed = false;
        try
        {
            if (AttributeSetter.Value == null)
                return false;

            var slot = player.Slot;
            if (!TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return false;
            }
            var pawnHandle = pawn.Handle;
            var fingerprint = GloveCosmeticFingerprint.From(cosmetic);
            // Glove material creation streams on the client. Rewriting identical econ state disposes
            // and recreates those materials while their texture requests are still outstanding.
            if (IsAppliedGloveCosmeticCurrent(slot, pawn, fingerprint))
                return true;

            var token = ++_nextGloveCosmeticToken;
            _gloveCosmeticTokens[slot] = token;
            if (!ApplyGloveEconItem(player, pawn, cosmetic, replaySteamId))
                return false;
            var item = pawn.EconGloves;
            _appliedGloveCosmetics[slot] = new AppliedGloveCosmetic(
                pawnHandle,
                fingerprint,
                replaySteamId,
                item.ItemID,
                item.ItemDefinitionIndex,
                item.AccountID);
            changed = true;
            AddTimer(0.10f, () => ApplyGloveCosmeticForSlot(slot, cosmetic, replaySteamId, token), TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(0.20f, () => FinishGloveCosmeticBodygroup(slot, pawnHandle, token), TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(0.25f, () => ApplyGloveCosmeticForSlot(slot, cosmetic, replaySteamId, token), TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic apply failed slot={player.Slot}: {ex.Message}");
            return false;
        }
    }

    private bool IsAppliedGloveCosmeticCurrent(
        int slot,
        CCSPlayerPawn pawn,
        GloveCosmeticFingerprint fingerprint)
    {
        if (!_appliedGloveCosmetics.TryGetValue(slot, out var applied) ||
            applied.PawnHandle != pawn.Handle ||
            applied.Fingerprint != fingerprint)
        {
            return false;
        }

        var item = pawn.EconGloves;
        return item.Initialized &&
               item.ItemID == applied.ItemId &&
               item.ItemDefinitionIndex == applied.ItemDefinitionIndex &&
               item.AccountID == applied.AccountId;
    }

    private void ApplyGloveCosmeticForSlot(
        int slot,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        int token)
    {
        try
        {
            if (!_gloveCosmeticTokens.TryGetValue(slot, out var activeToken) ||
                activeToken != token ||
                !IsReplaySlotStillSafe(slot) ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            _ = TryApplyGloveCosmetic(player, pawn, cosmetic, replaySteamId, out _);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic delayed apply failed slot={slot}: {ex.Message}");
        }
    }

    private void FinishGloveCosmeticBodygroup(int slot, nint pawnHandle, int token)
    {
        if (!_gloveCosmeticTokens.TryGetValue(slot, out var activeToken) ||
            activeToken != token ||
            !_appliedGloveCosmetics.TryGetValue(slot, out var applied) ||
            applied.PawnHandle != pawnHandle ||
            !TryValidateBotRandomizerClaim(
                slot,
                applied.ReplaySteamId,
                DemoTracerCosmeticWriteField.Gloves))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            pawn.Handle != pawnHandle ||
            !IsAppliedGloveCosmeticCurrent(slot, pawn, applied.Fingerprint))
        {
            return;
        }

        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
    }

    private bool ApplyGloveEconItem(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        if (!TryValidateBotRandomizerClaim(
                player.Slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Gloves))
        {
            return false;
        }
        var item = pawn.EconGloves;
        if (cosmetic.ItemDefIndex is { } itemDef)
            item.ItemDefinitionIndex = (ushort)itemDef;
        item.AccountID = (uint)player.SteamID;
        item.Initialized = true;
        UpdateReplayEconItemId(item);

        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        if (!TrySetTextureAttributes(item.NetworkedDynamicAttributes.Handle, cosmetic) ||
            !TrySetTextureAttributes(item.AttributeList.Handle, cosmetic))
        {
            return false;
        }

        MarkGloveCosmeticStateChanged(pawn);
        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
        return true;
    }

    private const ulong SteamId64AccountBase = 76_561_197_960_265_728;
    private static ulong _nextReplayEconItemId = 10_000_000_000;

    private static void MarkWeaponPaintStateChanged(CBasePlayerWeapon weapon)
    {
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackSeed");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_flFallbackWear");
    }

    private static void MarkGloveCosmeticStateChanged(CCSPlayerPawn pawn)
    {
        try
        {
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic state change failed: {ex.Message}");
        }
    }

    private static void UpdateReplayEconItemId(CEconItemView item)
    {
        var itemId = _nextReplayEconItemId++;
        SetReplayEconItemId(item, itemId);
    }

    private static void SetReplayEconItemId(CEconItemView item, ulong itemId)
    {
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private void ApplyReplayEconIdentity(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        CEconItemView item,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        var ownerSteamId = NormalizeOptionalULong(cosmetic.OriginalOwnerSteamId);
        var replayPlayerSteamId = NormalizeOptionalULong(replaySteamId);
        var sourceOwnerSteamId = ownerSteamId ?? replayPlayerSteamId;
        var effectiveOwnerSteamId = sourceOwnerSteamId;
        var playerAccountId = AccountIdForReplayPlayer(player, replayPlayerSteamId);
        item.AccountID = AccountIdFromSteamId(effectiveOwnerSteamId)
                         ?? cosmetic.ItemAccountId
                         ?? playerAccountId;
        if (cosmetic.ItemId is { } itemId && itemId != 0)
            SetReplayEconItemId(item, itemId);
        else
            UpdateReplayEconItemId(item);
        TrySetOriginalOwnerXuid(weapon, effectiveOwnerSteamId);
    }

    private static uint AccountIdForReplayPlayer(CCSPlayerController player, ulong? replaySteamId)
        => AccountIdFromSteamId(replaySteamId)
           ?? AccountIdFromSteamId(NormalizeOptionalULong(player.SteamID))
           ?? (uint)player.SteamID;

    private static uint? AccountIdFromSteamId(ulong? steamId)
    {
        if (steamId is not { } value || value == 0)
            return null;
        if (value >= SteamId64AccountBase)
        {
            var accountId = value - SteamId64AccountBase;
            return accountId <= uint.MaxValue ? (uint)accountId : null;
        }
        return value <= uint.MaxValue ? (uint)value : null;
    }

    private static void TrySetOriginalOwnerXuid(CBasePlayerWeapon weapon, ulong? ownerSteamId)
    {
        var value = ownerSteamId.GetValueOrDefault();
        var low = (uint)(value & 0xFFFFFFFF);
        var high = (uint)(value >> 32);
        var setLow = TrySetOriginalOwnerSchemaValue(weapon, "m_OriginalOwnerXuidLow", low) ||
                     TrySetIntegralMember(weapon, "OriginalOwnerXuidLow", low);
        var setHigh = TrySetOriginalOwnerSchemaValue(weapon, "m_OriginalOwnerXuidHigh", high) ||
                      TrySetIntegralMember(weapon, "OriginalOwnerXuidHigh", high);
        if (!setLow && !setHigh)
            return;

        TrySetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
        TrySetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
    }

    private static bool TrySetOriginalOwnerSchemaValue(
        CBasePlayerWeapon weapon,
        string fieldName,
        uint value)
    {
        if (weapon.Handle == IntPtr.Zero)
            return false;
        try
        {
            Schema.SetSchemaValue(weapon.Handle, "CEconEntity", fieldName, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetIntegralMember(object target, string name, ulong value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var property = target.GetType().GetProperty(name, flags);
            if (property?.CanWrite == true &&
                TryConvertIntegralValue(value, property.PropertyType, out var propertyValue))
            {
                property.SetValue(target, propertyValue);
                return true;
            }

            var field = target.GetType().GetField(name, flags);
            if (field != null && TryConvertIntegralValue(value, field.FieldType, out var fieldValue))
            {
                field.SetValue(target, fieldValue);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryConvertIntegralValue(
        ulong value,
        Type targetType,
        out object converted)
    {
        converted = 0U;
        if (targetType == typeof(uint))
        {
            if (value > uint.MaxValue)
                return false;
            converted = (uint)value;
            return true;
        }
        if (targetType == typeof(ulong))
        {
            converted = value;
            return true;
        }
        if (targetType == typeof(int))
        {
            if (value > int.MaxValue)
                return false;
            converted = (int)value;
            return true;
        }
        if (targetType == typeof(long))
        {
            if (value > long.MaxValue)
                return false;
            converted = (long)value;
            return true;
        }
        return false;
    }

    private static void TrySetStateChanged(CBasePlayerWeapon weapon, string className, string fieldName)
    {
        try
        {
            Utilities.SetStateChanged(weapon, className, fieldName);
        }
        catch
        {
            // Some server/API versions expose the field but reject state-change marking.
        }
    }

    private static MemoryFunctionVoid<nint, string, float>? CreateAttributeSetter()
    {
        try
        {
            var signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AttributeSetterWindowsSignature
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? AttributeSetterLinuxSignature
                    : string.Empty;
            return string.IsNullOrWhiteSpace(signature)
                ? null
                : new MemoryFunctionVoid<nint, string, float>(signature);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute setter unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool TrySetTextureAttributes(nint attributeListHandle, ReplayItemCosmetic cosmetic)
    {
        if (attributeListHandle == IntPtr.Zero || AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture prefab", cosmetic.PaintKit);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture seed", cosmetic.Seed);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture wear", cosmetic.Wear);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute write failed: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetStattrakAttributes(nint attributeListHandle, int counter)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater", BitConverter.Int32BitsToSingle(counter));
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater score type", 0.0f);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: StatTrak attribute write failed: {ex.Message}");
            return false;
        }
    }

    private bool ApplyWeaponStickers(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countStickerStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_stickerAlignEnabled ||
            cosmetic.Stickers.Count == 0)
        {
            return false;
        }
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponStickers,
                cosmetic.WeaponDefIndex))
        {
            return false;
        }

        if (AttributeSetter.Value == null)
        {
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
            return false;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var sticker in cosmetic.Stickers)
            {
                var networkedOk = TrySetStickerAttributes(item.NetworkedDynamicAttributes.Handle, sticker);
                var listOk = TrySetStickerAttributes(item.AttributeList.Handle, sticker);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countStickerStats)
            {
                _stickerAppliedCount += applied;
                _stickerSkippedCount += skipped;
            }
            return skipped == 0;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
            return false;
        }
    }

    private void RecordStickerSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _stickerAlignEnabled && count > 0)
            _stickerSkippedCount += count;
    }

    private bool ApplyWeaponCharms(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countCharmStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_charmAlignEnabled ||
            cosmetic.Charms.Count == 0)
        {
            return false;
        }
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponKeychain,
                cosmetic.WeaponDefIndex))
        {
            return false;
        }

        if (AttributeSetter.Value == null)
        {
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
            return false;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var charm in cosmetic.Charms)
            {
                var networkedOk = TrySetCharmAttributes(item.NetworkedDynamicAttributes.Handle, charm);
                var listOk = TrySetCharmAttributes(item.AttributeList.Handle, charm);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countCharmStats)
            {
                _charmAppliedCount += applied;
                _charmSkippedCount += skipped;
            }
            return skipped == 0;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
            return false;
        }
    }

    private void RecordCharmSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _charmAlignEnabled && count > 0)
            _charmSkippedCount += count;
    }

    private static bool TrySetStickerAttributes(nint attributeListHandle, ReplayWeaponSticker sticker)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"sticker slot {sticker.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(sticker.StickerId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} wear", sticker.Wear);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", sticker.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", sticker.OffsetY);
            if (sticker.Scale.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} scale", sticker.Scale.Value);
            if (sticker.Rotation.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} rotation", sticker.Rotation.Value);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker attribute write failed slot={sticker.Slot}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetCharmAttributes(nint attributeListHandle, ReplayWeaponCharm charm)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"keychain slot {charm.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(charm.CharmId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", charm.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", charm.OffsetY);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset z", charm.OffsetZ);
            if (charm.Seed is { } seed)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} seed", BitConverter.UInt32BitsToSingle(seed));
            if (charm.Highlight is { } highlight)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} highlight", BitConverter.UInt32BitsToSingle(highlight));
            if (charm.StickerId is { } stickerId)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} sticker", BitConverter.UInt32BitsToSingle(stickerId));
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm attribute write failed slot={charm.Slot}: {ex.Message}");
            return false;
        }
    }

    private readonly record struct GloveCosmeticFingerprint(
        int ItemDefinitionIndex,
        uint PaintKit,
        uint Seed,
        bool SeedKnown,
        int WearBits)
    {
        public static GloveCosmeticFingerprint From(ReplayItemCosmetic cosmetic)
            => new(
                cosmetic.ItemDefIndex ?? -1,
                cosmetic.PaintKit,
                cosmetic.Seed,
                HasCosmeticSeedEvidence(cosmetic.SeedKnown),
                BitConverter.SingleToInt32Bits(cosmetic.Wear));
    }

    private readonly record struct AppliedGloveCosmetic(
        nint PawnHandle,
        GloveCosmeticFingerprint Fingerprint,
        ulong ReplaySteamId,
        ulong ItemId,
        ushort ItemDefinitionIndex,
        uint AccountId);
}
