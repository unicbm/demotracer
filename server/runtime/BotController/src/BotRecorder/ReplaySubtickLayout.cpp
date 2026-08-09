#include "ReplaySubtickLayout.h"

#include <cmath>
#include <cstdint>
#include <limits>

namespace
{
    using namespace BotController;

    bool FiniteSnapshot(const MovementSnapshot &snapshot) noexcept
    {
        return std::isfinite(snapshot.originX) &&
               std::isfinite(snapshot.originY) &&
               std::isfinite(snapshot.originZ) &&
               std::isfinite(snapshot.velX) &&
               std::isfinite(snapshot.velY) &&
               std::isfinite(snapshot.velZ) &&
               std::isfinite(snapshot.pitch) &&
               std::isfinite(snapshot.yaw) &&
               std::isfinite(snapshot.roll) &&
               std::isfinite(snapshot.duckAmount) &&
               std::isfinite(snapshot.duckSpeed) &&
               std::isfinite(snapshot.ladderNormalX) &&
               std::isfinite(snapshot.ladderNormalY) &&
               std::isfinite(snapshot.ladderNormalZ) &&
               snapshot.ducked <= 1 &&
               snapshot.ducking <= 1 &&
               snapshot.desiresDuck <= 1;
    }

    bool ValidReplaySemantics(
        const ReplayTick *ticks,
        int tickCount,
        const SubtickMove *subs,
        int subCount,
        const ReplayCommandFrameData *commands,
        int commandCount,
        const ReplayMovementExtra *movementExtras,
        int movementExtraCount,
        const ReplayInputHistoryEntry *inputHistoryEntries,
        int inputHistoryEntryCount) noexcept
    {
        constexpr std::uint32_t commandFieldsAll =
            MotionRecorder::kCommandFieldForwardMove |
            MotionRecorder::kCommandFieldLeftMove |
            MotionRecorder::kCommandFieldUpMove |
            MotionRecorder::kCommandFieldViewAngles |
            MotionRecorder::kCommandFieldButtons |
            MotionRecorder::kCommandFieldMouse |
            MotionRecorder::kCommandFieldWeaponSelect |
            MotionRecorder::kCommandFieldLeftHand;

        for (int i = 0; i < tickCount; ++i)
        {
            if (!FiniteSnapshot(ticks[i].pre) ||
                !FiniteSnapshot(ticks[i].post) ||
                ticks[i].weaponDefIndex < -1)
            {
                return false;
            }
        }
        for (int i = 0; i < subCount; ++i)
        {
            const auto &sub = subs[i];
            if (!std::isfinite(sub.when) || sub.when < 0.0f || sub.when >= 1.0f ||
                !std::isfinite(sub.pressed) ||
                !std::isfinite(sub.analogForward) ||
                !std::isfinite(sub.analogLeft) ||
                !std::isfinite(sub.pitchDelta) ||
                !std::isfinite(sub.yawDelta))
            {
                return false;
            }
        }
        for (int i = 0; i < commandCount; ++i)
        {
            const auto &command = commands[i];
            if ((command.fields & ~commandFieldsAll) != 0 ||
                command.leftHandDesired > 1 ||
                command.weaponSelect < -1 ||
                !std::isfinite(command.forwardMove) ||
                !std::isfinite(command.leftMove) ||
                !std::isfinite(command.upMove) ||
                !std::isfinite(command.pitch) ||
                !std::isfinite(command.yaw) ||
                !std::isfinite(command.roll))
            {
                return false;
            }
        }
        for (int i = 0; i < movementExtraCount; ++i)
        {
            const auto &extra = movementExtras[i];
            if (!std::isfinite(extra.jumpPressedTime) ||
                !std::isfinite(extra.lastDuckTime) ||
                !std::isfinite(extra.lastActualJumpPressFrac) ||
                !std::isfinite(extra.lastUsableJumpPressFrac) ||
                !std::isfinite(extra.lastLandedFrac) ||
                !std::isfinite(extra.lastLandedVelocityX) ||
                !std::isfinite(extra.lastLandedVelocityY) ||
                !std::isfinite(extra.lastLandedVelocityZ))
            {
                return false;
            }
        }
        for (int i = 0; i < inputHistoryEntryCount; ++i)
        {
            const auto &entry = inputHistoryEntries[i];
            const float values[] = {
                entry.viewPitch, entry.viewYaw, entry.viewRoll,
                entry.renderTickFraction, entry.playerTickFraction,
                entry.clInterpFraction, entry.svInterp0Fraction,
                entry.svInterp1Fraction, entry.playerInterpFraction,
                entry.shootPositionX, entry.shootPositionY, entry.shootPositionZ,
                entry.targetHeadPosCheckX, entry.targetHeadPosCheckY, entry.targetHeadPosCheckZ,
                entry.targetAbsPosCheckX, entry.targetAbsPosCheckY, entry.targetAbsPosCheckZ,
                entry.targetAbsAngCheckX, entry.targetAbsAngCheckY, entry.targetAbsAngCheckZ};
            if ((entry.fields & ~MotionRecorder::kInputHistoryFieldsAll) != 0)
                return false;
            for (float value : values)
            {
                if (!std::isfinite(value))
                    return false;
            }
        }
        return true;
    }
}

