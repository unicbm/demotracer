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

    public static int AbiVersion => AbiInfo.AbiMajor;

    public static BotControllerAbiInfo AbiInfo
        => TryGetAbiInfo(out var info) ? info : BotControllerAbiInfo.Unavailable;

    public static ulong Capabilities => AbiInfo.Capabilities;

    public static string BuildId
    {
        get
        {
            try
            {
                var buildId = Marshal.PtrToStringAnsi(DtrController_GetBuildId());
                return string.IsNullOrWhiteSpace(buildId) ? "unknown" : buildId;
            }
            catch
            {
                return "unavailable";
            }
        }
    }

    public static bool IsCompatible => AbiVersion == ExpectedAbiVersion;

    public static ulong MissingRequiredCapabilities
        => RequiredCapabilityMask & ~Capabilities;

    public static bool WriteLeftHandDesired { get; set; } = true;

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
            return DtrController_GetNativePerceptionState(
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
            return DtrController_SetReplayNativeFovOverride(enabled ? 1 : 0) == 0;
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
                return DtrController_CanSendVoice() == 1;
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
                return DtrController_GetVoiceStatus();
            }
            catch (EntryPointNotFoundException)
            {
                return -9;
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
            -9 => "missing_status_export",
            -10 => "missing_capability",
            _ => $"status_{VoiceStatus}",
        };

    public static string RuntimeSummary
    {
        get
        {
            var abiInfo = AbiInfo;
            return $"expected_abi={ExpectedAbiVersion} runtime_abi={AbiVersion} abi_minor={abiInfo.AbiMinor} " +
                   $"compatible={IsCompatible} caps=0x{Capabilities:X} missing=0x{MissingRequiredCapabilities:X} " +
                   $"build={BuildId} " +
                   $"voice_send={VoiceStatusText} " +
                   $"release_replay_buffer={HasReleaseReplayBufferCapability} " +
                   $"replay_pawn_equipment={HasReplayPawnEquipmentCapability} " +
                   $"dtr_reader={RecFormatVersion} " +
                   $"platform={RuntimePlatformName} api={DemoTracerApiVersion}";
        }
    }

    public static int SetLeftHandDesiredLatch(int slot, bool enabled, bool leftHandDesired)
    {
        try
        {
            return DtrController_SetLeftHandDesiredLatch(
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

    public static bool SetControllerControllingBotOffset(int offset)
    {
        try
        {
            return DtrController_SetControllerControllingBotOffset(offset) == 0;
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
            return DtrController_SetReplayPawn(slot, unchecked((ulong)pawnHandle)) == 0;
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
            return DtrController_SetReplayPawnEquipment(
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
            return DtrController_ClearReplayPawnEquipment(slot) == 0;
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
            return DtrController_GetReplayPawnEquipmentState(
                slot, out state, ReplayPawnEquipmentStateByteSize) == 0;
        }
        catch
        {
            state = default;
            return false;
        }
    }

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
            var replay = DtrReplayReader.ReadForPlayback(path);
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
            if (!IsCompatible || !HasNativePerceptionCapability ||
                (Capabilities & CapabilityExtendedReplay) == 0)
            {
                LastLoadError = $"replay requires the matched playback bundle; {RuntimeSummary}";
                return false;
            }

            if (DtrController_LoadReplayExtended(
                    slot, replay.Ticks, replay.Ticks.Length,
                    replay.Subticks, replay.Subticks.Length,
                    replay.CommandFrames, replay.CommandFrames.Length,
                    replay.MovementExtras, replay.MovementExtras.Length) != 0)
            {
                LastLoadError = "DtrController_LoadReplayExtended failed";
                return false;
            }
            if ((Capabilities & CapabilityReplaySourceState) == 0 ||
                 DtrController_LoadReplaySourceState(slot, replay.SourceState,
                     replay.SourceState.Length, replay.TickRate, CounterStrikeSharp.API.Server.TickInterval) != 0)
            {
                DtrController_ReleaseReplayBuffer(slot);
                LastLoadError = "BotController source state load failed";
                return false;
            }
            LastLoadError = string.Empty;
            return true;
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
            var replay = DtrReplayReader.ReadForPlayback(path);
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
        var released = DtrController_ReleaseReplayBuffer(slot) == 0;
        LastLoadError = released ? string.Empty : "DtrController_ReleaseReplayBuffer failed";
        return released;
    }

    public static bool StartReplayAt(int slot, bool loop, uint startIndex)
    {
        if (!ValidSlot(slot))
            return false;
        // Replay injection owns movement/view output. Keep the native bot
        // state machine and perception running underneath for warm handoff.
        UnlockReplayControl(slot);
        return DtrController_StartReplayAt(slot, loop ? 1 : 0, checked((int)startIndex)) == 0;
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
        return DtrController_StartReplayUntil(
            slot,
            loop ? 1 : 0,
            checked((int)startIndex),
            checked((int)holdBeforeIndex)) == 0;
    }

    public static bool StopReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        return DtrController_StopReplay(slot) == 0;
    }

    public static ReplayState GetReplayState(int slot)
        => ValidSlot(slot) && DtrController_GetReplaySlotState(slot, out var state) == 0
            ? new ReplayState(state.Cursor, state.Total, state.Playing != 0,
                state.CurrentTickIndex, state.WeaponDefIndex, state.NumSubtick)
            : ReplayState.Empty;

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => ValidSlot(slot) && DtrController_SwitchBotWeapon(slot, defIndex) == 0;

    public static bool RequestEquipBestWeapon(int slot)
    {
        if (!ValidSlot(slot) || !HasHandoffBestWeaponCapability)
            return false;
        try
        {
            return DtrController_RequestEquipBestWeapon(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int BotActiveWeaponDef(int slot)
        => ValidSlot(slot) ? DtrController_GetBotActiveWeaponDef(slot) : -1;

    public static bool SetBuySkip(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return DtrController_SetBuySkip(slot) == 0;
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
            return DtrController_ClearBuyPlan(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool LockWeaponSlot(int slot, int target)
        => ValidSlot(slot) && target is >= 1 and <= 5 && DtrController_Lock(slot, LockKindWeapon, target) == 0;

    public static bool UnlockWeaponSlot(int slot)
        => ValidSlot(slot) && DtrController_Unlock(slot, LockKindWeapon) == 0;

    public static void UnlockReplayControl(int slot)
    {
        if (!ValidSlot(slot))
            return;
        DtrController_Unlock(slot, LockKindAll);
        DtrController_Unlock(slot, LockKindAim);
    }

    private static bool ValidSlot(int slot)
        => slot is >= 0 and < MaxSlots;

    private static bool TryGetAbiInfo(out BotControllerAbiInfo info)
    {
        info = default;
        try
        {
            return DtrController_GetAbiInfo(out info, BotControllerAbiInfo.ByteSize) == 0;
        }
        catch
        {
            info = BotControllerAbiInfo.Unavailable;
            return false;
        }
    }
}
