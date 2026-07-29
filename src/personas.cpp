// personas.cpp

#include "personas.h"

#include "bot_info.h"

#include <chrono>
#include <utility>

namespace cs2bh
{

    namespace
    {

        PersonaPool g_PersonaPool;

    } // namespace

    PersonaPool &Personas() { return g_PersonaPool; }

    PersonaPool::PersonaPool()
    {
        m_RosterRngState =
            static_cast<uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count()) ^ 0x9E3779B97F4A7C15ULL;
    }

    void PersonaPool::Push(const char *name)
    {
        if (!name || !name[0])
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        for (const auto &q : m_Fifo)
            if (q == name)
                return; // dedupe
        m_Fifo.emplace_back(name);
    }

    std::string PersonaPool::Pop()
    {
        std::lock_guard<std::mutex> g(m_Mutex);
        if (m_Fifo.empty())
            return {};
        auto s = std::move(m_Fifo.front());
        m_Fifo.pop_front();
        return s;
    }

    size_t PersonaPool::PendingCount() const
    {
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_Fifo.size();
    }

    std::string PersonaPool::PickFromRoster()
    {
        const auto &entries = BotInfo().All();
        if (entries.empty())
            return {};
        std::lock_guard<std::mutex> g(m_Mutex);
        uint64_t x = m_RosterRngState;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        m_RosterRngState = x;
        return entries[x % entries.size()].Name;
    }

    void PersonaPool::MarkSlotManaged(int slot, const char *name)
    {
        if (slot < 0 || slot >= kMaxSlots)
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        m_SlotManaged[slot] = true;
        m_SlotNames[slot].assign(name ? name : "");
    }

    void PersonaPool::ClearSlot(int slot)
    {
        if (slot < 0 || slot >= kMaxSlots)
            return;
        std::lock_guard<std::mutex> g(m_Mutex);
        m_SlotManaged[slot] = false;
        m_SlotNames[slot].clear();
    }

    bool PersonaPool::IsSlotManaged(int slot) const
    {
        if (slot < 0 || slot >= kMaxSlots)
            return false;
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_SlotManaged[slot];
    }

    std::string PersonaPool::GetSlotName(int slot) const
    {
        if (slot < 0 || slot >= kMaxSlots)
            return {};
        std::lock_guard<std::mutex> g(m_Mutex);
        return m_SlotNames[slot];
    }

} // namespace cs2bh
