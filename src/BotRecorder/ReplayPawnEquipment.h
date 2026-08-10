// Replay-owned pawn equipment initialization.

#pragma once

#include <cstdint>

namespace BotController::ReplayPawnEquipment
{
    constexpr int kMaxSlots = 64;

#pragma pack(push, 4)
    struct State
    {
        int32_t configured;
        int32_t pending;
        int32_t applied;
        int32_t expectedArmor;
        int32_t expectedHelmet;
        int32_t expectedDefuser;
        int32_t pawnArmor;
        int32_t controllerArmor;
        int32_t itemServicesHelmet;
        int32_t controllerHelmet;
        int32_t itemServicesDefuser;
        int32_t controllerDefuser;
    };
#pragma pack(pop)

    static_assert(sizeof(State) == 48);

    // Bind replay equipment to one validated pawn/controller identity. The
    // values are written immediately, at replay start, and once more from the
    // first movement/usercmd hook. They are not enforced after initialization,
    // so real combat damage remains authoritative.
    bool Set(int slot, void *pawn, void *controller,
             int armor, bool helmet, bool defuser);
    bool PrepareForReplayStart(int slot);
    bool ApplyPendingForPawn(int slot, void *pawn);
    bool Clear(int slot);
    void ClearAll();
    bool GetState(int slot, State &out);
}
