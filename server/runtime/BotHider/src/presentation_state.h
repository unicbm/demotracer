#pragma once
#include <array>
#include <cstdint>
#include <thread>

namespace cs2bh
{
    inline constexpr int kNativePresentationAbi = 3;
    using PresentationChanged = void (*)(uint32_t reason, int slot);
    inline constexpr uint32_t kPresentationRosterChanged = 1, kPresentationPingChanged = 2;
#pragma pack(push, 4)
    struct PresentationSlot
    {
        uint64_t Session = 0, Incarnation = 0, BaseSteamId = 0, SteamId = 0;
        int32_t Managed = 0, Ping = 0;
        uint32_t ScoreboardFlair = 0;
        char BaseName[32]{}, Name[32]{}, Crosshair[64]{};
    };
    struct PresentationSignature { char Name[32]{}; uint64_t Address = 0; };
#pragma pack(pop)
    static_assert(sizeof(PresentationSlot) == 172);
    static_assert(sizeof(PresentationSignature) == 40);

    // In-process state; no shared mapping, queue or deferred identity writes.
    // Engine access is restricted to the thread that loaded the plugin.
    class SlotPublisher
    {
    public:
        bool Init();
        void Shutdown();
        bool Active() const;
        bool Listen(uint64_t session, PresentationChanged listener);
        uint64_t Session() const { return Active() ? m_session : 0; }
        bool ReadSlot(int slot, PresentationSlot &out) const;
        bool Matches(int slot, uint64_t session, uint64_t incarnation) const;
        void PublishAdopt(int slot, uint64_t sid, const char *name, const char *crosshair, uint32_t flair);
        void PublishRelease(int slot);
        void UpdateSyntheticSid(int slot, uint64_t sid);
        void UpdatePersonaName(int slot, const char *name);
        void UpdatePing(int slot, int ping);
        void PublishSignature(const char *name, const void *address);
        bool ReadSignature(int index, PresentationSignature &out) const;
    private:
        uint64_t m_session = 0, m_nextIncarnation = 0;
        bool m_active = false;
        std::thread::id m_thread;
        std::array<PresentationSlot, 64> m_slots{};
        std::array<PresentationSignature, 8> m_signatures{};
        int m_signatureCount = 0;
        PresentationChanged m_listener = nullptr;
        void Notify(uint32_t reason, int slot) const { if (m_listener) m_listener(reason, slot); }
    };
    SlotPublisher &Publisher();
}
