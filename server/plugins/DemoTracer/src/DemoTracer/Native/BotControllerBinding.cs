/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplaySourceState(int slot, [In] NativeReplaySourceStateChange[] changes, int count, float tickRate, float liveTickInterval);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_Lock(int slot, int kind, int arg);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_Unlock(int slot, int kind);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetVersion();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetAbiInfo(out BotControllerAbiInfo info, int size);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DtrController_GetCapabilities();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr DtrController_GetBuildId();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetNativePerceptionState(
        int slot,
        out NativePerceptionState state,
        int size);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayNativeFovOverride(int enabled);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_RequestEquipBestWeapon(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_CanSendVoice();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetVoiceStatus();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
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

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetControllerControllingBotOffset(int offset);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayPawn(int slot, ulong pawnPtr);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetReplayPawnEquipment(
        int slot,
        ulong pawnPtr,
        ulong controllerPtr,
        int armor,
        int helmet,
        int defuser);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearReplayPawnEquipment(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayPawnEquipmentState(
        int slot,
        out NativeReplayPawnEquipmentState state,
        int size);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetUsercmdMovementIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearUsercmdMovementIntent(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetLeftHandIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearLeftHandIntent(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetLeftHandDesiredLatch(
        int slot,
        int enabled,
        int leftHandDesired);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_LoadReplay(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
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

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
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

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplay(int slot, int loop);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplayAt(int slot, int loop, int startIndex);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StartReplayUntil(
        int slot,
        int loop,
        int startIndex,
        int holdBeforeIndex);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_StopReplay(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ReleaseReplayBuffer(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayCursor(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayTotal(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplaySlotState(
        int slot,
        out NativeReplaySlotState state);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetBotActiveWeaponDef(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetBuyPlan(
        int slot,
        [MarshalAs(UnmanagedType.LPStr)] string aliases);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_SetBuySkip(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearBuyPlan(int slot);

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_ClearAllBuyPlans();

    [DllImport("dot-controller", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DtrController_GetBuyPlanItemCount(int slot);
}
