// bot_info.cpp

#include "bot_info.h"

#include <fstream>
#include <cstdio>
#include <chrono>

#include <nlohmann/json.hpp>

namespace cs2bh
{

    namespace
    {
        BotInfoStore g_BotInfo;

        // xorshift64* — local RNG.
        uint64_t NextRand(uint64_t &s)
        {
            uint64_t x = s ? s : 0x9E3779B97F4A7C15ULL;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            s = x;
            return x * 0x2545F4914F6CDD1DULL;
        }
    } // namespace

    BotInfoStore &BotInfo() { return g_BotInfo; }

    bool BotInfoStore::Load(const char *path)
    {
        m_Entries.clear();
        m_ByName.clear();
        m_Assigned.clear();
        m_RngState = static_cast<uint64_t>(
                         std::chrono::steady_clock::now().time_since_epoch().count()) |
                     1ULL;

        std::ifstream ifs(path);
        if (!ifs.is_open())
            return false;

        nlohmann::json root;
        try
        {
            root = nlohmann::json::parse(ifs);
        }
        catch (...)
        {
            return false;
        }

        if (!root.is_object())
            return false;

        for (auto &[key, val] : root.items())
        {
            if (!val.is_object())
                continue;
            BotEntry e;
            e.Name = key;
            if (val.contains("steamid") && val["steamid"].is_number_unsigned())
                e.AccountId = val["steamid"].get<uint32_t>();
            else if (val.contains("steamid") && val["steamid"].is_number_integer())
                e.AccountId = static_cast<uint32_t>(val["steamid"].get<int64_t>());
            e.SteamId64 = kSteamId64Base + static_cast<uint64_t>(e.AccountId);
            if (val.contains("crosshair_code") && val["crosshair_code"].is_string())
                e.CrosshairCode = val["crosshair_code"].get<std::string>();
            if (val.contains("scoreboard_flair") && val["scoreboard_flair"].is_number_unsigned())
            {
                uint64_t flair = val["scoreboard_flair"].get<uint64_t>();
                e.ScoreboardFlair = flair <= 0xFFFFu ? static_cast<uint32_t>(flair) : 0;
            }
            else if (val.contains("scoreboard_flair") && val["scoreboard_flair"].is_number_integer())
            {
                int64_t flair = val["scoreboard_flair"].get<int64_t>();
                e.ScoreboardFlair = (flair >= 0 && flair <= 0xFFFF) ? static_cast<uint32_t>(flair) : 0;
            }
            m_ByName[e.Name] = m_Entries.size();
            m_Entries.push_back(std::move(e));
        }
        m_Assigned.assign(m_Entries.size(), false);
        return !m_Entries.empty();
    }

    const BotEntry *BotInfoStore::FindByName(const char *name) const
    {
        if (!name)
            return nullptr;
        auto it = m_ByName.find(name);
        if (it == m_ByName.end())
            return nullptr;
        return &m_Entries[it->second];
    }

    const BotEntry *BotInfoStore::PickForBot(const char *engineName)
    {
        if (m_Entries.empty())
            return nullptr;

        // Same-name
        if (engineName)
        {
            auto it = m_ByName.find(engineName);
            if (it != m_ByName.end())
            {
                m_Assigned[it->second] = true;
                return &m_Entries[it->second];
            }
        }

        // pick a random entry
        std::vector<size_t> free;
        free.reserve(m_Entries.size());
        for (size_t i = 0; i < m_Entries.size(); ++i)
            if (!m_Assigned[i])
                free.push_back(i);

        // fallback
        if (free.empty())
        {
            size_t idx = NextRand(m_RngState) % m_Entries.size();
            return &m_Entries[idx];
        }
        size_t pick = free[NextRand(m_RngState) % free.size()];
        m_Assigned[pick] = true;
        return &m_Entries[pick];
    }

    void BotInfoStore::ReleaseAssignment(const BotEntry *entry)
    {
        if (!entry)
            return;
        auto it = m_ByName.find(entry->Name);
        if (it != m_ByName.end())
            m_Assigned[it->second] = false;
    }

    void BotInfoStore::ResetAssignments()
    {
        m_Assigned.assign(m_Entries.size(), false);
    }

} // namespace cs2bh
