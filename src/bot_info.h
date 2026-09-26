// bot_info.h
//
// Loads bot identity data from addons/BotHider/bot_info.json

#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>
#include <unordered_map>

namespace cs2bh
{

    struct BotEntry
    {
        std::string Name;
        uint32_t AccountId = 0;
        uint64_t SteamId64 = 0;
        std::string CrosshairCode;
        uint32_t ScoreboardFlair = 0;
    };

    class BotInfoStore
    {
    public:
        // Load from disk. Returns false + logs on failure
        bool Load(const char *path);

        const BotEntry *PickForBot(const char *engineName);

        // Used only when adopting a persona base. Lease identities remain exact.
        uint64_t ResolveBaseSteamId(uint64_t desired,
                                   const std::function<bool(uint64_t)> &isInUse) const;

        // Release an entry's assignment (slot freed / mapchange).
        void ReleaseAssignment(const BotEntry *entry);
        void ResetAssignments();

        size_t Count() const { return m_Entries.size(); }

        // Steam3 → SteamID64
        static constexpr uint64_t kSteamId64Base = 76561197960265728ULL;

    private:
        std::vector<BotEntry> m_Entries;
        std::unordered_map<std::string, size_t> m_ByName;
        std::vector<bool> m_Assigned;
        uint64_t m_RngState = 0;
    };

    BotInfoStore &BotInfo();

} // namespace cs2bh
