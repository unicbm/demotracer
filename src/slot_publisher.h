// slot_publisher.h
//
// Owns the shared-memory mapping

#pragma once

#include "slot_shm.h"

#include <cstdint>
#include <functional>

namespace cs2bh
{

    class SlotPublisher
    {
    public:
        using SteamIdSink = std::function<void(int slot, uint64_t sid)>;
        using PersonaSink = std::function<void(int slot, const char *name)>;
        using DisguiseSink = std::function<void(bool enabled)>;
        using RebuildSink = std::function<void()>;
        using NameSourceSink = std::function<void(bool useBotInfo)>;

        ~SlotPublisher();

        // Create + map the shared region
        bool Init();
        void Shutdown();

        // Data-region writers
        void PublishAdopt(int slot, uint64_t syntheticSid, const char *personaName,
                          const char *crosshairCode, uint32_t scoreboardFlair);
        void PublishRelease(int slot);
        void UpdateSyntheticSid(int slot, uint64_t sid);
        void UpdatePersonaName(int slot, const char *name);
        void UpdatePing(int slot, int ping);

        // Append a signature/hook status entry (addr==0 means unresolved)
        void PublishSignature(const char *name, const void *addr);

        // CSS->C++
        void DrainCommands(const SteamIdSink &onSteamId, const PersonaSink &onPersona,
                           const DisguiseSink &onDisguise, const RebuildSink &onRebuild,
                           const NameSourceSink &onNameSource);

        bool Active() const { return m_pView != nullptr; }

    private:
        unsigned char *SlotStatePtr() const;
        uint64_t *SidPtr(int slot) const;
        char *NamePtr(int slot) const;
        uint64_t *BaseSidPtr(int slot) const;
        char *BaseNamePtr(int slot) const;
        int *PingPtr(int slot) const;
        char *CrosshairPtr(int slot) const;
        uint32_t *ScoreboardFlairPtr(int slot) const;
        void BumpGen();

        void *m_hMapping = nullptr; // HANDLE
        unsigned char *m_pView = nullptr;
    };

    SlotPublisher &Publisher();

} // namespace cs2bh
