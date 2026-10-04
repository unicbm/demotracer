#include "ReplaySubtickLayout.h"

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <vector>

namespace
{
    using BotController::ReplayCommandFrameData;
    using BotController::ReplayMovementExtra;
    using BotController::ReplayInputHistoryEntry;
    using BotController::ReplayInputHistoryTick;
    using BotController::ReplaySubtickLayout::ProjectSubtickWhenForEngine;
    using BotController::ReplaySubtickLayout::ReplayLoadStaging;
    using BotController::ReplaySubtickLayout::TryBuildReplaySubtickOffsets;
    using BotController::ReplaySubtickLayout::TryGetReplaySubtickRange;
    using BotController::ReplaySubtickLayout::TryStageReplayLoad;
    using BotController::ReplayTick;
    using BotController::SubtickMove;

    [[noreturn]] void Fail(const char *message)
    {
        std::fprintf(stderr, "FAIL: %s\n", message);
        std::exit(1);
    }

    void Check(bool condition, const char *message)
    {
        if (!condition)
            Fail(message);
    }

    ReplayTick Tick(std::uint32_t subticks, int weapon = -1)
    {
        ReplayTick tick{};
        tick.weaponDefIndex = weapon;
        tick.numSubtick = subticks;
        return tick;
    }

    SubtickMove Sub(float when)
    {
        SubtickMove sub{};
        sub.when = when;
        return sub;
    }

    ReplayCommandFrameData Command(float forward)
    {
        ReplayCommandFrameData command{};
        command.forwardMove = forward;
        command.fields = BotController::MotionRecorder::kCommandFieldForwardMove;
        return command;
    }

    ReplayMovementExtra Extra(float jumpPressedTime)
    {
        ReplayMovementExtra extra{};
        extra.jumpPressedTime = jumpPressedTime;
        return extra;
    }

    void CheckStagingA(const ReplayLoadStaging &staged)
    {
        Check(staged.ticks.size() == 2, "staged tick count changed");
        Check(staged.ticks[0].numSubtick == 1, "staged tick layout changed");
        Check(staged.ticks[1].weaponDefIndex == 22, "staged tick payload changed");
        Check(staged.subs.size() == 1 && staged.subs[0].when == 0.25f,
              "staged subticks changed");
        Check(staged.commands.size() == 2 && staged.commands[1].forwardMove == 2.0f,
              "staged commands changed");
        Check(staged.offsets == std::vector<std::size_t>({0, 1, 1}),
              "staged offsets changed");
    }

    void TestValidOffsetsAndRanges()
    {
        const std::vector<ReplayTick> ticks{Tick(0), Tick(36), Tick(1)};
        std::vector<std::size_t> offsets{999};
        Check(TryBuildReplaySubtickOffsets(
                  ticks.data(), static_cast<int>(ticks.size()), 37, offsets),
              "valid layout rejected");
        Check(offsets == std::vector<std::size_t>({0, 0, 36, 37}),
              "valid offsets are wrong");

        std::size_t begin = 123;
        std::size_t end = 456;
        Check(TryGetReplaySubtickRange(
                  ticks.data(), ticks.size(), offsets, 37, 1, begin, end),
              "valid range rejected");
        Check(begin == 0 && end == 36, "valid range is wrong");
    }

    void TestEngineWhenProjection()
    {
        Check(ProjectSubtickWhenForEngine(-1.671875f) == 0.0f,
              "backdated subtick phase reached the engine unchanged");
        Check(ProjectSubtickWhenForEngine(-0.0f) == 0.0f,
              "negative zero subtick phase was not normalized");
        Check(ProjectSubtickWhenForEngine(0.0f) == 0.0f,
              "zero subtick phase changed");
        Check(ProjectSubtickWhenForEngine(0.984375f) == 0.984375f,
              "valid positive subtick phase changed");
    }

    void TestZeroTickLayout()
    {
        ReplayTick dummy = Tick(0);
        std::vector<std::size_t> offsets{999};
        Check(TryBuildReplaySubtickOffsets(&dummy, 0, 0, offsets),
              "zero tick/subtick layout rejected");
        Check(offsets == std::vector<std::size_t>({0}),
              "zero tick/subtick offsets are wrong");

        ReplayLoadStaging staged;
        Check(TryStageReplayLoad(
                  &dummy, 0, nullptr, 0, nullptr, 0, nullptr, 0, staged),
              "zero tick/subtick staging rejected");
        Check(staged.ticks.empty() && staged.subs.empty() &&
                  staged.commands.empty() &&
                  staged.offsets == std::vector<std::size_t>({0}),
              "zero tick/subtick staging is wrong");
    }

