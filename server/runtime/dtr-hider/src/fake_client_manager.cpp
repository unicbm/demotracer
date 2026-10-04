// fake_client_manager.cpp

#include "fake_client_manager.h"
#include "presentation_state.h"

#include <chrono>

namespace cs2bh
{

    namespace
    {

        FakeClientManager g_Manager;

        // Rand
        uint64_t SimpleRand(uint64_t &state)
        {
            uint64_t x = state ? state : 0x9E3779B97F4A7C15ULL;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            state = x;
            return x * 0x2545F4914F6CDD1DULL;
        }

    } // namespace

    FakeClientManager &Manager() { return g_Manager; }

    FakeClientManager::FakeClientManager()
        : m_PingSeed(static_cast<uint64_t>(
              std::chrono::steady_clock::now().time_since_epoch().count())) {}

    bool FakeClientManager::AdoptSlot(int slot, const char *pszName,
                                      uint64_t steamId64, const char *crosshairCode,
                                      uint32_t scoreboardFlair)
    {
        if (slot < 0 || slot >= kMaxSlots || !Publisher().Active())
            return false;

        std::lock_guard<std::mutex> g(m_Mutex);
        auto &s = m_Slots[slot];

        // Per-bot baseline ping: 20 + (rand % 70) → [20, 90) ms
        uint64_t state = static_cast<uint64_t>(slot) ^ m_PingSeed;
        int baseline = 20 + static_cast<int>(SimpleRand(state) % 70);

        s.Active = true;
        s.Jitter = PingJitter(baseline);
        s.Display = PingDisplay{};

        // The caller has already resolved the base identity. Zero preserves
        // the engine bot SteamID when no configured persona was supplied.
        Publisher().PublishAdopt(slot, steamId64, pszName, crosshairCode, scoreboardFlair);
        Publisher().UpdatePing(slot, baseline);
        return true;
    }

    void FakeClientManager::ReleaseSlot(int slot)
    {
        if (slot < 0 || slot >= kMaxSlots)
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        m_Slots[slot].Active = false;
        m_Slots[slot].Display.Reset();
        Publisher().PublishRelease(slot);
    }

    void FakeClientManager::ReleaseAll()
    {
        std::lock_guard<std::mutex> g(m_Mutex);
        for (int i = 0; i < kMaxSlots; ++i)
        {
            m_Slots[i].Active = false;
            m_Slots[i].Display.Reset();
            Publisher().PublishRelease(i);
        }
    }

    void FakeClientManager::OnTick()
    {
        struct Pending
        {
            int slot;
            int ping;
        };
        Pending pending[kMaxSlots];
        int n = 0;
        {
            std::lock_guard<std::mutex> g(m_Mutex);
            for (int i = 0; i < kMaxSlots; ++i)
            {
                auto &s = m_Slots[i];
                if (!s.Active)
                    continue;
                s.Display.RecordSample(s.Jitter.NextSample());
                int produced = s.Display.MaybeProduce();
                if (produced >= 0)
                    pending[n++] = {i, produced};
            }
        }
        for (int i = 0; i < n; ++i)
            Publisher().UpdatePing(pending[i].slot, pending[i].ping);
    }

    bool FakeClientManager::IsManaged(int slot) const
    {
        if (slot < 0 || slot >= kMaxSlots)
            return false;
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_Slots[slot].Active;
    }

    bool FakeClientManager::HasManagedSlots() const
    {
        std::lock_guard<std::mutex> g(m_Mutex);
        for (const auto &slot : m_Slots)
            if (slot.Active) return true;
        return false;
    }

} // namespace cs2bh
