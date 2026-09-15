#pragma once
#include <cstdint>
#include <nlohmann/json.hpp>

namespace BotController::LiveEntities
{
    bool Init(void *resourceService, const nlohmann::json &gamedata);
    void Reset();
    void *FromHandle(uint32_t handle);
    // Current controller/pawn relationship, including entity serials and takeover.
    void *PawnForSlot(int slot);
    void *BotForSlot(int slot);
}
