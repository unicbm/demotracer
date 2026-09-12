// Provider-side implementation of IBotControllerApi.

using CounterStrikeSharp.API;

namespace BotControllerApi
{
    public sealed class BotControllerApiImpl : IBotControllerApi
    {
        private readonly Func<int, bool> _isReplayOwned;
        private readonly OwnedSlotResources _owned = new();
        public BotControllerApiImpl(Func<int, bool> isReplayOwned) => _isReplayOwned = isReplayOwned;
        private bool CanControl(int slot)
            => slot is >= 0 and < 64 && !_isReplayOwned(slot) &&
               Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: true, IsHLTV: false, ControllingBot: false };
        private bool CanUseReplayBuffer(int slot)
            => CanControl(slot) && (_owned.Has(slot, SlotResource.Replay) || BotController.ReplayTotal(slot) == 0);
        public int AbiVersion => BotController.AbiVersion;

        // ---- locks ----
        private static SlotResource LockResource(LockKind kind) => kind switch
        {
            LockKind.All => SlotResource.AllLock,
            LockKind.Aim => SlotResource.AimLock,
            _ => SlotResource.WeaponLock,
        };
        public bool Lock(int slot, LockKind kind) => _owned.Track(slot, LockResource(kind), CanControl(slot) && BotController.Lock(slot, kind));
        public bool Lock(int slot, LockTarget target) => _owned.Track(slot, SlotResource.WeaponLock, CanControl(slot) && BotController.Lock(slot, target));
        public bool Unlock(int slot, LockKind kind)
        {
            if (!CanControl(slot) || !BotController.Unlock(slot, kind)) return false;
            _owned.Forget(slot, LockResource(kind));
            return true;
        }
        public bool UnlockAll(LockKind kind)
        {
            foreach (var slot in _owned.Slots)
                if (_owned.Has(slot, LockResource(kind))) Unlock(slot, kind);
            return true;
        }
        public bool IsLocked(int slot, LockKind kind) => BotController.IsLocked(slot, kind);
        public LockTarget GetWeaponLock(int slot) => BotController.GetWeaponLock(slot);

        // ---- recording ----
        public bool StartRecord(int slot) => _owned.Track(slot, SlotResource.Recording, BotController.StartRecord(slot));
        public bool StopRecord(int slot) => BotController.StopRecord(slot);
        public int RecordedTickCount(int slot) => BotController.RecordedTickCount(slot);
        public (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
            => BotController.GetRecordedMotion(slot);
        // Returns aligned tick, subtick, and command-frame buffers
        public (ReplayTick[] ticks, SubtickMove[] subs, ReplayCommandFrame[] commands)
            GetRecordedMotionExtended(int slot)
            => BotController.GetRecordedMotionExtended(slot);

        // ---- replay ----
        public bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => _owned.Track(slot, SlotResource.Replay, CanUseReplayBuffer(slot) && BotController.LoadReplay(slot, ticks, subs));
        // Loads aligned command frames without movement-extra data
        public bool LoadReplayExtended(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands)
            => _owned.Track(slot, SlotResource.Replay, CanUseReplayBuffer(slot) && BotController.LoadReplayExtended(
                slot, ticks, subs, commands, Array.Empty<ReplayMovementExtra>()));
        public bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => _owned.Track(dstSlot, SlotResource.Replay, CanUseReplayBuffer(dstSlot) && BotController.TransferRecordingToReplay(srcSlot, dstSlot));
        // Registers the authoritative native pawn pointer for replay.
        public bool SetReplayPawn(int slot, nint pawn) => _owned.Track(slot, SlotResource.Replay, CanUseReplayBuffer(slot) && BotController.SetReplayPawn(slot, pawn));
        public bool StartReplay(int slot, bool loop = false) => CanControl(slot) && _owned.Has(slot, SlotResource.Replay) && BotController.StartReplay(slot, loop);
        public bool StopReplay(int slot) => CanControl(slot) && _owned.Has(slot, SlotResource.Replay) && BotController.StopReplay(slot);
        public int ReplayCursor(int slot) => BotController.ReplayCursor(slot);
        public int ReplayTotal(int slot) => BotController.ReplayTotal(slot);
        public bool IsReplaying(int slot) => BotController.IsReplaying(slot);
        public bool TryGetReplayTick(int slot, out ReplayTick tick)
            => BotController.TryGetReplayTick(slot, out tick);

        // ---- weapons ----
        public bool SwitchBotWeapon(int slot, int defIndex)
            => CanControl(slot) && BotController.SwitchBotWeapon(slot, defIndex);
        public int BotActiveWeaponDef(int slot) => BotController.BotActiveWeaponDef(slot);
        // Creates an independently cancellable native usercmd injection
        public long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0)
            => _owned.Track(slot, SlotResource.Input, CanControl(slot) ? BotController.InjectUsercmd(slot, buttonMask, durationMs) : -1);
        // Cancels one native usercmd injection by its token
        public bool CancelUsercmdInjection(int slot, long injectionId)
            => BotController.CancelUsercmdInjection(slot, injectionId);
        // Creates an independently cancellable persistent analog movement override
        public long StartUsercmdMovement(int slot, float forwardMove, float leftMove)
            => _owned.Track(slot, SlotResource.Input, CanControl(slot) ? BotController.StartUsercmdMovement(slot, forwardMove, leftMove) : -1);
        // Updates one persistent analog movement override
        public bool UpdateUsercmdMovement(
            int slot,
            long movementId,
            float forwardMove,
            float leftMove)
            => CanControl(slot) && BotController.UpdateUsercmdMovement(
                slot, movementId, forwardMove, leftMove);
        // Cancels one persistent analog movement override
        public bool CancelUsercmdMovement(int slot, long movementId)
            => BotController.CancelUsercmdMovement(slot, movementId);
        // Suppresses selected usercmd buttons for a fixed duration
        public bool SuppressUsercmd(int slot, ulong buttonMask, int durationMs)
            => _owned.Track(slot, SlotResource.Input, CanControl(slot) && BotController.SuppressUsercmd(slot, buttonMask, durationMs));
        // Creates an independently cancellable persistent native usercmd suppression
        public long StartUsercmdSuppression(int slot, ulong buttonMask)
            => _owned.Track(slot, SlotResource.Input, CanControl(slot) ? BotController.StartUsercmdSuppression(slot, buttonMask) : -1);
        // Cancels one persistent native usercmd suppression by its token
        public bool CancelUsercmdSuppression(int slot, long suppressionId)
            => BotController.CancelUsercmdSuppression(slot, suppressionId);

