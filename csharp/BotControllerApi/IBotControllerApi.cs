// Cross-plugin BotController API.

namespace BotControllerApi
{
    public interface IBotControllerApi
    {
        int AbiVersion { get; }

        // ---- locks ----

        // All / Aim
        bool Lock(int slot, LockKind kind);

        // Weapon: lock the bot onto a specific engine weapon slot.
        bool Lock(int slot, LockTarget target);

        bool Unlock(int slot, LockKind kind);

        bool UnlockAll(LockKind kind);

        // For All/Aim returns true if locked; for Weapon use GetWeaponLock.
        bool IsLocked(int slot, LockKind kind);

        // Weapon-only query: the locked weapon slot, or None.
        LockTarget GetWeaponLock(int slot);

        // ---- recording ----

        // Captures movement/commands; native weapon-drop events are unsupported.
        bool StartRecord(int slot);

        bool StopRecord(int slot);

        int RecordedTickCount(int slot);

        // Pull a slot's recorded ticks + subticks out of memory.
        (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot);

        // Pull aligned tick, subtick, and command-frame buffers out of memory
        (ReplayTick[] ticks, SubtickMove[] subs, ReplayCommandFrame[] commands)
            GetRecordedMotionExtended(int slot);

        // ---- replay ----

        // Load ticks + subticks into a slot's replay buffer.
        // Returns false for any nonzero field in the reserved event tail.
        bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs);

        // Load ticks, subticks, and aligned command frames into a replay buffer
        // with the same reserved-zero event-tail requirement as LoadReplay.
        bool LoadReplayExtended(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands);

        // Move a slot's just-recorded buffers into another slot's replay buffer.
        bool TransferRecordingToReplay(int srcSlot, int dstSlot);

        // Registers the current native pawn pointer before replay starts.
        bool SetReplayPawn(int slot, nint pawn);

        bool StartReplay(int slot, bool loop = false);

        bool StopReplay(int slot);

        int ReplayCursor(int slot);

        int ReplayTotal(int slot);

        bool IsReplaying(int slot);

        // The tick currently being replayed on this slot, for driving weapon/fire.
        bool TryGetReplayTick(int slot, out ReplayTick tick);

        // ---- weapons ----

        // Switch a bot to the weapon with this def index.
        bool SwitchBotWeapon(int slot, int defIndex);

        // Def index of the bot's current active weapon. <0 if unresolved.
        int BotActiveWeaponDef(int slot);

        // Creates an independently cancellable usercmd injection and returns its token
        long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0);

        // Cancels one usercmd injection by its token
        bool CancelUsercmdInjection(int slot, long injectionId);

        // Creates an independently cancellable persistent analog movement override
        long StartUsercmdMovement(int slot, float forwardMove, float leftMove);

        // Updates one persistent analog movement override
        bool UpdateUsercmdMovement(
            int slot,
            long movementId,
            float forwardMove,
            float leftMove);

        // Cancels one persistent analog movement override
        bool CancelUsercmdMovement(int slot, long movementId);

        // Suppresses selected usercmd buttons for a fixed duration
        bool SuppressUsercmd(int slot, ulong buttonMask, int durationMs);

        // Creates an independently cancellable persistent usercmd suppression
        long StartUsercmdSuppression(int slot, ulong buttonMask);

        // Cancels one persistent usercmd suppression by its token
        bool CancelUsercmdSuppression(int slot, long suppressionId);

        // ---- profile ----

        // Read the BotProfile of the bot on this slot. False if the slot has no
        // live bot or a null profile.
        bool GetBotProfile(int slot, out BotProfileData profile);

        // ---- buy plans ----

        // Force a bot's per-round buy.
        bool SetBuyPlan(int slot, string aliases);

        // Force a bot to buy nothing each round.
        bool SetBuySkip(int slot);

        // Remove a bot's buy plan (back to vanilla AI buying).
        bool ClearBuyPlan(int slot);

        bool ClearAllBuyPlans();

        // Plan item count: -1 none, 0 skip/empty, >0 alias count.
        int BuyPlanItemCount(int slot);

        // ---- voice ----

        // Returns true when the native plugin can send voice net messages.
        bool CanSendVoice();

        // Returns 0 when voice sending is ready, otherwise a negative setup code.
        int GetVoiceStatus();

        // Sends one encoded Opus voice frame to a recipient player slot.
        int SendVoiceFrame(
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
            int audibleMask);
    }
}
