// fake_client_manager.h

#pragma once

#include "ping_display.h"

#include <array>
#include <cstdint>
#include <mutex>

namespace cs2bh
{

    struct ManagedSlot
    {
        bool Active = false;
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

        bool HasManagedSlots() const;

    private:
        mutable std::mutex m_Mutex;
        // PackEntities may query Active from a worker thread. Identity itself
        // lives only in the main-thread SlotPublisher; keep this lock for the
        // worker-visible managed set and ping state.
        std::array<ManagedSlot, kMaxSlots> m_Slots;
        uint64_t m_PingSeed;
    };

    FakeClientManager &Manager();

} // namespace cs2bh
