#include "ReplayNetworkState.h"
#include "version_targets.h"
#include <networksystem/networkvar.h>

namespace BotController::ReplayNetworkState
{
    void EntityFieldChanged(void *entity, int fieldOffset)
    {
        NetworkStateChangedData change(static_cast<uint32>(fieldOffset));
        static_cast<CEntityInstance *>(entity)->NetworkStateChanged(change);
    }

    void PawnComponentFieldChanged(void *component, int fieldOffset)
    {
        auto *chain = reinterpret_cast<CNetworkVarChainer *>(
            static_cast<char *>(component) + targets::kPawnComponent_ChainEntity);
        NetworkStateChangedData change(static_cast<uint32>(fieldOffset));
        chain->NetworkStateChanged(change);
    }
}
