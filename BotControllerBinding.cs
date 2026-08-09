/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_Lock(int slot, int kind, int arg);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_Unlock(int slot, int kind);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetVersion();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetAbiInfo(out BotControllerAbiInfo info, int size);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong BotController_GetCapabilities();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr BotController_GetBuildId();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetNativePerceptionState(
        int slot,
        out NativePerceptionState state,
        int size);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetReplayNativeFovOverride(int enabled);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_RequestEquipBestWeapon(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_CanSendVoice();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetVoiceStatus();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetProjectileBirthAlignOffsets(
        int initialPositionOffset,
        int initialVelocityOffset);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_QueueProjectileBirthAlign(
        ulong entityPtr,
        float posX,
        float posY,
        float posZ,
        float velX,
        float velY,
        float velZ);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearProjectileBirthAlign();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetProjectileBirthAlignStatus(
        out NativeProjectileBirthAlignStatus status,
        int size);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SendVoiceFrame(
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

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetControllerControllingBotOffset(int offset);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetReplayPovMask(ulong mask);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetReplayPawn(int slot, ulong pawnPtr);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetUsercmdMovementIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearUsercmdMovementIntent(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetLeftHandIntent(
        int slot,
        ulong buttonsSet,
        ulong buttonsClear,
        float analogForward,
        float analogLeft,
        int durationMs,
        int flags);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearLeftHandIntent(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetLeftHandDesiredLatch(
        int slot,
        int enabled,
        int leftHandDesired);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearAllLeftHandDesiredLatches();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_LoadReplay(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_LoadReplayExtended(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount,
        [In] NativeReplayCommandFrame[] commandFrames,
        int commandFrameCount,
        [In] NativeReplayMovementExtra[] movementExtras,
        int movementExtraCount);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_LoadReplayWithInputHistory(
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

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplay(int slot, int loop);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplayAt(int slot, int loop, int startIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplayUntil(
        int slot,
        int loop,
        int startIndex,
        int holdBeforeIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StopReplay(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ReleaseReplayBuffer(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayCursor(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTotal(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplaySlotState(
        int slot,
        out NativeReplaySlotState state);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetBotActiveWeaponDef(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetBuyPlan(
        int slot,
        [MarshalAs(UnmanagedType.LPStr)] string aliases);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetBuySkip(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearBuyPlan(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearAllBuyPlans();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetBuyPlanItemCount(int slot);
}
