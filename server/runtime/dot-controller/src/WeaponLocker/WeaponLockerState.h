// Per-slot lock target table

#pragma once

namespace BotController
{
    // Engine weapon slots
    // Slot1 = primary
    // Slot2 = pistol
    // Slot3 = knife/zeus
    // Slot4 = grenades (he/flash/smoke/molotov/decoy)
    // Slot5 = C4
    enum class LockTarget : int
    {
        None = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    };

    namespace WeaponLockerState
    {
        constexpr int kMaxSlots = 64;

        LockTarget Get(int slot);
        void Set(int slot, LockTarget tgt);
        void Clear(int slot);
        void ClearAll();

        // Returns count of currently locked slots.
        int CountLocked();
    }
}
