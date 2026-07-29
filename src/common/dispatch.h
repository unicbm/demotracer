// Unified lock dispatch. Per-slot lock kinds.

#pragma once

#include "WeaponLockerState.h"

class IVEngineServer2;
class ISource2GameClients;

namespace BotController
{
    // Mirror BotControllerApi.LockKind on the C# side.
    enum class LockKind : int
    {
        All = 0,
        Aim = 1,
        Weapon = 2,
        Jump = 3,
    };

    namespace Dispatch
    {
        extern IVEngineServer2 *g_pEngine;
        // Server-side command executor used for issuing bot "buy" commands.
        extern ISource2GameClients *g_pGameClients;

        // arg = LockTarget int for Weapon kind. quiet skips DebugLine.
        int Lock(int slot, LockKind kind, int arg, bool quiet = false);

        int Unlock(int slot, LockKind kind, bool quiet = false);

        int UnlockAll(LockKind kind, bool quiet = false);

        // 1 if All/Aim/Jump locked; Weapon returns LockTarget int.
        int IsLocked(int slot, LockKind kind);
    }
}
