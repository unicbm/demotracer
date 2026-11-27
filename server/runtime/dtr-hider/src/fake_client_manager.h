// fake_client_manager.h

#pragma once

#include "ping_display.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>

namespace cs2bh
{

    struct ManagedSlot
    {
        PingJitter Jitter{50}; // 50ms baseline
        PingDisplay Display;
    };

    class FakeClientManager
    {
    public:
        FakeClientManager();

        static constexpr int kMaxSlots = 64;

        bool AdoptSlot(int slot, const char *pszName, uint64_t steamId64,
                       const char *crosshairCode, uint32_t scoreboardFlair);

        // Release a slot on disconnect / mapchange
        void ReleaseSlot(int slot);
        void ReleaseAll();

        void OnTick();

        // True if the slot has a managed bot bound
        bool IsManaged(int slot) const;

        uint64_t ManagedSlotMask() const { return m_ActiveSlots.load(std::memory_order_acquire); }
        bool HasManagedSlots() const { return ManagedSlotMask() != 0; }

    private:
        std::mutex m_Mutex;
        // Packing workers enumerate slots without borrowing any engine entities.
        static_assert(kMaxSlots == 64);
        std::atomic<uint64_t> m_ActiveSlots{0};
        std::array<ManagedSlot, kMaxSlots> m_Slots;
        uint64_t m_PingSeed;
    };

    FakeClientManager &Manager();

} // namespace cs2bh
