// C-ABI exports for CounterStrikeSharp P/Invoke.

#include "dispatch.h"
#include "BotController.h"
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "BuyControllerState.h"
#include "VoiceSender.h"
#include "projectile_birth_align.h"

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#ifndef BOTCONTROLLER_BUILD_ID
#define BOTCONTROLLER_BUILD_ID "local"
#endif

namespace
{
    constexpr int kBotControllerAbiMajor = 18;
    constexpr int kBotControllerAbiMinor = 34;
    constexpr uint64_t kCapabilityReplaySlotState = 1ULL << 0;
    constexpr uint64_t kCapabilityStartReplayAt = 1ULL << 1;
    constexpr uint64_t kCapabilityStartReplayUntil = 1ULL << 2;
    constexpr uint64_t kCapabilityReplayTick = 1ULL << 3;
    constexpr uint64_t kCapabilityWeaponSwitchRead = 1ULL << 4;
    constexpr uint64_t kCapabilityPovMask = 1ULL << 5;
    constexpr uint64_t kCapabilityBuyPlan = 1ULL << 6;
    constexpr uint64_t kCapabilityControllerBotOffset = 1ULL << 7;
    constexpr uint64_t kCapabilityExtendedReplay = 1ULL << 8;
    constexpr uint64_t kCapabilityUsercmdMovementIntent = 1ULL << 9;
    constexpr uint64_t kCapabilityVoiceSend = 1ULL << 10;
    constexpr uint64_t kCapabilityNativePerception = 1ULL << 11;
    constexpr uint64_t kCapabilityReleaseReplayBuffer = 1ULL << 12;
    constexpr uint64_t kCapabilityButtonOnlyMovementIntent = 1ULL << 13;
    constexpr uint64_t kCapabilityHandoffBestWeapon = 1ULL << 14;
    constexpr uint64_t kCapabilityReplayInputHistory = 1ULL << 15;
    constexpr uint64_t kBotControllerCapabilities =
        kCapabilityReplaySlotState |
        kCapabilityStartReplayAt |
        kCapabilityStartReplayUntil |
        kCapabilityReplayTick |
        kCapabilityWeaponSwitchRead |
        kCapabilityPovMask |
        kCapabilityBuyPlan |
        kCapabilityControllerBotOffset |
        kCapabilityExtendedReplay |
        kCapabilityUsercmdMovementIntent |
        kCapabilityVoiceSend |
        kCapabilityNativePerception |
        kCapabilityReleaseReplayBuffer |
        kCapabilityButtonOnlyMovementIntent |
        kCapabilityHandoffBestWeapon |
        kCapabilityReplayInputHistory;

#pragma pack(push, 4)
    struct BotControllerAbiInfo
    {
        int32_t abiMajor;
        int32_t abiMinor;
        int32_t movementSnapshotSize;
        int32_t replayTickSize;
        int32_t subtickMoveSize;
        int32_t replaySlotStateSize;
        int32_t maxSlots;
        uint64_t capabilities;
        int32_t reserved0;
        int32_t reserved1;
    };
#pragma pack(pop)

    static_assert(sizeof(BotControllerAbiInfo) == 44);
} // namespace

extern "C" __declspec(dllexport) int BotController_Lock(int slot, int kind, int arg)
{
    return BotController::Dispatch::Lock(
        slot, static_cast<BotController::LockKind>(kind), arg);
}

extern "C" __declspec(dllexport) int BotController_Unlock(int slot, int kind)
{
    return BotController::Dispatch::Unlock(
        slot, static_cast<BotController::LockKind>(kind));
}

extern "C" __declspec(dllexport) int BotController_UnlockAll(int kind)
{
    return BotController::Dispatch::UnlockAll(
        static_cast<BotController::LockKind>(kind));
}

extern "C" __declspec(dllexport) int BotController_IsLocked(int slot, int kind)
{
    return BotController::Dispatch::IsLocked(slot,
                                             static_cast<BotController::LockKind>(kind));
}

extern "C" __declspec(dllexport) int BotController_GetVersion()
{
    return kBotControllerAbiMajor;
}

extern "C" __declspec(dllexport) int BotController_GetAbiInfo(BotControllerAbiInfo *out, int size)
{
    if (!out || size < static_cast<int>(sizeof(BotControllerAbiInfo)))
        return -1;

    BotControllerAbiInfo info{};
    info.abiMajor = kBotControllerAbiMajor;
    info.abiMinor = kBotControllerAbiMinor;
    info.movementSnapshotSize = static_cast<int32_t>(sizeof(BotController::MovementSnapshot));
    info.replayTickSize = static_cast<int32_t>(sizeof(BotController::ReplayTick));
    info.subtickMoveSize = static_cast<int32_t>(sizeof(BotController::SubtickMove));
    info.replaySlotStateSize = static_cast<int32_t>(sizeof(BotController::MotionRecorder::ReplaySlotState));
    info.maxSlots = BotController::MotionRecorder::kMaxSlots;
    info.capabilities = kBotControllerCapabilities;
    std::memcpy(out, &info, sizeof(info));
    return 0;
}