    void TestInvalidBuildPreservesOutput()
    {
        const std::vector<std::size_t> sentinel{7, 8, 9};
        const auto checkRejected = [&](const std::vector<ReplayTick> &ticks,
                                       int subCount,
                                       const char *message) {
            std::vector<std::size_t> offsets = sentinel;
            Check(!TryBuildReplaySubtickOffsets(
                      ticks.data(), static_cast<int>(ticks.size()), subCount, offsets),
                  message);
            Check(offsets == sentinel, "rejected build changed output");
        };

        checkRejected({Tick(100), Tick(1)}, 1,
                      "review regression layout accepted");
        checkRejected({Tick(37)}, 37, "37 subticks accepted");
        checkRejected({Tick(std::numeric_limits<std::uint32_t>::max())}, 0,
                      "UINT32_MAX subticks accepted");
        checkRejected({Tick(1), Tick(1)}, 1, "sum greater than subCount accepted");
        checkRejected({Tick(1)}, 2, "sum less than subCount accepted");

        ReplayTick dummy = Tick(0);
        std::vector<std::size_t> offsets = sentinel;
        Check(!TryBuildReplaySubtickOffsets(nullptr, 1, 0, offsets),
              "null tick pointer accepted");
        Check(offsets == sentinel, "null input changed output");
        Check(!TryBuildReplaySubtickOffsets(&dummy, -1, 0, offsets),
              "negative tick count accepted");
        Check(offsets == sentinel, "negative tick count changed output");
        Check(!TryBuildReplaySubtickOffsets(&dummy, 0, -1, offsets),
              "negative subtick count accepted");
        Check(offsets == sentinel, "negative subtick count changed output");
    }

    void TestInvalidRangesPreserveOutput()
    {
        const std::vector<ReplayTick> ticks{Tick(1), Tick(1)};
        const auto checkRejected = [&](const std::vector<std::size_t> &offsets,
                                       std::size_t subCount,
                                       std::size_t tickIndex,
                                       const char *message) {
            std::size_t begin = 123;
            std::size_t end = 456;
            Check(!TryGetReplaySubtickRange(
                      ticks.data(), ticks.size(), offsets, subCount,
                      tickIndex, begin, end),
                  message);
            Check(begin == 123 && end == 456,
                  "rejected range changed output");
        };

        checkRejected({}, 2, 0, "empty offsets accepted");
        checkRejected({0, 1}, 2, 0, "wrong offset count accepted");
        checkRejected({1, 1, 2}, 2, 0, "non-zero first offset accepted");
        checkRejected({0, 1, 1}, 2, 0, "wrong final offset accepted");
        checkRejected({0, 2, 1}, 1, 0, "offset past subtick end accepted");
        checkRejected({0, 0, 2}, 2, 0, "tick delta mismatch accepted");
        checkRejected({0, 1, 2}, 2, 2, "out-of-range tick accepted");

        const std::vector<ReplayTick> descendingTicks{
            Tick(1), Tick(1), Tick(0)};
        std::size_t descendingBegin = 123;
        std::size_t descendingEnd = 456;
        Check(!TryGetReplaySubtickRange(
                  descendingTicks.data(), descendingTicks.size(),
                  {0, 2, 1, 2}, 2, 1,
                  descendingBegin, descendingEnd),
              "descending begin/end range accepted");
        Check(descendingBegin == 123 && descendingEnd == 456,
              "descending range changed output");

        const std::vector<ReplayTick> tooMany{Tick(37)};
        std::size_t begin = 123;
        std::size_t end = 456;
        Check(!TryGetReplaySubtickRange(
                  tooMany.data(), tooMany.size(), {0, 37}, 37, 0, begin, end),
              "range accepted per-tick count above 36");
        Check(begin == 123 && end == 456,
              "rejected high-count range changed output");
    }

