// Per-slot lock target table.

#include "WeaponLockerState.h"

#include <array>
#include <atomic>

namespace BotController
{
    namespace WeaponLockerState
    {
        static std::array<std::atomic<int>, kMaxSlots> g_locks{};

        LockTarget Get(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return LockTarget::None;
            return static_cast<LockTarget>(g_locks[slot].load(std::memory_order_relaxed));
        }

        void Set(int slot, LockTarget tgt)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return;
            g_locks[slot].store(static_cast<int>(tgt), std::memory_order_relaxed);
        }

        void Clear(int slot)
        {
            Set(slot, LockTarget::None);
        }

        void ClearAll()
        {
            for (auto &x : g_locks)
                x.store(0, std::memory_order_relaxed);
        }

        int CountLocked()
        {
            int n = 0;
            for (auto &x : g_locks)
                if (x.load(std::memory_order_relaxed) != 0)
                    ++n;
            return n;
        }
    }
}