extern "C" __declspec(dllexport) uint64_t BotController_GetCapabilities()
{
    return kBotControllerCapabilities;
}

extern "C" __declspec(dllexport) const char *BotController_GetBuildId()
{
    return BOTCONTROLLER_BUILD_ID;
}

extern "C" __declspec(dllexport) int BotController_GetNativePerceptionState(
    int slot,
    BotController::NativePerceptionState *out,
    int size)
{
    if (!out || size < static_cast<int>(sizeof(BotController::NativePerceptionState)))
        return -1;

    BotController::NativePerceptionState state{};
    if (!BotController::BotControllerHooks::GetNativePerceptionState(slot, state))
        return -2;
    std::memcpy(out, &state, sizeof(state));
    return 0;
}

extern "C" __declspec(dllexport) int BotController_SetReplayNativeFovOverride(int enabled)
{
    BotController::BotControllerHooks::SetReplayNativeFovOverride(enabled != 0);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_RequestEquipBestWeapon(int slot)
{
    return BotController::BotControllerHooks::RequestEquipBestWeapon(slot) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_CanSendVoice()
{
    return BotController::VoiceSender::IsAvailable() ? 1 : 0;
}

extern "C" __declspec(dllexport) int BotController_GetVoiceStatus()
{
    return BotController::VoiceSender::GetStatus();
}

extern "C" __declspec(dllexport) int BotController_SetProjectileBirthAlignOffsets(
    int initialPositionOffset,
    int initialVelocityOffset)
{
    return BotController::ProjectileBirthAlign::ConfigureOffsets(
        initialPositionOffset,
        initialVelocityOffset);
}

extern "C" __declspec(dllexport) int BotController_QueueProjectileBirthAlign(
    uint64_t entityPtr,
    float posX,
    float posY,
    float posZ,
    float velX,
    float velY,
    float velZ)
{
    return BotController::ProjectileBirthAlign::Queue(
        entityPtr,
        posX,
        posY,
        posZ,
        velX,
        velY,
        velZ);
}

extern "C" __declspec(dllexport) int BotController_ClearProjectileBirthAlign()
{
    return BotController::ProjectileBirthAlign::Clear();
}

extern "C" __declspec(dllexport) int BotController_GetProjectileBirthAlignStatus(
    BotController::ProjectileBirthAlign::Status *out,
    int size)
{
    return BotController::ProjectileBirthAlign::GetStatus(out, size);
}

extern "C" __declspec(dllexport) int BotController_SendVoiceFrame(
    int recipientSlot,
    int senderClient,
    uint64_t senderXuid,
    const uint8_t *audio,
    int audioBytes,
    int sampleRate,
    float voiceLevel,
    int sequenceBytes,
    int sectionNumber,
    int uncompressedSampleOffset,
    uint32_t numPackets,
    const uint32_t *packetOffsets,
    int packetOffsetCount,
    int tick,
    int audibleMask)
{
    return BotController::VoiceSender::SendVoiceFrame(
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

extern "C" __declspec(dllexport) int BotController_SetControllerControllingBotOffset(int offset)
{
    return BotController::InputInjector::SetControllerControllingBotOffset(offset) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_SetReplayPovMask(uint64_t mask)
{
    BotController::MotionRecorder::SetReplayPovMask(mask);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_SetReplayPawn(int slot, uint64_t pawnPtr)
{
    return BotController::InputInjector::SetReplayPawn(
               slot,
               reinterpret_cast<void *>(static_cast<uintptr_t>(pawnPtr)))
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotController_SetUsercmdMovementIntent(
    int slot,
    uint64_t buttonsSet,
    uint64_t buttonsClear,
    float analogForward,
    float analogLeft,
    int durationMs,
    int flags)
{
    return BotController::InputInjector::SetUsercmdMovementIntent(
               slot, buttonsSet, buttonsClear, analogForward, analogLeft,
               durationMs, flags)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotController_ClearUsercmdMovementIntent(int slot)
{
    return BotController::InputInjector::ClearUsercmdMovementIntent(slot) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_SetLeftHandIntent(
    int slot,
    uint64_t buttonsSet,
    uint64_t buttonsClear,
    float analogForward,
    float analogLeft,
    int durationMs,
    int flags)
{
    return BotController_SetUsercmdMovementIntent(
        slot, buttonsSet, buttonsClear, analogForward, analogLeft,
        durationMs, flags);
}

extern "C" __declspec(dllexport) int BotController_ClearLeftHandIntent(int slot)
{
    return BotController_ClearUsercmdMovementIntent(slot);
}

extern "C" __declspec(dllexport) int BotController_SetLeftHandDesiredLatch(
    int slot,
    int enabled,
    int leftHandDesired)
{
    return BotController::InputInjector::SetLeftHandDesiredLatch(
               slot,
               enabled != 0,
               leftHandDesired != 0)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotController_ClearAllLeftHandDesiredLatches()
{
    BotController::InputInjector::ClearAllLeftHandDesiredLatches();
    return 0;
}

// ---- Bot buy plans ----

static std::vector<std::string> SplitAliases(const char *aliases)
{
    std::vector<std::string> out;
    if (!aliases)
        return out;

    std::string cur;
    for (const char *p = aliases; *p; ++p)
    {
        char c = *p;
        if (c == ' ' || c == ',' || c == '\t')
        {
            if (!cur.empty())
            {
                out.push_back(cur);
                cur.clear();
            }
        }
        else
        {
            cur.push_back(c);
        }
    }
    if (!cur.empty())
        out.push_back(cur);
    return out;
}

extern "C" __declspec(dllexport) int BotController_SetBuyPlan(int slot, const char *aliases)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Set(slot, SplitAliases(aliases), false);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_SetBuySkip(int slot)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Set(slot, {}, true);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_ClearBuyPlan(int slot)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Clear(slot);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_ClearAllBuyPlans()
{
    BotController::BuyControllerState::ClearAll();
    return 0;
}

extern "C" __declspec(dllexport) int BotController_GetBuyPlanItemCount(int slot)
{
    return BotController::BuyControllerState::ItemCount(slot);
}

// ---- Motion recording & replay ----

// Begin/stop recording a human slot's per-tick movement. 0 ok / -1 fail.
extern "C" __declspec(dllexport) int BotController_StartRecord(int slot)
{
    return BotController::MotionRecorder::StartRecord(slot) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_StopRecord(int slot)
{
    return BotController::MotionRecorder::StopRecord(slot) ? 0 : -1;
}

// Recorded tick / subtick counts for a slot. <0 on bad slot.
extern "C" __declspec(dllexport) int BotController_GetRecordedTickCount(int slot)
{
    return BotController::MotionRecorder::RecordedTickCount(slot);
}

extern "C" __declspec(dllexport) int BotController_GetRecordedSubtickCount(int slot)
{
    return BotController::MotionRecorder::RecordedSubtickCount(slot);
}

// Copy recorded ticks / subticks into caller buffers. Returns count written.
extern "C" __declspec(dllexport) int BotController_CopyRecordedTicks(int slot, BotController::ReplayTick *out, int maxTicks)
{
    return BotController::MotionRecorder::CopyTicks(slot, out, maxTicks);
}

extern "C" __declspec(dllexport) int BotController_CopyRecordedSubticks(int slot, BotController::SubtickMove *out, int maxSubticks)
{
    return BotController::MotionRecorder::CopySubticks(slot, out, maxSubticks);
}

// Load parallel tick + subtick arrays into a slot's replay buffer. 0 ok.
extern "C" __declspec(dllexport) int BotController_LoadReplay(int slot,
                                                              const BotController::ReplayTick *ticks, int tickCount,
                                                              const BotController::SubtickMove *subs, int subCount) noexcept
{
    try
    {
        return BotController::MotionRecorder::LoadReplay(slot, ticks, tickCount,
                                                         subs, subCount)
                   ? 0
                   : -1;
    }
    catch (...)
    {
        return -1;
    }
}

extern "C" __declspec(dllexport) int BotController_LoadReplayExtended(
    int slot,
    const BotController::ReplayTick *ticks, int tickCount,
    const BotController::SubtickMove *subs, int subCount,
    const BotController::ReplayCommandFrameData *commands, int commandCount,
    const BotController::ReplayMovementExtra *movementExtras, int movementExtraCount) noexcept
{
    try
    {
        return BotController::MotionRecorder::LoadReplayExtended(
                   slot, ticks, tickCount, subs, subCount,
                   commands, commandCount, movementExtras, movementExtraCount)
                   ? 0
                   : -1;
    }
    catch (...)
    {
        return -1;
    }
}

extern "C" __declspec(dllexport) int BotController_LoadReplayWithInputHistory(
    int slot,
    const BotController::ReplayTick *ticks, int tickCount,
    const BotController::SubtickMove *subs, int subCount,
    const BotController::ReplayCommandFrameData *commands, int commandCount,
    const BotController::ReplayMovementExtra *movementExtras, int movementExtraCount,
    const BotController::ReplayInputHistoryTick *inputHistoryTicks, int inputHistoryTickCount,
    const BotController::ReplayInputHistoryEntry *inputHistoryEntries, int inputHistoryEntryCount) noexcept
{
    try
    {
        return BotController::MotionRecorder::LoadReplayWithInputHistory(
                   slot, ticks, tickCount, subs, subCount,
                   commands, commandCount, movementExtras, movementExtraCount,
                   inputHistoryTicks, inputHistoryTickCount,
                   inputHistoryEntries, inputHistoryEntryCount)
                   ? 0
                   : -1;
    }
    catch (...)
    {
        return -1;
    }
}

// Move a slot's just-recorded buffers into another slot's replay buffer
extern "C" __declspec(dllexport) int BotController_TransferRecordingToReplay(int srcSlot, int dstSlot) noexcept
{
    try
    {
        int nt = BotController::MotionRecorder::RecordedTickCount(srcSlot);
        if (nt <= 0)
            return -1;
        int ns = BotController::MotionRecorder::RecordedSubtickCount(srcSlot);
        if (ns < 0)
            ns = 0;
        std::vector<BotController::ReplayTick> ticks(nt);
        std::vector<BotController::SubtickMove> subs(ns > 0 ? ns : 1);
        int gotT = BotController::MotionRecorder::CopyTicks(srcSlot, ticks.data(), nt);
        int gotS = ns > 0
                       ? BotController::MotionRecorder::CopySubticks(srcSlot, subs.data(), ns)
                       : 0;
        if (gotT <= 0)
            return -1;
        return BotController::MotionRecorder::LoadReplay(
                   dstSlot, ticks.data(), gotT, subs.data(), gotS)
                   ? 0
                   : -1;
    }
    catch (...)
    {
        return -1;
    }
}

extern "C" __declspec(dllexport) int BotController_StartReplay(int slot, int loop)
{
    return BotController::MotionRecorder::StartReplay(slot, loop != 0) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_StartReplayAt(int slot, int loop, int startIndex)
{
    return BotController::MotionRecorder::StartReplayAt(slot, loop != 0, startIndex) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_StartReplayUntil(
    int slot, int loop, int startIndex, int holdBeforeIndex)
{
    return BotController::MotionRecorder::StartReplayUntil(
               slot, loop != 0, startIndex, holdBeforeIndex)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotController_StopReplay(int slot)
{
    return BotController::MotionRecorder::StopReplay(slot) ? 0 : -1;
}

// Stop replay and return all loaded replay-buffer capacity to the allocator.
// Callers expecting to restart the same loaded replay should use StopReplay.
extern "C" __declspec(dllexport) int BotController_ReleaseReplayBuffer(int slot)
{
    return BotController::MotionRecorder::ReleaseReplayBuffer(slot) ? 0 : -1;
}

// Current replay tick index, or <0 if the slot is not replaying.
extern "C" __declspec(dllexport) int BotController_GetReplayCursor(int slot)
{
    return BotController::MotionRecorder::ReplayCursor(slot);
}

// Total ticks loaded in a slot's replay buffer.
extern "C" __declspec(dllexport) int BotController_GetReplayTotal(int slot)
{
    return BotController::MotionRecorder::ReplayTotal(slot);
}

// Combined replay state for CSS hot paths. Returns 0 on success.
extern "C" __declspec(dllexport) int BotController_GetReplaySlotState(
    int slot,
    BotController::MotionRecorder::ReplaySlotState *out)
{
    if (!out)
        return -1;
    return BotController::MotionRecorder::GetReplaySlotState(slot, *out) ? 0 : -1;
}

// Copy the tick currently being replayed (for C# to drive weapon/fire).
// Returns 0 on success, -1 if the slot isn't replaying.
extern "C" __declspec(dllexport) int BotController_GetReplayTick(int slot, BotController::ReplayTick *out)
{
    if (!out)
        return -1;
    return BotController::MotionRecorder::CurrentReplayTick(slot, *out) ? 0 : -1;
}

// Switch a bot to the weapon with this def index
// Returns 0 ok / -1 not found or bot not ready.
extern "C" __declspec(dllexport) int BotController_SwitchBotWeapon(int slot, int defIndex)
{
    return BotController::MotionRecorder::SwitchBotWeaponByDef(slot, defIndex) ? 0 : -1;
}

// Def index of the bot's current active weapon (same normalization as the
// recorded WeaponDefIndex). <0 if unresolved. For C# to reconcile replay.
extern "C" __declspec(dllexport) int BotController_GetBotActiveWeaponDef(int slot)
{
    return BotController::MotionRecorder::BotActiveWeaponDef(slot);
}
