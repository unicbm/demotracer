// Per-slot bot lock flags.

#pragma once

namespace BotController
{
    namespace BotControllerState
    {
        constexpr int kMaxSlots = 64;

        // All lock: Update + Upkeep.
        bool GetAll(int slot);
        void SetAll(int slot, bool locked);
        void ClearAllAll();
        int CountAll();

        // Aim lock: Upkeep only.
        bool GetAim(int slot);
        void SetAim(int slot, bool locked);
        void ClearAllAim();
        int CountAim();

        // Jump lock: Jump only.
        bool GetJump(int slot);
        void SetJump(int slot, bool locked);
        void ClearAllJump();
        int CountJump();
    }
}
