// fake_client_manager.h

#pragma once

#include "personas.h"
#include "ping_display.h"
#include "steamid_provider.h"

#include <array>
#include <cstdint>
#include <memory>
#include <mutex>

namespace cs2bh
{

    struct ManagedSlot
    {
        bool Active = false;
        uint64_t SyntheticSid = 0;
        uint32_t ScoreboardFlair = 0;
        PingJitter Jitter{50}; // 50ms baseline
        PingDisplay Display;
        bool SteamIdWritten = false;
    };

    class FakeClientManager
    {
    public:
        FakeClientManager();

        void Init();

        bool AdoptSlot(int slot, const char *pszName, uint64_t steamId64,
                       const char *crosshairCode, uint32_t scoreboardFlair);

        // Release a slot on disconnect / mapchange
        void ReleaseSlot(int slot);
        void ReleaseAll();

        void OnTick();

        // True if the slot has a managed bot bound
        bool IsManaged(int slot) const;

        uint64_t GetSyntheticSid(int slot) const;

        // Override the SteamID64
        void SetSyntheticSid(int slot, uint64_t sid);

        SteamIdProvider *SteamIds() { return m_pSteamIds.get(); }

    private:
        mutable std::mutex m_Mutex;
        std::array<ManagedSlot, PersonaPool::kMaxSlots> m_Slots;
        std::unique_ptr<SteamIdProvider> m_pSteamIds;
    };

    FakeClientManager &Manager();

} // namespace cs2bh