namespace BotController::ReplaySubtickLayout
{
    void ReplayLoadStaging::Swap(ReplayLoadStaging &other) noexcept
    {
        ticks.swap(other.ticks);
        subs.swap(other.subs);
        commands.swap(other.commands);
        movementExtras.swap(other.movementExtras);
        inputHistoryTicks.swap(other.inputHistoryTicks);
        inputHistoryEntries.swap(other.inputHistoryEntries);
        offsets.swap(other.offsets);
        inputHistoryOffsets.swap(other.inputHistoryOffsets);
    }

    bool TryBuildReplaySubtickOffsets(
        const ReplayTick *ticks,
        int tickCount,
        int subCount,
        std::vector<std::size_t> &offsets) noexcept
    {
        try
        {
            if (!ticks || tickCount < 0 || subCount < 0)
                return false;

            const auto tickSize = static_cast<std::size_t>(tickCount);
            if (tickSize == std::numeric_limits<std::size_t>::max())
                return false;

            std::uint64_t total = 0;
            const auto expected = static_cast<std::uint64_t>(subCount);
            for (std::size_t i = 0; i < tickSize; ++i)
            {
                const std::uint64_t count = ticks[i].numSubtick;
                if (count > static_cast<std::uint64_t>(MotionRecorder::kMaxSubtickPerTick) ||
                    total > std::numeric_limits<std::uint64_t>::max() - count)
                {
                    return false;
                }
                total += count;
                if (total > expected)
                    return false;
            }
            if (total != expected || total > std::numeric_limits<std::size_t>::max())
                return false;

            std::vector<std::size_t> candidate(tickSize + 1, 0);
            std::size_t accumulated = 0;
            for (std::size_t i = 0; i < tickSize; ++i)
            {
                candidate[i] = accumulated;
                accumulated += static_cast<std::size_t>(ticks[i].numSubtick);
            }
            candidate[tickSize] = accumulated;
            offsets.swap(candidate);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    bool TryGetReplaySubtickRange(
        const ReplayTick *ticks,
        std::size_t tickCount,
        const std::vector<std::size_t> &offsets,
        std::size_t subCount,
        std::size_t tickIndex,
        std::size_t &begin,
        std::size_t &end) noexcept
    {
        if (!ticks || tickIndex >= tickCount ||
            tickCount == std::numeric_limits<std::size_t>::max() ||
            offsets.size() != tickCount + 1 || offsets.empty() ||
            offsets.front() != 0 || offsets.back() != subCount)
        {
            return false;
        }

        const std::size_t candidateBegin = offsets[tickIndex];
        const std::size_t candidateEnd = offsets[tickIndex + 1];
        const std::uint32_t expectedCount = ticks[tickIndex].numSubtick;
        if (expectedCount > static_cast<std::uint32_t>(MotionRecorder::kMaxSubtickPerTick) ||
            candidateBegin > candidateEnd || candidateEnd > subCount ||
            candidateEnd - candidateBegin != static_cast<std::size_t>(expectedCount))
        {
            return false;
        }

        begin = candidateBegin;
        end = candidateEnd;
        return true;
    }

    bool TryStageReplayLoad(
        const ReplayTick *ticks,
        int tickCount,
        const SubtickMove *subs,
        int subCount,
        const ReplayCommandFrameData *commands,
        int commandCount,
        const ReplayMovementExtra *movementExtras,
        int movementExtraCount,
        ReplayLoadStaging &staged) noexcept
    {
        return TryStageReplayLoad(
            ticks, tickCount, subs, subCount,
            commands, commandCount, movementExtras, movementExtraCount,
            nullptr, 0, nullptr, 0, staged);
    }

    bool TryStageReplayLoad(
        const ReplayTick *ticks,
        int tickCount,
        const SubtickMove *subs,
        int subCount,
        const ReplayCommandFrameData *commands,
        int commandCount,
        const ReplayMovementExtra *movementExtras,
        int movementExtraCount,
        const ReplayInputHistoryTick *inputHistoryTicks,
        int inputHistoryTickCount,
        const ReplayInputHistoryEntry *inputHistoryEntries,
        int inputHistoryEntryCount,
        ReplayLoadStaging &staged) noexcept
    {
        try
        {
            if (!ticks || tickCount < 0 || subCount < 0 ||
                (subCount > 0 && !subs) ||
                (commandCount != 0 && commandCount != tickCount) ||
                (commandCount > 0 && !commands) ||
                (movementExtraCount != 0 && movementExtraCount != tickCount) ||
                (movementExtraCount > 0 && !movementExtras) ||
                (inputHistoryTickCount != 0 && inputHistoryTickCount != tickCount) ||
                (inputHistoryTickCount > 0 && !inputHistoryTicks) ||
                inputHistoryEntryCount < 0 ||
                (inputHistoryEntryCount > 0 && !inputHistoryEntries) ||
                (inputHistoryTickCount == 0 && inputHistoryEntryCount != 0))
            {
                return false;
            }

            // Check the raw ABI layout before copying the other parallel
            // buffers, then validate the exact staged tick bytes again below.
            std::vector<std::size_t> rawOffsets;
            if (!TryBuildReplaySubtickOffsets(
                    ticks, tickCount, subCount, rawOffsets))
            {
                return false;
            }
            if (!ValidReplaySemantics(
                    ticks, tickCount, subs, subCount,
                    commands, commandCount,
                    movementExtras, movementExtraCount,
                    inputHistoryEntries, inputHistoryEntryCount))
            {
                return false;
            }

            ReplayLoadStaging candidate;
            if (tickCount > 0)
                candidate.ticks.assign(ticks, ticks + tickCount);
            if (subCount > 0)
                candidate.subs.assign(subs, subs + subCount);
            if (commandCount > 0)
                candidate.commands.assign(commands, commands + commandCount);
            if (movementExtraCount > 0)
            {
                candidate.movementExtras.assign(
                    movementExtras, movementExtras + movementExtraCount);
            }
            if (inputHistoryTickCount > 0)
            {
                std::uint64_t total = 0;
                candidate.inputHistoryOffsets.reserve(
                    static_cast<std::size_t>(inputHistoryTickCount) + 1);
                candidate.inputHistoryOffsets.push_back(0);
                for (int i = 0; i < inputHistoryTickCount; ++i)
                {
                    const auto &tick = inputHistoryTicks[i];
                    if (tick.numEntries > MotionRecorder::kMaxInputHistoryPerTick ||
                        tick.attack1StartHistoryIndex < -1 ||
                        tick.attack2StartHistoryIndex < -1 ||
                        tick.attack1StartHistoryIndex >= static_cast<int32_t>(tick.numEntries) ||
                        tick.attack2StartHistoryIndex >= static_cast<int32_t>(tick.numEntries))
                    {
                        return false;
                    }
                    total += tick.numEntries;
                    if (total > static_cast<std::uint64_t>(inputHistoryEntryCount))
                        return false;
                    candidate.inputHistoryOffsets.push_back(static_cast<std::size_t>(total));
                }
                if (total != static_cast<std::uint64_t>(inputHistoryEntryCount))
                    return false;
                candidate.inputHistoryTicks.assign(
                    inputHistoryTicks, inputHistoryTicks + inputHistoryTickCount);
                if (inputHistoryEntryCount > 0)
                {
                    candidate.inputHistoryEntries.assign(
                        inputHistoryEntries, inputHistoryEntries + inputHistoryEntryCount);
                }
            }

            const ReplayTick *candidateTicks =
                candidate.ticks.empty() ? ticks : candidate.ticks.data();
            if (!TryBuildReplaySubtickOffsets(
                    candidateTicks, tickCount, subCount, candidate.offsets))
            {
                return false;
            }

            staged.Swap(candidate);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }
} // namespace BotController::ReplaySubtickLayout
