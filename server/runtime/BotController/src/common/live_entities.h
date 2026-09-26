#pragma once
#include <cstdint>
#include <nlohmann/json.hpp>

namespace BotController::LiveEntities
{
    bool Init(void *resourceService, const nlohmann::json &gamedata);
    void Reset();
    void *FromHandle(uint32_t handle);
    // Returns the complete current entity handle, or zero for stale storage.
    uint32_t HandleForEntity(const void *entity);
    // Current controller/pawn relationship, including entity serials and takeover.
    void *PawnForSlot(int slot);
    void *BotPawnForSlot(int slot);
    void *BotForSlot(int slot);
}