    void TestTransactionalStaging()
    {
        const std::vector<ReplayTick> ticksA{Tick(1, 11), Tick(0, 22)};
        const std::vector<SubtickMove> subsA{Sub(0.25f)};
        const std::vector<ReplayCommandFrameData> commandsA{
            Command(1.0f), Command(2.0f)};
        const std::vector<ReplayMovementExtra> extrasA{
            Extra(3.0f), Extra(4.0f)};

        ReplayLoadStaging staged;
        Check(TryStageReplayLoad(
                  ticksA.data(), static_cast<int>(ticksA.size()),
                  subsA.data(), static_cast<int>(subsA.size()),
                  commandsA.data(), static_cast<int>(commandsA.size()),
                  extrasA.data(), static_cast<int>(extrasA.size()), staged),
              "valid replay staging failed");
        CheckStagingA(staged);

        auto invalidExtras = extrasA;
        invalidExtras[0].jumpPressedTime = std::numeric_limits<float>::quiet_NaN();
        Check(!TryStageReplayLoad(
                  ticksA.data(), static_cast<int>(ticksA.size()),
                  subsA.data(), static_cast<int>(subsA.size()),
                  commandsA.data(), static_cast<int>(commandsA.size()),
                  invalidExtras.data(), static_cast<int>(invalidExtras.size()), staged),
              "legacy movement extras must still be validated even though they are not retained");
        CheckStagingA(staged);

        const std::vector<ReplayTick> invalidTicks{Tick(100), Tick(1)};
        const std::vector<SubtickMove> invalidSubs{Sub(0.75f)};
        Check(!TryStageReplayLoad(
                  invalidTicks.data(), static_cast<int>(invalidTicks.size()),
                  invalidSubs.data(), static_cast<int>(invalidSubs.size()),
                  commandsA.data(), static_cast<int>(commandsA.size()),
                  extrasA.data(), static_cast<int>(extrasA.size()), staged),
              "invalid replay staging succeeded");
        CheckStagingA(staged);

        Check(!TryStageReplayLoad(
                  ticksA.data(), static_cast<int>(ticksA.size()),
                  subsA.data(), static_cast<int>(subsA.size()),
                  commandsA.data(), 1,
                  extrasA.data(), static_cast<int>(extrasA.size()), staged),
              "mismatched command count accepted");
        CheckStagingA(staged);

        Check(!TryStageReplayLoad(
                  ticksA.data(), static_cast<int>(ticksA.size()),
                  subsA.data(), static_cast<int>(subsA.size()),
                  commandsA.data(), static_cast<int>(commandsA.size()),
                  nullptr, static_cast<int>(extrasA.size()), staged),
              "null movement extras accepted");
        CheckStagingA(staged);
    }

    void TestSemanticValidationPreservesStaging()
    {
        const std::vector<ReplayTick> validTicks{Tick(1, 11), Tick(0, 22)};
        const std::vector<SubtickMove> validSubs{Sub(0.25f)};
        const std::vector<ReplayCommandFrameData> validCommands{
            Command(1.0f), Command(2.0f)};
        const std::vector<ReplayMovementExtra> validExtras{
            Extra(3.0f), Extra(4.0f)};
        ReplayLoadStaging staged;
        Check(TryStageReplayLoad(
                  validTicks.data(), static_cast<int>(validTicks.size()),
                  validSubs.data(), static_cast<int>(validSubs.size()),
                  validCommands.data(), static_cast<int>(validCommands.size()),
                  validExtras.data(), static_cast<int>(validExtras.size()), staged),
              "valid semantic baseline rejected");
        CheckStagingA(staged);

        auto invalidTicks = validTicks;
        invalidTicks[0].pre.originX = std::numeric_limits<float>::quiet_NaN();
        Check(!TryStageReplayLoad(
                  invalidTicks.data(), static_cast<int>(invalidTicks.size()),
                  validSubs.data(), static_cast<int>(validSubs.size()),
                  validCommands.data(), static_cast<int>(validCommands.size()),
                  validExtras.data(), static_cast<int>(validExtras.size()), staged),
              "non-finite snapshot accepted");
        CheckStagingA(staged);

        auto invalidSubs = validSubs;
        invalidSubs[0].when = 1.0f;
        Check(!TryStageReplayLoad(
                  validTicks.data(), static_cast<int>(validTicks.size()),
                  invalidSubs.data(), static_cast<int>(invalidSubs.size()),
                  validCommands.data(), static_cast<int>(validCommands.size()),
                  validExtras.data(), static_cast<int>(validExtras.size()), staged),
              "out-of-range subtick time accepted");
        CheckStagingA(staged);

        const std::vector<SubtickMove> backdatedSubs{Sub(-1.671875f)};
        ReplayLoadStaging backdatedStaged;
        Check(TryStageReplayLoad(
                  validTicks.data(), static_cast<int>(validTicks.size()),
                  backdatedSubs.data(), static_cast<int>(backdatedSubs.size()),
                  validCommands.data(), static_cast<int>(validCommands.size()),
                  validExtras.data(), static_cast<int>(validExtras.size()), backdatedStaged),
              "backdated engine subtick time rejected");
        Check(backdatedStaged.subs.size() == 1,
              "backdated subtick staging count changed");
        Check(backdatedStaged.subs[0].when == -1.671875f,
              "backdated subtick time changed during staging");

        auto invalidCommands = validCommands;
        invalidCommands[0].fields = 1u << 31;
        Check(!TryStageReplayLoad(
                  validTicks.data(), static_cast<int>(validTicks.size()),
                  validSubs.data(), static_cast<int>(validSubs.size()),
                  invalidCommands.data(), static_cast<int>(invalidCommands.size()),
                  validExtras.data(), static_cast<int>(validExtras.size()), staged),
              "unknown command field accepted");
        CheckStagingA(staged);
    }

