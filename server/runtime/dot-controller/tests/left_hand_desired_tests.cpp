#include "LeftHandDesiredLatch.h"

#include <cstdio>
#include <cstdlib>

using BotController::LeftHandDesiredLatch;

static void Check(bool value, const char *message)
{
    if (value) return;
    std::fprintf(stderr, "FAIL: %s\n", message);
    std::exit(1);
}

int main()
{
    constexpr uintptr_t pawn = 0x1000;
    constexpr uint32_t handle = 0x8005;
    LeftHandDesiredLatch latch;
    bool desired = false;

    // Command-only replay starts from the native right-hand preference.
    latch.Set(pawn, handle, false);
    latch.ObserveReplayCommand(true);
    Check(latch.Get(desired) && desired, "demo command replaces initial preference");
    // No replay command is supplied after handoff or natural finish. The next
    // native command still desires left, even if bot AI defaults to right.
    for (int command = 0; command < 128; ++command)
    {
        Check(latch.Validate(pawn, handle, true), "same living autonomous pawn");
        Check(latch.Get(desired) && desired, "first and later handoff commands retain left");
    }
    latch.ObserveReplayCommand(false);
    Check(latch.Get(desired) && !desired, "real replay right-hand switch remains authoritative");
    latch.ObserveReplayCommand(true);
    Check(latch.Get(desired) && desired, "real replay left-hand switch remains authoritative");

    Check(!latch.Validate(pawn, handle + 0x8000, true), "same address reused with new entity serial expires");
    Check(!latch.Get(desired), "expired serial cannot leak the preference");
    latch.Set(pawn, handle, true);
    Check(!latch.Validate(pawn + 0x1000, handle, true), "new pawn expires");
    latch.Set(pawn, handle, true);
    Check(!latch.Validate(pawn, handle, false), "death or human ownership expires");
    Check(!latch.Validate(pawn, handle, true), "returning to AI does not resurrect old preference");
    latch.Set(pawn, handle, true);
    latch.Clear();
    latch.ObserveReplayCommand(true);
    Check(!latch.Get(desired), "explicit cleanup or disabled fidelity stays cleared");
    latch.Set(pawn, handle, false);
    Check(latch.Get(desired) && !desired, "new replay starts with its own preference");
    std::puts("Left-hand command continuity and ownership tests passed");
}
