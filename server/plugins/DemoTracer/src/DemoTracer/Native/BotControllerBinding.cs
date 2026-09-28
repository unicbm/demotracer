/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplaySourceState(int slot, [In] NativeReplaySourceStateChange[] changes, int count, float tickRate, float liveTickInterval);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_Lock(int slot, int kind, int arg);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_Unlock(int slot, int kind);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetVersion();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetAbiInfo(out BotControllerAbiInfo info, int size);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DtrController_GetCapabilities();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr DtrController_GetBuildId();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetNativePerceptionState(
        int slot,
        out NativePerceptionState state,
        int size);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayNativeFovOverride(int enabled);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_RequestEquipBestWeapon(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_CanSendVoice();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetVoiceStatus();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetProjectileBirthAlignOffsets(
        int initialPositionOffset,
        int initialVelocityOffset);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_QueueProjectileBirthAlign(
        ulong entityPtr,
        float posX,
        float posY,
        float posZ,
        float velX,
        float velY,
        float velZ);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearProjectileBirthAlign();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetProjectileBirthAlignStatus(
        out NativeProjectileBirthAlignStatus status,
        int size);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SendVoiceFrame(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        [In] byte[] audio,
        int audioBytes,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        [In] uint[] packetOffsets,
        int packetOffsetCount,
        int tick,
        int audibleMask);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetControllerControllingBotOffset(int offset);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayPawn(int slot, ulong pawnPtr);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayPawnEquipment(
        int slot,
        ulong pawnPtr,
        ulong controllerPtr,
        int armor,
        int helmet,
        int defuser);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearReplayPawnEquipment(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayPawnEquipmentState(
        int slot,
        out NativeReplayPawnEquipmentState state,
        int size);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetUsercmdMovementIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearUsercmdMovementIntent(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetLeftHandIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearLeftHandIntent(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetLeftHandDesiredLatch(
        int slot,
        int enabled,
        int leftHandDesired);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplay(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplayExtended(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount,
        [In] NativeReplayCommandFrame[] commandFrames,
        int commandFrameCount,
        [In] NativeReplayMovementExtra[] movementExtras,
        int movementExtraCount);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplayWithInputHistory(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount,
        [In] NativeReplayCommandFrame[] commandFrames,
        int commandFrameCount,
        [In] NativeReplayMovementExtra[] movementExtras,
        int movementExtraCount,
        [In] NativeReplayInputHistoryTick[] inputHistoryTicks,
        int inputHistoryTickCount,
        [In] NativeReplayInputHistoryEntry[] inputHistoryEntries,
        int inputHistoryEntryCount);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplay(int slot, int loop);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplayAt(int slot, int loop, int startIndex);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplayUntil(
        int slot,
        int loop,
        int startIndex,
        int holdBeforeIndex);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StopReplay(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ReleaseReplayBuffer(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayCursor(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayTotal(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplaySlotState(
        int slot,
        out NativeReplaySlotState state);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetBotActiveWeaponDef(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetBuyPlan(
        int slot,
        [MarshalAs(UnmanagedType.LPStr)] string aliases);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetBuySkip(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearBuyPlan(int slot);

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearAllBuyPlans();

    [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetBuyPlanItemCount(int slot);
}
