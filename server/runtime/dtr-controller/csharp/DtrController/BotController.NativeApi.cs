// Upstream API over the matched DemoTracer ABI 21 runtime.
// Main-thread only.

using System.Runtime.InteropServices;

namespace DtrControllerApi
{
    // Thin static binding over the native exports. No orchestration here.
    public static class BotController
    {
        private const int ExpectedAbiVersion = 21;

        // Sentinel weapon def meaning "any knife"
        public const int KnifeDef = 9001;

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_Lock(int slot, int kind, int arg);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_Unlock(int slot, int kind);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_UnlockAll(int kind);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_IsLocked(int slot, int kind);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetVersion();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetPublicApiVersion();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        public static extern void DtrController_ClearUsercmdInjections(int slot);

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
            out ProjectileBirthAlignStatus status,
            int size);

        // Imports the native usercmd injection export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern long DtrController_InjectUsercmd(
            int slot, ulong buttonMask, int durationMs);

        // Imports the persistent native analog movement export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern long DtrController_StartUsercmdMovement(
            int slot, float forwardMove, float leftMove);

        // Imports the persistent native analog movement update export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_UpdateUsercmdMovement(
            int slot, long movementId, float forwardMove, float leftMove);

        // Imports the persistent native analog movement cancellation export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CancelUsercmdMovement(
            int slot, long movementId);

        // Imports the native usercmd injection cancellation export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CancelUsercmdInjection(
            int slot, long injectionId);

        // Imports the native usercmd suppression export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SuppressUsercmd(
            int slot, ulong buttonMask, int durationMs);

        // Imports the persistent native usercmd suppression export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern long DtrController_StartUsercmdSuppression(
            int slot, ulong buttonMask);

        // Imports the persistent native usercmd suppression cancellation export
        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CancelUsercmdSuppression(
            int slot, long suppressionId);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_StartRecord(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_StopRecord(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_ClearRecordedMotion(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetRecordedTickCount(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetRecordedSubtickCount(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetRecordedCommandCount(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CopyRecordedTicks(
            int slot, [Out] ReplayTick[] ticks, int maxTicks);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CopyRecordedSubticks(
            int slot, [Out] SubtickMove[] subs, int maxSubticks);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CopyRecordedCommands(
            int slot, [Out] ReplayCommandFrame[] commands, int maxCommands);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_LoadReplayExtended(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount,
            [In] ReplayCommandFrame[] commands, int commandCount,
            [In] ReplayMovementExtra[] movementExtras, int movementExtraCount);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_TransferRecordingToReplay(int srcSlot, int dstSlot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_StartReplay(int slot, int loop);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SetReplayPawn(int slot, ulong pawnPtr);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_StopReplay(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_ReleaseReplayBuffer(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetReplayCursor(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetReplayTotal(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetReplayTick(int slot, out ReplayTick tick);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SwitchBotWeapon(int slot, int defIndex);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetBotActiveWeaponDef(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetProfile(int slot, out BotProfileData profile);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SetBuyPlan(int slot,
            [MarshalAs(UnmanagedType.LPStr)] string aliases);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SetBuySkip(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_ClearBuyPlan(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetBuyStatus();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_ClearAllBuyPlans();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetBuyPlanItemCount(int slot);

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_CanSendVoice();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetVoiceStatus();

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_SendVoiceFrame(
            int recipientSlot,
            int senderClient,
            ulong senderXuid,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] byte[] audio,
            int audioBytes,
            int sampleRate,
            float voiceLevel,
            int sequenceBytes,
            int sectionNumber,
            int uncompressedSampleOffset,
            uint numPackets,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 12)] uint[] packetOffsets,
            int packetOffsetCount,
            int tick,
            int audibleMask);

        // Native ABI must match what this wrapper expects.
        public static bool IsCompatible()
        {
            try { return DtrController_GetVersion() == ExpectedAbiVersion && DtrController_GetPublicApiVersion() == 20; }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }

        // Native C-ABI version the loaded DLL reports.
        public static int AbiVersion => DtrController_GetVersion();

        // Configures native projectile birth offsets for the loaded server build
        public static bool ConfigureProjectileBirthAlign(int initialPositionOffset, int initialVelocityOffset)
            => DtrController_SetProjectileBirthAlignOffsets(initialPositionOffset, initialVelocityOffset) == 0;

        // Queues one projectile's recorded birth position and velocity
        public static bool QueueProjectileBirthAlign(
            nint entityPtr,
            ReplayVector3 position,
            ReplayVector3 velocity)
            => entityPtr != 0 &&
               DtrController_QueueProjectileBirthAlign(
                   unchecked((ulong)entityPtr),
                   position.X,
                   position.Y,
                   position.Z,
                   velocity.X,
                   velocity.Y,
                   velocity.Z) == 0;

        // Clears pending native projectile birth writes
        public static int ClearProjectileBirthAlign()
            => DtrController_ClearProjectileBirthAlign();

        // Returns native projectile birth alignment diagnostics
        public static bool TryGetProjectileBirthAlignStatus(out ProjectileBirthAlignStatus status)
            => DtrController_GetProjectileBirthAlignStatus(
                   out status,
                   Marshal.SizeOf<ProjectileBirthAlignStatus>()) == 0;

        // Creates an independently cancellable native usercmd injection
        public static long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0)
            => DtrController_InjectUsercmd(slot, buttonMask, durationMs);

        // Cancels one native usercmd injection by its token
        public static bool CancelUsercmdInjection(int slot, long injectionId)
            => DtrController_CancelUsercmdInjection(slot, injectionId) == 0;

        // Creates an independently cancellable persistent analog movement override
        public static long StartUsercmdMovement(
            int slot,
            float forwardMove,
            float leftMove)
            => DtrController_StartUsercmdMovement(slot, forwardMove, leftMove);

        // Updates one persistent analog movement override
        public static bool UpdateUsercmdMovement(
            int slot,
            long movementId,
            float forwardMove,
            float leftMove)
            => DtrController_UpdateUsercmdMovement(
                slot, movementId, forwardMove, leftMove) == 0;

        // Cancels one persistent analog movement override
        public static bool CancelUsercmdMovement(int slot, long movementId)
            => DtrController_CancelUsercmdMovement(slot, movementId) == 0;

        // Suppresses selected usercmd buttons for a fixed duration
        public static bool SuppressUsercmd(int slot, ulong buttonMask, int durationMs)
            => DtrController_SuppressUsercmd(slot, buttonMask, durationMs) == 0;

        // Creates an independently cancellable persistent native usercmd suppression
        public static long StartUsercmdSuppression(int slot, ulong buttonMask)
            => DtrController_StartUsercmdSuppression(slot, buttonMask);

        // Cancels one persistent native usercmd suppression by its token
        public static bool CancelUsercmdSuppression(int slot, long suppressionId)
            => DtrController_CancelUsercmdSuppression(slot, suppressionId) == 0;

        // ---- locks ----

        // All / Aim
        public static bool Lock(int slot, LockKind kind)
            => DtrController_Lock(slot, (int)kind, 0) == 0;

        // Weapon: arg is the engine slot to lock onto
        public static bool Lock(int slot, LockTarget target)
            => DtrController_Lock(slot, (int)LockKind.Weapon, (int)target) == 0;

        public static bool Unlock(int slot, LockKind kind)
            => DtrController_Unlock(slot, (int)kind) == 0;

        public static bool UnlockAll(LockKind kind)
            => DtrController_UnlockAll((int)kind) == 0;

        // For All/Aim returns true if locked; for Weapon use GetWeaponLock.
        public static bool IsLocked(int slot, LockKind kind)
            => DtrController_IsLocked(slot, (int)kind) != 0;

        // Weapon-only query: returns the locked weapon slot, or None.
        public static LockTarget GetWeaponLock(int slot)
            => (LockTarget)DtrController_IsLocked(slot, (int)LockKind.Weapon);

        // ---- recording ----

        public static bool StartRecord(int slot) => DtrController_StartRecord(slot) == 0;

        public static bool StopRecord(int slot) => DtrController_StopRecord(slot) == 0;

        internal static bool ClearRecordedMotion(int slot) => DtrController_ClearRecordedMotion(slot) == 0;

        public static int RecordedTickCount(int slot) => DtrController_GetRecordedTickCount(slot);

        // Pull a slot's recorded ticks + subticks out of native memory.
        public static (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
        {
            var (ticks, subs, _) = GetRecordedMotionExtended(slot);
            return (ticks, subs);
        }

        // Pull aligned tick, subtick, and command-frame buffers from native memory
        public static (ReplayTick[] ticks, SubtickMove[] subs, ReplayCommandFrame[] commands)
            GetRecordedMotionExtended(int slot)
        {
            int nt = DtrController_GetRecordedTickCount(slot);
            if (nt <= 0)
                return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>(), Array.Empty<ReplayCommandFrame>());

            var ticks = new ReplayTick[nt];
            int gotT = DtrController_CopyRecordedTicks(slot, ticks, nt);
            if (gotT <= 0)
                return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>(), Array.Empty<ReplayCommandFrame>());
            if (gotT != nt) Array.Resize(ref ticks, gotT);

            int ns = DtrController_GetRecordedSubtickCount(slot);
            SubtickMove[] subs;
            if (ns <= 0)
                subs = Array.Empty<SubtickMove>();
            else
            {
                subs = new SubtickMove[ns];
                int gotS = DtrController_CopyRecordedSubticks(slot, subs, ns);
                if (gotS <= 0) subs = Array.Empty<SubtickMove>();
                else if (gotS != ns) Array.Resize(ref subs, gotS);
            }

            int nc = DtrController_GetRecordedCommandCount(slot);
            ReplayCommandFrame[] commands;
            if (nc <= 0)
                commands = Array.Empty<ReplayCommandFrame>();
            else
            {
                commands = new ReplayCommandFrame[nc];
                int gotC = DtrController_CopyRecordedCommands(slot, commands, nc);
                if (gotC <= 0) commands = Array.Empty<ReplayCommandFrame>();
                else if (gotC != nc) Array.Resize(ref commands, gotC);
            }
            return (ticks, subs, commands);
        }

        // ---- replay ----

        // Load ticks + subticks into a slot's replay buffer (native copies in).
        public static bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => LoadReplayExtended(
                slot, ticks, subs,
                Array.Empty<ReplayCommandFrame>(),
                Array.Empty<ReplayMovementExtra>());

        // Load replay buffers with optional per-tick command and movement data
        public static bool LoadReplayExtended(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands,
            ReplayMovementExtra[] movementExtras)
            => ticks is { Length: > 0 }
               && DtrController_LoadReplayExtended(
                   slot,
                   ticks,
                   ticks.Length,
                   subs ?? Array.Empty<SubtickMove>(),
                   subs?.Length ?? 0,
                   commands ?? Array.Empty<ReplayCommandFrame>(),
                   commands?.Length ?? 0,
                   movementExtras ?? Array.Empty<ReplayMovementExtra>(),
                   movementExtras?.Length ?? 0) == 0;

        // Move a slot's just-recorded buffers straight into another slot's
        // replay buffer, no managed round-trip.
        public static bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => DtrController_TransferRecordingToReplay(srcSlot, dstSlot) == 0;

        public static bool StartReplay(int slot, bool loop = false)
            => DtrController_StartReplay(slot, loop ? 1 : 0) == 0;

        // Registers the current native pawn before replay starts.
        public static bool SetReplayPawn(int slot, nint pawn)
            => pawn != 0 &&
               DtrController_SetReplayPawn(slot, unchecked((ulong)pawn)) == 0;

        public static bool StopReplay(int slot) => DtrController_StopReplay(slot) == 0;

        internal static bool ReleaseReplayBuffer(int slot) => DtrController_ReleaseReplayBuffer(slot) == 0;

        public static int ReplayCursor(int slot) => DtrController_GetReplayCursor(slot);

        public static int ReplayTotal(int slot) => DtrController_GetReplayTotal(slot);

        public static bool IsReplaying(int slot) => DtrController_GetReplayCursor(slot) >= 0;

        // The tick currently being replayed on this slot, for driving weapon/fire
        // C#-side. Returns false if the slot isn't replaying.
        public static bool TryGetReplayTick(int slot, out ReplayTick tick)
            => DtrController_GetReplayTick(slot, out tick) == 0;

        // Switch a bot to the weapon with this def index.
        public static bool SwitchBotWeapon(int slot, int defIndex)
            => DtrController_SwitchBotWeapon(slot, defIndex) == 0;

        // Def index of the bot's current active weapon, same normalization as the
        // recorded WeaponDefIndex. <0 if unresolved.
        public static int BotActiveWeaponDef(int slot)
            => DtrController_GetBotActiveWeaponDef(slot);

        // ---- profile ----

        // Read the BotProfile of the bot on this slot. Returns false if the slot
        // has no live bot (it must have ticked at least once) or null profile.
        public static bool GetBotProfile(int slot, out BotProfileData profile)
            => DtrController_GetProfile(slot, out profile) == 0;

        // ---- buy plans ----

        // Force a bot's per-round buy.
        // 0 ready; negative when the native buy hook is unavailable.
        public static int GetBuyStatus() => DtrController_GetBuyStatus();

        public static bool SetBuyPlan(int slot, string aliases)
            => DtrController_SetBuyPlan(slot, aliases ?? "") == 0;

        // Force a bot to buy nothing each round.
        public static bool SetBuySkip(int slot)
            => DtrController_SetBuySkip(slot) == 0;

        // Remove a bot's buy plan (back to vanilla AI buying).
        public static bool ClearBuyPlan(int slot)
            => DtrController_ClearBuyPlan(slot) == 0;

        public static bool ClearAllBuyPlans()
            => DtrController_ClearAllBuyPlans() == 0;

        // Plan item count: -1 none, 0 skip/empty, >0 alias count.
        public static int BuyPlanItemCount(int slot)
            => DtrController_GetBuyPlanItemCount(slot);

        // ---- voice ----

        // Returns true when the native plugin can send voice net messages.
        public static bool CanSendVoice() => DtrController_CanSendVoice() != 0;

        // Returns 0 when voice sending is ready, otherwise a negative setup code.
        public static int GetVoiceStatus() => DtrController_GetVoiceStatus();

        // Sends one encoded Opus voice frame to a recipient player slot.
        public static int SendVoiceFrame(
            int recipientSlot,
            int senderClient,
            ulong senderXuid,
            byte[] audio,
            int audioBytes,
            int sampleRate,
            float voiceLevel,
            int sequenceBytes,
            int sectionNumber,
            int uncompressedSampleOffset,
            uint numPackets,
            uint[] packetOffsets,
            int packetOffsetCount,
            int tick,
            int audibleMask)
        {
            audio ??= Array.Empty<byte>();
            packetOffsets ??= Array.Empty<uint>();
            if (audioBytes < 0 || audioBytes > audio.Length ||
                packetOffsetCount < 0 || packetOffsetCount > packetOffsets.Length)
                return -2;

            return DtrController_SendVoiceFrame(
                recipientSlot,
                senderClient,
                senderXuid,
                audio,
                audioBytes,
                sampleRate,
                voiceLevel,
                sequenceBytes,
                sectionNumber,
                uncompressedSampleOffset,
                numPackets,
                packetOffsets,
                packetOffsetCount,
                tick,
                audibleMask);
        }
    }
}
