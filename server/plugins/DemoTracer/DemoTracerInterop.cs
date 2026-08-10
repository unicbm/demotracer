/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    public static string LastLoadError { get; private set; } = string.Empty;

    public static int AbiVersion
    {
        get
        {
            try
            {
                return BotController_GetVersion();
            }
            catch
            {
                return -1;
            }
        }
    }

    public static BotControllerAbiInfo AbiInfo
        => TryGetAbiInfo(out var info) ? info : BotControllerAbiInfo.Unavailable;

    public static ulong Capabilities
    {
        get
        {
            try
            {
                return BotController_GetCapabilities();
            }
            catch
            {
                return 0;
            }
        }
    }

    public static string BuildId
    {
        get
        {
            try
            {
                var buildId = Marshal.PtrToStringAnsi(BotController_GetBuildId());
                return string.IsNullOrWhiteSpace(buildId) ? "unknown" : buildId;
            }
            catch
            {
                return "unavailable";
            }
        }
    }

    public static bool IsCompatible => AbiVersion == ExpectedAbiVersion;

    public static bool HasRequiredCapabilities
        => (Capabilities & RequiredCapabilityMask) == RequiredCapabilityMask;

    public static ulong MissingRequiredCapabilities
        => RequiredCapabilityMask & ~Capabilities;

    public static bool WriteLeftHandDesired { get; set; } = true;

    public static bool HasUsercmdMovementIntentCapability
        => (Capabilities & CapabilityUsercmdMovementIntent) == CapabilityUsercmdMovementIntent;

    public static bool HasButtonOnlyMovementIntentCapability
        => (Capabilities & CapabilityButtonOnlyMovementIntent) == CapabilityButtonOnlyMovementIntent;

    public static bool HasVoiceSendCapability
        => (Capabilities & CapabilityVoiceSend) == CapabilityVoiceSend;

    public static bool HasNativePerceptionCapability
        => (Capabilities & CapabilityNativePerception) == CapabilityNativePerception;

    public static bool HasReleaseReplayBufferCapability
        => (Capabilities & CapabilityReleaseReplayBuffer) == CapabilityReleaseReplayBuffer;

    public static bool HasHandoffBestWeaponCapability
        => (Capabilities & CapabilityHandoffBestWeapon) == CapabilityHandoffBestWeapon;

    public static bool HasReplayPawnEquipmentCapability
        => (Capabilities & CapabilityReplayPawnEquipment) == CapabilityReplayPawnEquipment;

    public static bool TryGetNativePerceptionState(int slot, out NativePerceptionState state)
    {
        state = default;
        if (!ValidSlot(slot) || !HasNativePerceptionCapability)
            return false;
        try
        {
            return BotController_GetNativePerceptionState(
                       slot, out state, NativePerceptionState.ByteSize) == 0 &&
                   state.Valid != 0;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    public static bool SetReplayNativeFovOverride(bool enabled)
    {
        if (!HasNativePerceptionCapability)
            return false;
        try
        {
            return BotController_SetReplayNativeFovOverride(enabled ? 1 : 0) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool CanSendVoice
    {
        get
        {
            if (!HasVoiceSendCapability)
                return false;
            try
            {
                return BotController_CanSendVoice() == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    public static int VoiceStatus
    {
        get
        {
            if (!HasVoiceSendCapability)
                return -10;
            try
            {
                return BotController_GetVoiceStatus();
            }
            catch (EntryPointNotFoundException)
            {
                return CanSendVoice ? 0 : -9;
            }
            catch
            {
                return -8;
            }
        }
    }

    public static string VoiceStatusText
        => VoiceStatus switch
        {
            0 => "available",
            -1 => "no_engine",
            -2 => "no_network_messages",
            -3 => "no_voice_message",
            -8 => "status_error",
            -9 => CanSendVoice ? "available_legacy" : "missing_legacy",
            -10 => "missing_capability",
            _ => $"status_{VoiceStatus}",
        };

    public static bool HasUsercmdMovementIntentExports => ProbeUsercmdMovementIntentExports();

    public static bool HasLeftHandIntentAliasExports => ProbeLeftHandIntentAliasExports();

    public static bool HasLeftHandDesiredLatchExports => ProbeLeftHandDesiredLatchExports();

    public static bool HasProjectileBirthAlignExports => ProbeProjectileBirthAlignExports();

    public static string UsercmdMovementIntentStatus
    {
        get
        {
            var hasCapability = HasUsercmdMovementIntentCapability;
            var hasExport = HasUsercmdMovementIntentExports;
            if (hasCapability && hasExport)
                return "available";
            if (hasCapability)
                return "missing_export";
            if (hasExport)
                return "export_without_cap";
            return "missing";
        }
    }

    public static string RuntimeSummary
    {
        get
        {
            var abiInfo = AbiInfo;
            return $"expected_abi={ExpectedAbiVersion} runtime_abi={AbiVersion} abi_minor={abiInfo.AbiMinor} " +
                   $"compatible={IsCompatible} caps=0x{Capabilities:X} missing=0x{MissingRequiredCapabilities:X} " +
                   $"build={BuildId} usercmd_movement_intent={UsercmdMovementIntentStatus} " +
                   $"voice_send={VoiceStatusText} " +
                   $"left_hand_alias={HasLeftHandIntentAliasExports} left_hand_latch={HasLeftHandDesiredLatchExports} " +
                   $"release_replay_buffer={HasReleaseReplayBufferCapability} " +
                   $"replay_pawn_equipment={HasReplayPawnEquipmentCapability} " +
                   $"projectile_birth_align={HasProjectileBirthAlignExports} " +
                   $"dtr_reader={MinRecFormatVersion}..{RecFormatVersion} " +
                   $"platform={RuntimePlatformName} api={DemoTracerApiVersion}";
        }
    }

    public static int SetLeftHandDesiredLatch(int slot, bool enabled, bool leftHandDesired)
    {
        try
        {
            return BotController_SetLeftHandDesiredLatch(
                slot,
                enabled ? 1 : 0,
                leftHandDesired ? 1 : 0);
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static int ClearAllLeftHandDesiredLatches()
    {
        try
        {
            return BotController_ClearAllLeftHandDesiredLatches();
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static int SetProjectileBirthAlignOffsets(int initialPositionOffset, int initialVelocityOffset)
    {
        try
        {
            return BotController_SetProjectileBirthAlignOffsets(
                initialPositionOffset,
                initialVelocityOffset);
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static int QueueProjectileBirthAlign(ulong entityPtr, ReplayVector3 position, ReplayVector3 velocity)
    {
        if (entityPtr == 0)
            return -2;
        try
        {
            return BotController_QueueProjectileBirthAlign(
                entityPtr,
                position.X,
                position.Y,
                position.Z,
                velocity.X,
                velocity.Y,
                velocity.Z);
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static int ClearProjectileBirthAlign()
    {
        try
        {
            return BotController_ClearProjectileBirthAlign();
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static NativeProjectileBirthAlignStatus ProjectileBirthAlignStatus
    {
        get
        {
            try
            {
                return BotController_GetProjectileBirthAlignStatus(
                    out var status,
                    ProjectileBirthAlignStatusByteSize) == 0
                    ? status
                    : new NativeProjectileBirthAlignStatus { Size = ProjectileBirthAlignStatusByteSize };
            }
            catch
            {
                return new NativeProjectileBirthAlignStatus { Size = ProjectileBirthAlignStatusByteSize };
            }
        }
    }

    public static int SendVoiceFrame(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        byte[] audio,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes = -1,
        int sectionNumber = -1,
        int uncompressedSampleOffset = -1,
        uint numPackets = 0,
        uint[]? packetOffsets = null,
        int tick = -1,
        int audibleMask = 1)
    {
        if (!ValidSlot(recipientSlot) || senderClient < 0 || audio.Length == 0)
            return -2;
        packetOffsets ??= [];
        try
        {
            return BotController_SendVoiceFrame(
                recipientSlot,
                senderClient,
                senderXuid,
                audio,
                audio.Length,
                sampleRate,
                voiceLevel,
                sequenceBytes,
                sectionNumber,
                uncompressedSampleOffset,
                numPackets,
                packetOffsets,
                packetOffsets.Length,
                tick,
                audibleMask);
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    public static bool SetControllerControllingBotOffset(int offset)
    {
        try
        {
            return BotController_SetControllerControllingBotOffset(offset) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetReplayPovMask(ulong mask)
    {
        try
        {
            return BotController_SetReplayPovMask(mask) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetReplayPawn(int slot, nint pawnHandle)
    {
        if (!ValidSlot(slot))
            return false;

        try
        {
            return BotController_SetReplayPawn(slot, unchecked((ulong)pawnHandle)) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetReplayPawnEquipment(
        int slot,
        nint pawnHandle,
        nint controllerHandle,
        int armor,
        bool helmet,
        bool defuser)
    {
        if (!ValidSlot(slot) ||
            !HasReplayPawnEquipmentCapability ||
            pawnHandle == IntPtr.Zero ||
            controllerHandle == IntPtr.Zero ||
            armor is < 0 or > 100)
        {
            return false;
        }

        try
        {
            return BotController_SetReplayPawnEquipment(
                slot,
                unchecked((ulong)pawnHandle),
                unchecked((ulong)controllerHandle),
                armor,
                helmet ? 1 : 0,
                defuser ? 1 : 0) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearReplayPawnEquipment(int slot)
    {
        if (!ValidSlot(slot) || !HasReplayPawnEquipmentCapability)
            return false;
        try
        {
            return BotController_ClearReplayPawnEquipment(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetReplayPawnEquipmentState(
        int slot,
        out NativeReplayPawnEquipmentState state)
    {
        state = default;
        if (!ValidSlot(slot) || !HasReplayPawnEquipmentCapability)
            return false;
        try
        {
            return BotController_GetReplayPawnEquipmentState(
                slot, out state, ReplayPawnEquipmentStateByteSize) == 0;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    public static bool SetUsercmdMovementIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags = 0)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_SetUsercmdMovementIntent(
                slot, buttonsSet, buttonsClear, analogForward, analogLeft,
                durationMs, flags) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearUsercmdMovementIntent(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_ClearUsercmdMovementIntent(slot) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeUsercmdMovementIntentExports()
    {
        try
        {
            _ = BotController_ClearUsercmdMovementIntent(-1);
            _ = BotController_SetUsercmdMovementIntent(-1, 0, 0, 0.0f, 0.0f, 1, 0);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeLeftHandIntentAliasExports()
    {
        try
        {
            _ = BotController_ClearLeftHandIntent(-1);
            _ = BotController_SetLeftHandIntent(-1, 0, 0, 0.0f, 0.0f, 1, 0);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeLeftHandDesiredLatchExports()
    {
        try
        {
            _ = BotController_SetLeftHandDesiredLatch(-1, 0, 0);
            _ = BotController_ClearAllLeftHandDesiredLatches();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeProjectileBirthAlignExports()
    {
        try
        {
            return BotController_GetProjectileBirthAlignStatus(
                out _,
                ProjectileBirthAlignStatusByteSize) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool LoadReplayFromFile(int slot, string path)
        => LoadReplayFromFile(slot, path, out _);

    public static bool LoadReplayFromFile(int slot, string path, out ReplayFileMetadata metadata)
    {
        metadata = ReplayFileMetadata.Empty;
        if (!ValidSlot(slot))
        {
            LastLoadError = $"slot {slot} out of range 0..{MaxSlots - 1}";
            return false;
        }

        try
        {
            var replay = DtrReplayReader.Read(path);
            return LoadReplay(slot, replay, out metadata);
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    internal static bool LoadReplay(
        int slot,
        DtrReplayFile replay,
        out ReplayFileMetadata metadata)
    {
        metadata = ReplayFileMetadata.Empty;
        if (!ValidSlot(slot))
        {
            LastLoadError = $"slot {slot} out of range 0..{MaxSlots - 1}";
            return false;
        }

        try
        {
            EnsureNativeLayout();
            if (!WriteLeftHandDesired)
                StripLeftHandDesired(replay.CommandFrames);
            metadata = ReplayNativeMapper.BuildMetadata(replay);
            if (replay.Ticks.Length == 0)
            {
                LastLoadError = "replay has no ticks";
                return false;
            }

            var subticks = replay.Subticks.Length == 0
                ? [new NativeSubtickMove()]
                : replay.Subticks;
            if (replay.Version >= 7)
            {
                if (!IsCompatible)
                {
                    LastLoadError = $"v7+ replay requires BotController ABI {ExpectedAbiVersion}; {RuntimeSummary}";
                    return false;
                }
                if ((Capabilities & CapabilityExtendedReplay) == 0)
                {
                    LastLoadError = $"v7+ replay requires extended replay capability; {RuntimeSummary}";
                    return false;
                }
                if ((Capabilities & CapabilityReplayInputHistory) == 0)
                {
                    LastLoadError = $"v7+ replay requires input history capability; {RuntimeSummary}";
                    return false;
                }

                var commandFrames = replay.CommandFrames.Length == 0
                    ? [new NativeReplayCommandFrame()]
                    : replay.CommandFrames;
                var movementExtras = replay.MovementExtras.Length == 0
                    ? [new NativeReplayMovementExtra()]
                    : replay.MovementExtras;
                var inputHistoryTicks = replay.InputHistoryTicks.Length == 0
                    ? [new NativeReplayInputHistoryTick()]
                    : replay.InputHistoryTicks;
                var inputHistoryEntries = replay.InputHistoryEntries.Length == 0
                    ? [new NativeReplayInputHistoryEntry()]
                    : replay.InputHistoryEntries;
                var extendedOk = BotController_LoadReplayWithInputHistory(
                    slot,
                    replay.Ticks,
                    replay.Ticks.Length,
                    subticks,
                    replay.Subticks.Length,
                    commandFrames,
                    replay.CommandFrames.Length,
                    movementExtras,
                    replay.MovementExtras.Length,
                    inputHistoryTicks,
                    replay.InputHistoryTicks.Length,
                    inputHistoryEntries,
                    replay.InputHistoryEntries.Length) == 0;
                LastLoadError = extendedOk ? string.Empty : "BotController_LoadReplayWithInputHistory failed";
                return extendedOk;
            }

            var ok = BotController_LoadReplay(
                slot,
                replay.Ticks,
                replay.Ticks.Length,
                subticks,
                replay.Subticks.Length) == 0;
            LastLoadError = ok ? string.Empty : "BotController_LoadReplay failed";
            return ok;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    public static bool TryReadReplayMetadata(string path, out ReplayFileMetadata metadata)
    {
        try
        {
            var replay = DtrReplayReader.Read(path);
            metadata = ReplayNativeMapper.BuildMetadata(replay);
            return true;
        }
        catch
        {
            metadata = ReplayFileMetadata.Empty;
            return false;
        }
    }

    private static void StripLeftHandDesired(NativeReplayCommandFrame[] commandFrames)
    {
        for (var i = 0; i < commandFrames.Length; i++)
        {
            var frame = commandFrames[i];
            frame.Fields &= ~CommandFieldLeftHand;
            frame.LeftHandDesired = 0;
            commandFrames[i] = frame;
        }
    }

    public static bool UnloadReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;

        if (!HasReleaseReplayBufferCapability)
        {
            var stopped = StopReplay(slot);
            LastLoadError = stopped
                ? "native runtime can stop this replay but cannot release its buffer; update BotController"
                : "native runtime cannot release replay buffers, and stopping the replay failed";
            return stopped;
        }

        try
        {
            var released = BotController_ReleaseReplayBuffer(slot) == 0;
            BotController_Unlock(slot, LockKindAll);
            LastLoadError = released ? string.Empty : "BotController_ReleaseReplayBuffer failed";
            return released;
        }
        catch (EntryPointNotFoundException)
        {
            _ = StopReplay(slot);
            LastLoadError = "runtime advertises replay-buffer release without exporting it";
            return false;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    public static bool StartReplay(int slot, bool loop)
        => StartReplayAt(slot, loop, 0);

    public static bool StartReplayAt(int slot, bool loop, uint startIndex)
    {
        if (!ValidSlot(slot))
            return false;
        // Replay injection owns movement/view output. Keep the native bot
        // state machine and perception running underneath for warm handoff.
        UnlockReplayControl(slot);
        return startIndex == 0
            ? BotController_StartReplay(slot, loop ? 1 : 0) == 0
            : BotController_StartReplayAt(slot, loop ? 1 : 0, checked((int)startIndex)) == 0;
    }

    public static bool StartReplayUntil(
        int slot,
        bool loop,
        uint startIndex,
        uint holdBeforeIndex)
    {
        if (!ValidSlot(slot))
            return false;
        if (holdBeforeIndex <= startIndex)
            return false;
        UnlockReplayControl(slot);
        return BotController_StartReplayUntil(
            slot,
            loop ? 1 : 0,
            checked((int)startIndex),
            checked((int)holdBeforeIndex)) == 0;
    }

    public static bool StopReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        var ok = BotController_StopReplay(slot) == 0;
        BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static ReplayState GetReplayState(int slot)
    {
        if (!ValidSlot(slot))
            return ReplayState.Empty;

        try
        {
            if (BotController_GetReplaySlotState(slot, out var state) == 0)
            {
                return new ReplayState(
                    state.Cursor,
                    state.Total,
                    state.Playing != 0,
                    state.CurrentTickIndex,
                    state.WeaponDefIndex,
                    state.NumSubtick);
            }
        }
        catch
        {
        }

        var cursor = BotController_GetReplayCursor(slot);
        var total = BotController_GetReplayTotal(slot);
        return new ReplayState(cursor, total, cursor >= 0, -1, -1, 0);
    }

    public static bool TryGetReplayTick(int slot, out NativeReplayTick tick)
    {
        tick = default;
        return ValidSlot(slot) && BotController_GetReplayTick(slot, out tick) == 0;
    }

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => ValidSlot(slot) && BotController_SwitchBotWeapon(slot, defIndex) == 0;

    public static bool RequestEquipBestWeapon(int slot)
    {
        if (!ValidSlot(slot) || !HasHandoffBestWeaponCapability)
            return false;
        try
        {
            return BotController_RequestEquipBestWeapon(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int BotActiveWeaponDef(int slot)
        => ValidSlot(slot) ? BotController_GetBotActiveWeaponDef(slot) : -1;

    public static bool SetBuyPlan(int slot, string aliases)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_SetBuyPlan(slot, aliases ?? string.Empty) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetBuySkip(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_SetBuySkip(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearBuyPlan(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_ClearBuyPlan(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearAllBuyPlans()
    {
        try
        {
            return BotController_ClearAllBuyPlans() == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int BuyPlanItemCount(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        try
        {
            return BotController_GetBuyPlanItemCount(slot);
        }
        catch
        {
            return -1;
        }
    }

    public static bool LockWeaponSlot(int slot, int target)
        => ValidSlot(slot) && target is >= 1 and <= 5 && BotController_Lock(slot, LockKindWeapon, target) == 0;

    public static bool UnlockWeaponSlot(int slot)
        => ValidSlot(slot) && BotController_Unlock(slot, LockKindWeapon) == 0;

    public static bool LockReplayBrain(int slot)
        => ValidSlot(slot) && BotController_Lock(slot, LockKindAll, 0) == 0;

    public static bool UnlockReplayBrain(int slot)
        => ValidSlot(slot) && BotController_Unlock(slot, LockKindAll) == 0;

    public static void UnlockReplayControl(int slot)
    {
        if (!ValidSlot(slot))
            return;
        BotController_Unlock(slot, LockKindAll);
        BotController_Unlock(slot, LockKindAim);
    }

    private static bool ValidSlot(int slot)
        => slot is >= 0 and < MaxSlots;

    private static bool TryGetAbiInfo(out BotControllerAbiInfo info)
    {
        info = default;
        try
        {
            return BotController_GetAbiInfo(out info, BotControllerAbiInfo.ByteSize) == 0;
        }
        catch
        {
            info = BotControllerAbiInfo.Unavailable;
            return false;
        }
    }
}