    void TestInputHistoryStaging()
    {
        const std::vector<ReplayTick> ticks{Tick(0, 7), Tick(0, 7)};
        const std::vector<ReplayInputHistoryTick> historyTicks{
            ReplayInputHistoryTick{100, 0, -1, 1},
            ReplayInputHistoryTick{101, -1, -1, 0}};
        ReplayInputHistoryEntry entry{};
        entry.fields = (1u << 1) | (1u << 2);
        entry.renderTickCount = 99;
        entry.renderTickFraction = 0.75f;
        const std::vector<ReplayInputHistoryEntry> entries{entry};

        ReplayLoadStaging staged;
        Check(TryStageReplayLoad(
                  ticks.data(), static_cast<int>(ticks.size()),
                  nullptr, 0, nullptr, 0, nullptr, 0,
                  historyTicks.data(), static_cast<int>(historyTicks.size()),
                  entries.data(), static_cast<int>(entries.size()), staged),
              "valid input history rejected");
        Check(staged.ticks.size() == 2 && staged.ticks[0].weaponDefIndex == 7 &&
                  staged.offsets == std::vector<std::size_t>({0, 0, 0}),
              "legacy history must not change the supported replay payload");

        auto invalidTicks = historyTicks;
        invalidTicks[0].attack1StartHistoryIndex = 1;
        Check(!TryStageReplayLoad(
                  ticks.data(), static_cast<int>(ticks.size()),
                  nullptr, 0, nullptr, 0, nullptr, 0,
                  invalidTicks.data(), static_cast<int>(invalidTicks.size()),
                  entries.data(), static_cast<int>(entries.size()), staged),
              "out-of-range attack history index accepted");
        Check(staged.ticks.size() == 2 && staged.ticks[0].weaponDefIndex == 7 &&
                  staged.offsets == std::vector<std::size_t>({0, 0, 0}),
              "rejected input history changed staging");
    }
} // namespace

static void TestReservedEventTailIsRejectedTransactionally()
{
    std::vector<ReplayTick> ticks{Tick(0, 7), Tick(0, 7)};
    ReplayLoadStaging staged;
    auto load = [&] { return TryStageReplayLoad(ticks.data(), 2, nullptr, 0, nullptr, 0, nullptr, 0, staged); };
    Check(load(), "zero reserved event tail rejected");
    // Each 4-byte field of the ABI's reserved tail must independently reject
    // nonzero data; flags=0 cannot conceal a partial/legacy drop payload.
    for (std::size_t offset = offsetof(ReplayTick, eventFlags); offset < sizeof(ReplayTick); offset += 4)
    {
        ticks[0] = Tick(0, 42);
        const std::uint32_t nonzero = 1;
        std::memcpy(reinterpret_cast<unsigned char *>(&ticks[0]) + offset, &nonzero, sizeof(nonzero));
        Check(!load(), "unsupported native event payload accepted");
        Check(staged.ticks.size() == 2 && staged.ticks[0].weaponDefIndex == 7 && staged.ticks[0].eventFlags == 0,
              "rejected native event replaced previous staging");
    }
    ticks[0] = Tick(0, 42);
    ticks[0].eventDropVelocityZ = std::numeric_limits<float>::quiet_NaN();
    Check(!load(), "NaN accepted as an empty reserved event tail");
    ticks[0] = Tick(0, 7);
    Check(load(), "valid replay rejected after an unsupported event load");
}

int main()
{
    TestReservedEventTailIsRejectedTransactionally();
    TestValidOffsetsAndRanges();
    TestEngineWhenProjection();
    TestZeroTickLayout();
    TestInvalidBuildPreservesOutput();
    TestInvalidRangesPreserveOutput();
    TestTransactionalStaging();
    TestSemanticValidationPreservesStaging();
    TestInputHistoryStaging();
    std::puts("BotController replay subtick safety tests passed");
    return 0;
}
