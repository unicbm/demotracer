#include "ReplaySubtickLayout.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <vector>

namespace
{
    using BotController::ReplayCommandFrameData;
    using BotController::ReplayMovementExtra;
    using BotController::ReplaySubtickLayout::ReplayLoadStaging;
    using BotController::ReplaySubtickLayout::TryBuildReplaySubtickOffsets;
    using BotController::ReplaySubtickLayout::TryGetReplaySubtickRange;
    using BotController::ReplaySubtickLayout::TryStageReplayLoad;
    using BotController::ReplayTick;
    using BotController::SubtickMove;

    [[noreturn]] void Trap()
    {
        std::abort();
    }

    std::uint32_t ReadU32(const std::uint8_t *data, std::size_t size,
                          std::size_t &cursor)
    {
        if (cursor + 4 > size)
            return 0;
        std::uint32_t value = 0;
        std::memcpy(&value, data + cursor, sizeof(value));
        cursor += sizeof(value);
        return value;
    }

    template <typename T>
    void HashBytes(std::uint64_t &hash, const T *data, std::size_t count)
    {
        const auto *bytes = reinterpret_cast<const std::uint8_t *>(data);
        const std::size_t byteCount = sizeof(T) * count;
        for (std::size_t i = 0; i < byteCount; ++i)
        {
            hash ^= bytes[i];
            hash *= 1099511628211ull;
        }
    }

    std::uint64_t Fingerprint(const ReplayLoadStaging &staged)
    {
        std::uint64_t hash = 1469598103934665603ull;
        const std::size_t sizes[] = {
            staged.ticks.size(), staged.subs.size(), staged.commands.size(),
            staged.movementExtras.size(), staged.offsets.size()};
        HashBytes(hash, sizes, std::size(sizes));
        HashBytes(hash, staged.ticks.data(), staged.ticks.size());
        HashBytes(hash, staged.subs.data(), staged.subs.size());
        HashBytes(hash, staged.commands.data(), staged.commands.size());
        HashBytes(hash, staged.movementExtras.data(), staged.movementExtras.size());
        HashBytes(hash, staged.offsets.data(), staged.offsets.size());
        return hash;
    }

    ReplayLoadStaging SentinelStaging()
    {
        ReplayLoadStaging staged;
        ReplayTick tick{};
        tick.weaponDefIndex = 777;
        tick.numSubtick = 0;
        staged.ticks.push_back(tick);
        staged.offsets = {0, 0};
        return staged;
    }

