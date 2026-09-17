#include "presentation_state.h"
#include <algorithm>
#include <chrono>
#include <cstring>

namespace cs2bh
{
    namespace
    {
        SlotPublisher publisher;
        template <size_t N> void Copy(char (&to)[N], const char *from)
        {
            std::memset(to, 0, N);
            if (from) std::strncpy(to, from, N - 1);
        }
    }
    SlotPublisher &Publisher() { return publisher; }
    bool SlotPublisher::Init()
    {
        m_thread = std::this_thread::get_id();
        m_session = std::max(m_session + 1, static_cast<uint64_t>(
            std::chrono::steady_clock::now().time_since_epoch().count()));
        m_slots = {}; m_signatures = {}; m_signatureCount = 0; m_listener = nullptr;
        m_active = true;
        return true;
    }
    bool SlotPublisher::Active() const
    {
        return std::this_thread::get_id() == m_thread && m_active;
    }
    bool SlotPublisher::Listen(uint64_t session, PresentationChanged listener)
    {
        if (!Active() || session != m_session) return false;
        m_listener = listener;
        return true;
    }
    void SlotPublisher::Shutdown()
    {
        m_active = false; m_slots = {};
        auto listener = m_listener; m_listener = nullptr;
        if (listener) listener(kPresentationRosterChanged, -1);
    }
    bool SlotPublisher::ReadSlot(int slot, PresentationSlot &out) const
    {
        if (!Active() || slot < 0 || slot >= 64) return false;
        out = m_slots[slot]; out.Session = m_session;
        return true;
    }
    bool SlotPublisher::Matches(int slot, uint64_t session, uint64_t incarnation) const
    {
        return Active() && session == m_session && incarnation != 0 && slot >= 0 && slot < 64 &&
            m_slots[slot].Managed && m_slots[slot].Incarnation == incarnation;
    }
    void SlotPublisher::PublishAdopt(int slot, uint64_t sid, const char *name, const char *crosshair, uint32_t flair)
    {
        if (!Active() || slot < 0 || slot >= 64) return;
        auto &s = m_slots[slot]; s = {};
        s.Session = m_session; s.Incarnation = ++m_nextIncarnation;
        s.BaseSteamId = s.SteamId = sid; s.Managed = 1; s.ScoreboardFlair = flair;
        Copy(s.BaseName, name); Copy(s.Name, name); Copy(s.Crosshair, crosshair);
        Notify(kPresentationRosterChanged, slot);
    }
    void SlotPublisher::PublishRelease(int slot)
    {
        if (Active() && slot >= 0 && slot < 64 && m_slots[slot].Managed)
        {
            m_slots[slot] = {};
            Notify(kPresentationRosterChanged, slot);
        }
    }
    void SlotPublisher::UpdateSyntheticSid(int slot, uint64_t sid)
    {
        if (Active() && slot >= 0 && slot < 64) m_slots[slot].SteamId = sid;
    }
    void SlotPublisher::UpdatePersonaName(int slot, const char *name)
    {
        if (Active() && slot >= 0 && slot < 64) Copy(m_slots[slot].Name, name);
    }
    void SlotPublisher::UpdatePing(int slot, int ping)
    {
        if (Active() && slot >= 0 && slot < 64 && m_slots[slot].Ping != ping)
        {
            m_slots[slot].Ping = ping;
            Notify(kPresentationPingChanged, slot);
        }
    }
    void SlotPublisher::PublishSignature(const char *name, const void *address)
    {
        if (!Active() || m_signatureCount >= 8) return;
        auto &s = m_signatures[m_signatureCount++]; Copy(s.Name, name);
        s.Address = reinterpret_cast<uint64_t>(address);
    }
    bool SlotPublisher::ReadSignature(int index, PresentationSignature &out) const
    {
        if (!Active() || index < 0 || index >= m_signatureCount) return false;
        out = m_signatures[index]; return true;
    }
}
