// Engine-independent validation for the parallel replay tick/subtick layout.

#pragma once

#include "MotionRecorder.h"

#include <cstddef>
#include <vector>

namespace BotController
{
    namespace ReplaySubtickLayout
    {
        // DTR v10 preserves signed demo-relative phases as evidence, but the
        // live CS2 CSubtickMoveStepPB domain starts at zero. Keep the recorded
        // value untouched in staging and project only at the engine boundary.
        constexpr float ProjectSubtickWhenForEngine(float recordedWhen) noexcept
        {
            return recordedWhen <= 0.0f ? 0.0f : recordedWhen;
        }

        struct ReplayLoadStaging
        {
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            std::vector<ReplayCommandFrameData> commands;
            std::vector<std::size_t> offsets;

            void Swap(ReplayLoadStaging &other) noexcept;
        };

        // Validate the raw C ABI tick/subtick counts and build a prefix-sum
        // table. On failure, offsets is left unchanged.
        bool TryBuildReplaySubtickOffsets(
            const ReplayTick *ticks,
            int tickCount,
            int subCount,
            std::vector<std::size_t> &offsets) noexcept;

        // Validate and return one tick's range in the parallel subtick array.
        // On failure, begin and end are left unchanged.
        bool TryGetReplaySubtickRange(
            const ReplayTick *ticks,
            std::size_t tickCount,
            const std::vector<std::size_t> &offsets,
            std::size_t subCount,
            std::size_t tickIndex,
            std::size_t &begin,
            std::size_t &end) noexcept;

        // Validate every parallel ABI buffer and stage the supported playback data.
        // Legacy movement extras and unsupported input history are validated only.
        // On failure, staged is left unchanged.
        bool TryStageReplayLoad(
            const ReplayTick *ticks,
            int tickCount,
            const SubtickMove *subs,
            int subCount,
            const ReplayCommandFrameData *commands,
            int commandCount,
            const ReplayMovementExtra *movementExtras,
            int movementExtraCount,
            ReplayLoadStaging &staged) noexcept;

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
            ReplayLoadStaging &staged) noexcept;
    } // namespace ReplaySubtickLayout
} // namespace BotController