    void VerifyAcceptedLayout(const ReplayLoadStaging &staged)
    {
        if (staged.offsets.size() != staged.ticks.size() + 1 ||
            staged.offsets.empty() || staged.offsets.front() != 0 ||
            staged.offsets.back() != staged.subs.size())
        {
            Trap();
        }
        for (std::size_t i = 0; i < staged.ticks.size(); ++i)
        {
            std::size_t begin = 111;
            std::size_t end = 222;
            if (!TryGetReplaySubtickRange(
                    staged.ticks.data(), staged.ticks.size(), staged.offsets,
                    staged.subs.size(), i, begin, end) ||
                begin > end || end > staged.subs.size() ||
                end - begin != staged.ticks[i].numSubtick)
            {
                Trap();
            }
        }
    }
} // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, std::size_t size)
{
    if (!data || size < 2)
        return 0;

    std::size_t cursor = 0;
    const std::uint8_t flags = data[cursor++];
    const std::uint8_t countModes = data[cursor++];
    const std::size_t tickCount =
        std::min<std::size_t>(countModes % 17, 16);

    std::vector<ReplayTick> ticks(tickCount);
    for (std::size_t i = 0; i < tickCount; ++i)
    {
        ticks[i] = ReplayTick{};
        ticks[i].numSubtick = ReadU32(data, size, cursor);
    }

    const std::size_t safeSubCount =
        std::min<std::size_t>(ReadU32(data, size, cursor) % 577, 576);
    std::vector<SubtickMove> subs(safeSubCount);
    std::vector<ReplayCommandFrameData> commands(tickCount);
    std::vector<ReplayMovementExtra> extras(tickCount);

    ReplayTick dummyTick{};
    const ReplayTick *tickData = tickCount == 0 ? &dummyTick : ticks.data();
    const bool nullTicks = (flags & 0x01u) != 0;
    const bool nullSubs = (flags & 0x02u) != 0;
    const bool nullCommands = (flags & 0x04u) != 0;
    const bool nullExtras = (flags & 0x08u) != 0;
    const auto chooseParallelCount = [&](unsigned shift) {
        switch ((countModes >> shift) & 0x03u)
        {
        case 0:
            return static_cast<int>(tickCount);
        case 1:
            return 0;
        case 2:
            return static_cast<int>(tickCount) + 1;
        default:
            return -1;
        }
    };
    const int commandCount = (flags & 0x10u) != 0
                                 ? 0
                                 : chooseParallelCount(2);
    const int extraCount = (flags & 0x20u) != 0
                               ? 0
                               : chooseParallelCount(4);

    // Exercise the production raw pointer/count validator independently. The
    // advertised tick count never exceeds the backing allocation.
    std::vector<std::size_t> offsets{91, 92};
    const auto offsetSentinel = offsets;
    int advertisedTickCount = static_cast<int>(tickCount);
    if ((flags & 0x40u) != 0)
        advertisedTickCount = -1;
    else if ((countModes & 0x80u) != 0 && advertisedTickCount > 0)
        --advertisedTickCount;
    const int advertisedSubCount = (flags & 0x80u) != 0
                                       ? -1
                                       : static_cast<int>(safeSubCount);
    const bool layoutOk = TryBuildReplaySubtickOffsets(
        nullTicks ? nullptr : tickData,
        advertisedTickCount,
        advertisedSubCount, offsets);
    if (!layoutOk && offsets != offsetSentinel)
        Trap();
    if (layoutOk &&
        (offsets.size() != static_cast<std::size_t>(advertisedTickCount) + 1 ||
         offsets.back() != static_cast<std::size_t>(advertisedSubCount)))
    {
        Trap();
    }
    if (layoutOk)
    {
        for (int i = 0; i < advertisedTickCount; ++i)
        {
            std::size_t begin = 555;
            std::size_t end = 666;
            if (!TryGetReplaySubtickRange(
                    tickData, static_cast<std::size_t>(advertisedTickCount),
                    offsets, static_cast<std::size_t>(advertisedSubCount),
                    static_cast<std::size_t>(i), begin, end))
            {
                Trap();
            }
        }
    }

    ReplayLoadStaging staged = SentinelStaging();
    const std::uint64_t before = Fingerprint(staged);
    const bool stagedOk = TryStageReplayLoad(
        nullTicks ? nullptr : tickData,
        advertisedTickCount,
        nullSubs ? nullptr : (subs.empty() ? nullptr : subs.data()),
        advertisedSubCount,
        nullCommands ? nullptr : (commands.empty() ? nullptr : commands.data()),
        commandCount,
        nullExtras ? nullptr : (extras.empty() ? nullptr : extras.data()),
        extraCount,
        staged);
    if (!stagedOk)
    {
        if (Fingerprint(staged) != before)
            Trap();
        return 0;
    }

    VerifyAcceptedLayout(staged);

    // Mutate a valid table and exercise the production O(1) consumer guard.
    if (!staged.ticks.empty() && size > cursor)
    {
        std::vector<std::size_t> mutated = staged.offsets;
        const std::size_t position = data[cursor] % mutated.size();
        mutated[position] ^= static_cast<std::size_t>(data[size - 1]) + 1;
        const std::size_t tickIndex = data[size - 1] % staged.ticks.size();
        std::size_t begin = 333;
        std::size_t end = 444;
        const bool rangeOk = TryGetReplaySubtickRange(
            staged.ticks.data(), staged.ticks.size(), mutated,
            staged.subs.size(), tickIndex, begin, end);
        if (!rangeOk && (begin != 333 || end != 444))
            Trap();
        if (rangeOk &&
            (begin > end || end > staged.subs.size() ||
             end - begin != staged.ticks[tickIndex].numSubtick))
        {
            Trap();
        }
    }

    return 0;
}
