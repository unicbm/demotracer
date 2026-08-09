// P/Invoke wrapper for BotController.dll (ABI 18). Check IsCompatible() before use.
// Main-thread only.

using System;
using System.Runtime.InteropServices;

namespace BotControllerApi
{
    // Lock category, mirrors BotController::LockKind.
    //   All    - freezes both CCSBot::Update and CCSBot::Upkeep
    //   Aim    - freezes CCSBot::Upkeep only
    //   Weapon - locks the bot's weapon to a specific engine slot
    public enum LockKind
    {
        All = 0,
        Aim = 1,
        Weapon = 2,
    }

    // Engine weapon slots, mirrors BotController::LockTarget.
    public enum LockTarget
    {
        None = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    }

    /** One boundary of a movement tick. Captured pre (before mover) and post (after) */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MovementSnapshot
    {
        public float OriginX, OriginY, OriginZ;
        public float VelX, VelY, VelZ;
        public float Pitch, Yaw, Roll;
        public uint EntityFlags;
        public byte MoveType;
        public byte Pad0, Pad1, Pad2;
        public ulong Buttons;        // states[0] (pressed)
        public ulong Buttons1;       // states[1]
        public ulong Buttons2;       // states[2]
        public float DuckAmount;     // m_flDuckAmount (0=stand, 1=full crouch)
        public float DuckSpeed;      // m_flDuckSpeed
        public float LadderNormalX;  // m_vecLadderNormal
        public float LadderNormalY;
        public float LadderNormalZ;
        public byte Ducked;         // m_bDucked
        public byte Ducking;        // m_bDucking
        public byte DesiresDuck;    // m_bDesiresDuck
        public byte ActualMoveType; // m_nActualMoveType
    }

    /** One recorded server tick. Must match C++ ReplayTick byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayTick
    {
        public MovementSnapshot Pre;
        public MovementSnapshot Post;
        public int WeaponDefIndex;
        public uint NumSubtick;
    }

    /** One subtick input step. Must match C++ SubtickMove byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SubtickMove
    {
        public float When;
        public uint Button;
        public float Pressed;
        public float AnalogForward;
        public float AnalogLeft;
        public float PitchDelta;
        public float YawDelta;
    }

    /** Optional replay usercmd frame. Must match C++ ReplayCommandFrameData */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayCommandFrame
    {
        public float ForwardMove;
        public float LeftMove;
        public float UpMove;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public ulong Buttons;
        public ulong Buttons1;
        public ulong Buttons2;
        public int MouseDx;
        public int MouseDy;
        public int WeaponSelect;
        public uint Fields;
        public byte LeftHandDesired;
        public byte Pad0;
        public byte Pad1;
        public byte Pad2;
    }

