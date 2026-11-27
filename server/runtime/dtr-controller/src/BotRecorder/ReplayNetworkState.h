#pragma once

namespace BotController::ReplayNetworkState
{
    void EntityFieldChanged(void *entity, int fieldOffset);
    void PawnComponentFieldChanged(void *component, int fieldOffset);
}