        // ---- profile ----
        public bool GetBotProfile(int slot, out BotProfileData profile)
            => BotController.GetBotProfile(slot, out profile);

        // ---- buy plans ----
        public bool SetBuyPlan(int slot, string aliases) => _owned.Track(slot, SlotResource.BuyPlan, CanControl(slot) && BotController.SetBuyPlan(slot, aliases));
        public bool SetBuySkip(int slot) => _owned.Track(slot, SlotResource.BuyPlan, CanControl(slot) && BotController.SetBuySkip(slot));
        public bool ClearBuyPlan(int slot)
        {
            if (!CanControl(slot) || !BotController.ClearBuyPlan(slot)) return false;
            _owned.Forget(slot, SlotResource.BuyPlan);
            return true;
        }
        public bool ClearAllBuyPlans()
        {
            foreach (var slot in _owned.Slots)
                if (_owned.Has(slot, SlotResource.BuyPlan)) ClearBuyPlan(slot);
            return true;
        }
        public int BuyPlanItemCount(int slot) => BotController.BuyPlanItemCount(slot);

        internal void ObserveDemoTracerOwnership(Func<int, bool> ownsSlot, Action<int> onTakeover)
        {
            foreach (var slot in _owned.Slots)
                if (_owned.Has(slot, SlotResource.Control) && ownsSlot(slot))
                {
                    _owned.Forget(slot, SlotResource.Control);
                    onTakeover(slot);
                }
        }

        internal void ReleaseOwnedSlot(int slot, bool takenByDemoTracer)
            => _owned.Release(slot, takenByDemoTracer, ReleaseResources);

        internal void ReleaseOwnedReplay(int slot, bool takenByDemoTracer)
            => _owned.Release(slot, SlotResource.Replay, takenByDemoTracer, ReleaseResources);

        private static void ReleaseResources(int slot, SlotResource held)
        {
            List<Exception> errors = new();
            void Release(SlotResource resource, Func<bool> action)
            {
                if ((held & resource) == 0) return;
                try
                {
                    if (!action()) errors.Add(new InvalidOperationException($"Slot {slot}: failed to release {resource}"));
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            Release(SlotResource.Recording, () => BotController.ClearRecordedMotion(slot));
            Release(SlotResource.Replay, () => BotController.ReleaseReplayBuffer(slot));
            Release(SlotResource.Input, () => { BotController.BotController_ClearUsercmdInjections(slot); return true; });
            Release(SlotResource.AllLock, () => BotController.Unlock(slot, LockKind.All));
            Release(SlotResource.AimLock, () => BotController.Unlock(slot, LockKind.Aim));
            Release(SlotResource.WeaponLock, () => BotController.Unlock(slot, LockKind.Weapon));
            Release(SlotResource.BuyPlan, () => BotController.ClearBuyPlan(slot));
            if (errors.Count != 0) throw new AggregateException(errors);
        }

        internal void ReleaseAllOwnedSlots(Func<int, bool> ownsSlot)
            => _owned.ReleaseAll(ownsSlot, ReleaseResources);

        // ---- voice ----
        public bool CanSendVoice() => BotController.CanSendVoice();
        public int GetVoiceStatus() => BotController.GetVoiceStatus();
        public int SendVoiceFrame(
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
            => BotController.SendVoiceFrame(
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