    /** Optional offset-backed replay movement state. Must match C++ ReplayMovementExtra */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayMovementExtra
    {
        public uint Fields;
        public float JumpPressedTime;
        public float LastDuckTime;
        public int LastActualJumpPressTick;
        public float LastActualJumpPressFrac;
        public int LastUsableJumpPressTick;
        public float LastUsableJumpPressFrac;
        public int LastLandedTick;
        public float LastLandedFrac;
        public float LastLandedVelocityX;
        public float LastLandedVelocityY;
        public float LastLandedVelocityZ;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayInputHistoryTick
    {
        public int SourceClientTick;
        public int Attack1StartHistoryIndex;
        public int Attack2StartHistoryIndex;
        public uint NumEntries;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayInputHistoryEntry
    {
        public uint Fields;
        public float ViewPitch, ViewYaw, ViewRoll;
        public int RenderTickCount;
        public float RenderTickFraction;
        public int PlayerTickCount;
        public float PlayerTickFraction;
        public float ClInterpFraction;
        public int SvInterp0SrcTick, SvInterp0DstTick;
        public float SvInterp0Fraction;
        public int SvInterp1SrcTick, SvInterp1DstTick;
        public float SvInterp1Fraction;
        public int PlayerInterpSrcTick, PlayerInterpDstTick;
        public float PlayerInterpFraction;
        public int FrameNumber, TargetEntIndex;
        public float ShootPositionX, ShootPositionY, ShootPositionZ;
        public float TargetHeadPosCheckX, TargetHeadPosCheckY, TargetHeadPosCheckZ;
        public float TargetAbsPosCheckX, TargetAbsPosCheckY, TargetAbsPosCheckZ;
        public float TargetAbsAngCheckX, TargetAbsAngCheckY, TargetAbsAngCheckZ;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct AbiInfo
    {
        public const int ByteSize = 44;

        public int AbiMajor;
        public int AbiMinor;
        public int MovementSnapshotSize;
        public int ReplayTickSize;
        public int SubtickMoveSize;
        public int ReplaySlotStateSize;
        public int MaxSlots;
        public ulong Capabilities;
        public int Reserved0;
        public int Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativePerceptionState
    {
        public const int ByteSize = 44;

        public int Valid;
        public uint EnemyHandle;
        public int HasEnemy;
        public int EnemyVisible;
        public int VisibleEnemyParts;
        public int NearbyEnemyCount;
        public int LastEnemyDead;
        public float LastSawEnemyTimestamp;
        public float FirstSawEnemyTimestamp;
        public float CurrentEnemyAcquireTimestamp;
        public uint UpdateSerial;
    }

    // Thin static binding over the native exports. No orchestration here.
    public static class BotController
    {
        public const int ExpectedAbiVersion = 18;
        public const ulong CapabilityReplaySlotState = 1UL << 0;
        public const ulong CapabilityStartReplayAt = 1UL << 1;
        public const ulong CapabilityStartReplayUntil = 1UL << 2;
        public const ulong CapabilityReplayTick = 1UL << 3;
        public const ulong CapabilityWeaponSwitchRead = 1UL << 4;
        public const ulong CapabilityPovMask = 1UL << 5;
        public const ulong CapabilityBuyPlan = 1UL << 6;
        public const ulong CapabilityControllerBotOffset = 1UL << 7;
        public const ulong CapabilityExtendedReplay = 1UL << 8;
        public const ulong CapabilityUsercmdMovementIntent = 1UL << 9;
        public const ulong CapabilityNativePerception = 1UL << 11;
        public const ulong CapabilityReleaseReplayBuffer = 1UL << 12;
        public const ulong CapabilityButtonOnlyMovementIntent = 1UL << 13;
        public const ulong CapabilityHandoffBestWeapon = 1UL << 14;
        public const ulong CapabilityReplayInputHistory = 1UL << 15;
        public const int MovementIntentPreserveMoveAxes = 1 << 0;

        // Sentinel weapon def meaning "any knife"
        public const int KnifeDef = 9001;

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Lock(int slot, int kind, int arg);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Unlock(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_UnlockAll(int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_IsLocked(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetVersion();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetAbiInfo(out AbiInfo info, int size);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong BotController_GetCapabilities();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr BotController_GetBuildId();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetNativePerceptionState(
            int slot, out NativePerceptionState state, int size);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetReplayNativeFovOverride(int enabled);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_RequestEquipBestWeapon(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetReplayPovMask(ulong mask);

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
        private static extern int BotController_StartRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedTickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedSubtickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedTicks(
            int slot, [Out] ReplayTick[] ticks, int maxTicks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedSubticks(
            int slot, [Out] SubtickMove[] subs, int maxSubticks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_LoadReplay(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_LoadReplayExtended(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount,
            [In] ReplayCommandFrame[] commands, int commandCount,
            [In] ReplayMovementExtra[] movementExtras, int movementExtraCount);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_LoadReplayWithInputHistory(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount,
            [In] ReplayCommandFrame[] commands, int commandCount,
            [In] ReplayMovementExtra[] movementExtras, int movementExtraCount,
            [In] ReplayInputHistoryTick[] inputHistoryTicks, int inputHistoryTickCount,
            [In] ReplayInputHistoryEntry[] inputHistoryEntries, int inputHistoryEntryCount);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_TransferRecordingToReplay(int srcSlot, int dstSlot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartReplay(int slot, int loop);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartReplayAt(int slot, int loop, int startIndex);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartReplayUntil(
            int slot, int loop, int startIndex, int holdBeforeIndex);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopReplay(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ReleaseReplayBuffer(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayCursor(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTotal(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTick(int slot, out ReplayTick tick);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBotActiveWeaponDef(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuyPlan(int slot,
            [MarshalAs(UnmanagedType.LPStr)] string aliases);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuySkip(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearBuyPlan(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearAllBuyPlans();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBuyPlanItemCount(int slot);

        // Native ABI must match what this wrapper expects.
        public static bool IsCompatible() => BotController_GetVersion() == ExpectedAbiVersion;

        public static int AbiVersion() => BotController_GetVersion();

        public static bool TryGetAbiInfo(out AbiInfo info)
        {
            try
            {
                return BotController_GetAbiInfo(out info, AbiInfo.ByteSize) == 0;
            }
            catch
            {
                info = default;
                return false;
            }
        }

        public static ulong Capabilities()
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

        public static bool HasUsercmdMovementIntentCapability()
            => (Capabilities() & CapabilityUsercmdMovementIntent) == CapabilityUsercmdMovementIntent;

        public static bool HasButtonOnlyMovementIntentCapability()
            => (Capabilities() & CapabilityButtonOnlyMovementIntent) == CapabilityButtonOnlyMovementIntent;

        public static bool HasUsercmdMovementIntentExports()
        {
            try
            {
                _ = BotController_ClearUsercmdMovementIntent(-1);
                _ = BotController_SetUsercmdMovementIntent(-1, 0, 0, 0.0f, 0.0f, 1, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasLeftHandIntentAliasExports()
        {
            try
            {
                _ = BotController_ClearLeftHandIntent(-1);
                _ = BotController_SetLeftHandIntent(-1, 0, 0, 0.0f, 0.0f, 1, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string BuildId()
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

        // ---- locks ----

        // All / Aim
        public static bool Lock(int slot, LockKind kind)
            => BotController_Lock(slot, (int)kind, 0) == 0;

        // Weapon: arg is the engine slot to lock onto
        public static bool Lock(int slot, LockTarget target)
            => BotController_Lock(slot, (int)LockKind.Weapon, (int)target) == 0;

        public static bool Unlock(int slot, LockKind kind)
            => BotController_Unlock(slot, (int)kind) == 0;

        public static bool UnlockAll(LockKind kind)
            => BotController_UnlockAll((int)kind) == 0;

        // For All/Aim returns true if locked; for Weapon use GetWeaponLock.
        public static bool IsLocked(int slot, LockKind kind)
            => BotController_IsLocked(slot, (int)kind) != 0;

        // Weapon-only query: returns the locked weapon slot, or None.
        public static LockTarget GetWeaponLock(int slot)
            => (LockTarget)BotController_IsLocked(slot, (int)LockKind.Weapon);

        // ---- recording ----

        public static bool StartRecord(int slot) => BotController_StartRecord(slot) == 0;

        public static bool StopRecord(int slot) => BotController_StopRecord(slot) == 0;

        public static int RecordedTickCount(int slot) => BotController_GetRecordedTickCount(slot);

        // Pull a slot's recorded ticks + subticks out of native memory.
        public static (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
        {
            int nt = BotController_GetRecordedTickCount(slot);
            if (nt <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());

            var ticks = new ReplayTick[nt];
            int gotT = BotController_CopyRecordedTicks(slot, ticks, nt);
            if (gotT <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());
            if (gotT != nt) Array.Resize(ref ticks, gotT);

            int ns = BotController_GetRecordedSubtickCount(slot);
            SubtickMove[] subs;
            if (ns <= 0)
                subs = Array.Empty<SubtickMove>();
            else
            {
                subs = new SubtickMove[ns];
                int gotS = BotController_CopyRecordedSubticks(slot, subs, ns);
                if (gotS <= 0) subs = Array.Empty<SubtickMove>();
                else if (gotS != ns) Array.Resize(ref subs, gotS);
            }
            return (ticks, subs);
        }

        // ---- replay ----

        // Load ticks + subticks into a slot's replay buffer (native copies in).
        public static bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => ticks is { Length: > 0 }
               && BotController_LoadReplay(slot, ticks, ticks.Length,
                                       subs ?? Array.Empty<SubtickMove>(),
                                       subs?.Length ?? 0) == 0;

        public static bool LoadReplayExtended(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands,
            ReplayMovementExtra[] movementExtras)
            => ticks is { Length: > 0 }
               && BotController_LoadReplayExtended(
                   slot,
                   ticks,
                   ticks.Length,
                   subs ?? Array.Empty<SubtickMove>(),
                   subs?.Length ?? 0,
                   commands ?? Array.Empty<ReplayCommandFrame>(),
                   commands?.Length ?? 0,
                   movementExtras ?? Array.Empty<ReplayMovementExtra>(),
                   movementExtras?.Length ?? 0) == 0;

        public static bool LoadReplayWithInputHistory(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands,
            ReplayMovementExtra[] movementExtras,
            ReplayInputHistoryTick[] inputHistoryTicks,
            ReplayInputHistoryEntry[] inputHistoryEntries)
            => ticks is { Length: > 0 }
               && BotController_LoadReplayWithInputHistory(
                   slot, ticks, ticks.Length,
                   subs ?? Array.Empty<SubtickMove>(), subs?.Length ?? 0,
                   commands ?? Array.Empty<ReplayCommandFrame>(), commands?.Length ?? 0,
                   movementExtras ?? Array.Empty<ReplayMovementExtra>(), movementExtras?.Length ?? 0,
                   inputHistoryTicks ?? Array.Empty<ReplayInputHistoryTick>(), inputHistoryTicks?.Length ?? 0,
                   inputHistoryEntries ?? Array.Empty<ReplayInputHistoryEntry>(), inputHistoryEntries?.Length ?? 0) == 0;

        // Move a slot's just-recorded buffers straight into another slot's
        // replay buffer, no managed round-trip.
        public static bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => BotController_TransferRecordingToReplay(srcSlot, dstSlot) == 0;

        public static bool StartReplay(int slot, bool loop = false)
            => BotController_StartReplay(slot, loop ? 1 : 0) == 0;

        public static bool StartReplayAt(int slot, bool loop, int startIndex)
            => BotController_StartReplayAt(slot, loop ? 1 : 0, startIndex) == 0;

        public static bool StartReplayUntil(int slot, bool loop, int startIndex, int holdBeforeIndex)
            => BotController_StartReplayUntil(slot, loop ? 1 : 0, startIndex, holdBeforeIndex) == 0;

        public static bool StopReplay(int slot) => BotController_StopReplay(slot) == 0;

        // Unlike StopReplay, this also returns native replay vector capacity.
        // Capability-probe so the ABI-16 wrapper remains safe with older DLLs.
        public static bool ReleaseReplayBuffer(int slot)
        {
            if ((Capabilities() & CapabilityReleaseReplayBuffer) == 0)
                return false;
            try
            {
                return BotController_ReleaseReplayBuffer(slot) == 0;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static int ReplayCursor(int slot) => BotController_GetReplayCursor(slot);

        public static int ReplayTotal(int slot) => BotController_GetReplayTotal(slot);

        public static bool IsReplaying(int slot) => BotController_GetReplayCursor(slot) >= 0;

        public static bool TryGetNativePerceptionState(
            int slot, out NativePerceptionState state)
        {
            state = default;
            return (Capabilities() & CapabilityNativePerception) != 0 &&
                   BotController_GetNativePerceptionState(
                       slot, out state, NativePerceptionState.ByteSize) == 0 &&
                   state.Valid != 0;
        }

        public static bool SetReplayNativeFovOverride(bool enabled)
            => (Capabilities() & CapabilityNativePerception) != 0 &&
               BotController_SetReplayNativeFovOverride(enabled ? 1 : 0) == 0;

        public static bool RequestEquipBestWeapon(int slot)
            => (Capabilities() & CapabilityHandoffBestWeapon) != 0 &&
               BotController_RequestEquipBestWeapon(slot) == 0;

        // Bit n means replay slot n is currently watched in first-person.
        public static bool SetReplayPovMask(ulong mask)
            => BotController_SetReplayPovMask(mask) == 0;

        // Low-level usercmd/movedata movement lease. Policy and targeting live
        // in the caller; active DTR replay owns its replay slot.
        public static bool SetUsercmdMovementIntent(
            int slot,
            ulong buttonsSet,
            ulong buttonsClear,
            float analogForward,
            float analogLeft,
            int durationMs,
            int flags = 0)
            => BotController_SetUsercmdMovementIntent(
                   slot, buttonsSet, buttonsClear, analogForward, analogLeft,
                   durationMs, flags) == 0;

        public static bool ClearUsercmdMovementIntent(int slot)
            => BotController_ClearUsercmdMovementIntent(slot) == 0;

        // Compatibility aliases for existing left-hand movement callers.
        public static bool SetLeftHandIntent(
            int slot,
            ulong buttonsSet,
            ulong buttonsClear,
            float analogForward,
            float analogLeft,
            int durationMs,
            int flags = 0)
            => BotController_SetLeftHandIntent(
                   slot, buttonsSet, buttonsClear, analogForward, analogLeft,
                   durationMs, flags) == 0;

        public static bool ClearLeftHandIntent(int slot)
            => BotController_ClearLeftHandIntent(slot) == 0;

        // The tick currently being replayed on this slot, for driving weapon/fire
        // C#-side. Returns false if the slot isn't replaying.
        public static bool TryGetReplayTick(int slot, out ReplayTick tick)
            => BotController_GetReplayTick(slot, out tick) == 0;

        // Switch a bot to the weapon with this def index.
        public static bool SwitchBotWeapon(int slot, int defIndex)
            => BotController_SwitchBotWeapon(slot, defIndex) == 0;

        // Def index of the bot's current active weapon, same normalization as the
        // recorded WeaponDefIndex. <0 if unresolved.
        public static int BotActiveWeaponDef(int slot)
            => BotController_GetBotActiveWeaponDef(slot);

        // ---- buy plans ----

        // Force a bot's per-round buy.
        public static bool SetBuyPlan(int slot, string aliases)
            => BotController_SetBuyPlan(slot, aliases ?? "") == 0;

        // Force a bot to buy nothing each round.
        public static bool SetBuySkip(int slot)
            => BotController_SetBuySkip(slot) == 0;

        // Remove a bot's buy plan (back to vanilla AI buying).
        public static bool ClearBuyPlan(int slot)
            => BotController_ClearBuyPlan(slot) == 0;

        public static bool ClearAllBuyPlans()
            => BotController_ClearAllBuyPlans() == 0;

        // Plan item count: -1 none, 0 skip/empty, >0 alias count.
        public static int BuyPlanItemCount(int slot)
            => BotController_GetBuyPlanItemCount(slot);
    }
}
