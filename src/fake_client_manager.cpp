// fake_client_manager.cpp

#include "fake_client_manager.h"
#include "slot_publisher.h"

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

    FakeClientManager::FakeClientManager() = default;

    void FakeClientManager::Init()
    {
        if (m_pSteamIds)
            return;

        char sessionId[32];
        auto nowNs = std::chrono::system_clock::now().time_since_epoch().count();
        std::snprintf(sessionId, sizeof(sessionId), "%lld", static_cast<long long>(nowNs));

        m_pSteamIds = std::make_unique<SteamIdProvider>(sessionId);
    }

    bool FakeClientManager::AdoptSlot(int slot, const char *pszName,
                                      uint64_t steamId64, const char *crosshairCode,
                                      uint32_t scoreboardFlair)
    {
        if (slot < 0 || slot >= PersonaPool::kMaxSlots)
            return false;
        if (!m_pSteamIds)
            return false;

        std::lock_guard<std::mutex> g(m_Mutex);
        auto &s = m_Slots[slot];

        // Per-bot baseline ping: 20 + (rand % 70) → [20, 90) ms
        uint64_t state = static_cast<uint64_t>(slot) ^ m_pSteamIds->Generate(slot);
        int baseline = 20 + static_cast<int>(SimpleRand(state) % 70);

        s.Active = true;
        // Prefer the bot_info.json id
        s.SyntheticSid = steamId64 != 0 ? steamId64 : m_pSteamIds->Generate(slot);
        s.ScoreboardFlair = scoreboardFlair;
        s.Jitter = PingJitter(baseline);
        s.Display = PingDisplay{};
        s.SteamIdWritten = false;

        Personas().MarkSlotManaged(slot, pszName);
        Publisher().PublishAdopt(slot, s.SyntheticSid, pszName, crosshairCode, s.ScoreboardFlair);
        Publisher().UpdatePing(slot, baseline);
        return true;
    }

    void FakeClientManager::ReleaseSlot(int slot)
    {
        if (slot < 0 || slot >= PersonaPool::kMaxSlots)
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        m_Slots[slot].Active = false;
        m_Slots[slot].SteamIdWritten = false;
        m_Slots[slot].ScoreboardFlair = 0;
        m_Slots[slot].Display.Reset();
        Personas().ClearSlot(slot);
        Publisher().PublishRelease(slot);
    }

    void FakeClientManager::ReleaseAll()
    {
        std::lock_guard<std::mutex> g(m_Mutex);
        for (int i = 0; i < PersonaPool::kMaxSlots; ++i)
        {
            m_Slots[i].Active = false;
            m_Slots[i].SteamIdWritten = false;
            m_Slots[i].ScoreboardFlair = 0;
            m_Slots[i].Display.Reset();
            Personas().ClearSlot(i);
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
        Pending pending[PersonaPool::kMaxSlots];
        int n = 0;
        {
            std::lock_guard<std::mutex> g(m_Mutex);
            for (int i = 0; i < PersonaPool::kMaxSlots; ++i)
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
        if (slot < 0 || slot >= PersonaPool::kMaxSlots)
            return false;
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_Slots[slot].Active;
    }

    uint64_t FakeClientManager::GetSyntheticSid(int slot) const
    {
        if (slot < 0 || slot >= PersonaPool::kMaxSlots)
            return 0;
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_Slots[slot].SyntheticSid;
    }

    void FakeClientManager::SetSyntheticSid(int slot, uint64_t sid)
    {
        if (slot < 0 || slot >= PersonaPool::kMaxSlots)
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        m_Slots[slot].SyntheticSid = sid;
    }

} // namespace cs2bh
